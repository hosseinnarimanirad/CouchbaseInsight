using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// Persists the user's saved connection profiles locally (per Windows user).
/// </summary>
public interface IConnectionStore
{
    /// <summary>Loads all saved profiles. Returns an empty list when nothing is stored yet.</summary>
    List<ConnectionProfile> Load();

    /// <summary>Persists the full set of profiles, replacing whatever was stored before.</summary>
    void Save(IEnumerable<ConnectionProfile> profiles);
}
