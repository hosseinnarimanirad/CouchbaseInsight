using System.Windows;
using System.Windows.Media;
using System.Xml;
using CouchbaseInsight.Themes;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Search;

namespace CouchbaseInsight.Views;

/// <summary>
/// Attaches an AvalonEdit editor to the app: the syntax definition it uses, the text it is bound
/// to, and the theme colours it cannot pick up from a WPF style.
///
/// <para>
/// Two things here are not stylable in XAML and so have to be done in code. AvalonEdit's selection,
/// caret and current-line brushes live on <c>TextArea</c> and <c>TextView</c> rather than on the
/// control, so no <c>Style</c> can reach them; and a highlighting definition holds fixed colours
/// that the resource system knows nothing about, so nothing repaints them when the theme is
/// swapped. Both are re-applied on <see cref="ThemeManager.ThemeChanged"/> for every editor that is
/// still alive.
/// </para>
/// </summary>
public static class Editors
{
    private static readonly Dictionary<string, IHighlightingDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WeakReference<TextEditor>> Live = [];
    private static bool _subscribed;

    // ---- Syntax: which definition an editor uses ("Json" or "N1ql") ----

    public static readonly DependencyProperty SyntaxProperty = DependencyProperty.RegisterAttached(
        "Syntax", typeof(string), typeof(Editors),
        new PropertyMetadata(null, OnSyntaxChanged));

    public static void SetSyntax(DependencyObject element, string value) =>
        element.SetValue(SyntaxProperty, value);

    public static string GetSyntax(DependencyObject element) =>
        (string)element.GetValue(SyntaxProperty);

