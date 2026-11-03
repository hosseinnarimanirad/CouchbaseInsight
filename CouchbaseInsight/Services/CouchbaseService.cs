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

    public async Task<ClassCounts> GetClassesAsync(
        string bucket, string scope, string collection, string discriminator, int? scanLimit,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var source = Source(Keyspace(bucket, scope, collection), scanLimit);
        var field = EscapeIdentifier(discriminator);

        // No WHERE: the documents without a class are counted too (their group has cls missing or
        // null), so the percentages are of every document read, not only the classified ones.
        var statement =
            $"SELECT c.{field} AS cls, COUNT(*) AS n FROM {source} GROUP BY c.{field}";

        var result = await cluster.QueryAsync<JsonElement>(
            statement, new QueryOptions().CancellationToken(cancellationToken));

        var counts = new List<(string Name, long Count)>();
        long total = 0, withoutClass = 0;
        await foreach (var row in result)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("n", out var n)) continue;
            var count = n.GetInt64();
            total += count;

            if (row.TryGetProperty("cls", out var cls) && cls.ValueKind != JsonValueKind.Null)
                counts.Add((ValueToString(cls), count));
            else
                withoutClass += count;
        }

        var classes = counts
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ClassEntry(c.Name, c.Count, total == 0 ? 0 : c.Count * 100.0 / total))
            .ToList();
        return new ClassCounts(classes, total, withoutClass);
    }

    public async Task<long> CountDocumentsAsync(string bucket, string scope, string collection)
    {
        var cluster = EnsureConnected();
        // COUNT(*) with no WHERE is answered from the collection's item count, not by a scan.
        var result = await cluster.QueryAsync<long>(
            $"SELECT RAW COUNT(*) FROM {Keyspace(bucket, scope, collection)}");
        await foreach (var n in result) return n;
        return 0;
    }

    public async Task<string?> GetBatchEndKeyAsync(
        string bucket, string scope, string collection, string afterKey, int batchSize,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        // Keys only: the primary index answers this without fetching a single document, so
        // finding where a batch of a million ends costs a walk over a million index entries.
        var statement =
            $"SELECT RAW META(d).id FROM {Keyspace(bucket, scope, collection)} AS d " +
            $"WHERE META(d).id > $after ORDER BY META(d).id OFFSET {batchSize - 1} LIMIT 1";

        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        var result = await cluster.QueryAsync<string>(statement, options);
        await foreach (var key in result.WithCancellation(cancellationToken))
            return key;
        return null;
    }

    public async Task<IReadOnlyList<(string? Class, long Count)>> CountClassesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        string afterKey, string? endKey, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var field = EscapeIdentifier(discriminator);
        var statement =
            $"SELECT c.{field} AS cls, COUNT(*) AS n FROM {Keyspace(bucket, scope, collection)} AS c " +
            KeyRange("c", endKey) +
            $" GROUP BY c.{field}";

        var options = RangeOptions(afterKey, endKey, cancellationToken);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var counts = new List<(string?, long)>();
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("n", out var n)) continue;
            var name = row.TryGetProperty("cls", out var cls) && cls.ValueKind != JsonValueKind.Null
                ? ValueToString(cls)
                : null;
            counts.Add((name, n.GetInt64()));
        }
        return counts;
    }

    public async IAsyncEnumerable<JsonElement> ReadClassInRangeAsync(
        string bucket, string scope, string collection, string discriminator, string className,
        string afterKey, string? endKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var field = EscapeIdentifier(discriminator);
        var statement =
            $"SELECT RAW c FROM {Keyspace(bucket, scope, collection)} AS c " +
            KeyRange("c", endKey) +
            $" AND c.{field} = $cls";

        var options = RangeOptions(afterKey, endKey, cancellationToken).Parameter("cls", className);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);
        await foreach (var row in result.WithCancellation(cancellationToken))
            yield return row;
    }

    /// <summary>WHERE on a key range: (after, end], or everything after <c>after</c> for the last batch.</summary>
    private static string KeyRange(string alias, string? endKey) =>
        endKey is null
            ? $"WHERE META({alias}).id > $after"
            : $"WHERE META({alias}).id > $after AND META({alias}).id <= $end";

    private static QueryOptions RangeOptions(string afterKey, string? endKey, CancellationToken ct)
    {
        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .Timeout(PageTimeout)
            .CancellationToken(ct);
        return endKey is null ? options : options.Parameter("end", endKey);
    }

    public async IAsyncEnumerable<(string Key, JsonElement Document)> ReadPageAsync(
        string bucket, string scope, string collection, string? afterKey, int pageSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();

        // Keyset paging on the primary index: the range on META().id and the ORDER BY both follow
        // the index's own key order, so each page starts where the last one stopped without the
        // server skipping over the pages before it (which an OFFSET would make it do).
        var statement =
            $"SELECT META(d).id AS k, d AS doc FROM {Keyspace(bucket, scope, collection)} AS d " +
            $"WHERE META(d).id > $after ORDER BY META(d).id LIMIT {pageSize}";

        var options = new QueryOptions()
            .Parameter("after", afterKey ?? "")
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);

        var result = await cluster.QueryAsync<JsonElement>(statement, options);
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("k", out var key)) continue;
            // A binary (non-JSON) document has a key but no doc: hand it on as Undefined.
            var doc = row.TryGetProperty("doc", out var d) ? d : default;
            yield return (key.GetString() ?? "", doc);
        }
    }

    /// <summary>One page streams up to a few million documents; the SDK's default 75 s query
    /// timeout is for interactive queries and would cut it off part-way.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromHours(3);

    public async Task<ClassSchema> AnalyzeClassAsync(
        string bucket, string scope, string collection,
        string discriminator, string className, int? limit, int? scanLimit,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var source = Source(Keyspace(bucket, scope, collection), scanLimit);
        var field = EscapeIdentifier(discriminator);

        var statement =
            $"SELECT c.* FROM {source} WHERE c.{field} = $cls";
        if (limit is > 0)
            statement += $" LIMIT {limit.Value}";

        var options = new QueryOptions().Parameter("cls", className).CancellationToken(cancellationToken);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var analyzer = new SchemaAnalyzer();
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            analyzer.Add(row);
            if (analyzer.SampleSize % 250 == 0) progress?.Report(analyzer.SampleSize);
        }
        progress?.Report(analyzer.SampleSize);

        return analyzer.Build(className);
    }

    public async Task<IReadOnlyList<string>> GetDistinctValuesAsync(
        string bucket, string scope, string collection,
        string discriminator, string className,
        string fieldPath, int limit, int? scanLimit,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var source = Source(Keyspace(bucket, scope, collection), scanLimit);
        var discField = EscapeIdentifier(discriminator);
        var fieldExpr = NestedFieldExpr("c", fieldPath);

        var statement =
            $"SELECT DISTINCT RAW {fieldExpr} " +
            $"FROM {source} " +
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

    /// <summary>
    /// The FROM term, aliased <c>c</c>: the whole collection, or only its first
    /// <paramref name="scanLimit"/> documents. The LIMIT sits inside the subquery, before any
    /// WHERE, so on a collection with only a primary index the server stops after that many keys
    /// instead of fetching every document to test the condition — the outer query then filters and
    /// groups a bounded sample. "First" is primary-key order, not newest.
    /// </summary>
    private static string Source(string keyspace, int? scanLimit) =>
        scanLimit is > 0
            ? $"(SELECT RAW d FROM {keyspace} AS d LIMIT {scanLimit.Value}) AS c"
            : $"{keyspace} AS c";

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
