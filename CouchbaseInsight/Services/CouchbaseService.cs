using System.Text.Json;
using Couchbase;
using Couchbase.Core.IO.Serializers;
using Couchbase.Diagnostics;
using Couchbase.Query;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public class CouchbaseService : ICouchbaseService
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    private ICluster? _cluster;

    public bool IsConnected => _cluster is not null;

    public async Task<string> ConnectAsync(ConnectionSettings settings)
    {
        await DisconnectAsync();

        var info = ConnectionStringInspector.Inspect(settings.ConnectionString);

        var options = new ClusterOptions()
            .WithCredentials(settings.Username, settings.Password);

        options.Serializer = SystemTextJsonSerializer.Create();

        // Set this in both directions rather than only when the box is ticked. Leaving it unset
        // means NetworkResolution.Auto, and Auto does not mean "use the addresses I typed": as soon
        // as any node in the cluster map publishes an `external` alternate address, Auto can decide
        // the whole cluster is to be addressed by those alternates. On a cluster where a single node
        // publishes one, that swaps reachable LAN IPs for a public IP this machine cannot open a KV
        // socket to, the node list ends up empty, and every later call fails with "Service mgmt/n1ql
        // is either not configured or cannot be reached" — from a bootstrap that reported success.
        options.NetworkResolution = settings.UseNetworkResolutionExternal
            ? NetworkResolution.External
            : NetworkResolution.Default;

        ICluster cluster;
        try
        {
            cluster = await Cluster.ConnectAsync(settings.ConnectionString, options);
        }
        catch (Exception ex)
        {
            throw new ClusterConnectException(
                "Bootstrap failed — no seed host could be reached.",
                ConnectionDiagnostics.BuildReport(settings, info, resolvedNodes: null, ex),
                ex);
        }

        try
        {
            // Gate on the two services whose absence makes a session useless, not on the default
            // set, which demands every service on every node: a node hosting one this tool never
            // touches would otherwise fail an entirely usable connection. (The "Service mgmt is
            // either not configured or cannot be reached" this gate once reported was not a
            // lazy-registration race — it was the network resolution above leaving the SDK with no
            // nodes at all, which the empty-map check after this block now reports directly.)
            await cluster.WaitUntilReadyAsync(
                ReadyTimeout,
                new WaitUntilReadyOptions().ServiceTypes(ServiceType.KeyValue, ServiceType.Query));
        }
        catch (Exception ex)
        {
            // Bootstrap succeeded, so the cluster object already holds the addresses that came back
            // in the config map. Capture them before disposing — comparing those against the seeds
            // is what makes an otherwise blank timeout diagnosable.
            var nodes = await ConnectionDiagnostics.ResolvedNodesAsync(cluster);
            await cluster.DisposeAsync();

            throw new ClusterConnectException(
                $"Reached a seed host, but the cluster was not ready within {ReadyTimeout.TotalSeconds:0}s. " +
                "This gate waits for every service on every node, so one unreachable node fails it.",
                ConnectionDiagnostics.BuildReport(settings, info, nodes, ex),
                ex);
        }

        // WaitUntilReady passes vacuously when the SDK holds no nodes at all — it waits for the
        // endpoints it knows about, and zero of them are trivially ready. Catch that here, while
        // the seed list is still in hand, instead of letting it surface later as an unrelated-
        // looking "Service mgmt is either not configured or cannot be reached" from the first call.
        var resolved = await ConnectionDiagnostics.ResolvedNodesAsync(cluster);
        if (resolved.Count == 0)
        {
            await cluster.DisposeAsync();
            throw new ClusterConnectException(
                "Bootstrapped, but the cluster map produced no usable nodes — the addresses the " +
                "cluster advertises are not reachable from this machine. Check 'External network'.",
                ConnectionDiagnostics.BuildReport(settings, info, resolved, error: null));
        }

        _cluster = cluster;
        return ConnectionDiagnostics.BuildReport(settings, info, resolved, error: null);
    }

    public async Task DisconnectAsync()
    {
        if (_cluster is not null)
        {
            await _cluster.DisposeAsync();
            _cluster = null;
        }
    }

    public async Task<IReadOnlyList<string>> GetBucketsAsync()
    {
        var cluster = EnsureConnected();
        var buckets = await cluster.Buckets.GetAllBucketsAsync();
        return buckets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(string bucket)
    {
        var cluster = EnsureConnected();
        var bucketObj = await cluster.BucketAsync(bucket);
        var scopes = await bucketObj.Collections.GetAllScopesAsync();

        return scopes
            .Select(s => new ScopeInfo
            {
                Name = s.Name,
                Collections = s.Collections
                    .Select(c => c.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetClassesAsync(
        string bucket, string scope, string collection, string discriminator)
    {
        var cluster = EnsureConnected();
        var keyspace = Keyspace(bucket, scope, collection);
        var field = EscapeIdentifier(discriminator);

        var statement =
            $"SELECT DISTINCT {field} AS cls FROM {keyspace} " +
            $"WHERE {field} IS NOT MISSING AND {field} IS NOT NULL " +
            $"ORDER BY cls";

        var result = await cluster.QueryAsync<JsonElement>(statement);

        var classes = new List<string>();
        await foreach (var row in result)
        {
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("cls", out var cls))
            {
                classes.Add(ValueToString(cls));
            }
        }
        return classes;
    }

    public async Task<ClassSchema> AnalyzeClassAsync(
        string bucket, string scope, string collection,
        string discriminator, string className, int? limit)
    {
        var cluster = EnsureConnected();
        var keyspace = Keyspace(bucket, scope, collection);
        var field = EscapeIdentifier(discriminator);

        var statement =
            $"SELECT c.* FROM {keyspace} AS c WHERE c.{field} = $cls";
        if (limit is > 0)
            statement += $" LIMIT {limit.Value}";

        var options = new QueryOptions().Parameter("cls", className);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var analyzer = new SchemaAnalyzer();
        await foreach (var row in result)
            analyzer.Add(row);

        return analyzer.Build(className);
    }

    public async Task<IReadOnlyList<string>> GetDistinctValuesAsync(
        string bucket, string scope, string collection,
        string discriminator, string className,
        string fieldPath, int limit,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var keyspace = Keyspace(bucket, scope, collection);
        var discField = EscapeIdentifier(discriminator);
        var fieldExpr = NestedFieldExpr("c", fieldPath);

        var statement =
            $"SELECT DISTINCT RAW {fieldExpr} " +
            $"FROM {keyspace} AS c " +
            $"WHERE c.{discField} = $cls " +
            $"AND {fieldExpr} IS NOT MISSING " +
            $"LIMIT {limit}";

        var options = new QueryOptions()
            .Parameter("cls", className)
            .CancellationToken(cancellationToken);

        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var values = new List<string>();
        await foreach (var row in result.WithCancellation(cancellationToken))
            values.Add(DisplayValue(row));

        values.Sort(StringComparer.OrdinalIgnoreCase);
        return values;
    }

    public async Task<IReadOnlyList<FieldInfo>> GetCollectionFieldsAsync(
        string bucket, string scope, string collection, int limit = 500)
    {
        var cluster = EnsureConnected();
        var keyspace = Keyspace(bucket, scope, collection);
        var statement = $"SELECT c.* FROM {keyspace} AS c LIMIT {limit}";
        var result = await cluster.QueryAsync<JsonElement>(statement);

        var analyzer = new SchemaAnalyzer();
        await foreach (var row in result)
            analyzer.Add(row);

        return analyzer.Build(collection).Fields;
    }

    public async Task<(int Count, string Json)> RunQueryAsync(string statement, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var options = new QueryOptions().CancellationToken(cancellationToken);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var rows = new List<JsonElement>();
        await foreach (var row in result.WithCancellation(cancellationToken))
            rows.Add(row);

        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        return (rows.Count, json);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private ICluster EnsureConnected() =>
        _cluster ?? throw new InvalidOperationException("Not connected to a cluster.");

    private static string Keyspace(string bucket, string scope, string collection) =>
        $"{EscapeIdentifier(bucket)}.{EscapeIdentifier(scope)}.{EscapeIdentifier(collection)}";

    private static string EscapeIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    /// <summary>Converts a dotted field path like "address.city" to c.`address`.`city`.</summary>
    private static string NestedFieldExpr(string alias, string dottedPath) =>
        alias + "." + string.Join(".", dottedPath.Split('.').Select(EscapeIdentifier));

    private static string ValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText()
    };

    private static string DisplayValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "(null)",
        JsonValueKind.Null => "(null)",
        JsonValueKind.Undefined => "(undefined)",
        _ => element.GetRawText()
    };
}
