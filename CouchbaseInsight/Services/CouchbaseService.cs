using System.Text.Json;
using Couchbase;
using Couchbase.Core.IO.Serializers;
using Couchbase.Query;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public class CouchbaseService : ICouchbaseService
{
    private ICluster? _cluster;

    public bool IsConnected => _cluster is not null;

    public async Task ConnectAsync(ConnectionSettings settings)
    {
        await DisconnectAsync();

        var options = new ClusterOptions()
            .WithCredentials(settings.Username, settings.Password);

        options.Serializer = SystemTextJsonSerializer.Create();

        if (settings.UseNetworkResolutionExternal)
            options.NetworkResolution = NetworkResolution.External;

        _cluster = await Cluster.ConnectAsync(settings.ConnectionString, options);
        await _cluster.WaitUntilReadyAsync(TimeSpan.FromSeconds(15));
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
