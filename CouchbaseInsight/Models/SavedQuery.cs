namespace CouchbaseInsight.Models;

/// <summary>Whether a statement only reads, or changes the cluster.</summary>
public enum QueryKind
{
    Read,

    /// <summary>
    /// UPDATE, DELETE, INSERT, UPSERT, MERGE — and anything that is not plainly a read
    /// (CREATE INDEX, DROP, …), so an unknown statement is never treated as harmless.
    /// </summary>
    Write
}

/// <summary>
/// A SQL++ statement the user saved under a name, owned by one connection profile.
/// Persisted by <see cref="Services.ISavedQueryStore"/>; holds no secret.
/// </summary>
public sealed class SavedQuery
{
    /// <summary>Stable identity, so a rename or a move to another folder is still the same query.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The <see cref="ConnectionProfile.Name"/> this query belongs to.</summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>
    /// A '/'-separated path such as <c>fixes/otp</c>; empty means the top level. Always stored in
    /// the normal form <see cref="Services.SavedQueryStore.NormalizeFolder"/> gives.
    /// </summary>
    public string Folder { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    public string Statement { get; set; } = string.Empty;

    public QueryKind Kind { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The folder and the name together, as shown in a flat list: <c>fixes/otp/by key</c>.</summary>
    public string Path => Folder.Length == 0 ? Name : $"{Folder}/{Name}";

    /// <summary>The tags as one line for the list, e.g. "otp, fix".</summary>
    public string TagsText => string.Join(", ", Tags);

    public SavedQuery Clone() => new()
    {
        Id = Id,
        Connection = Connection,
        Folder = Folder,
        Name = Name,
        Tags = [.. Tags],
        Statement = Statement,
        Kind = Kind,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };

    public override string ToString() => Path;
}
