using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

/// <summary>
/// The saved-queries pane of the Query tab. Queries belong to the connection profile picked in the
/// connection form (<see cref="MainViewModel.SelectedConnection"/>), so they can be browsed and
/// edited while disconnected; running one still needs a connection. Opening a query puts it in the
/// SQL++ editor; its name, folder and tags are edited in the row above the editor, and Save writes
/// all of it back under the same id, so renaming or moving a query is just editing those fields.
/// </summary>
public partial class MainViewModel
{
    private ISavedQueryStore? _queryStore;

    /// <summary>The query as it was last opened or saved; null for a new, never-saved one.</summary>
    private SavedQuery? _openQuery;

    /// <summary>Set while this class itself moves the list selection, so that does not open a query.</summary>
    private bool _settingQuerySelection;

    public ObservableCollection<SavedQuery> SavedQueries { get; } = new();

    /// <summary>Grouped by folder and filtered by <see cref="SavedQueryFilter"/>; what the pane shows.</summary>
    public ICollectionView SavedQueriesView { get; private set; } = null!;

    [ObservableProperty] private string _savedQueryFilter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSavedQueryCommand))]
    private SavedQuery? _selectedSavedQuery;

    [ObservableProperty] private string _queryName = "";
    [ObservableProperty] private string _queryFolder = "";

    /// <summary>Comma-separated, as typed; split and tidied on save.</summary>
    [ObservableProperty] private string _queryTags = "";

    /// <summary>The profile the pane lists queries for, or null when none is picked.</summary>
    public string? QueryOwner => SelectedConnection?.Name;

    public bool HasQueryOwner => QueryOwner is not null;

    /// <summary>Header of the pane: whose queries these are.</summary>
    public string SavedQueriesCaption => QueryOwner is null
        ? "Pick a saved connection to keep queries for it."
        : $"Queries of '{QueryOwner}'";

    /// <summary>"Untitled", or the open query's folder/name, with a dot while it has unsaved edits.</summary>
    public string OpenQueryTitle =>
        (_openQuery?.Path ?? "Untitled query") + (IsQueryDirty ? "  •" : "");

    /// <summary>The statement Save would store: the hand-written one, or what the builder composes.</summary>
    private string CurrentStatement => IsRawQuery ? RawStatement : ComposedStatement();

    /// <summary>Whether the editor or the name/folder/tags row differs from what was opened or last saved.</summary>
    public bool IsQueryDirty
    {
        get
        {
            if (_openQuery is null)
                return !string.IsNullOrWhiteSpace(QueryName) || (IsRawQuery && !string.IsNullOrWhiteSpace(RawStatement));

            return !string.Equals(CurrentStatement, _openQuery.Statement, StringComparison.Ordinal) ||
                   !string.Equals(QueryName.Trim(), _openQuery.Name, StringComparison.Ordinal) ||
                   !string.Equals(SavedQueryStore.NormalizeFolder(QueryFolder), _openQuery.Folder, StringComparison.Ordinal) ||
                   !SplitTags(QueryTags).SequenceEqual(_openQuery.Tags, StringComparer.Ordinal);
        }
    }

    /// <summary>Called from the constructor once the store exists.</summary>
    private void InitSavedQueries(ISavedQueryStore store)
    {
        _queryStore = store;
        SavedQueriesView = CollectionViewSource.GetDefaultView(SavedQueries);
        SavedQueriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SavedQuery.Folder)));
        SavedQueriesView.Filter = o => o is SavedQuery q && MatchesSavedQuery(q, SavedQueryFilter);

        if (store.LoadWarning is { } warning)
        {
            Diagnostics = warning;
            Report("Saved queries could not be loaded — see the details.", StatusKind.Error);
        }
    }

    private static bool MatchesSavedQuery(SavedQuery q, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        Matches(q.Path, filter) ||
        q.Tags.Any(t => Matches(t, filter)) ||
        Matches(q.Statement, filter);

    partial void OnSavedQueryFilterChanged(string value) => SavedQueriesView.Refresh();

    // Anything that changes what Save would store changes the dirty mark and the title.
    private static readonly HashSet<string> QueryEditProperties =
    [
        nameof(RawStatement), nameof(IsRawQuery), nameof(SelectExpression), nameof(FromKeyspace),
        nameof(WhereClause), nameof(QueryName), nameof(QueryFolder), nameof(QueryTags)
    ];

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && QueryEditProperties.Contains(name))
            OnQueryEditStateChanged();
    }

    private void OnQueryEditStateChanged()
    {
        OnPropertyChanged(nameof(IsQueryDirty));
        OnPropertyChanged(nameof(OpenQueryTitle));
        SaveQueryCommand.NotifyCanExecuteChanged();
        SaveQueryAsNewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Lists the picked profile's queries. Called whenever <see cref="SelectedConnection"/> changes.</summary>
    private void ReloadSavedQueries()
    {
        OnPropertyChanged(nameof(QueryOwner));
        OnPropertyChanged(nameof(HasQueryOwner));
        OnPropertyChanged(nameof(SavedQueriesCaption));
        NewQueryCommand.NotifyCanExecuteChanged();
        ImportQueriesCommand.NotifyCanExecuteChanged();
        ExportQueriesCommand.NotifyCanExecuteChanged();
        SaveQueryCommand.NotifyCanExecuteChanged();
        SaveQueryAsNewCommand.NotifyCanExecuteChanged();

        if (_queryStore is null) return;

        SavedQueries.Clear();
        if (QueryOwner is { } owner)
            foreach (var q in _queryStore.ForConnection(owner))
                SavedQueries.Add(q);

        // The open query belongs to the previous profile; keep its text in the editor, but as a
        // new, unsaved query, so Save cannot write it into the wrong list or onto a stale id.
        if (_openQuery is not null && !SavedQueries.Any(q => q.Id == _openQuery.Id))
        {
            _openQuery = null;
            OnQueryEditStateChanged();
        }

        SelectSavedQuerySilently(_openQuery is null ? null : SavedQueries.First(q => q.Id == _openQuery.Id));
    }

    private void SelectSavedQuerySilently(SavedQuery? query)
    {
        _settingQuerySelection = true;
        try { SelectedSavedQuery = query; }
        finally { _settingQuerySelection = false; }
    }

    partial void OnSelectedSavedQueryChanged(SavedQuery? oldValue, SavedQuery? newValue)
    {
        if (_settingQuerySelection || newValue is null || newValue.Id == _openQuery?.Id)
            return;

        if (!ConfirmLeavingOpenQuery())
        {
            // Put the selection back once the list has finished processing this click.
            Application.Current.Dispatcher.BeginInvoke(() => SelectSavedQuerySilently(oldValue));
            return;
        }

        OpenSavedQuery(newValue);
    }

    private void OpenSavedQuery(SavedQuery query)
    {
        _openQuery = query.Clone();
        IsRawQuery = true;
        RawStatement = query.Statement;
        QueryName = query.Name;
        QueryFolder = query.Folder;
        QueryTags = string.Join(", ", query.Tags);
        OnQueryEditStateChanged();
        Report($"Opened '{query.Path}'.", StatusKind.Idle);
    }

    /// <summary>
    /// Before the editor's content is replaced: when it has unsaved edits, ask whether to save them.
    /// Returns false when the user cancels (or a save they asked for failed), so nothing is replaced.
    /// </summary>
    private bool ConfirmLeavingOpenQuery()
    {
        if (!IsQueryDirty) return true;

        var answer = MessageBox.Show(
            $"'{_openQuery?.Path ?? "Untitled query"}' has unsaved changes.\n\nSave them first?",
            "Unsaved query", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        return answer switch
        {
            MessageBoxResult.Yes => CanSaveQuery() && StoreQuery(asNew: false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    /// <summary>For the window's Closing: false keeps the window open (the user cancelled, or a save failed).</summary>
    public bool ConfirmClose() => ConfirmLeavingOpenQuery();

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(HasQueryOwner))]
    private void NewQuery()
    {
        if (!ConfirmLeavingOpenQuery()) return;

        _openQuery = null;
        QueryName = "";
        QueryTags = "";
        // Keep the folder: a new query usually goes next to the one just worked on.
        IsRawQuery = true;
        RawStatement = "";
        SelectSavedQuerySilently(null);
        OnQueryEditStateChanged();
    }

    /// <summary>Ctrl+S. Writes the open query back under its id, or saves a new one.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveQuery))]
    private void SaveQuery() => StoreQuery(asNew: false);

    private bool CanSaveQuery() =>
        HasQueryOwner && !string.IsNullOrWhiteSpace(CurrentStatement) &&
        (_openQuery is null || IsQueryDirty);

    /// <summary>A copy under a free name (the typed one, else "name (2)"), leaving the original as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveQueryAsNew))]
    private void SaveQueryAsNew() => StoreQuery(asNew: true);

    private bool CanSaveQueryAsNew() => HasQueryOwner && !string.IsNullOrWhiteSpace(CurrentStatement);

    private bool StoreQuery(bool asNew)
    {
        if (_queryStore is null || QueryOwner is not { } owner) return false;

        var name = QueryName.Trim();
        if (name.Length == 0)
        {
            Report("Type a name for the query in the Name box, then save.", StatusKind.Error);
            return false;
        }

        var isNew = asNew || _openQuery is null;
        var query = isNew ? new SavedQuery() : _openQuery!.Clone();
        query.Connection = owner;
        query.Folder = QueryFolder;
        query.Name = isNew && asNew ? _queryStore.UniqueName(owner, QueryFolder, name) : name;
        query.Tags = SplitTags(QueryTags);
        query.Statement = CurrentStatement;

        SavedQuery stored;
        try
        {
            stored = _queryStore.Save(query);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Report($"Not saved: {ex.Message}", StatusKind.Error);
            return false;
        }

        // A query saved from the builder is a hand-written one from now on, so what the editor
        // shows is exactly what was stored.
        if (!IsRawQuery)
        {
            IsRawQuery = true;
            RawStatement = stored.Statement;
        }

        _openQuery = stored;
        QueryName = stored.Name;
        QueryFolder = stored.Folder;
        QueryTags = string.Join(", ", stored.Tags);

        var index = IndexOfSavedQuery(stored.Id);
        if (index >= 0) SavedQueries[index] = stored;
        else SavedQueries.Add(stored);
        SortSavedQueries();
        SelectSavedQuerySilently(stored);
        OnQueryEditStateChanged();

        Report(isNew ? $"Saved '{stored.Path}'." : $"Saved changes to '{stored.Path}'.", StatusKind.Ok);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSavedQuery))]
    private void DeleteSavedQuery()
    {
        var query = SelectedSavedQuery!;
        var answer = MessageBox.Show(
            $"Delete the saved query '{query.Path}'?\n\nThis cannot be undone.",
            "Delete query", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK || _queryStore is null) return;

        try
        {
            _queryStore.Delete(query.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Report($"Not deleted: {ex.Message}", StatusKind.Error);
            return;
        }

        var index = IndexOfSavedQuery(query.Id);
        if (index >= 0) SavedQueries.RemoveAt(index);

        // The text stays in the editor as an unsaved query, so a mistaken delete can be saved again.
        if (_openQuery?.Id == query.Id)
            _openQuery = null;
        SelectSavedQuerySilently(null);
        OnQueryEditStateChanged();
        Report($"Deleted '{query.Path}'.", StatusKind.Ok);
    }

    private bool CanDeleteSavedQuery() => SelectedSavedQuery is not null;

    /// <summary>Exports what the pane shows: all of this profile's queries, or only those the filter matches.</summary>
    [RelayCommand(CanExecute = nameof(HasQueryOwner))]
    private void ExportQueries()
    {
        var queries = SavedQueriesView.Cast<SavedQuery>().ToList();
        if (_queryStore is null || queries.Count == 0)
        {
            Report("No saved queries to export" + (string.IsNullOrWhiteSpace(SavedQueryFilter) ? "." : " — the filter matches none."),
                StatusKind.Idle);
            return;
        }

        if (AskExportPath(QueryOwner!) is not { } path) return;

        try
        {
            _queryStore.Export(path, queries);
            Report($"Exported {queries.Count} quer{(queries.Count == 1 ? "y" : "ies")} to {path}", StatusKind.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Export failed: {ex.Message}", StatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(HasQueryOwner))]
    private void ImportQueries()
    {
        if (_queryStore is null || QueryOwner is not { } owner) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Import saved queries into '{owner}'",
            Filter = "Saved queries (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var added = _queryStore.Import(dialog.FileName, owner);
            foreach (var q in added) SavedQueries.Add(q);
            SortSavedQueries();
            Report(added.Count == 0
                    ? "The file holds no queries."
                    : $"Imported {added.Count} quer{(added.Count == 1 ? "y" : "ies")} into '{owner}'.",
                added.Count == 0 ? StatusKind.Idle : StatusKind.Ok);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Report($"Import failed: {ex.Message}", StatusKind.Error);
        }
    }

    // ---- Deleting a connection ----

    /// <summary>
    /// Before a profile is deleted with its queries: when it has any, ask whether to export them.
    /// Yes exports (cancelling the file dialog cancels the delete), No deletes them unexported.
    /// Returns false when the delete should not go ahead.
    /// </summary>
    private bool ConfirmDeletingConnectionQueries(string connection, out string note)
    {
        note = "";
        var queries = _queryStore?.ForConnection(connection) ?? [];
        if (queries.Count == 0) return true;

        var answer = MessageBox.Show(
            $"'{connection}' has {queries.Count} saved quer{(queries.Count == 1 ? "y" : "ies")}, " +
            "which are deleted with it." + Environment.NewLine + Environment.NewLine +
            "Export them to a file first? They can be imported into another connection later.",
            "Delete connection", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;

        var path = AskExportPath(connection);
        if (path is null) return false;
        try
        {
            _queryStore!.Export(path, queries);
            note = $" The queries were exported to {path}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Export failed, so nothing was deleted: {ex.Message}", StatusKind.Error);
            return false;
        }
    }

    private int DeleteConnectionQueries(string connection)
    {
        if (_queryStore is null) return 0;
        try
        {
            var removed = _queryStore.DeleteConnection(connection);
            // The open query belonged to that connection: keep its text, as an untitled query.
            if (_openQuery is not null && SavedQueryStoreNames.Equals(_openQuery.Connection, connection))
                _openQuery = null;
            return removed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Report($"The connection was deleted, but its saved queries were not: {ex.Message}", StatusKind.Error);
            return 0;
        }
    }

    private static readonly StringComparer SavedQueryStoreNames = StringComparer.OrdinalIgnoreCase;

    private static string? AskExportPath(string connection)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = $"Export the saved queries of '{connection}'",
            FileName = $"{SafeFileName(connection)}-queries-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "Saved queries (*.json)|*.json",
            DefaultExt = ".json"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // ---- Helpers ----

    private int IndexOfSavedQuery(Guid id)
    {
        for (var i = 0; i < SavedQueries.Count; i++)
            if (SavedQueries[i].Id == id) return i;
        return -1;
    }

    /// <summary>Folder, then name — the order the store returns and the grouping expects.</summary>
    private void SortSavedQueries()
    {
        var sorted = SavedQueries
            .OrderBy(q => q.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var at = SavedQueries.IndexOf(sorted[i]);
            if (at != i) SavedQueries.Move(at, i);
        }
    }

    private static List<string> SplitTags(string tags) =>
        SavedQueryStore.NormalizeTags(tags.Split(',', ';'));

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
