using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public interface ICouchbaseService : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Batch queries go easy on the cluster: one fetch thread, small fetch groups, a short
    /// server-side timeout. The caller also runs one batch at a time with a pause between batches.</summary>
    bool GentleMode { get; set; }

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

    /// <summary>The collection's document count (from its item count, not a scan).</summary>
    Task<long> CountDocumentsAsync(
        string bucket, string scope, string collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// The documents with keys in (<paramref name="afterKey"/>, <paramref name="endKey"/>], streamed
    /// in no particular order; a null end runs to the end of the collection. This is how Load reads
    /// documents: ranges cut by <see cref="GetBatchEndKeyAsync"/>, several read at once.
    /// </summary>
    IAsyncEnumerable<DocumentRow> ReadRangeAsync(
        string bucket, string scope, string collection, string afterKey, string? endKey,
        CancellationToken cancellationToken = default);

    // ---- Exact counts ----
    // Counting every class over the whole collection without bringing documents to the app: the
    // collection is cut into key ranges of N documents (GetBatchEndKeyAsync, keys only), and each
    // range is counted on the server (CountClassesInRangeAsync). See BatchPlanner.

    /// <summary>The key of the <paramref name="batchSize"/>-th document after
    /// <paramref name="afterKey"/>, or null when fewer than that remain.</summary>
    Task<string?> GetBatchEndKeyAsync(
        string bucket, string scope, string collection, string afterKey, int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Documents per class in the key range (after, end] (end null: to the end of the
    /// collection); a null class counts those without one. With <paramref name="keysPerClass"/>,
    /// also up to that many document keys of each class, for sampling it by key later.</summary>
    Task<IReadOnlyList<(string? Class, long Count, IReadOnlyList<string> Keys)>> CountClassesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        string afterKey, string? endKey, int keysPerClass = 0, CancellationToken cancellationToken = default);

    /// <summary>The collection's first <paramref name="limit"/> documents by key, with their keys.</summary>
    Task<IReadOnlyList<(string Key, System.Text.Json.JsonElement Document)>> GetFirstDocumentsAsync(
        string bucket, string scope, string collection, int limit, CancellationToken cancellationToken = default);

    /// <summary>The documents with these keys, fetched directly (no scan).</summary>
    Task<IReadOnlyList<(string Key, System.Text.Json.JsonElement Document)>> GetDocumentsByKeysAsync(
        string bucket, string scope, string collection, IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);

    // ---- Two-step analysis ----

    /// <summary>Up to <paramref name="limit"/> documents of one class (null: documents without a
    /// class), whole, to list the class's fields.</summary>
    Task<IReadOnlyList<(string Key, System.Text.Json.JsonElement Document)>> GetClassSampleAsync(
        string bucket, string scope, string collection, string discriminator, string? className, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// For every class and each of <paramref name="fields"/>, the documents in the key range per
    /// value, counted on the server in one pass; plus, as the pseudo-field
    /// <c>CouchbaseService.DocumentsField</c>, the documents of each class in the range.
    /// <paramref name="windowed"/>: also the per-field totals and min/max, keeping the top
    /// <paramref name="topPerField"/> values per class and field (needs window functions); otherwise
    /// every value comes back and the app works the totals out.
    /// </summary>
    Task<IReadOnlyList<FieldValueRow>> CountFieldValuesInRangeAsync(
        string bucket, string scope, string collection, string discriminator,
        IReadOnlyList<string> fields, string afterKey, string? endKey, int topPerField, bool windowed,
        CancellationToken cancellationToken = default);

    /// <summary>Samples up to <paramref name="limit"/> documents from a collection and returns the discovered fields.</summary>
    Task<IReadOnlyList<FieldInfo>> GetCollectionFieldsAsync(
        string bucket, string scope, string collection, int limit = 500);

    /// <summary>
    /// Executes an arbitrary N1QL statement and returns its rows as a pretty-printed JSON array,
    /// with the server's mutation count and elapsed time. With <paramref name="readOnly"/> the
    /// server itself refuses any statement that would change data.
    /// </summary>
    Task<QueryOutcome> RunQueryAsync(string statement, bool readOnly, CancellationToken cancellationToken = default);
}

/// <summary>What one statement returned: its rows, and for a write how many documents it changed.</summary>
/// <param name="MutationCount">Documents inserted, updated or deleted; null when the server did not say.</param>
/// <param name="Elapsed">The server's own elapsed time for the request, as it reports it (e.g. "12.3ms").</param>
public sealed record QueryOutcome(int Count, string Json, ulong? MutationCount, string? Elapsed);
