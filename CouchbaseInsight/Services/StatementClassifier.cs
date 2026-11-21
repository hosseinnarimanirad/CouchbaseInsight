using CouchbaseInsight.Models;

namespace CouchbaseInsight.Services;

/// <summary>
/// Tells a reading SQL++ statement from one that changes the cluster, by its first keyword.
/// Leading whitespace, <c>--</c> and <c>/* */</c> comments and opening parentheses are skipped.
/// Only a known read keyword makes a read; anything else, an unknown or empty statement included,
/// is a write, so the guard errs towards asking. The check is a convenience, not the safety net:
/// reads are also sent with the server's read-only flag, so a write that got past it is refused
/// by the cluster itself.
/// </summary>
public static class StatementClassifier
{
    private static readonly HashSet<string> ReadKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT",
        "WITH",     // WITH … SELECT
        "EXPLAIN",  // shows the plan, runs nothing
        "ADVISE",   // index advice, runs nothing
        "INFER",    // schema inference, reads only
    };

    public static QueryKind Classify(string? statement) =>
        FirstKeyword(statement) is { } word && ReadKeywords.Contains(word)
            ? QueryKind.Read
            : QueryKind.Write;

    /// <summary>The first word of the statement, upper-cased; null when there is none.</summary>
    public static string? FirstKeyword(string? statement)
    {
        if (string.IsNullOrEmpty(statement)) return null;

        var s = statement;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == '(' || c == ';')
            {
                i++;
            }
            else if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end + 1;
            }
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        var start = i;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        return i > start ? s[start..i].ToUpperInvariant() : null;
    }
}
