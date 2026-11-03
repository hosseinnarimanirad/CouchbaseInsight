using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// The running summary Load builds from the documents it reads, and the checkpoint as well: after
/// each page it is written to disk as is, so a load that is cancelled, loses its connection or has
/// the app closed under it resumes from the last finished page rather than from the beginning.
///
/// Everything is gathered in the one pass over the documents: per class the count, TTL count,
/// key patterns, last write, first/last date and an oldest and a newest sample document; per field
/// the types, presence, an example, its most frequent values with counts, and the first and last
/// date it was seen. Its size depends on the number of classes and fields, not on the collection.
/// </summary>
public sealed class ExportState
{
    /// <summary>Checkpoints of another version are not resumed: they lack what this one gathers.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Distinct scalar values tracked per field per class. Past this, values not already
    /// tracked are no longer added (the tracked ones keep counting), so memory stays bounded on
    /// id-like fields that have one value per document.</summary>
    public const int DistinctTracked = 200;

    /// <summary>Distinct values written per field: all of them up to this many, else the most frequent.</summary>
    public const int DistinctShown = 50;

    /// <summary>Strings longer than this are cut before being tracked as a distinct value.</summary>
    public const int ValueMaxLength = 200;

    /// <summary>Key patterns kept per class; keys of any further pattern are counted as "(other)".</summary>
    public const int KeyPatternsTracked = 50;

    /// <summary>Elements walked per array, so a document with a huge array does not dominate the read.</summary>
    public const int ArrayElementsWalked = 100;

    public const string NoClass = "(no class)";

