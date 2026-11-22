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

    public async Task<long> CountDocumentsAsync(
        string bucket, string scope, string collection, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        // COUNT(*) with no WHERE is answered from the collection's item count, not by a scan.
        var result = await cluster.QueryAsync<long>(
            $"SELECT RAW COUNT(*) FROM {Keyspace(bucket, scope, collection)}",
            new QueryOptions().CancellationToken(cancellationToken));
        await foreach (var n in result) return n;
        return 0;
    }

    public async IAsyncEnumerable<DocumentRow> ReadRangeAsync(
        string bucket, string scope, string collection, string afterKey, string? endKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();

        // A key range on the primary index, with no ORDER BY: the range alone says which documents
        // belong to it, so the server can hand rows over as soon as they are fetched. The expiry and
        // CAS are metadata the server holds with each document: a few bytes, no extra lookup.
        var statement =
            $"SELECT META(d).id AS k, META(d).expiration AS exp, META(d).cas AS cas, d AS doc " +
            $"FROM {Keyspace(bucket, scope, collection)} AS d " +
            (endKey is null ? "WHERE META(d).id > $after" : "WHERE META(d).id > $after AND META(d).id <= $end");

        // Fetching is what limits a read: the query service gets documents from the data service in
        // groups of pipeline_batch (default 16), with at most pipeline_cap rows queued per operator.
        // Larger groups mean far fewer round trips per document.
        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .PipelineBatch(FetchBatch)
            .PipelineCap(FetchQueue)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        if (endKey is not null) options.Parameter("end", endKey);

        var result = await cluster.QueryAsync<JsonElement>(statement, ForBatchWork(options));
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("k", out var key)) continue;
            // A binary (non-JSON) document has a key but no doc: hand it on as Undefined.
            var doc = row.TryGetProperty("doc", out var d) ? d : default;
            var exp = row.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number &&
                      e.TryGetInt64(out var expValue) ? expValue : 0;
            // A CAS is a 64-bit unsigned integer: read it as one, not through a double.
            var cas = row.TryGetProperty("cas", out var c) && c.ValueKind == JsonValueKind.Number &&
                      c.TryGetUInt64(out var casValue) ? casValue : 0UL;
            yield return new DocumentRow(key.GetString() ?? "", doc, exp, cas);
        }
    }

    private const int FetchBatch = 256;
    private const int FetchQueue = 8192;

    /// <summary>
    /// Gentle mode: batch queries leave room for the live application on the same cluster. One
    /// fetch thread per query (max_parallelism 1), the server's own fetch sizes (pipeline_batch 16,
    /// pipeline_cap 512) instead of large groups, and a short timeout so a query that has stalled
    /// is stopped by the server rather than holding resources for hours. Off: fetch in large groups
    /// with a long timeout, for the fastest read.
    /// </summary>
    public bool GentleMode { get; set; } = true;

    private static readonly TimeSpan GentleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The settings every batch query runs with, gentle or fast. Applied last, so they win.</summary>
    private QueryOptions ForBatchWork(QueryOptions options) => GentleMode
        ? options.PipelineBatch(16).PipelineCap(512).MaxServerParallelism(1).Timeout(GentleTimeout)
        : options.PipelineBatch(FetchBatch).PipelineCap(FetchQueue).Timeout(PageTimeout);

    public async Task<string?> GetBatchEndKeyAsync(
        string bucket, string scope, string collection, string afterKey, int batchSize,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        // Keys only: the primary index answers this without fetching a single document.
        var statement =
            $"SELECT RAW META(d).id FROM {Keyspace(bucket, scope, collection)} AS d " +
            $"WHERE META(d).id > $after ORDER BY META(d).id OFFSET {batchSize - 1} LIMIT 1";

        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        var result = await cluster.QueryAsync<string>(statement, ForBatchWork(options));
        await foreach (var key in result.WithCancellation(cancellationToken))
            return key;
        return null;
    }

    public async Task<IReadOnlyList<(string? Class, long Count, IReadOnlyList<string> Keys)>> CountClassesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        string afterKey, string? endKey, int keysPerClass = 0, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var field = EscapeIdentifier(discriminator);
        // Counted on the server: only one row per class comes back, however many documents.
        var statement =
            $"SELECT c.{field} AS cls, COUNT(*) AS n" +
            // A few keys of each class, so its sample can later be fetched by key without searching.
            (keysPerClass > 0 ? $", ARRAY_AGG(META(c).id)[0:LEAST({keysPerClass}, COUNT(*))] AS ks" : "") +
            $" FROM {Keyspace(bucket, scope, collection)} AS c " +
            (endKey is null ? "WHERE META(c).id > $after" : "WHERE META(c).id > $after AND META(c).id <= $end") +
            $" GROUP BY c.{field}";

        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        if (endKey is not null) options.Parameter("end", endKey);

        var result = await cluster.QueryAsync<JsonElement>(statement, ForBatchWork(options));
        var counts = new List<(string?, long, IReadOnlyList<string>)>();
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("n", out var n)) continue;
            var name = row.TryGetProperty("cls", out var cls) && cls.ValueKind != JsonValueKind.Null
                ? ValueToString(cls)
                : null;
            var keys = row.TryGetProperty("ks", out var ks) && ks.ValueKind == JsonValueKind.Array
                ? ks.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).ToList()
                : new List<string>();
            counts.Add((name, n.GetInt64(), keys));
        }
        return counts;
    }

    public async Task<IReadOnlyList<(string Key, JsonElement Document)>> GetDocumentsByKeysAsync(
        string bucket, string scope, string collection, IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        // USE KEYS: fetched directly by key, no scan or index lookup.
        var statement = $"SELECT META(c).id AS k, c AS doc FROM {Keyspace(bucket, scope, collection)} AS c USE KEYS $keys";
        var options = new QueryOptions().Parameter("keys", keys).CancellationToken(cancellationToken);

        var result = await cluster.QueryAsync<JsonElement>(statement, ForBatchWork(options));
        return await KeyedRows(result, cancellationToken);
    }

    /// <summary>Rows of {k, doc} as (key, document).</summary>
    private static async Task<IReadOnlyList<(string Key, JsonElement Document)>> KeyedRows(
        IQueryResult<JsonElement> result, CancellationToken ct)
    {
        var docs = new List<(string, JsonElement)>();
        await foreach (var row in result.WithCancellation(ct))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("k", out var k)) continue;
            docs.Add((k.GetString() ?? "", row.TryGetProperty("doc", out var d) ? d.Clone() : default));
        }
        return docs;
    }

    public async Task<IReadOnlyList<(string Key, JsonElement Document)>> GetFirstDocumentsAsync(
        string bucket, string scope, string collection, int limit, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        // The first documents by key: straight off the primary index, in its own order, so the
        // server reads only these few.
        var statement =
            $"SELECT META(c).id AS k, c AS doc FROM {Keyspace(bucket, scope, collection)} AS c " +
            $"ORDER BY META(c).id LIMIT {limit}";
        var result = await cluster.QueryAsync<JsonElement>(
            statement, new QueryOptions().Timeout(TimeSpan.FromSeconds(30)).CancellationToken(cancellationToken));
        return await KeyedRows(result, cancellationToken);
    }

    public async Task<IReadOnlyList<(string Key, JsonElement Document)>> GetClassSampleAsync(
        string bucket, string scope, string collection, string discriminator, string? className, int limit,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var disc = EscapeIdentifier(discriminator);
        var statement =
            $"SELECT META(c).id AS k, c AS doc FROM {Keyspace(bucket, scope, collection)} AS c WHERE " +
            (className is null ? $"(c.{disc} IS MISSING OR c.{disc} IS NULL)" : $"c.{disc} = $cls") +
            $" LIMIT {limit}";

        var options = new QueryOptions()
            .PipelineBatch(FetchBatch)
            .PipelineCap(FetchQueue)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        if (className is not null) options.Parameter("cls", className);

        var result = await cluster.QueryAsync<JsonElement>(statement, ForBatchWork(options));
        return await KeyedRows(result, cancellationToken);
    }

    /// <summary>The pseudo-field each document contributes once: counts the documents of each class read.</summary>
    public const string DocumentsField = "__documents";

    public async Task<IReadOnlyList<FieldValueRow>> CountFieldValuesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        IReadOnlyList<string> fields, string afterKey, string? endKey, int topPerField, bool windowed,
        CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var disc = EscapeIdentifier(discriminator);

        // One pass over the range for every class and every field asked: each document becomes one
        // (field name, value) pair per field, and the pairs are grouped by class, field and value.
        // The documents are read once however many fields and classes there are, and only counts
        // come back. A constant pseudo-field counts the documents of each class that were read.
        var pairs = string.Join(", ",
            new[] { $"{{\"n\": \"{DocumentsField}\", \"v\": true}}" }.Concat(fields.Select(f =>
                $"{{\"n\": {JsonSerializer.Serialize(f)}, \"v\": {NestedFieldExpr("c", f)}}}")));
        var where =
            (endKey is null ? "META(c).id > $after" : "META(c).id > $after AND META(c).id <= $end") +
            " AND f.v IS NOT MISSING";

        // Windowed: the server also returns, per class and field, how many distinct values the range
        // held, how many documents held the field, and min/max, and keeps only the top values.
        var inner =
            $"SELECT c.{disc} AS cls, f.n AS fld, f.v AS val, COUNT(1) AS cnt, MIN(META(c).id) AS ex" +
            (windowed
                ? $", RANK() OVER (PARTITION BY c.{disc}, f.n ORDER BY COUNT(1) DESC) AS rk" +
                  $", COUNT(1) OVER (PARTITION BY c.{disc}, f.n) AS ng" +
                  $", SUM(COUNT(1)) OVER (PARTITION BY c.{disc}, f.n) AS total" +
                  $", MIN(f.v) OVER (PARTITION BY c.{disc}, f.n) AS mn" +
                  $", MAX(f.v) OVER (PARTITION BY c.{disc}, f.n) AS mx"
                : "") +
            $" FROM {Keyspace(bucket, scope, collection)} AS c UNNEST [{pairs}] AS f" +
            $" WHERE {where} GROUP BY c.{disc}, f.n, f.v";
        var statement = windowed
            ? $"SELECT t.cls, t.fld, t.val, t.cnt, t.ex, t.ng, t.total, t.mn, t.mx FROM ({inner}) AS t WHERE t.rk <= {topPerField}"
            : inner;

        var options = new QueryOptions()
            .Parameter("after", afterKey)
            .PipelineBatch(FetchBatch)
            .PipelineCap(FetchQueue)
            .Timeout(PageTimeout)
            .CancellationToken(cancellationToken);
        if (endKey is not null) options.Parameter("end", endKey);

        var result = await cluster.QueryAsync<JsonElement>(statement, ForBatchWork(options));
        var rows = new List<FieldValueRow>();
        await foreach (var row in result.WithCancellation(cancellationToken))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("fld", out var fld)) continue;
            var cls = row.TryGetProperty("cls", out var c) && c.ValueKind != JsonValueKind.Null ? ValueToString(c) : null;
            var val = row.TryGetProperty("val", out var v) ? v.Clone() : default;
            long? Num(string name) => row.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number
                ? n.GetInt64() : null;
            JsonElement? El(string name) => row.TryGetProperty(name, out var e) ? e.Clone() : null;
            rows.Add(new FieldValueRow(
                cls, fld.GetString() ?? "", val, Num("cnt") ?? 0,
                windowed ? Num("ng") : null, windowed ? Num("total") : null,
                windowed ? El("mn") : null, windowed ? El("mx") : null,
                row.TryGetProperty("ex", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() : null));
        }
        return rows;
    }

    /// <summary>One page streams up to a few million documents; the SDK's default 75 s query
    /// timeout is for interactive queries and would cut it off part-way.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromHours(3);

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

    public async Task<QueryOutcome> RunQueryAsync(
        string statement, bool readOnly, CancellationToken cancellationToken = default)
    {
        var cluster = EnsureConnected();
        var options = new QueryOptions()
            .Readonly(readOnly)
            .Metrics(true)
            .CancellationToken(cancellationToken);
        var result = await cluster.QueryAsync<JsonElement>(statement, options);

        var rows = new List<JsonElement>();
        await foreach (var row in result.WithCancellation(cancellationToken))
            rows.Add(row);

        // Complete only once the rows have been read.
        var metrics = result.MetaData?.Metrics;
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        return new QueryOutcome(rows.Count, json, metrics?.MutationCount, metrics?.ElapsedTime);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private ICluster EnsureConnected() =>
        _cluster ?? throw new InvalidOperationException("Not connected to a cluster.");

    private static string Keyspace(string bucket, string scope, string collection) =>
        SqlText.Keyspace(bucket, scope, collection);

    private static string EscapeIdentifier(string identifier) => SqlText.Identifier(identifier);

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
