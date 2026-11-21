using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// Keeps saved queries in %APPDATA%\CouchbaseInsight\queries.json, all connections in one file.
/// The whole file is rewritten on every change: written to a temporary file first and then swapped
/// in, with the previous version kept as queries.json.bak, so a crash mid-write cannot lose the
/// list. A file that cannot be read is set aside rather than overwritten (see <see cref="LoadWarning"/>).
/// Exports use the same layout as the store file, so an export can also be read as a backup.
/// </summary>
public sealed class SavedQueryStore : ISavedQueryStore
{
    /// <summary>Marks a file as ours, so Import can refuse an unrelated JSON file with a clear message.</summary>
    private const string FormatName = "CouchbaseInsight.queries";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly string _filePath;
    private readonly object _gate = new();
    private List<SavedQuery> _queries;

    /// <summary>
    /// Set when the file exists but could not be opened (locked, no access). Its content may be
    /// fine, so saving over it with an empty list would destroy it: changes are refused instead.
    /// </summary>
    private readonly bool _readOnly;

    public string? LoadWarning { get; }

    public SavedQueryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CouchbaseInsight", "queries.json"))
    {
    }

    /// <summary>For tests, or a store kept somewhere else.</summary>
    public SavedQueryStore(string filePath)
    {
        _filePath = filePath;
        (_queries, LoadWarning, _readOnly) = LoadFile(filePath);
    }

    // ---- On-disk shape ----

    private sealed class FileShape
    {
        // No defaults: a file that lacks these must not pass as ours. Writers set them (Of).
        public string? Format { get; set; }
        public int Version { get; set; }
        public List<StoredQuery> Queries { get; set; } = [];

        public static FileShape Of(IEnumerable<SavedQuery> queries) => new()
        {
            Format = FormatName,
            Version = FormatVersion,
            Queries = queries.Select(StoredQuery.From).ToList()
        };
    }

