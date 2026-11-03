using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchbaseInsight.Services;

/// <summary>
/// The running summary of a whole-collection export. It is the checkpoint as well: after each
/// batch it is written to disk as is, so an export that is cancelled, loses its connection or has
/// the app closed under it resumes from the last finished batch rather than from the beginning.
/// Holds counts only, never documents, so its size depends on the number of classes and fields,
/// not on the size of the collection.
/// </summary>
public sealed class ExportState
{
    /// <summary>Distinct scalar values tracked per field per class. Past this, values not already
    /// tracked are no longer added (the tracked ones keep counting), so memory stays bounded on
    /// id-like fields that have one value per document.</summary>
    public const int DistinctTracked = 200;

    /// <summary>Distinct values written per field: all of them up to this many, else the most frequent.</summary>
    public const int DistinctShown = 50;

    /// <summary>Strings longer than this are cut before being tracked as a distinct value.</summary>
    public const int ValueMaxLength = 200;

    public const string NoClass = "(no class)";

    public int Version { get; set; } = 1;
    public string Bucket { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Discriminator { get; set; } = "";
    public string DateField { get; set; } = "";
    public int BatchSize { get; set; }
    public string OutputPath { get; set; } = "";

    /// <summary>Key of the last document of the last finished batch; the next batch reads after it.</summary>
    public string? LastKey { get; set; }

    public long DocumentsRead { get; set; }
    public int BatchesDone { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Dictionary<string, ClassAccumulator> Classes { get; set; } = new(StringComparer.Ordinal);

    public sealed class ClassAccumulator
    {
        public long Count { get; set; }
        public Dictionary<string, FieldAccumulator> Fields { get; set; } = new(StringComparer.Ordinal);
        public double? DateMinNumber { get; set; }
        public double? DateMaxNumber { get; set; }
        public string? DateMinText { get; set; }
        public string? DateMaxText { get; set; }
        public long DateMissing { get; set; }
    }

    public sealed class FieldAccumulator
    {
        public long Seen { get; set; }
        public HashSet<string> Types { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Scalar value as JSON text → number of occurrences.</summary>
        public Dictionary<string, long> Distinct { get; set; } = new(StringComparer.Ordinal);

        /// <summary>True once a value arrived that could not be tracked: the field has more than
        /// <see cref="DistinctTracked"/> distinct values.</summary>
        public bool DistinctOverflow { get; set; }

        public bool HasScalar { get; set; }
    }

    // ---- Accumulating ----

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string[]? _datePath;

    public void Add(JsonElement document)
    {
        DocumentsRead++;

        var className = NoClass;
        if (document.ValueKind == JsonValueKind.Object &&
            document.TryGetProperty(Discriminator, out var cls) &&
            cls.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            className = cls.ValueKind == JsonValueKind.String ? cls.GetString() ?? "" : cls.GetRawText();

        if (!Classes.TryGetValue(className, out var acc))
            Classes[className] = acc = new ClassAccumulator();
        acc.Count++;

        if (document.ValueKind != JsonValueKind.Object)
        {
            acc.DateMissing++;
            return;
        }

        _seen.Clear();
        Walk(document, "", acc);
        foreach (var path in _seen) acc.Fields[path].Seen++;

        AddDate(document, acc);
    }

    private void Walk(JsonElement element, string prefix, ClassAccumulator acc)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            if (!acc.Fields.TryGetValue(path, out var field))
                acc.Fields[path] = field = new FieldAccumulator();

            field.Types.Add(SchemaAnalyzer.DescribeType(prop.Value));
            _seen.Add(path);

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    Walk(prop.Value, path, acc);
                    break;
                case JsonValueKind.Array:
                    break;
                default:
                    field.HasScalar = true;
                    Track(field, DistinctKey(prop.Value));
                    break;
            }
        }
    }

    private static void Track(FieldAccumulator field, string key)
    {
        if (field.Distinct.TryGetValue(key, out var n))
            field.Distinct[key] = n + 1;
        else if (field.Distinct.Count < DistinctTracked)
            field.Distinct[key] = 1;
        else
            field.DistinctOverflow = true;
    }

    /// <summary>The value as JSON text, so a string "1" and a number 1 stay apart and the report
    /// can write each back with its own type.</summary>
    private static string DistinctKey(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            if (s.Length > ValueMaxLength) s = s[..ValueMaxLength] + "…";
            return JsonSerializer.Serialize(s);
        }
        return value.GetRawText();
    }

