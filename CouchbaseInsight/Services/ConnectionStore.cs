using System.IO;
using System.Text.Json;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// Persists connection profiles across two backends:
/// <list type="bullet">
///   <item>non-secret metadata (name, connection string, username, external flag) as JSON under
///   %APPDATA%\CouchbaseInsight\connections.json;</item>
///   <item>each password in the Windows Credential Vault via <see cref="ICredentialStore"/>,
///   keyed by <c>CouchbaseInsight:&lt;profileName&gt;</c>.</item>
/// </list>
/// The split is hidden behind the <see cref="IConnectionStore"/> API.
/// </summary>
public class ConnectionStore : IConnectionStore
{
    private const string CredentialPrefix = "CouchbaseInsight:";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ICredentialStore _credentials;
    private readonly string _filePath;

    public ConnectionStore(ICredentialStore credentials)
    {
        _credentials = credentials;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CouchbaseInsight");
        _filePath = Path.Combine(dir, "connections.json");
    }

    // On-disk shape: metadata only — no secret ever touches this file.
    private sealed class StoredProfile
    {
        public string Name { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public bool UseNetworkResolutionExternal { get; set; }
    }

    public List<ConnectionProfile> Load()
    {
        if (!File.Exists(_filePath))
            return new List<ConnectionProfile>();

        try
        {
            var json = File.ReadAllText(_filePath);
            var stored = JsonSerializer.Deserialize<List<StoredProfile>>(json, JsonOptions)
                         ?? new List<StoredProfile>();

            return stored.Select(s => new ConnectionProfile
            {
                Name = s.Name,
                ConnectionString = s.ConnectionString,
                Username = s.Username,
                Password = _credentials.ReadSecret(TargetFor(s.Name)) ?? string.Empty,
                UseNetworkResolutionExternal = s.UseNetworkResolutionExternal
            }).ToList();
        }
        catch
        {
            // Corrupt or unreadable file: start clean rather than crashing the app.
            return new List<ConnectionProfile>();
        }
    }

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var list = profiles.ToList();

        // 1. Write metadata JSON (no secrets).
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var stored = list.Select(p => new StoredProfile
        {
            Name = p.Name,
            ConnectionString = p.ConnectionString,
            Username = p.Username,
            UseNetworkResolutionExternal = p.UseNetworkResolutionExternal
        }).ToList();
        File.WriteAllText(_filePath, JsonSerializer.Serialize(stored, JsonOptions));

        // 2. Store each password in the vault.
        var desiredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
        {
            var target = TargetFor(p.Name);
            desiredTargets.Add(target);
            _credentials.Write(target, p.Username, p.Password);
        }

        // 3. Reconcile: remove vault entries for profiles that no longer exist (deletes/renames).
        foreach (var target in _credentials.Enumerate(CredentialPrefix))
        {
            if (!desiredTargets.Contains(target))
                _credentials.Delete(target);
        }
    }

    private static string TargetFor(string profileName) => CredentialPrefix + profileName;
}