    // Separate from the model so a computed or UI-only member added to SavedQuery never leaks
    // into the file, and the file layout changes only on purpose.
    private sealed class StoredQuery
    {
        public Guid Id { get; set; }
        public string Connection { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<string>? Tags { get; set; }
        public string Statement { get; set; } = string.Empty;
        public QueryKind Kind { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public static StoredQuery From(SavedQuery q) => new()
        {
            Id = q.Id,
            Connection = q.Connection,
            Folder = q.Folder,
            Name = q.Name,
            Tags = q.Tags.Count == 0 ? null : [.. q.Tags],
            Statement = q.Statement,
            Kind = q.Kind,
            CreatedAt = q.CreatedAt,
            UpdatedAt = q.UpdatedAt
        };

        public SavedQuery ToModel() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Connection = Connection.Trim(),
            Folder = NormalizeFolder(Folder),
            Name = Name.Trim(),
            Tags = NormalizeTags(Tags),
            Statement = Statement,
            Kind = Kind,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
    }

    // ---- Reading ----

    private static (List<SavedQuery> Queries, string? Warning, bool ReadOnly) LoadFile(string path)
    {
        if (!File.Exists(path))
            return ([], null, false);

        try
        {
            return (ReadShape(path).Select(s => s.ToModel()).ToList(), null, false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // Starting clean and saving would overwrite the user's queries for good. Move the
            // unreadable file out of the way instead, and say where it went.
            var aside = Path.Combine(
                Path.GetDirectoryName(path)!,
                $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Move(path, aside);
                return ([], $"The saved-queries file could not be read ({ex.Message}). It was kept as {aside}.", false);
            }
            catch (IOException)
            {
                return ([], $"The saved-queries file could not be read ({ex.Message}) and could not be set aside; it is left as {path}.", true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or not permitted: the content may be fine, so leave the file exactly where it is.
            return ([], $"The saved-queries file could not be opened ({ex.Message}); saving is off until the app restarts.", true);
        }
    }

    private static List<StoredQuery> ReadShape(string path)
    {
        var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("The file is empty.");

        if (!string.Equals(shape.Format, FormatName, StringComparison.Ordinal))
            throw new InvalidDataException(shape.Format is null
                    ? "Not a CouchbaseInsight query file."
                    : $"Not a CouchbaseInsight query file (format '{shape.Format}').");
        if (shape.Version > FormatVersion)
            throw new InvalidDataException(
                $"The file was written by a newer CouchbaseInsight (version {shape.Version}; this one reads up to {FormatVersion}).");

        return shape.Queries;
    }

    // ---- Queries ----

    public IReadOnlyList<SavedQuery> ForConnection(string connection)
    {
        lock (_gate)
        {
            return _queries
                .Where(q => NameComparer.Equals(q.Connection, connection))
                .OrderBy(q => q.Folder, NameComparer)
                .ThenBy(q => q.Name, NameComparer)
                .Select(q => q.Clone())
                .ToList();
        }
    }

    public SavedQuery Save(SavedQuery query)
    {
        var stored = query.Clone();
        stored.Connection = stored.Connection.Trim();
        stored.Folder = NormalizeFolder(stored.Folder);
        stored.Name = stored.Name.Trim();
        stored.Tags = NormalizeTags(stored.Tags);

        if (stored.Connection.Length == 0)
            throw new ArgumentException("A saved query needs a connection.", nameof(query));
        if (stored.Name.Length == 0)
            throw new ArgumentException("Give the query a name.", nameof(query));
        if (stored.Name.Contains('/') || stored.Name.Contains('\\'))
            throw new ArgumentException("A query name cannot contain / or \\ — those separate folders.", nameof(query));

        lock (_gate)
        {
            var clash = _queries.FirstOrDefault(q =>
                q.Id != stored.Id &&
                NameComparer.Equals(q.Connection, stored.Connection) &&
                NameComparer.Equals(q.Folder, stored.Folder) &&
                NameComparer.Equals(q.Name, stored.Name));
            if (clash is not null)
                throw new InvalidOperationException($"'{clash.Path}' already exists in this connection.");

            var next = _queries.ToList();
            var index = next.FindIndex(q => q.Id == stored.Id);
            stored.UpdatedAt = DateTimeOffset.UtcNow;
            if (index >= 0)
            {
                stored.CreatedAt = next[index].CreatedAt;
                next[index] = stored;
            }
            else
            {
                stored.CreatedAt = stored.UpdatedAt;
                next.Add(stored);
            }

            Commit(next);
            return stored.Clone();
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var next = _queries.Where(q => q.Id != id).ToList();
            if (next.Count == _queries.Count)
                return false;
            Commit(next);
            return true;
        }
    }

    public string UniqueName(string connection, string folder, string name, Guid? except = null)
    {
        lock (_gate)
            return UniqueNameIn(_queries, connection, NormalizeFolder(folder), name.Trim(), except);
    }

    private static string UniqueNameIn(
        IEnumerable<SavedQuery> queries, string connection, string folder, string name, Guid? except)
    {
        var taken = queries
            .Where(q => q.Id != except &&
                        NameComparer.Equals(q.Connection, connection) &&
                        NameComparer.Equals(q.Folder, folder))
            .Select(q => q.Name)
            .ToHashSet(NameComparer);

        if (!taken.Contains(name))
            return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    public int RenameConnection(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0)
            throw new ArgumentException("The new connection name is empty.", nameof(newName));

        lock (_gate)
        {
            var next = _queries.Select(q => q.Clone()).ToList();
            var moving = next.Where(q => NameComparer.Equals(q.Connection, oldName)).ToList();
            if (moving.Count == 0)
                return 0;

            // Renaming onto a profile that already has queries merges the two lists; a name taken
            // in the same folder gets a "(2)" rather than one query silently replacing the other.
            var staying = next.Except(moving).ToList();
            foreach (var q in moving)
            {
                q.Connection = newName;
                q.Name = UniqueNameIn(staying, newName, q.Folder, q.Name, q.Id);
                staying.Add(q);
            }

            Commit(next);
            return moving.Count;
        }
    }

    // ---- Export / import ----

    public void Export(string path, IEnumerable<SavedQuery> queries)
    {
        var shape = FileShape.Of(queries);
        WriteAtomically(path, JsonSerializer.Serialize(shape, JsonOptions), keepBackup: false);
    }

    public IReadOnlyList<SavedQuery> Import(string path, string connection)
    {
        connection = connection.Trim();
        if (connection.Length == 0)
            throw new ArgumentException("Pick a connection to import into.", nameof(connection));

        List<StoredQuery> incoming;
        try
        {
            incoming = ReadShape(path);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Not a readable query file: {ex.Message}", ex);
        }

        lock (_gate)
        {
            var next = _queries.ToList();
            var added = new List<SavedQuery>();
            var now = DateTimeOffset.UtcNow;

            foreach (var q in incoming.Select(s => s.ToModel()))
            {
                q.Id = Guid.NewGuid();
                q.Connection = connection;
                var name = q.Name.Replace('/', '-').Replace('\\', '-');
                q.Name = UniqueNameIn(next, connection, q.Folder, name.Length == 0 ? "Imported query" : name, null);
                if (q.CreatedAt == default) q.CreatedAt = now;
                q.UpdatedAt = now;
                next.Add(q);
                added.Add(q.Clone());
            }

            if (added.Count > 0)
                Commit(next);
            return added;
        }
    }

    // ---- Writing ----

    /// <summary>Writes the list, then makes it current; on failure memory is left as the file is.</summary>
    private void Commit(List<SavedQuery> next)
    {
        if (_readOnly)
            throw new InvalidOperationException(
                $"Saved queries cannot be changed this session: {LoadWarning}");

        var shape = FileShape.Of(next);
        WriteAtomically(_filePath, JsonSerializer.Serialize(shape, JsonOptions), keepBackup: true);
        _queries = next;
    }

    private static void WriteAtomically(string path, string json, bool keepBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(path))
            File.Replace(temp, path, keepBackup ? path + ".bak" : null);
        else
            File.Move(temp, path);
    }

    // ---- Normal forms ----

    /// <summary><c> fixes\\ otp/ </c> → <c>fixes/otp</c>: '/' separators, no blank or padded segments.</summary>
    public static string NormalizeFolder(string? folder) =>
        string.Join('/', (folder ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Trimmed, blanks dropped, duplicates (any letter case) removed, first spelling kept.</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
        .Select(t => t.Trim())
        .Where(t => t.Length > 0)
        .Distinct(NameComparer)
        .ToList();
}
