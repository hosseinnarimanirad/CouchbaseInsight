using System.Globalization;
using System.Text.RegularExpressions;

namespace CouchbaseInsight.Services;

/// <summary>A statement after an insertion, with where the caret goes.</summary>
public readonly record struct TextEdit(string Text, int Caret);

/// <summary>
/// Writing SQL++ text: identifiers, literals, keyspace paths, and inserting a piece into a
/// statement at the caret. Pure string work, no UI and no cluster, so every rule here can be
/// checked on its own.
/// </summary>
public static partial class SqlText
{
    /// <summary>A backticked identifier, with backticks inside it doubled.</summary>
    public static string Identifier(string name) => $"`{name.Replace("`", "``")}`";

    /// <summary>A dotted field path as identifiers: "address.city" → `address`.`city`.</summary>
    public static string FieldPath(string dottedPath) =>
        string.Join('.', dottedPath.Split('.').Select(Identifier));

    /// <summary>`bucket`.`scope`.`collection`.</summary>
    public static string Keyspace(string bucket, string scope, string collection) =>
        $"{Identifier(bucket)}.{Identifier(scope)}.{Identifier(collection)}";

    /// <summary>A single-quoted string: backslashes (escape characters in SQL++ strings) and
    /// single quotes doubled.</summary>
    public static string StringLiteral(string value) =>
        $"'{value.Replace("\\", "\\\\").Replace("'", "''")}'";

    /// <summary>
    /// A typed value as a literal. The field's sampled JSON types decide: a field only ever seen as
    /// a string gets a string even for "09121234567" or "true", since an unquoted number never
    /// equals a string. A value typed in matching quotes ('x' or "x") is taken as that string, so
    /// typing the quotes does not end up searching for them.
    /// </summary>
    /// <param name="fieldTypes">Comma-separated JSON types, as <c>FieldInfo.Types</c>; empty when unknown.</param>
    public static string Literal(string value, string? fieldTypes)
    {
        var text = value.Trim();
        if (text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[^1] == text[0])
            return StringLiteral(text[1..^1]);

        var types = (fieldTypes ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        bool Allows(string type) => types.Count == 0 || types.Contains(type);

        if (Allows("number") &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            double.IsFinite(number))
            return text;

        var lower = text.ToLowerInvariant();
        if (lower is "true" or "false" && Allows("boolean")) return lower;
        if (lower is "null" && Allows("null")) return lower;
        return StringLiteral(value);
    }

    /// <summary>One condition: <c>`field` op literal</c>, or <c>`field` IS [NOT] NULL</c>.</summary>
    /// <param name="field">A dotted path, as the field list names nested fields.</param>
    public static string Condition(string field, string op, string value, string? fieldTypes) =>
        op is "IS NULL" or "IS NOT NULL"
            ? $"{FieldPath(field)} {op}"
            : $"{FieldPath(field)} {op} {Literal(value, fieldTypes)}";

    /// <summary>Puts <paramref name="snippet"/> at the caret, spaced from the words around it.</summary>
    public static TextEdit Insert(string text, int caret, string snippet)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var before = caret > 0 && !char.IsWhiteSpace(text[caret - 1]) && text[caret - 1] != '(' ? " " : "";
        var after = caret < text.Length && !char.IsWhiteSpace(text[caret]) && text[caret] != ')' ? " " : "";
        var piece = before + snippet + after;
        return new TextEdit(text.Insert(caret, piece), caret + before.Length + snippet.Length);
    }

    /// <summary>
    /// Puts a condition at the caret with the keyword it needs there: <c>WHERE</c> when no WHERE
    /// comes before the caret, nothing right after WHERE, AND, OR, NOT or "(", and <c>AND</c>
    /// after anything else.
    /// </summary>
    public static TextEdit InsertCondition(string text, int caret, string condition)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var head = text[..caret];
        var prefix = !WhereKeyword().IsMatch(head) ? "WHERE "
                   : ConditionStart().IsMatch(head) ? ""
                   : "AND ";
        return Insert(text, caret, prefix + condition);
    }

    // Keywords inside quotes or comments are not told apart: good enough for placing a condition.
    [GeneratedRegex(@"\bWHERE\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhereKeyword();

    [GeneratedRegex(@"(\b(WHERE|AND|OR|NOT)|\()\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConditionStart();
}