    public int Version { get; set; } = CurrentVersion;
    public string Bucket { get; set; } = "";
    public string Scope { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Discriminator { get; set; } = "";
    public string DateField { get; set; } = "";
    public int BatchSize { get; set; }

    /// <summary>Key ranges each page is split into and read at the same time.</summary>
    public int ParallelReads { get; set; } = 1;

    /// <summary>Gentle mode: wait this long after each page before reading the next.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan PauseBetweenPages { get; set; }

    /// <summary>Read only the first this-many documents by key (the scan limit), or null for all.</summary>
    public long? MaxDocuments { get; set; }

    /// <summary>Key of the last document of the last finished page; the next page reads after it.</summary>
    public string? LastKey { get; set; }

    public long DocumentsRead { get; set; }
    public int BatchesDone { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Dictionary<string, ClassAccumulator> Classes { get; set; } = new(StringComparer.Ordinal);

    // ---- Timing of this run (not saved) ----
    // Where a slow load spends its time: waiting for a page's first row (the server preparing the
    // page — long if it has to sort), streaming rows (server fetch + network), or the app itself
    // walking the documents.

    /// <summary>How long the current or last page took to deliver its first row.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan LastFirstRowWait { get; private set; }

    /// <summary>Time spent in this app summarising documents, this run.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan ProcessingTime { get; private set; }

    /// <summary>JSON text of the documents received this run, in characters (≈ bytes on the wire).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public long BytesRead { get; private set; }

    /// <summary>Key ranges streaming rows right now.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int RangesActive { get; private set; }

    /// <summary>Wall time of this run's reading so far.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan ReadingTime => _runClock.Elapsed;

    private readonly System.Diagnostics.Stopwatch _runClock = new();

    // ---- Exact counts (optional second step, server-side) ----

    /// <summary>Documents per class over the whole collection, counted on the server; null until counted.</summary>
    public Dictionary<string, long>? ExactCounts { get; set; }
    public long ExactWithoutClass { get; set; }
    public long ExactTotal { get; set; }
    public DateTimeOffset? ExactCountedAt { get; set; }

    public sealed class ClassAccumulator
    {
        public long Count { get; set; }
        public Dictionary<string, FieldAccumulator> Fields { get; set; } = new(StringComparer.Ordinal);

        public double? DateMinNumber { get; set; }
        public double? DateMaxNumber { get; set; }
        public string? DateMinText { get; set; }
        public string? DateMaxText { get; set; }
        public long DateMissing { get; set; }

        /// <summary>Documents with an expiry set (META().expiration &gt; 0).</summary>
        public long TtlCount { get; set; }

        /// <summary>Key shape ("user::{uuid}") → documents.</summary>
        public Dictionary<string, long> KeyPatterns { get; set; } = new(StringComparer.Ordinal);
        public long KeyPatternsOther { get; set; }

        /// <summary>Highest CAS seen: when a document of the class was last written.</summary>
        public ulong LastWriteCas { get; set; }

        public SampleDocument? Oldest { get; set; }
        public SampleDocument? Newest { get; set; }
    }

    public sealed class FieldAccumulator
    {
        /// <summary>Documents the field appears in (once per document, however often inside it).</summary>
        public long Seen { get; set; }
        public HashSet<string> Types { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Scalar value as JSON text → number of occurrences.</summary>
        public Dictionary<string, long> Distinct { get; set; } = new(StringComparer.Ordinal);

        /// <summary>True once a value arrived that could not be tracked: the field has more than
        /// <see cref="DistinctTracked"/> distinct values.</summary>
        public bool DistinctOverflow { get; set; }

        public bool HasScalar { get; set; }

        /// <summary>Scalar values seen in all (array elements count one each).</summary>
        public long ScalarCount { get; set; }

        /// <summary>The first scalar value met, as JSON text.</summary>
        public string? Example { get; set; }

        /// <summary>Earliest and latest date (epoch ms, from the date field) of a document holding it.</summary>
        public double? FirstSeenMs { get; set; }
        public double? LastSeenMs { get; set; }
    }

    /// <summary>A whole document kept as an example of the class.</summary>
    public sealed class SampleDocument
    {
        public string Key { get; set; } = "";
        public ulong Cas { get; set; }
        public long Expiration { get; set; }

        /// <summary>Its date (epoch ms) when it has one; null when picked by key order instead.</summary>
        public double? DateMs { get; set; }
        public string Json { get; set; } = "";
    }

    // ---- Accumulating ----

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private string[]? _datePath;

    public void Add(DocumentRow row) => Add(row.Key, row.Document, row.Expiration, row.Cas);

    public void Add(string key, JsonElement document, long expiration = 0, ulong cas = 0)
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

        if (expiration > 0) acc.TtlCount++;
        if (cas > acc.LastWriteCas) acc.LastWriteCas = cas;
        AddKeyPattern(acc, key);

        if (document.ValueKind != JsonValueKind.Object)
        {
            acc.DateMissing++;
            return;
        }

        _seen.Clear();
        foreach (var prop in document.EnumerateObject())
            WalkValue(prop.Value, prop.Name, acc);

        var dateMs = AddDate(document, acc);
        foreach (var path in _seen)
        {
            var field = acc.Fields[path];
            field.Seen++;
            if (dateMs is { } ms)
            {
                if (field.FirstSeenMs is null || ms < field.FirstSeenMs) field.FirstSeenMs = ms;
                if (field.LastSeenMs is null || ms > field.LastSeenMs) field.LastSeenMs = ms;
            }
        }

        KeepSamples(acc, key, document, expiration, cas, dateMs);
    }

    /// <summary>Registers a value at a path, then walks into it: object members as "path.member",
    /// array elements as "path[]" (so "items[].amount" is a field of its own).</summary>
    private void WalkValue(JsonElement value, string path, ClassAccumulator acc)
    {
        if (!acc.Fields.TryGetValue(path, out var field))
            acc.Fields[path] = field = new FieldAccumulator();

        field.Types.Add(SchemaAnalyzer.DescribeType(value));
        _seen.Add(path);

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                    WalkValue(prop.Value, $"{path}.{prop.Name}", acc);
                break;

            case JsonValueKind.Array:
                var walked = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (++walked > ArrayElementsWalked) break;
                    WalkValue(item, path + "[]", acc);
                }
                break;

            default:
                field.HasScalar = true;
                field.ScalarCount++;
                var key = DistinctKey(value);
                field.Example ??= key;
                Track(field, key);
                break;
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

    private static void AddKeyPattern(ClassAccumulator acc, string key)
    {
        var pattern = KeyPattern(key);
        if (acc.KeyPatterns.TryGetValue(pattern, out var n))
            acc.KeyPatterns[pattern] = n + 1;
        else if (acc.KeyPatterns.Count < KeyPatternsTracked)
            acc.KeyPatterns[pattern] = 1;
        else
            acc.KeyPatternsOther++;
    }

    private static readonly Regex UuidPart = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex HexPart = new("(?<![0-9A-Za-z])(?=[0-9a-fA-F]*[0-9])[0-9a-fA-F]{16,}(?![0-9A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex DigitsPart = new("[0-9]{2,}", RegexOptions.Compiled);

    /// <summary>The key's shape: "user::4b1e…-…" → "user::{uuid}", "otp::989121234567" → "otp::{n}".</summary>
    public static string KeyPattern(string key)
    {
        var p = UuidPart.Replace(key, "{uuid}");
        p = HexPart.Replace(p, "{hex}");
        return DigitsPart.Replace(p, "{n}");
    }

    /// <summary>Folds the document's date into the class's range; returns it as epoch ms when it
    /// can be read as a date (an epoch number, or text a date parser accepts).</summary>
    private double? AddDate(JsonElement document, ClassAccumulator acc)
    {
        if (string.IsNullOrWhiteSpace(DateField)) return null;
        _datePath ??= DateField.Split('.');

        var value = document;
        foreach (var part in _datePath)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                acc.DateMissing++;
                return null;
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                var d = value.GetDouble();
                if (acc.DateMinNumber is null || d < acc.DateMinNumber) acc.DateMinNumber = d;
                if (acc.DateMaxNumber is null || d > acc.DateMaxNumber) acc.DateMaxNumber = d;
                return EpochToMs(d);

            case JsonValueKind.String:
                var s = value.GetString() ?? "";
                if (acc.DateMinText is null || string.CompareOrdinal(s, acc.DateMinText) < 0) acc.DateMinText = s;
                if (acc.DateMaxText is null || string.CompareOrdinal(s, acc.DateMaxText) > 0) acc.DateMaxText = s;
                return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                    ? parsed.ToUnixTimeMilliseconds()
                    : null;

            default:
                acc.DateMissing++;
                return null;
        }
    }

    /// <summary>Keeps the oldest and the newest document by date; for documents without a usable
    /// date, the first and the last by key.</summary>
    private static void KeepSamples(
        ClassAccumulator acc, string key, JsonElement document, long expiration, ulong cas, double? dateMs)
    {
        SampleDocument Make() => new()
        {
            Key = key, Cas = cas, Expiration = expiration, DateMs = dateMs, Json = document.GetRawText()
        };

        if (dateMs is { } ms)
        {
            if (acc.Oldest?.DateMs is not { } oldMs || ms < oldMs) acc.Oldest = Make();
            if (acc.Newest?.DateMs is not { } newMs || ms >= newMs) acc.Newest = Make();
        }
        else
        {
            // No usable date: the lowest and the highest key (rows arrive in no particular order).
            if (acc.Oldest is null || (acc.Oldest.DateMs is null && string.CompareOrdinal(key, acc.Oldest.Key) < 0))
                acc.Oldest = Make();
            if (acc.Newest is null || (acc.Newest.DateMs is null && string.CompareOrdinal(key, acc.Newest.Key) > 0))
                acc.Newest = Make();
        }
    }

    // ---- Reading ----

    /// <summary>
    /// Reads the documents after <see cref="LastKey"/> page by page. Each page of
    /// <see cref="BatchSize"/> documents is cut into <see cref="ParallelReads"/> key ranges by
    /// keys-only queries (the n-th key after the last one, from the primary index), and the ranges
    /// are read at the same time: (after, end₁], (end₁, end₂], … Ranges meet without overlapping, so
    /// no document is read twice or skipped. After each page the summary is saved; a page cut off
    /// by Cancel or an error is not, so the checkpoint always describes exactly the documents up to
    /// <see cref="LastKey"/>.
    /// </summary>
    public async Task ReadAllAsync(
        ICouchbaseService couchbase, string checkpointPath, Action onProgress, CancellationToken ct)
    {
        _runClock.Restart();
        ProcessingTime = TimeSpan.Zero;
        BytesRead = 0;
        var lastReport = System.Diagnostics.Stopwatch.StartNew();
        var sync = new object();
        var parallel = Math.Max(1, ParallelReads);

        while (true)
        {
            // A page never runs past the scan limit: the last one is shortened to land on it.
            var pageSize = MaxDocuments is { } max ? (int)Math.Min(BatchSize, max - DocumentsRead) : BatchSize;
            if (pageSize <= 0) break;

            // Cut the page into ranges. A null end means fewer keys remain than asked for: that
            // range runs to the end of the collection, and this page is the last.
            var ranges = new List<(string After, string? End)>();
            var after = LastKey ?? "";
            var chunk = (int)Math.Ceiling(pageSize / (double)parallel);
            var planned = 0;
            var reachedEnd = false;
            while (planned < pageSize)
            {
                var size = Math.Min(chunk, pageSize - planned);
                var end = await couchbase.GetBatchEndKeyAsync(Bucket, Scope, Collection, after, size, ct);
                ranges.Add((after, end));
                planned += size;
                if (end is null) { reachedEnd = true; break; }
                after = end;
            }

            var inPage = 0;
            string? highestKey = null;
            var pageClock = System.Diagnostics.Stopwatch.StartNew();
            var firstRow = true;

            await Task.WhenAll(ranges.Select(range => Task.Run(async () =>
            {
                lock (sync) RangesActive++;
                try
                {
                await foreach (var row in couchbase.ReadRangeAsync(Bucket, Scope, Collection, range.After, range.End, ct))
                {
                    lock (sync)
                    {
                        if (firstRow) { LastFirstRowWait = pageClock.Elapsed; firstRow = false; }
                        if (row.Document.ValueKind != JsonValueKind.Undefined) BytesRead += row.Document.GetRawText().Length;

                        var started = System.Diagnostics.Stopwatch.GetTimestamp();
                        Add(row);
                        ProcessingTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);

                        inPage++;
                        if (highestKey is null || string.CompareOrdinal(row.Key, highestKey) > 0) highestKey = row.Key;

                        // Refresh by time, not by count: smooth whatever the speed, and cheap.
                        if (lastReport.ElapsedMilliseconds >= 250)
                        {
                            onProgress();
                            lastReport.Restart();
                        }
                    }
                }
                }
                finally
                {
                    lock (sync) RangesActive--;
                }
            }, ct)));

            ct.ThrowIfCancellationRequested();
            if (inPage == 0) break;   // nothing after LastKey: the previous page was the last

            // The next page starts after this page's last range: its end key, or, when the last
            // range ran to the end of the collection, the highest key it held.
            LastKey = reachedEnd ? highestKey : ranges[^1].End;
            BatchesDone++;
            UpdatedAt = DateTimeOffset.UtcNow;
            Save(checkpointPath);
            onProgress();

            if (reachedEnd) break;
            if (PauseBetweenPages > TimeSpan.Zero) await Task.Delay(PauseBetweenPages, ct);
        }
    }

    // ---- Checkpoint ----

    private static readonly JsonSerializerOptions CheckpointJson = new() { WriteIndented = false };

    public static string CheckpointPath(
        string bucket, string scope, string collection, string discriminator, long? maxDocuments)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CouchbaseInsight", "exports");
        Directory.CreateDirectory(dir);
        var coverage = maxDocuments is null ? "all" : $"first{maxDocuments}";
        var name = string.Join("_", $"{bucket}.{scope}.{collection}.{discriminator}.{coverage}"
            .Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(dir, name + ".checkpoint.json");
    }

    public static ExportState? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ExportState>(File.ReadAllText(path), CheckpointJson);
            return state?.Version == CurrentVersion ? state : null;
        }
        catch
        {
            return null;   // half-written or unreadable: start over
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
        var names = Classes.Keys.ToHashSet(StringComparer.Ordinal);
        if (ExactCounts is not null) names.UnionWith(ExactCounts.Keys);

        long Order(string n) => ExactCounts?.GetValueOrDefault(n) ?? Classes.GetValueOrDefault(n)?.Count ?? 0;

        var classes = new JsonArray();
        foreach (var name in names.OrderByDescending(Order).ThenBy(n => n, StringComparer.OrdinalIgnoreCase))
            classes.Add(ClassReport(name, Classes.GetValueOrDefault(name)));

        return new JsonObject
        {
            ["bucket"] = Bucket,
            ["scope"] = Scope,
            ["collection"] = Collection,
            ["discriminator"] = Discriminator,
            ["dateField"] = DateField,
            ["startedAt"] = Iso(StartedAt),
            ["finishedAt"] = Iso(DateTimeOffset.UtcNow),
            ["scanLimit"] = MaxDocuments,
            ["documentsRead"] = DocumentsRead,
            ["collectionCountAtEnd"] = collectionCount,
            ["classCount"] = names.Count,
            ["exactCounts"] = ExactCounts is null ? null : new JsonObject
            {
                ["total"] = ExactTotal,
                ["withoutClass"] = ExactWithoutClass,
                ["countedAt"] = ExactCountedAt is { } at ? Iso(at) : null
            },
            ["notes"] = new JsonObject
            {
                ["sample"] = "Everything except exactCount comes from the documents read: the first scanLimit " +
                             "documents by key (all of them when scanLimit is null). Keys are random UUIDs in most " +
                             "collections, so the first N by key is a random sample in time.",
                ["percent"] = "Share of the documents read, including those without a class (\"(no class)\"). " +
                              "exactPercent is the share of the whole collection, from a server-side count.",
                ["presencePercent"] = "Share of the class's documents in which the field appears (seen / count). " +
                                      "missing = count - seen.",
                ["parallelReads"] = $"Pages of {BatchSize} documents were read as {ParallelReads} key range(s) at once.",
                ["arrays"] = $"A field inside an array is listed as \"path[].member\"; its presence counts the " +
                             $"documents where any element has it. Up to {ArrayElementsWalked} elements per array are read.",
                ["distinct"] = $"Scalar values only. complete=true: values lists every distinct value. " +
                               $"complete=false: values lists the {DistinctShown} most frequent; count is exact up to " +
                               $"{DistinctTracked}, beyond that it is null and moreThan={DistinctTracked}. otherCount is " +
                               $"the occurrences not in values. Strings longer than {ValueMaxLength} characters are cut.",
                ["firstSeen"] = "firstSeenUtc / lastSeenUtc: the earliest and latest date-field value of a document " +
                                "of the class holding the field — when the field appeared and when it was last written.",
                ["dateRange"] = "Min/max of the date field per class. Numbers are shown raw and as UTC (unit guessed " +
                                "from size: s, ms, µs or ns); text is compared as text.",
                ["ttl"] = "ttlCount: documents with an expiry set (META().expiration > 0).",
                ["keyPatterns"] = "Key shapes: UUIDs → {uuid}, 16+ hex characters → {hex}, 2+ digits → {n}.",
                ["samples"] = "oldest/newest: whole documents with the lowest and highest date; by \"key\" when the " +
                              "class has no usable date. Values are not masked.",
                ["documentsRead"] = "Documents that changed while the read ran may be counted in their old or new " +
                                    "shape; documents added behind the read position are not counted."
            },
            ["classes"] = classes
        };
    }

