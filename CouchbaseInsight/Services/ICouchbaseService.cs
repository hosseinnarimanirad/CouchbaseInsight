using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public interface ICouchbaseService : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(ConnectionSettings settings);
    Task DisconnectAsync();

    Task<IReadOnlyList<string>> GetBucketsAsync();
    Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(string bucket);

    Task<IReadOnlyList<string>> GetClassesAsync(
        string bucket, string scope, string collection, string discriminator);

    /// <param name="limit">Max documents to sample, or null to analyze all.</param>
    Task<ClassSchema> AnalyzeClassAsync(
        string bucket, string scope, string collection,
        string discriminator, string className, int? limit);

    /// <summary>Returns up to <paramref name="limit"/> distinct display-string values for a field.</summary>
    Task<IReadOnlyList<string>> GetDistinctValuesAsync(
        string bucket, string scope, string collection,
        string discriminator, string className,
        string fieldPath, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Samples up to <paramref name="limit"/> documents from a collection and returns the discovered fields.</summary>
    Task<IReadOnlyList<FieldInfo>> GetCollectionFieldsAsync(
        string bucket, string scope, string collection, int limit = 500);

    /// <summary>Executes an arbitrary N1QL statement and returns the row count and rows as a pretty-printed JSON array.</summary>
    Task<(int Count, string Json)> RunQueryAsync(string statement, CancellationToken cancellationToken = default);
}
