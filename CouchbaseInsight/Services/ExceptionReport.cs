using System.Text;
using Couchbase;

namespace CouchbaseInsight.Services;

/// <summary>
/// Flattens exception chains for display. The Couchbase SDK buries the real cause several levels
/// down — a bare <c>ex.Message</c> is usually just "The operation has timed out" — so the status
/// bar needs the innermost message and the diagnostics pane needs the whole tree.
/// </summary>
public static class ExceptionReport
{
    /// <summary>One line for the status bar: the outer message plus the innermost, useful one.</summary>
    public static string Summarize(Exception ex)
    {
        var leaf = Leaves(ex).First();
        return ReferenceEquals(leaf, ex)
            ? ex.Message
            : $"{ex.Message} → {leaf.GetType().Name}: {leaf.Message}";
    }

    /// <summary>
    /// The full tree, one indented line per level (outermost first, real cause last), including
    /// the error context the SDK attaches to <see cref="CouchbaseException"/>.
    /// </summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        Write(sb, ex, 0);
        return sb.ToString().TrimEnd();
    }

    private static void Write(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.Append(indent).Append(ex.GetType().Name).Append(": ").AppendLine(ex.Message);

        if (ex is CouchbaseException { Context: not null } couchbaseException)
        {
            var context = couchbaseException.Context.ToString();
            if (!string.IsNullOrWhiteSpace(context))
                sb.Append(indent).Append("  context: ").AppendLine(context.Trim());
        }

        foreach (var inner in Children(ex))
            Write(sb, inner, depth + 1);
    }

    // AggregateException is checked first: it also sets InnerException, and taking both would
    // print the first inner exception twice.
    private static IEnumerable<Exception> Children(Exception ex) => ex switch
    {
        AggregateException aggregate => aggregate.InnerExceptions,
        { InnerException: { } inner } => new[] { inner },
        _ => Array.Empty<Exception>()
    };

    /// <summary>Depth-first walk to the leaves — the exceptions holding the concrete cause.</summary>
    private static IEnumerable<Exception> Leaves(Exception ex)
    {
        var children = Children(ex).ToList();
        if (children.Count == 0)
        {
            yield return ex;
            yield break;
        }

        foreach (var leaf in children.SelectMany(Leaves))
            yield return leaf;
    }
}