    private JsonObject ClassReport(string name, ClassAccumulator? acc)
    {
        var exact = ExactCounts?.GetValueOrDefault(name);
        var report = new JsonObject
        {
            ["class"] = name,
            ["count"] = acc?.Count ?? 0,
            ["percent"] = Math.Round(DocumentsRead == 0 || acc is null ? 0 : acc.Count * 100.0 / DocumentsRead, 2),
            ["exactCount"] = exact,
            ["exactPercent"] = exact is { } e && ExactTotal > 0 ? Math.Round(e * 100.0 / ExactTotal, 4) : null,
            ["inSample"] = acc is not null
        };
        if (acc is null) return report;   // found only by the exact count: nothing read to describe

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
                ["missing"] = acc.Count - f.Seen,
                ["presencePercent"] = Math.Round(acc.Count == 0 ? 0 : f.Seen * 100.0 / acc.Count, 2),
                ["requirement"] = isRequired ? "Required" : "Optional",
                ["flags"] = flags,
                ["example"] = f.Example is null ? null : JsonNode.Parse(f.Example),
                ["firstSeenUtc"] = MsToUtc(f.FirstSeenMs),
                ["lastSeenUtc"] = MsToUtc(f.LastSeenMs),
                ["distinct"] = f.HasScalar ? DistinctReport(f) : null
            });
        }

        report["dateRange"] = DateReport(acc);
        report["ttlCount"] = acc.TtlCount;
        report["ttlPercent"] = Math.Round(acc.Count == 0 ? 0 : acc.TtlCount * 100.0 / acc.Count, 2);
        report["lastWriteUtc"] = acc.LastWriteCas == 0 ? null : EpochToUtc(acc.LastWriteCas);
        report["keyPatterns"] = KeyPatternReport(acc);
        report["samples"] = new JsonObject
        {
            ["oldest"] = SampleReport(acc.Oldest),
            ["newest"] = SampleReport(acc.Newest)
        };
        report["shapeChange"] = ShapeChange(acc.Oldest, acc.Newest);
        report["fieldCount"] = acc.Fields.Count;
        report["requiredCount"] = required;
        report["flaggedCount"] = flagged;
        report["fields"] = fields;
        return report;
    }

    private static JsonObject DistinctReport(FieldAccumulator f)
    {
        var complete = !f.DistinctOverflow && f.Distinct.Count <= DistinctShown;
        var shown = f.Distinct.OrderByDescending(kv => kv.Value)
                              .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                              .Take(DistinctShown)
                              .ToList();
        var values = new JsonArray();
        foreach (var (key, count) in shown)
            values.Add(new JsonObject { ["value"] = JsonNode.Parse(key), ["count"] = count });

        return new JsonObject
        {
            ["complete"] = complete,
            ["count"] = f.DistinctOverflow ? null : f.Distinct.Count,
            ["moreThan"] = f.DistinctOverflow ? DistinctTracked : null,
            ["otherCount"] = f.ScalarCount - shown.Sum(kv => kv.Value),
            ["values"] = values
        };
    }

    private static JsonArray KeyPatternReport(ClassAccumulator acc)
    {
        var list = new JsonArray();
        foreach (var (pattern, count) in acc.KeyPatterns.OrderByDescending(kv => kv.Value))
            list.Add(new JsonObject { ["pattern"] = pattern, ["count"] = count });
        if (acc.KeyPatternsOther > 0)
            list.Add(new JsonObject { ["pattern"] = "(other)", ["count"] = acc.KeyPatternsOther });
        return list;
    }

    private JsonObject? SampleReport(SampleDocument? sample) => sample is null ? null : new JsonObject
    {
        ["key"] = sample.Key,
        ["by"] = sample.DateMs is null ? "key" : DateField,
        ["date"] = MsToUtc(sample.DateMs),
        ["lastWriteUtc"] = sample.Cas == 0 ? null : EpochToUtc(sample.Cas),
        ["expiration"] = sample.Expiration == 0 ? null : sample.Expiration,
        ["expirationUtc"] = sample.Expiration == 0 ? null : EpochToUtc(sample.Expiration),
        ["doc"] = JsonNode.Parse(sample.Json)
    };

    /// <summary>Field paths only in the oldest sample, and only in the newest: how the shape changed.</summary>
    private static JsonObject? ShapeChange(SampleDocument? oldest, SampleDocument? newest)
    {
        if (oldest is null || newest is null || oldest.Key == newest.Key) return null;
        var before = Paths(oldest.Json);
        var after = Paths(newest.Json);
        return new JsonObject
        {
            ["onlyInOldest"] = new JsonArray(before.Except(after).Order().Select(p => (JsonNode?)p).ToArray()),
            ["onlyInNewest"] = new JsonArray(after.Except(before).Order().Select(p => (JsonNode?)p).ToArray())
        };
    }

    private static HashSet<string> Paths(string json)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement e, string path)
        {
            if (path.Length > 0) paths.Add(path);
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}");
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray().Take(ArrayElementsWalked)) Walk(item, path + "[]");
        }
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, "");
        return paths;
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

    private static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Epoch seconds, ms, µs or ns (guessed from size) → epoch ms.</summary>
    private static double EpochToMs(double value) => value switch
    {
        < 1e11 => value * 1000,      // seconds
        < 1e14 => value,             // milliseconds
        < 1e17 => value / 1000,      // microseconds
        _ => value / 1_000_000       // nanoseconds (a CAS)
    };

    private static string? MsToUtc(double? ms)
    {
        if (ms is not { } v) return null;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)v)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    internal static string? EpochToUtc(double value) => MsToUtc(EpochToMs(value));
}