    private static void OnSyntaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor || e.NewValue is not string name) return;

        Track(editor);
        editor.SyntaxHighlighting = Definition(name);
        ApplyChrome(editor);
    }

    // ---- Text: a two-way binding target, which Document.Text is not ----

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Editors),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public static void SetText(DependencyObject element, string value) =>
        element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) =>
        (string)element.GetValue(TextProperty);

    // Set while pushing a change in either direction, so the echo back is ignored rather than
    // fighting the caret.
    private static readonly HashSet<TextEditor> Syncing = [];

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;

        Track(editor);
        Hook(editor);

        if (Syncing.Contains(editor)) return;

        var incoming = e.NewValue as string ?? string.Empty;
        if (editor.Document.Text == incoming) return;

        Syncing.Add(editor);
        try
        {
            // Setting Text drops the caret to the start; remembering the offset keeps a WHERE
            // clause editable while the view model normalises what was typed.
            var caret = editor.CaretOffset;
            editor.Document.Text = incoming;
            editor.CaretOffset = Math.Min(caret, editor.Document.TextLength);
        }
        finally
        {
            Syncing.Remove(editor);
        }
    }

    private static readonly HashSet<TextEditor> Hooked = [];

    private static void Hook(TextEditor editor)
    {
        HookCaret(editor);
        if (!Hooked.Add(editor)) return;

        editor.TextChanged += (_, _) =>
        {
            // A read-only editor is bound one way. Writing back would replace that binding with a
            // local value, and the pane would never update again.
            if (editor.IsReadOnly || Syncing.Contains(editor)) return;

            Syncing.Add(editor);
            try { SetText(editor, editor.Document.Text); }
            finally { Syncing.Remove(editor); }
        };
    }

    // ---- CaretOffset: a two-way binding target, so a view model can insert at the caret ----

    public static readonly DependencyProperty CaretOffsetProperty = DependencyProperty.RegisterAttached(
        "CaretOffset", typeof(int), typeof(Editors),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnCaretOffsetChanged));

    public static void SetCaretOffset(DependencyObject element, int value) =>
        element.SetValue(CaretOffsetProperty, value);

    public static int GetCaretOffset(DependencyObject element) =>
        (int)element.GetValue(CaretOffsetProperty);

    // Kept apart from Syncing: setting the text moves the caret, and that move must still reach
    // the binding, or the view model's caret goes stale.
    private static readonly HashSet<TextEditor> CaretSyncing = [];
    private static readonly HashSet<TextEditor> CaretHooked = [];

    private static void OnCaretOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor) return;

        HookCaret(editor);
        if (CaretSyncing.Contains(editor)) return;
        CaretSyncing.Add(editor);
        try { editor.CaretOffset = Math.Clamp((int)e.NewValue, 0, editor.Document.TextLength); }
        finally { CaretSyncing.Remove(editor); }
    }

    /// <summary>Caret moves go to the binding. Hooked with the text (see <see cref="Hook"/>) rather
    /// than on the first CaretOffset change, which never comes while the bound value stays 0.</summary>
    private static void HookCaret(TextEditor editor)
    {
        if (!CaretHooked.Add(editor)) return;

        editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (CaretSyncing.Contains(editor)) return;
            CaretSyncing.Add(editor);
            try { editor.SetCurrentValue(CaretOffsetProperty, editor.CaretOffset); }
            finally { CaretSyncing.Remove(editor); }
        };
    }

    // ---- Definitions and theming ----

    private static IHighlightingDefinition? Definition(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Definitions.TryGetValue(name, out var cached)) return cached;

        var resource = $"CouchbaseInsight.Themes.{name}.xshd";
        using var stream = typeof(Editors).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;

        using var reader = new XmlTextReader(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

        Definitions[name] = definition;
        Recolour(definition);
        return definition;
    }

    /// <summary>Overwrites a definition's named colours with the active theme's syntax palette.</summary>
    private static void Recolour(IHighlightingDefinition definition)
    {
        foreach (var name in new[]
                 { "Key", "String", "Number", "Literal", "Keyword", "Function", "Punctuation", "Comment" })
        {
            var colour = definition.GetNamedColor(name);
            if (colour is null) continue;
            if (Application.Current?.TryFindResource($"Color.Syntax.{name}") is Color themed)
                colour.Foreground = new SimpleHighlightingBrush(themed);
        }
    }

    /// <summary>The brushes that live below the control and so cannot be reached by a style.</summary>
    private static void ApplyChrome(TextEditor editor)
    {
        // No current-line highlight. These panes are read, not written line by line, and a band
        // across the first line of an empty editor reads as a selection that is not there.
        editor.Options.HighlightCurrentLine = false;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;

        editor.LineNumbersForeground = Brush("Brush.TextTertiary", Colors.Gray);
        editor.TextArea.SelectionBrush = Brush("Brush.Selected", Colors.SteelBlue);
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.SelectionForeground = null;
        editor.TextArea.Caret.CaretBrush = Brush("Brush.Text", Colors.Black);

        // Ctrl+F over a result that is thousands of lines long. Installed once per editor;
        // SearchPanel.Install is not idempotent, so it is guarded by the Searchable set.
        if (Searchable.Add(editor))
        {
            var panel = SearchPanel.Install(editor);
            panel.MarkerBrush = Brush("Brush.Accent", Colors.Goldenrod);
        }
    }

    private static readonly HashSet<TextEditor> Searchable = [];

    private static SolidColorBrush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush found)
            return found;
        return new SolidColorBrush(fallback);
    }

    private static void Track(TextEditor editor)
    {
        // Hooked here rather than only from the Text callback. A binding whose first value equals
        // the property's default — an empty WHERE clause against an empty string — never raises the
        // changed callback, so an editor that started empty would never have been listening, and
        // the first thing typed into it would not have reached the view model.
        Hook(editor);

        if (!_subscribed)
        {
            ThemeManager.ThemeChanged += _ => Refresh();
            _subscribed = true;
        }

        foreach (var reference in Live)
        {
            if (reference.TryGetTarget(out var existing) && ReferenceEquals(existing, editor))
                return;
        }

        Live.Add(new WeakReference<TextEditor>(editor));
    }

    private static void Refresh()
    {
        foreach (var definition in Definitions.Values)
            Recolour(definition);

        Live.RemoveAll(reference => !reference.TryGetTarget(out _));

        foreach (var reference in Live)
        {
            if (!reference.TryGetTarget(out var editor)) continue;

            ApplyChrome(editor);

            // The definition object was mutated in place, which the colorizer has already cached.
            // Re-assigning it is what makes the view re-run highlighting over the visible lines.
            var definition = editor.SyntaxHighlighting;
            editor.SyntaxHighlighting = null;
            editor.SyntaxHighlighting = definition;
            editor.TextArea.TextView.Redraw();
        }
    }
}
