using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace CouchbaseInsight.Views;

/// <summary>
/// Where JSON can fold: every <c>{ … }</c> and <c>[ … ]</c> that spans more than one line.
/// Brackets inside strings are skipped, escapes included. Text that is not well-formed JSON (an
/// error report in the result pane) just folds what pairs up and leaves the rest alone.
/// </summary>
public static class JsonFolding
{
    /// <summary>The fold regions of <paramref name="document"/>, ordered by start as AvalonEdit requires.</summary>
    public static IEnumerable<NewFolding> Create(TextDocument document)
    {
        var text = document.Text;
        var folds = new List<NewFolding>();
        var open = new Stack<(char Bracket, int Offset)>();
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;              // skip the escaped character
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    open.Push((c, i));
                    break;
                case '}' or ']':
                    if (open.Count == 0 || open.Peek().Bracket != (c == '}' ? '{' : '[')) break;
                    var start = open.Pop().Offset;
                    if (document.GetLineByOffset(start).LineNumber != document.GetLineByOffset(i).LineNumber)
                        folds.Add(new NewFolding(start, i + 1) { Name = c == '}' ? "{…}" : "[…]" });
                    break;
            }
        }

        // Closing brackets come innermost first; the manager wants them by start offset.
        folds.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return folds;
    }
}
