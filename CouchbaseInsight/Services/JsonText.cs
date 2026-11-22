using System.Text.Encodings.Web;
using System.Text.Json;

namespace CouchbaseInsight.Services;

/// <summary>How the app writes JSON for people to read: indented, with Persian (and any other
/// non-ASCII) text kept as it is instead of \uXXXX escapes.</summary>
public static class JsonText
{
    /// <summary>
    /// For panes, exports and files shown to a person. The relaxed encoder only skips the HTML-safety
    /// escaping (&lt;, &gt;, &amp;, non-ASCII) that matters when JSON is embedded in a web page; the
    /// output is still valid JSON with quotes and control characters escaped.
    /// </summary>
    public static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
