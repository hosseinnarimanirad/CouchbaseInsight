using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

/// <summary>
/// The Query tab's editor: one statement, typed by hand, with helpers that write into it at the
/// caret: the picked collection's path and one condition at a time. There is no second, composed
/// statement to keep in step; what the editor shows is what Run sends and Save stores.
/// </summary>
public partial class QueryEditorViewModel : ObservableObject
{
    private readonly ICouchbaseService _couchbase;
    private readonly Action<string, StatusKind> _report;
    private int _fieldsRequest;

    public QueryEditorViewModel(
        ICouchbaseService couchbase, ObservableCollection<string> buckets, Action<string, StatusKind> report)
    {
        _couchbase = couchbase;
        _report = report;
        Picker = new KeyspacePickerViewModel(couchbase, buckets, report);
        Picker.PropertyChanged += OnPickerChanged;

        FieldsView = new ListCollectionView(Fields) { Filter = MatchesFieldSearch };
        FieldsView.SortDescriptions.Add(new SortDescription(nameof(FieldInfo.Name), ListSortDirection.Ascending));
    }

    /// <summary>The Query tab's own bucket › scope › collection, independent of the Schema explorer's.</summary>
    public KeyspacePickerViewModel Picker { get; }

    /// <summary>The whole statement: what Run sends and Save stores.</summary>
    [ObservableProperty] private string _statement = "";

    /// <summary>The editor's caret, bound both ways so insertions land where the user is typing.</summary>
    [ObservableProperty] private int _caretOffset;

    /// <summary>What <see cref="StartNew"/> last put in, so an untouched template is not unsaved work.</summary>
    private string? _template;

    /// <summary>The editor holds something typed, pasted or opened: not empty, not the untouched template.</summary>
    public bool HasOwnText => !string.IsNullOrWhiteSpace(Statement) && Statement != _template;

    // ---- Condition row ----

    /// <summary>Top-level and nested fields of the picked collection, from a sample of its documents.</summary>
    public ObservableCollection<FieldInfo> Fields { get; } = new();

    /// <summary>What the field dropdown lists: <see cref="Fields"/> sorted by name, narrowed to the
    /// names containing <see cref="FieldSearch"/>.</summary>
    public ICollectionView FieldsView { get; }

    /// <summary>The text typed in the field dropdown.</summary>
    [ObservableProperty] private string _fieldSearch = "";

    /// <summary>Opens the dropdown while typing narrows it, so the matches show without a click.</summary>
    [ObservableProperty] private bool _isFieldListOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddConditionCommand))]
    private FieldInfo? _pendingField;

    partial void OnFieldSearchChanged(string value)
    {
        // Typing (or picking) a whole name selects that field; anything else is still searching.
        // Set before the refresh, which reads it.
        PendingField = Fields.FirstOrDefault(f => string.Equals(f.Name, value.Trim(), StringComparison.OrdinalIgnoreCase));
        FieldsView.Refresh();
        IsFieldListOpen = PendingField is null && value.Length > 0 && !FieldsView.IsEmpty;
    }

    private bool MatchesFieldSearch(object item)
    {
        // A picked field shows its full name in the box; that must not hide the rest of the list.
        if (PendingField is not null || string.IsNullOrWhiteSpace(FieldSearch)) return true;
        return item is FieldInfo f && f.Name.Contains(FieldSearch.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueEnabled))]
    [NotifyCanExecuteChangedFor(nameof(AddConditionCommand))]
    private string _pendingOperator = "=";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddConditionCommand))]
    private string _pendingValue = "";

    public IReadOnlyList<string> Operators { get; } =
        ["=", "!=", "<", "<=", ">", ">=", "LIKE", "IS NULL", "IS NOT NULL"];

    public bool ValueEnabled => PendingOperator is not "IS NULL" and not "IS NOT NULL";

    [RelayCommand(CanExecute = nameof(CanAddCondition))]
    private void AddCondition()
    {
        var condition = SqlText.Condition(PendingField!.Name, PendingOperator, PendingValue, PendingField.Types);
        Apply(SqlText.InsertCondition(Statement, CaretOffset, condition));
        PendingValue = "";
    }

    private bool CanAddCondition() =>
        PendingField is not null && (!ValueEnabled || !string.IsNullOrWhiteSpace(PendingValue));

    [RelayCommand(CanExecute = nameof(CanPasteCollection))]
    private void PasteCollection() => Apply(SqlText.Insert(Statement, CaretOffset, Picker.Keyspace));

    private bool CanPasteCollection() => Picker.Keyspace.Length > 0;

    // ---- Statements put in from outside ----

    /// <summary>Replaces the statement, e.g. with an opened saved query; the caret goes to the start.</summary>
    public void Load(string statement)
    {
        Statement = statement;
        CaretOffset = 0;

        // Follow the collection the statement reads from, so the picker, Paste collection and the
        // field list match it. A statement without one leaves the picker where it was.
        if (SqlText.FirstKeyspace(statement) is { } k)
            _ = Picker.SelectAsync(k.Bucket, k.Scope, k.Collection);
    }

    /// <summary>A new query on the picked collection, with the caret after WHERE.</summary>
    public void StartNew()
    {
        var from = Picker.Keyspace.Length > 0 ? Picker.Keyspace : "`bucket`.`scope`.`collection`";
        _template = $"SELECT META(c).id, c.*\nFROM {from} AS c\nWHERE ";
        Statement = _template;
        CaretOffset = Statement.Length;
    }

    private void Apply(TextEdit edit)
    {
        Statement = edit.Text;
        CaretOffset = edit.Caret;
    }

    // ---- Fields follow the picked collection ----

    private void OnPickerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KeyspacePickerViewModel.Keyspace)) return;
        PasteCollectionCommand.NotifyCanExecuteChanged();
        _ = LoadFieldsAsync(++_fieldsRequest);
    }

    private async Task LoadFieldsAsync(int request)
    {
        Fields.Clear();
        PendingField = null;
        FieldSearch = "";
        if (Picker.Keyspace.Length == 0) return;

        var (bucket, scope, collection) = (Picker.SelectedBucket!, Picker.SelectedScope!.Name, Picker.SelectedCollection!);
        try
        {
            var fields = await _couchbase.GetCollectionFieldsAsync(bucket, scope, collection);
            if (request != _fieldsRequest) return; // another collection was picked meanwhile
            foreach (var f in fields) Fields.Add(f);
        }
        catch (Exception ex)
        {
            if (request == _fieldsRequest)
                _report($"Could not list the fields of '{collection}': {ExceptionReport.Summarize(ex)}", StatusKind.Error);
        }
    }
}
