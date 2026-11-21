using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// The user's saved SQL++ statements, kept locally (per Windows user) and grouped by the
/// connection profile they were written for. Every change is written to disk at once.
/// </summary>
public interface ISavedQueryStore
{
    /// <summary>
    /// Set when the file on disk could not be read at startup. The unreadable file is set aside
    /// (never overwritten) and its new name is given here, so the user can be told where it went.
    /// </summary>
    string? LoadWarning { get; }

    /// <summary>The queries of one connection, ordered by folder, then name. Copies: edit and pass to <see cref="Save"/>.</summary>
    IReadOnlyList<SavedQuery> ForConnection(string connection);

    /// <summary>
    /// Adds the query, or replaces the stored one with the same <see cref="SavedQuery.Id"/>.
    /// Normalizes the folder, name and tags and sets <see cref="SavedQuery.UpdatedAt"/>.
    /// Throws <see cref="ArgumentException"/> when the name is empty, and
    /// <see cref="InvalidOperationException"/> when another query of the same connection already
    /// has that name in that folder (letter case ignored). Returns the stored copy.
    /// </summary>
    SavedQuery Save(SavedQuery query);

    /// <summary>Removes one query. Returns false when there was none with that id.</summary>
    bool Delete(Guid id);

    /// <summary>A name not yet used in that folder: <paramref name="name"/>, else "name (2)", "name (3)", ….</summary>
    string UniqueName(string connection, string folder, string name, Guid? except = null);

    /// <summary>Removes every query of one connection, when the profile itself is deleted. Returns how many went.</summary>
    int DeleteConnection(string connection);

    /// <summary>Moves every query of one connection to another, after the profile was renamed. Returns how many moved.</summary>
    int RenameConnection(string oldName, string newName);

    /// <summary>Writes the given queries to a file of the user's choosing.</summary>
    void Export(string path, IEnumerable<SavedQuery> queries);

    /// <summary>
    /// Reads an exported file and adds its queries to <paramref name="connection"/>, each with a
    /// new id; a name already taken in its folder becomes "name (2)". Returns what was added.
    /// Throws <see cref="InvalidDataException"/> when the file is not a query export.
    /// </summary>
    IReadOnlyList<SavedQuery> Import(string path, string connection);
}
