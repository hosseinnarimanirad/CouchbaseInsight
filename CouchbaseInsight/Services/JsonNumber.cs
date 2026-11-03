using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchbaseInsight.Services;

/// <summary>
/// Reads a number from a report node, whatever it holds. A report parsed from a file holds JSON
/// numbers; a report built in memory by Load holds the CLR values it was given (long, int, double,
/// ulong). <c>TryGetValue&lt;double&gt;</c> alone answers false for a node holding a long, which
/// is why every count once showed as 0 straight after a Load.
/// </summary>
public static class JsonNumber
{
    public static double ToDouble(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<ulong>(out var u)) return u;
        if (v.TryGetValue<decimal>(out var m)) return (double)m;
        if (v.TryGetValue<float>(out var f)) return f;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        return 0;
    }

    public static long ToLong(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<long>(out var l)) return l;
        return (long)ToDouble(node);
    }
}
