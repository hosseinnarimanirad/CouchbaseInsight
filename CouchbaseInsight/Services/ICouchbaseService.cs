using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public interface ICouchbaseService : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Opens the cluster connection and returns a diagnostic report: the seed hosts, anything
    /// suspicious about the connection string, and the node addresses the SDK resolved from the
    /// cluster map. On failure throws <see cref="ClusterConnectException"/> carrying the same
    /// report plus the unwrapped exception chain.
    /// </summary>
    Task<string> ConnectAsync(ConnectionSettings settings);

    Task DisconnectAsync();

    Task<IReadOnlyList<string>> GetBucketsAsync();
    Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(string bucket);

    // scanLimit, on the three calls below: read only the collection's first N documents (primary-key
    // order), or null for the whole collection.

    /// <summary>Every class with its document count, plus the totals the percentages are of.</summary>
    Task<ClassCounts> GetClassesAsync(
        string bucket, string scope, string collection, string discriminator, int? scanLimit,
        CancellationToken cancellationToken = default);

    /// <summary>The collection's document count (from its item count, not a scan).</summary>
    Task<long> CountDocumentsAsync(string bucket, string scope, string collection);

    // ---- Batched reading ----
    // A whole-collection read is split into batches of N documents by key: GetBatchEndKeyAsync
    // finds where a batch ends (keys only, from the primary index), and the *InRange calls work
    // on (after, end]. An end of null means the batch runs to the end of the collection.

    /// <summary>The key of the <paramref name="batchSize"/>-th document after
    /// <paramref name="afterKey"/>, or null when fewer than that remain (the last batch).</summary>
    Task<string?> GetBatchEndKeyAsync(
        string bucket, string scope, string collection, string afterKey, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Documents per class in the key range; a null class counts those without one.</summary>
    Task<IReadOnlyList<(string? Class, long Count)>> CountClassesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        string afterKey, string? endKey, CancellationToken cancellationToken = default);

    /// <summary>The documents of one class in the key range, streamed.</summary>
    IAsyncEnumerable<System.Text.Json.JsonElement> ReadClassInRangeAsync(
        string bucket, string scope, string collection, string discriminator, string className,
        string afterKey, string? endKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="pageSize"/> documents with keys after <paramref name="afterKey"/>, in
    /// key order, streamed. Null or empty <paramref name="afterKey"/> starts at the beginning.
    /// </summary>
    IAsyncEnumerable<(string Key, System.Text.Json.JsonElement Document)> ReadPageAsync(
        string bucket, string scope, string collection, string? afterKey, int pageSize,
        CancellationToken cancellationToken = default);

    /// <param name="limit">Max documents of the class to analyse, or null for all of them.</param>
    Task<ClassSchema> AnalyzeClassAsync(
        string bucket, string scope, string collection,
        string discriminator, string className, int? limit, int? scanLimit,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Returns up to <paramref name="limit"/> distinct display-string values for a field.</summary>
    Task<IReadOnlyList<string>> GetDistinctValuesAsync(
        string bucket, string scope, string collection,
        string discriminator, string className,
        string fieldPath, int limit, int? scanLimit,
        CancellationToken cancellationToken = default);

    /// <summary>Samples up to <paramref name="limit"/> documents from a collection and returns the discovered fields.</summary>
    Task<IReadOnlyList<FieldInfo>> GetCollectionFieldsAsync(
        string bucket, string scope, string collection, int limit = 500);

    /// <summary>Executes an arbitrary N1QL statement and returns the row count and rows as a pretty-printed JSON array.</summary>
    Task<(int Count, string Json)> RunQueryAsync(string statement, CancellationToken cancellationToken = default);
}
