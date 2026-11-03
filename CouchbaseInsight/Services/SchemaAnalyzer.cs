using System.Text.Json;
using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

public class SchemaAnalyzer
{
    private readonly Dictionary<string, FieldAccumulator> _fields = new();
    private int _sampleSize;

    /// <summary>Documents added so far.</summary>
    public int SampleSize => _sampleSize;

    private sealed class FieldAccumulator
    {
        public int Occurrences;
        public readonly SortedSet<string> Types = new(StringComparer.Ordinal);
    }

    public void Add(JsonElement document)
    {
        _sampleSize++;
        if (document.ValueKind != JsonValueKind.Object)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(document, prefix: string.Empty, seen);

        foreach (var path in seen)
            _fields[path].Occurrences++;
    }

    private void Walk(JsonElement element, string prefix, HashSet<string> seen)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";

            if (!_fields.TryGetValue(path, out var acc))
            {
                acc = new FieldAccumulator();
                _fields[path] = acc;
            }

            acc.Types.Add(DescribeType(prop.Value));
            seen.Add(path);

            if (prop.Value.ValueKind == JsonValueKind.Object)
                Walk(prop.Value, path, seen);
        }
    }

    internal static string DescribeType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => DescribeArray(value),
        _ => "unknown"
    };

    private static string DescribeArray(JsonElement array)
    {
        var elementTypes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            elementTypes.Add(DescribeType(item));
            if (elementTypes.Count >= 3) break;
        }
        return elementTypes.Count == 0
            ? "array"
            : $"array<{string.Join("|", elementTypes)}>";
    }

    public ClassSchema Build(string className)
    {
        // Fields whose names only differ in case form a "case conflict" group.
        var caseDups = _fields.Keys
            .GroupBy(k => k.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet(StringComparer.Ordinal);

        var fields = _fields
            .Select(kvp => new FieldInfo
            {
                Name = kvp.Key,
                Types = string.Join(", ", kvp.Value.Types),
                Occurrences = kvp.Value.Occurrences,
                SampleSize = _sampleSize,
                HasCaseDuplicate = caseDups.Contains(kvp.Key)
            })
            .OrderByDescending(f => f.IsRequired)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ClassSchema
        {
            ClassName = className,
            SampleSize = _sampleSize,
            Fields = fields
        };
    }
}
