using System.IO;
using System.Text.Json;

namespace CouchbaseInsight.Services;

/// <summary>
/// App-wide settings edited in the Options dialog, kept in
/// <c>%APPDATA%\CouchbaseInsight\options.json</c>. A file of its own rather than <c>ui.json</c>,
/// which the theme switch rewrites whole.
/// </summary>
public sealed class AppOptions
{
    public const int DefaultBatchSize = 1_000_000;
    public const int MinBatchSize = 10_000;

    /// <summary>Documents per batch when a whole collection is read: Load classes and Analyze
    /// with no scan limit, and Export collection.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CouchbaseInsight", "options.json");

    public static AppOptions Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(FilePath)) is { } stored)
            {
                if (stored.BatchSize < MinBatchSize) stored.BatchSize = DefaultBatchSize;
                return stored;
            }
        }
        catch (Exception)
        {
            // Missing or unreadable: defaults.
        }
        return new AppOptions();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