    private void AddDate(JsonElement document, ClassAccumulator acc)
    {
        if (string.IsNullOrWhiteSpace(DateField)) return;
        _datePath ??= DateField.Split('.');

        var value = document;
        foreach (var part in _datePath)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                acc.DateMissing++;
                return;
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                var d = value.GetDouble();
                if (acc.DateMinNumber is null || d < acc.DateMinNumber) acc.DateMinNumber = d;
                if (acc.DateMaxNumber is null || d > acc.DateMaxNumber) acc.DateMaxNumber = d;
                break;
            case JsonValueKind.String:
                var s = value.GetString() ?? "";
                if (acc.DateMinText is null || string.CompareOrdinal(s, acc.DateMinText) < 0) acc.DateMinText = s;
                if (acc.DateMaxText is null || string.CompareOrdinal(s, acc.DateMaxText) > 0) acc.DateMaxText = s;
                break;
            default:
                acc.DateMissing++;
                break;
        }
    }

    // ---- Checkpoint ----

    private static readonly JsonSerializerOptions CheckpointJson = new() { WriteIndented = false };

    public static string CheckpointPath(string bucket, string scope, string collection, string discriminator)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CouchbaseInsight", "exports");
        Directory.CreateDirectory(dir);
        var name = string.Join("_", $"{bucket}.{scope}.{collection}.{discriminator}"
            .Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, name + ".checkpoint.json");
    }

    public static ExportState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ExportState>(File.ReadAllText(path), CheckpointJson);
        }
        catch
        {
            return null;   // a checkpoint from another version, or half-written: start over
        }
    }

    /// <summary>Writes to a temporary file and swaps it in, so a crash mid-write leaves the previous
    /// checkpoint intact instead of a truncated one.</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, CheckpointJson));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- Report ----

    public JsonObject BuildReport(long collectionCount)
    {
        var classes = new JsonArray();
        foreach (var (name, acc) in Classes.OrderByDescending(c => c.Value.Count)
                                           .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            classes.Add(ClassReport(name, acc));

        return new JsonObject
        {
            ["bucket"] = Bucket,
            ["scope"] = Scope,
            ["collection"] = Collection,
            ["discriminator"] = Discriminator,
            ["dateField"] = DateField,
            ["startedAt"] = StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["finishedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["documentsRead"] = DocumentsRead,
            ["collectionCountAtEnd"] = collectionCount,
            ["classCount"] = Classes.Count,
            ["notes"] = new JsonObject
            {
                ["percent"] = "Share of all documents read, including those without a class (listed as \"(no class)\").",
                ["presencePercent"] = "Share of the class's documents in which the field appears (seen / class count).",
                ["requirement"] = "Required when the field appears in every document of the class.",
                ["distinct"] = $"Scalar values only. complete=true: values lists every distinct value. " +
                               $"complete=false: values lists the {DistinctShown} most frequent; count is exact up to " +
                               $"{DistinctTracked}, beyond that it is null and moreThan={DistinctTracked}, and values are the " +
                               $"most frequent among the first {DistinctTracked} distinct values met (their counts are exact). " +
                               $"Strings longer than {ValueMaxLength} characters are cut.",
                ["dateRange"] = "Min/max of the date field per class. Numbers are shown raw and as UTC (unit guessed " +
                                "from size: s, ms, µs or ns); text is compared as text.",
                ["documentsRead"] = "Documents that changed while the export ran may be counted in their old or new shape; " +
                                    "documents added behind the read position are not counted."
            },
            ["classes"] = classes
        };
    }

    private JsonObject ClassReport(string name, ClassAccumulator acc)
    {
        var caseDups = acc.Fields.Keys
            .GroupBy(k => k.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet(StringComparer.Ordinal);

        var fields = new JsonArray();
        int required = 0, flagged = 0;
        foreach (var (path, f) in acc.Fields
                     .OrderByDescending(kv => kv.Value.Seen == acc.Count)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var isRequired = f.Seen == acc.Count;
            var flags = new JsonArray();
            if (caseDups.Contains(path)) flags.Add("Case conflict");
            if (f.Types.Count > 1) flags.Add("Multi-type");
            if (isRequired) required++;
            if (flags.Count > 0) flagged++;

            fields.Add(new JsonObject
            {
                ["name"] = path,
                ["types"] = new JsonArray(f.Types.Order(StringComparer.Ordinal).Select(t => (JsonNode?)t).ToArray()),
                ["seen"] = f.Seen,
                ["presencePercent"] = Math.Round(acc.Count == 0 ? 0 : f.Seen * 100.0 / acc.Count, 2),
                ["requirement"] = isRequired ? "Required" : "Optional",
                ["flags"] = flags,
                ["distinct"] = f.HasScalar ? DistinctReport(f) : null
            });
        }

        return new JsonObject
        {
            ["class"] = name,
            ["count"] = acc.Count,
            ["percent"] = Math.Round(DocumentsRead == 0 ? 0 : acc.Count * 100.0 / DocumentsRead, 2),
            ["dateRange"] = DateReport(acc),
            ["fieldCount"] = acc.Fields.Count,
            ["requiredCount"] = required,
            ["flaggedCount"] = flagged,
            ["fields"] = fields
        };
    }

    private static JsonObject DistinctReport(FieldAccumulator f)
    {
        var complete = !f.DistinctOverflow && f.Distinct.Count <= DistinctShown;
        var values = new JsonArray();
        foreach (var (key, count) in f.Distinct.OrderByDescending(kv => kv.Value)
                                               .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                               .Take(DistinctShown))
            values.Add(new JsonObject { ["value"] = JsonNode.Parse(key), ["count"] = count });

        return new JsonObject
        {
            ["complete"] = complete,
            ["count"] = f.DistinctOverflow ? null : f.Distinct.Count,
            ["moreThan"] = f.DistinctOverflow ? DistinctTracked : null,
            ["values"] = values
        };
    }

    private static JsonObject DateReport(ClassAccumulator acc)
    {
        var report = new JsonObject();
        if (acc.DateMinNumber is { } min && acc.DateMaxNumber is { } max)
        {
            report["min"] = min;
            report["max"] = max;
            report["minUtc"] = EpochToUtc(min);
            report["maxUtc"] = EpochToUtc(max);
        }
        if (acc.DateMinText is not null)
        {
            report[acc.DateMinNumber is null ? "min" : "minText"] = acc.DateMinText;
            report[acc.DateMinNumber is null ? "max" : "maxText"] = acc.DateMaxText;
        }
        report["missing"] = acc.DateMissing;
        return report;
    }

    internal static string? EpochToUtc(double value)
    {
        var ms = value switch
        {
            < 1e11 => value * 1000,      // seconds
            < 1e14 => value,             // milliseconds
            < 1e17 => value / 1000,      // microseconds
            _ => value / 1_000_000       // nanoseconds
        };
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
