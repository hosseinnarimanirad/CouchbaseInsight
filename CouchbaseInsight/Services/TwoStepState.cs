using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// What the two-step analysis has gathered for one collection, without downloading it:
///
/// 1. Load — every class with its exact document count, counted on the server.
/// 2. Opening a class — a small sample of its documents, which lists its fields.
/// 3. Analyze selected — for the fields ticked, counted on the server over every document of the
///    class: documents holding the field, the 50 most frequent values with counts, how many
///    distinct values, min and max.
///
/// <see cref="BuildReport"/> writes it in the same report format as a Deep load, so the explorer,
/// Export and Open export treat both alike.
/// </summary>
public sealed class TwoStepState
{
    /// <summary>Values asked per field per key range. A field with more distinct values than this in
    /// some range is marked approximate: its top values may be missing counts from that range.</summary>
    public const int TopPerRange = 1000;

    /// <summary>Values kept per field across all ranges, so an id-like field cannot fill memory.</summary>
    public const int ValuesKept = 20_000;

    public const int ValuesShown = 50;
    public const int ValueMaxLength = 200;
    public const int SampleSize = 1000;

    /// <summary>Keys taken per class from each range during Load (up to <see cref="SampleSize"/> in all).</summary>
    public const int KeysPerRange = 200;

