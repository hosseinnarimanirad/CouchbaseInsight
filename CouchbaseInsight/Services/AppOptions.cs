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
    public const int DefaultBatchSize = 100_000;
    public const int MinBatchSize = 10_000;

    /// <summary>Documents per batch: each Load, Analyze and Deep load query covers this many keys.</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>Gentle mode (default on): go easy on the cluster the live application uses. One batch
    /// at a time, a pause of <see cref="GentlePause"/> between batches, and batch queries with one
    /// fetch thread, small fetch groups and a short server-side timeout. Slower, safe on production.</summary>
    public bool GentleMode { get; set; } = true;

    public static readonly TimeSpan GentlePause = TimeSpan.FromSeconds(2);

    /// <summary>The reads at once actually used: one in gentle mode.</summary>
    public int EffectiveParallelReads => GentleMode ? 1 : ParallelReads;

    /// <summary>The pause actually used between batches.</summary>
    public TimeSpan EffectivePause => GentleMode ? GentlePause : TimeSpan.Zero;

    public const int DefaultParallelReads = 4;
    public const int MaxParallelReads = 16;

    /// <summary>Key ranges Load reads at the same time, each its own query. One query fetches
    /// documents one group at a time, so a few at once keep the cluster busier; each also loads it.</summary>
    public int ParallelReads { get; set; } = DefaultParallelReads;

    public const int DefaultScanLimitValue = 50_000;

    /// <summary>Whether Load starts with a scan limit on (first N documents) or off (whole collection).</summary>
    public bool ScanLimitEnabled { get; set; } = true;

    /// <summary>How many documents Load reads when the scan limit is on.</summary>
    public int ScanLimit { get; set; } = DefaultScanLimitValue;

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
                if (stored.ScanLimit <= 0) stored.ScanLimit = DefaultScanLimitValue;
                if (stored.ParallelReads is < 1 or > MaxParallelReads) stored.ParallelReads = DefaultParallelReads;
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