    public string Bucket { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Collection { get; init; } = "";
    public string Discriminator { get; init; } = "";

    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    public long Total { get; set; }
    public DateTimeOffset CountedAt { get; set; }

    public Dictionary<string, ClassSample> Samples { get; } = new(StringComparer.Ordinal);

    /// <summary>Document keys per class gathered by Load, so a class's sample is fetched by key.</summary>
    public Dictionary<string, List<string>> SampleKeys { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, FieldStats>> Analyses { get; } = new(StringComparer.Ordinal);

    /// <summary>Documents of a class's sample kept whole for viewing (all of them are summarised).</summary>
    public const int DocumentsKept = 50;

    public sealed class ClassSample
    {
        public int Size { get; init; }
        public IReadOnlyList<FieldInfo> Fields { get; init; } = [];
        public DateTimeOffset At { get; init; }

        /// <summary>The first <see cref="DocumentsKept"/> documents of the sample: key and JSON.</summary>
        public IReadOnlyList<(string Key, string Json)> Documents { get; init; } = [];
    }

    /// <summary>One field of one class, counted over every document of the class.</summary>
    public sealed class FieldStats
    {
        /// <summary>Documents of the class the analysis read: all of them, or those among the first
        /// <see cref="ScanLimit"/> documents by key. Presence and missing are out of this.</summary>
        public long Base { get; set; }

        /// <summary>The scan limit the analysis ran with, or null for the whole collection.</summary>
        public long? ScanLimit { get; set; }

        /// <summary>Documents of the class holding the field (a null value counts as held).</summary>
        public long NonMissing { get; set; }

        /// <summary>Value as JSON text → documents.</summary>
        public Dictionary<string, long> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>Some range had more distinct values than were returned for it, or the values
        /// kept were capped: the top values and the distinct count are then approximate.</summary>
        public bool Truncated { get; set; }

        /// <summary>The most distinct values any one range held: a floor for the true distinct count.</summary>
        public long MaxGroupsInRange { get; set; }

        public JsonElement? Min { get; set; }
        public JsonElement? Max { get; set; }
        public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset At { get; set; }

        /// <summary>Value key → one document holding it (the lowest key met).</summary>
        public Dictionary<string, string> Examples { get; } = new(StringComparer.Ordinal);

        /// <summary>Folds one range's rows for this field in.</summary>
        public void Merge(IReadOnlyList<FieldValueRow> rows)
        {
            if (rows.Count == 0) return;
            var first = rows[0];

            if (first.NonMissingInRange is { } nonMissing)
            {
                // Totals computed by the server over every value of the range, kept or not.
                NonMissing += nonMissing;
                var groups = first.GroupsInRange ?? rows.Count;
                MaxGroupsInRange = Math.Max(MaxGroupsInRange, groups);
                if (groups > rows.Count) Truncated = true;
                if (first.Min is { } mn) Min = Min is { } m0 && Compare(m0, mn) <= 0 ? m0 : mn;
                if (first.Max is { } mx) Max = Max is { } x0 && Compare(x0, mx) >= 0 ? x0 : mx;
            }
            else
            {
                // Every value of the range came back: the app works the totals out itself.
                NonMissing += rows.Sum(r => r.Count);
                MaxGroupsInRange = Math.Max(MaxGroupsInRange, rows.Count);
                foreach (var r in rows)
                {
                    Min = Min is { } m0 && Compare(m0, r.Value) <= 0 ? m0 : r.Value.Clone();
                    Max = Max is { } x0 && Compare(x0, r.Value) >= 0 ? x0 : r.Value.Clone();
                }
            }

            var kept = rows.OrderByDescending(r => r.Count).ToList();
            if (kept.Count > TopPerRange)
            {
                kept = kept.Take(TopPerRange).ToList();
                Truncated = true;
            }

            foreach (var r in kept)
            {
                Types.Add(SchemaAnalyzer.DescribeType(r.Value));
                var key = ValueKey(r.Value);
                Values[key] = Values.GetValueOrDefault(key) + r.Count;
                if (r.ExampleKey is { } ex &&
                    (!Examples.TryGetValue(key, out var had) || string.CompareOrdinal(ex, had) < 0))
                    Examples[key] = ex;
            }

            if (Values.Count > ValuesKept)
            {
                foreach (var drop in Values.OrderBy(kv => kv.Value).Take(Values.Count - ValuesKept).Select(kv => kv.Key).ToList())
                {
                    Values.Remove(drop);
                    Examples.Remove(drop);
                }
                Truncated = true;
            }
        }
    }

    /// <summary>The value as JSON text (strings cut to <see cref="ValueMaxLength"/>), so "1" and 1 stay apart.</summary>
    public static string ValueKey(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString() ?? "";
            if (s.Length > ValueMaxLength) s = s[..ValueMaxLength] + "…";
            return JsonSerializer.Serialize(s);
        }
        var raw = value.GetRawText();
        return raw.Length > ValueMaxLength * 4 ? JsonSerializer.Serialize(raw[..ValueMaxLength] + "…") : raw;
    }

    /// <summary>The server's order of JSON values: null &lt; false &lt; true &lt; numbers &lt; strings &lt; arrays &lt; objects.</summary>
    public static int Compare(JsonElement a, JsonElement b)
    {
        static int Rank(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.False => 1,
            JsonValueKind.True => 2,
            JsonValueKind.Number => 3,
            JsonValueKind.String => 4,
            JsonValueKind.Array => 5,
            JsonValueKind.Object => 6,
            _ => -1
        };
        var byKind = Rank(a).CompareTo(Rank(b));
        if (byKind != 0) return byKind;
        return a.ValueKind switch
        {
            JsonValueKind.Number => a.GetDouble().CompareTo(b.GetDouble()),
            JsonValueKind.String => string.CompareOrdinal(a.GetString(), b.GetString()),
            _ => string.CompareOrdinal(a.GetRawText(), b.GetRawText())
        };
    }

    // ---- Report ----

    public JsonObject BuildReport()
    {
        var classes = new JsonArray();
        foreach (var (name, count) in Counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            classes.Add(ClassReport(name, count));

        return new JsonObject
        {
            ["mode"] = "two-step",
            ["bucket"] = Bucket,
            ["scope"] = Scope,
            ["collection"] = Collection,
            ["discriminator"] = Discriminator,
            ["dateField"] = null,
            ["finishedAt"] = CountedAt.ToString("O", CultureInfo.InvariantCulture),
            ["documentsRead"] = Total,
            ["collectionCountAtEnd"] = Total,
            ["classCount"] = Counts.Count,
            ["notes"] = new JsonObject
            {
                ["count"] = "Exact: every document of the collection, counted on the server.",
                ["keys"] = $"keyPatterns / exampleKeys: from up to {SampleSize} keys of the class kept by Load (first " +
                           $"keys of each batch); key shapes: UUIDs → {{uuid}}, 16+ hex → {{hex}}, 2+ digits → {{n}}. " +
                           $"sampleDocuments: the first {DocumentsKept} documents of the sample, whole. values[].exampleKey: " +
                           "one document holding that value.",
                ["fields"] = $"The field list and its sample presence come from up to {SampleSize} documents of the class " +
                             "(sampleSize). A field with \"analyzed\": true was counted over every document of the class: " +
                             "seen/missing/presencePercent are then exact, and distinct holds its most frequent values.",
                ["distinct"] = $"values: the {ValuesShown} most frequent, with exact counts unless exact is false. exact is " +
                               $"false when some key range held more than {TopPerRange} distinct values of the field, or more " +
                               $"than {ValuesKept} were met in all: the counts of the top values may then be low, and " +
                               "count becomes moreThan."
            },
            ["classes"] = classes
        };
    }

    private JsonObject ClassReport(string name, long count)
    {
        var report = new JsonObject
        {
            ["class"] = name,
            ["count"] = count,
            ["percent"] = Math.Round(Total == 0 ? 0 : count * 100.0 / Total, 4)
        };

        var sample = Samples.GetValueOrDefault(name);
        var analyses = Analyses.GetValueOrDefault(name);
        AddKeys(report, name, sample);
        if (sample is null && analyses is null)
        {
            report["status"] = ClassEntry.Counted;
            report["sampled"] = false;
            report["fields"] = new JsonArray();
            return report;
        }

        // The sample's fields, then any analyzed field the sample did not happen to contain.
        var listed = (sample?.Fields ?? []).ToList();
        foreach (var (field, stats) in analyses ?? [])
            if (listed.All(f => f.Name != field))
                listed.Add(new FieldInfo { Name = field, Types = string.Join(", ", stats.Types.Order(StringComparer.Ordinal)) });

        var fields = new JsonArray();
        int required = 0, flagged = 0;
        foreach (var f in listed.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var stats = analyses?.GetValueOrDefault(f.Name);
            var flags = new JsonArray();
            if (f.HasCaseDuplicate) flags.Add("Case conflict");
            if (f.HasMultipleTypes) flags.Add("Multi-type");
            if (flags.Count > 0) flagged++;

            var seen = stats?.NonMissing ?? f.Occurrences;
            var basis = stats?.Base ?? sample?.Size ?? 0;
            if (basis > 0 && seen == basis) required++;

            fields.Add(new JsonObject
            {
                ["name"] = f.Name,
                ["types"] = new JsonArray(f.Types.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => (JsonNode?)t).ToArray()),
                ["seen"] = seen,
                ["base"] = basis,
                ["missing"] = basis - seen,
                ["presencePercent"] = Math.Round(basis == 0 ? 0 : seen * 100.0 / basis, 2),
                ["requirement"] = basis > 0 && seen == basis ? "Required" : "Optional",
                ["flags"] = flags,
                ["analyzed"] = stats is not null,
                ["sampleSeen"] = f.Occurrences,
                ["distinct"] = stats is null ? null : DistinctReport(stats, stats.Base)
            });
        }

        // How far this class has got: every field that holds plain values (never an object or an
        // array) analyzed means done.
        var scalar = listed.Where(f => f.IsScalar).Select(f => f.Name).ToList();
        var analyzedScalar = scalar.Count(n => analyses?.ContainsKey(n) == true);
        report["status"] = analyses is null || analyses.Count == 0 ? ClassEntry.Sampled
            : sample is not null && analyzedScalar == scalar.Count ? ClassEntry.Analyzed
            : ClassEntry.PartlyAnalyzed;
        report["scalarFieldCount"] = scalar.Count;
        report["analyzedScalarCount"] = analyzedScalar;
        report["sampled"] = sample is not null;
        report["sampleSize"] = sample?.Size ?? 0;
        report["analyzedFields"] = analyses?.Count ?? 0;
        report["fieldCount"] = listed.Count;
        report["requiredCount"] = required;
        report["flaggedCount"] = flagged;
        report["fields"] = fields;
        return report;
    }

    /// <summary>
    /// The class's key shapes and a few example keys, from the keys Load kept for it (up to
    /// <see cref="SampleSize"/>: a sample of the class's keys, not all of them), and the documents
    /// of its sample kept for viewing.
    /// </summary>
    private void AddKeys(JsonObject report, string name, ClassSample? sample)
    {
        var keys = SampleKeys.GetValueOrDefault(name) ?? sample?.Documents.Select(d => d.Key).ToList() ?? [];
        if (keys.Count > 0)
        {
            report["keysSampled"] = keys.Count;
            report["keyPatterns"] = new JsonArray(keys
                .GroupBy(ExportState.KeyPattern, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => (JsonNode?)new JsonObject { ["pattern"] = g.Key, ["count"] = g.Count() })
                .ToArray());
            report["exampleKeys"] = new JsonArray(keys.Take(5).Select(k => (JsonNode?)k).ToArray());
        }

        if (sample is { Documents.Count: > 0 })
            report["sampleDocuments"] = new JsonArray(sample.Documents
                .Select(d => (JsonNode?)new JsonObject { ["key"] = d.Key, ["doc"] = JsonNode.Parse(d.Json) })
                .ToArray());
    }

    private static JsonObject DistinctReport(FieldStats s, long classCount)
    {
        var shown = s.Values.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                            .Take(ValuesShown).ToList();
        var values = new JsonArray();
        foreach (var (key, count) in shown)
            values.Add(new JsonObject
            {
                ["value"] = JsonNode.Parse(key),
                ["count"] = count,
                ["exampleKey"] = s.Examples.GetValueOrDefault(key)
            });

        var distinct = s.Truncated ? (long?)null : s.Values.Count;
        return new JsonObject
        {
            ["over"] = classCount,
            ["scanLimit"] = s.ScanLimit,
            ["exact"] = !s.Truncated,
            ["complete"] = !s.Truncated && s.Values.Count <= ValuesShown,
            ["count"] = distinct,
            ["moreThan"] = s.Truncated ? Math.Max(s.Values.Count, s.MaxGroupsInRange) : null,
            ["otherCount"] = s.NonMissing - shown.Sum(kv => kv.Value),
            ["min"] = s.Min is { } mn ? JsonNode.Parse(ValueKey(mn)) : null,
            ["max"] = s.Max is { } mx ? JsonNode.Parse(ValueKey(mx)) : null,
            ["types"] = new JsonArray(s.Types.Order(StringComparer.Ordinal).Select(t => (JsonNode?)t).ToArray()),
            ["values"] = values
        };
    }
}
