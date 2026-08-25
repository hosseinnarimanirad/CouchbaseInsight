using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

/// <summary>
/// What the status line is reporting. Drives the colour of the dot in front of it — the one thing
/// on the window that says "the last thing you asked for worked" without being read.
/// </summary>
public enum StatusKind
{
    Idle,
    Busy,
    Ok,
    Error
}

public partial class MainViewModel : ObservableObject
{
    private readonly ICouchbaseService _couchbase;
    private readonly IConnectionStore _store;
    private CancellationTokenSource? _distinctValuesCts;

    public MainViewModel(
        ICouchbaseService couchbase, IConnectionStore store, IEnumerable<ConnectionProfile> profiles)
    {
        _couchbase = couchbase;
        _store = store;

        foreach (var p in profiles) SavedConnections.Add(p);

        // Filtering runs through the default views, so the lists the window binds to and the views
        // filtered here are the same object — there is no second copy to keep in step.
        ClassesView = CollectionViewSource.GetDefaultView(Classes);
        ClassesView.Filter = o => Matches(o as string, ClassFilter);

        FieldsView = CollectionViewSource.GetDefaultView(Fields);
        FieldsView.Filter = o => Matches((o as FieldInfo)?.Name, FieldFilter);

        // Select the first saved profile, which populates the connection fields. When none are
        // saved, the fields stay blank and external-network defaults to false.
        if (SavedConnections.Count > 0)
            SelectedConnection = SavedConnections[0];
    }

    // ---- Saved connections ----
    public ObservableCollection<ConnectionProfile> SavedConnections { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteConnectionCommand))]
    private ConnectionProfile? _selectedConnection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveConnectionCommand))]
    private string _connectionName = "";

    // ---- Connection ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveConnectionCommand))]
    private string _connectionString = "";

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _useNetworkResolutionExternal;

    /// <summary>
    /// Whether the connection form is showing. It is open on launch, because connecting is the
    /// first thing to do, and folds away on a successful connect, because after that it is a form
    /// holding a screenful of space for details nobody is going to read again this session.
    /// </summary>
    [ObservableProperty] private bool _isConnectionPanelOpen = true;

    /// <summary>The one-line "who am I talking to" for the title bar: host, plus how many more
    /// seeds were in the string, plus the bucket count once they are known.</summary>
    [ObservableProperty] private string _connectionSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncFromSelectionCommand))]
    private bool _isConnected;

    [ObservableProperty] private string _status = "Not connected.";
    [ObservableProperty] private StatusKind _statusLevel = StatusKind.Idle;

    /// <summary>
    /// Detail behind <see cref="Status"/>: the connect report after a Connect attempt, or the
    /// unwrapped exception chain after any other failure. The status bar only has room for one
    /// line, and for Couchbase errors that line is rarely the informative one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiagnostics))]
    private string _diagnostics = "";

    public bool HasDiagnostics => !string.IsNullOrWhiteSpace(Diagnostics);

    // ---- Hierarchy ----
    public ObservableCollection<string> Buckets { get; } = new();
    public ObservableCollection<ScopeInfo> Scopes { get; } = new();
    public ObservableCollection<string> Collections { get; } = new();
    public ObservableCollection<string> DiscriminatorFields { get; } = new();
    public ObservableCollection<string> Classes { get; } = new();
    public ObservableCollection<FieldInfo> Fields { get; } = new();
    public ObservableCollection<FieldInfo> QueryFields { get; } = new();
    public ObservableCollection<string> DistinctValues { get; } = new();

    /// <summary>The filtered views the window binds to. Same underlying collections as above.</summary>
    public ICollectionView ClassesView { get; }
    public ICollectionView FieldsView { get; }

    [ObservableProperty] private string _classFilter = "";
    [ObservableProperty] private string _fieldFilter = "";

    partial void OnClassFilterChanged(string value) => ClassesView.Refresh();
    partial void OnFieldFilterChanged(string value) => FieldsView.Refresh();

    private static bool Matches(string? candidate, string filter) =>
        string.IsNullOrWhiteSpace(filter) ||
        (candidate?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncFromSelectionCommand))]
    private string? _selectedBucket;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncFromSelectionCommand))]
    private ScopeInfo? _selectedScope;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncFromSelectionCommand))]
    private string? _selectedCollection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    private string _discriminator = "type";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string? _selectedClass;

    [ObservableProperty] private FieldInfo? _selectedField;
    [ObservableProperty] private string _distinctValuesHeader = "Distinct values";
    [ObservableProperty] private bool _analyzeAll;

    // ---- Schema headline numbers ----
    // One property per number rather than one sentence containing all of them: the pane header
    // shows them as separate pills, and a pill you can colour is a pill you can find.
    [ObservableProperty] private string _analyzedClass = "";
    [ObservableProperty] private int _fieldCount;
    [ObservableProperty] private int _requiredCount;
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private int _sampleSize;

    public bool HasSchema => FieldCount > 0;

    partial void OnFieldCountChanged(int value) => OnPropertyChanged(nameof(HasSchema));

    // ---- Query ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueryPreview))]
    private string _selectExpression = "*";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyPropertyChangedFor(nameof(QueryPreview))]
    private string _fromKeyspace = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueryPreview))]
    private string _whereClause = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyQueryResultCommand))]
    private string _queryResult = "";

    [ObservableProperty] private int _queryRowCount = -1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelQueryCommand))]
    private bool _isQueryRunning;

    /// <summary>
    /// The builder writes a statement; this hands it over to be edited by hand. Everything the
    /// three fields above cannot express — a JOIN, an ORDER BY, a LIMIT, a subquery — lives behind
    /// this switch, and switching it on seeds the editor with whatever the builder had composed so
    /// far rather than with a blank page.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyPropertyChangedFor(nameof(QueryPreview))]
    private bool _isRawQuery;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyPropertyChangedFor(nameof(QueryPreview))]
    private string _rawStatement = "";

    partial void OnIsRawQueryChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(RawStatement))
            RawStatement = ComposedStatement();
    }

    /// <summary>Exactly what Run will send, composed or hand-written.</summary>
    public string QueryPreview => IsRawQuery ? RawStatement : ComposedStatement();

    private CancellationTokenSource? _queryCts;

    // WHERE clause builder
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWhereConditionCommand))]
    private FieldInfo? _pendingWhereField;

    [ObservableProperty] private string _pendingWhereOperator = "=";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWhereConditionCommand))]
    private string _pendingWhereValue = "";

    public IReadOnlyList<string> WhereOperators { get; } =
        ["=", "!=", "<", "<=", ">", ">=", "LIKE", "IS NULL", "IS NOT NULL"];

    public bool WhereValueEnabled =>
        PendingWhereOperator is not "IS NULL" and not "IS NOT NULL";

    partial void OnPendingWhereOperatorChanged(string value)
    {
        OnPropertyChanged(nameof(WhereValueEnabled));
        AddWhereConditionCommand.NotifyCanExecuteChanged();
    }

    // ---- Commands ----

    // Switching the saved-connection dropdown loads its values into the editable fields.
    partial void OnSelectedConnectionChanged(ConnectionProfile? value)
    {
        if (value is null) return;

        ConnectionName = value.Name;
        ConnectionString = value.ConnectionString;
        Username = value.Username;
        Password = value.Password;            // also pushed to the PasswordBox via code-behind
        UseNetworkResolutionExternal = value.UseNetworkResolutionExternal;
    }

    [RelayCommand]
    private void ToggleConnectionPanel() => IsConnectionPanelOpen = !IsConnectionPanelOpen;

    [RelayCommand(CanExecute = nameof(CanSaveConnection))]
    private void SaveConnection()
    {
        var name = ConnectionName.Trim();

        var existing = SavedConnections.FirstOrDefault(
            p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        var profile = existing ?? new ConnectionProfile();
        profile.Name = name;
        profile.ConnectionString = ConnectionString;
        profile.Username = Username;
        profile.Password = Password;
        profile.UseNetworkResolutionExternal = UseNetworkResolutionExternal;

        if (existing is null) SavedConnections.Add(profile);

        _store.Save(SavedConnections);
        SelectedConnection = profile;
        Report($"Saved connection '{name}'.", StatusKind.Ok);
    }

    private bool CanSaveConnection() =>
        !string.IsNullOrWhiteSpace(ConnectionName) &&
        !string.IsNullOrWhiteSpace(ConnectionString);

    [RelayCommand(CanExecute = nameof(CanDeleteConnection))]
    private void DeleteConnection()
    {
        var profile = SelectedConnection!;
        SavedConnections.Remove(profile);
        _store.Save(SavedConnections);
        SelectedConnection = SavedConnections.FirstOrDefault();
        Report($"Deleted connection '{profile.Name}'.", StatusKind.Ok);
    }

    private bool CanDeleteConnection() => SelectedConnection is not null;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        await RunGuarded("Connecting…", async () =>
        {
            ResetHierarchy();
            Diagnostics = await _couchbase.ConnectAsync(new ConnectionSettings
            {
                ConnectionString = ConnectionString,
                Username = Username,
                Password = Password,
                UseNetworkResolutionExternal = UseNetworkResolutionExternal
            });
            IsConnected = true;

            var buckets = await _couchbase.GetBucketsAsync();
            foreach (var b in buckets) Buckets.Add(b);

            ConnectionSummary = DescribeTarget();

            // The form has done its job; the rest of the window is what matters now.
            IsConnectionPanelOpen = false;
            Report($"Connected. {Buckets.Count} bucket(s) found.", StatusKind.Ok);
        });

        // A failed connect is a form to correct, so put the form back.
        if (!IsConnected) IsConnectionPanelOpen = true;
    }

    private bool CanConnect() => !IsBusy && !string.IsNullOrWhiteSpace(ConnectionString);

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await RunGuarded("Disconnecting…", async () =>
        {
            await _couchbase.DisconnectAsync();
            IsConnected = false;
            ResetHierarchy();
            ConnectionSummary = "";
            IsConnectionPanelOpen = true;
            Report("Disconnected.", StatusKind.Idle);
        });
    }

    private bool CanDisconnect() => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoadClasses))]
    private async Task LoadClassesAsync()
    {
        await RunGuarded("Loading classes…", async () =>
        {
            Classes.Clear();
            SelectedClass = null;
            ClearSchema();

            var classes = await _couchbase.GetClassesAsync(
                SelectedBucket!, SelectedScope!.Name, SelectedCollection!, Discriminator);
            foreach (var c in classes) Classes.Add(c);

            if (classes.Count == 0)
                Report($"No values found for discriminator '{Discriminator}'. Try another field.",
                       StatusKind.Error);
            else
                Report($"{classes.Count} class(es) found.", StatusKind.Ok);
        });
    }

    private bool CanLoadClasses() =>
        IsConnected && !IsBusy &&
        SelectedScope is not null &&
        !string.IsNullOrWhiteSpace(SelectedCollection) &&
        !string.IsNullOrWhiteSpace(Discriminator);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        await RunGuarded("Analyzing schema…", async () =>
        {
            ClearSchema();

            int? limit = AnalyzeAll ? null : 1000;
            var schema = await _couchbase.AnalyzeClassAsync(
                SelectedBucket!, SelectedScope!.Name, SelectedCollection!,
                Discriminator, SelectedClass!, limit);

            foreach (var f in schema.Fields) Fields.Add(f);

            AnalyzedClass = schema.ClassName;
            SampleSize = schema.SampleSize;
            FieldCount = schema.Fields.Count;
            RequiredCount = schema.Fields.Count(f => f.IsRequired);
            IssueCount = schema.Fields.Count(f => f.HasCaseDuplicate || f.HasMultipleTypes);

            Report($"Analyzed '{schema.ClassName}' over {schema.SampleSize} document(s).",
                   StatusKind.Ok);
        });
    }

    private bool CanAnalyze() =>
        IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(SelectedClass);

    [RelayCommand(CanExecute = nameof(CanSyncFromSelection))]
    private async Task SyncFromSelectionAsync()
    {
        FromKeyspace =
            $"{Escape(SelectedBucket!)}.{Escape(SelectedScope!.Name)}.{Escape(SelectedCollection!)}";

        if (!string.IsNullOrWhiteSpace(Discriminator) && !string.IsNullOrWhiteSpace(SelectedClass))
            WhereClause = $"{Escape(Discriminator)} = '{SelectedClass}'";
        else if (!string.IsNullOrWhiteSpace(Discriminator))
            WhereClause = $"{Escape(Discriminator)} = ''";

        await RunGuarded("Loading fields…", async () =>
        {
            QueryFields.Clear();
            PendingWhereField = null;
            var fields = await _couchbase.GetCollectionFieldsAsync(
                SelectedBucket!, SelectedScope!.Name, SelectedCollection!);
            foreach (var f in fields) QueryFields.Add(f);
            Report($"Synced — {QueryFields.Count} field(s) discovered.", StatusKind.Ok);
        });
    }

    private bool CanSyncFromSelection() =>
        IsConnected &&
        SelectedBucket is not null &&
        SelectedScope is not null &&
        !string.IsNullOrWhiteSpace(SelectedCollection);

    [RelayCommand(CanExecute = nameof(CanAddWhereCondition))]
    private void AddWhereCondition()
    {
        var field = Escape(PendingWhereField!.Name);
        var condition = PendingWhereOperator is "IS NULL" or "IS NOT NULL"
            ? $"{field} {PendingWhereOperator}"
            : $"{field} {PendingWhereOperator} {FormatValue(PendingWhereValue)}";

        WhereClause = string.IsNullOrWhiteSpace(WhereClause)
            ? condition
            : $"{WhereClause}\n  AND {condition}";

        PendingWhereValue = "";
    }

    private bool CanAddWhereCondition() =>
        PendingWhereField is not null &&
        (PendingWhereOperator is "IS NULL" or "IS NOT NULL" ||
         !string.IsNullOrWhiteSpace(PendingWhereValue));

    [RelayCommand]
    private void ClearWhere() => WhereClause = "";

    [RelayCommand(CanExecute = nameof(CanRunQuery))]
    private async Task RunQueryAsync()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;

        IsBusy = true;
        IsQueryRunning = true;
        Report("Running query…", StatusKind.Busy);
        QueryResult = "";
        QueryRowCount = -1;
        try
        {
            var (count, json) = await _couchbase.RunQueryAsync(QueryPreview, ct);
            QueryResult = json;
            QueryRowCount = count;
            Report($"Query complete — {count} row(s).", StatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            Report("Query cancelled.", StatusKind.Idle);
        }
        catch (Exception ex)
        {
            QueryResult = $"// {ExceptionReport.Summarize(ex)}\n\n{ExceptionReport.Describe(ex)}";
            Report("Query failed.", StatusKind.Error);
        }
        finally
        {
            IsQueryRunning = false;
            IsBusy = false;
        }
    }

    private bool CanRunQuery() =>
        IsConnected && !IsBusy &&
        !string.IsNullOrWhiteSpace(IsRawQuery ? RawStatement : FromKeyspace);

    [RelayCommand(CanExecute = nameof(CanCancelQuery))]
    private void CancelQuery() => _queryCts?.Cancel();

    private bool CanCancelQuery() => IsQueryRunning;

    [RelayCommand(CanExecute = nameof(CanCopyQueryResult))]
    private void CopyQueryResult() => CopyToClipboard(QueryResult, "Result");

    private bool CanCopyQueryResult() => !string.IsNullOrWhiteSpace(QueryResult);

    [RelayCommand]
    private void CopyStatement() => CopyToClipboard(QueryPreview, "Statement");

    [RelayCommand]
    private void CopyDiagnostics() => CopyToClipboard(Diagnostics, "Diagnostics");

    [RelayCommand]
    private void CopyDistinctValues() =>
        CopyToClipboard(string.Join(Environment.NewLine, DistinctValues), "Values");

    private string ComposedStatement()
    {
        var sql = $"SELECT {SelectExpression} FROM {FromKeyspace}";
        if (!string.IsNullOrWhiteSpace(WhereClause))
            sql += $"\nWHERE {WhereClause.Trim()}";
        return sql;
    }

    // ---- Selection reactions ----

    partial void OnSelectedBucketChanged(string? value)
    {
        Scopes.Clear();
        Collections.Clear();
        SelectedScope = null;
        SelectedCollection = null;
        Classes.Clear();
        ClearSchema();

        if (value is null) return;

        _ = RunGuarded("Loading scopes…", async () =>
        {
            var scopes = await _couchbase.GetScopesAsync(value);
            foreach (var s in scopes) Scopes.Add(s);
            ConnectionSummary = DescribeTarget();
            Report($"{Scopes.Count} scope(s) in '{value}'.", StatusKind.Ok);
        });
    }

    partial void OnSelectedScopeChanged(ScopeInfo? value)
    {
        Collections.Clear();
        SelectedCollection = null;
        Classes.Clear();
        ClearSchema();

        if (value is null) return;
        foreach (var c in value.Collections) Collections.Add(c);
    }

    partial void OnSelectedCollectionChanged(string? value)
    {
        // The discriminator is a property of the collection — offer the collection's top-level
        // fields as choices. (Discriminator must be a single top-level identifier; see
        // CouchbaseService.GetClassesAsync.)
        DiscriminatorFields.Clear();
        if (value is null) return;

        _ = RunGuarded("Loading fields…", async () =>
        {
            var fields = await _couchbase.GetCollectionFieldsAsync(
                SelectedBucket!, SelectedScope!.Name, value);
            foreach (var name in fields
                         .Select(f => f.Name)
                         .Where(n => !n.Contains('.'))
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                DiscriminatorFields.Add(name);
            Report($"{DiscriminatorFields.Count} field(s) available as discriminator.", StatusKind.Ok);
        });
    }

    partial void OnSelectedFieldChanged(FieldInfo? value)
    {
        CancelDistinctValues();
        DistinctValues.Clear();

        if (value is null || SelectedBucket is null || SelectedScope is null ||
            SelectedCollection is null || SelectedClass is null)
        {
            DistinctValuesHeader = "Distinct values";
            return;
        }

        var cts = new CancellationTokenSource();
        _distinctValuesCts = cts;
        _ = LoadDistinctValuesAsync(value.Name, cts.Token);
    }

    private async Task LoadDistinctValuesAsync(string fieldPath, CancellationToken ct)
    {
        DistinctValuesHeader = $"Loading '{fieldPath}'…";
        try
        {
            var values = await _couchbase.GetDistinctValuesAsync(
                SelectedBucket!, SelectedScope!.Name, SelectedCollection!,
                Discriminator, SelectedClass!, fieldPath, 100, ct);

            if (ct.IsCancellationRequested) return;

            DistinctValues.Clear();
            foreach (var v in values) DistinctValues.Add(v);

            DistinctValuesHeader = values.Count == 0
                ? $"No values found for '{fieldPath}'"
                : $"Top {values.Count} distinct values of '{fieldPath}'";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                DistinctValuesHeader = $"Error: {ex.Message}";
        }
    }

    // ---- Helpers ----

    private static string Escape(string id) => $"`{id.Replace("`", "``")}`";

    private static string FormatValue(string value)
    {
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            return value;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return value.ToLowerInvariant();
        return $"'{value}'";
    }

    private void Report(string message, StatusKind level)
    {
        Status = message;
        StatusLevel = level;
    }

    private void CopyToClipboard(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
            Report($"{what} copied to the clipboard.", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that is not worth an error dialog.
            Report($"Could not copy: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Host and bucket for the title bar, from the connection string the SDK was given.</summary>
    private string DescribeTarget()
    {
        var seeds = ConnectionStringInspector.Inspect(ConnectionString).Seeds;
        var host = seeds.Count > 0 ? seeds[0].Host : ConnectionString;
        var extra = seeds.Count > 1 ? $" +{seeds.Count - 1}" : "";
        var scope = SelectedBucket is null
            ? $"{Buckets.Count} bucket(s)"
            : SelectedBucket;

        return $"{host}{extra} · {scope}";
    }

    private void ClearSchema()
    {
        CancelDistinctValues();
        Fields.Clear();
        DistinctValues.Clear();
        SelectedField = null;
        AnalyzedClass = "";
        FieldCount = 0;
        RequiredCount = 0;
        IssueCount = 0;
        SampleSize = 0;
        DistinctValuesHeader = "Distinct values";
    }

    private void CancelDistinctValues()
    {
        _distinctValuesCts?.Cancel();
        _distinctValuesCts?.Dispose();
        _distinctValuesCts = null;
    }

    private void ResetHierarchy()
    {
        Buckets.Clear();
        Scopes.Clear();
        Collections.Clear();
        Classes.Clear();
        ClearSchema();
        SelectedBucket = null;
        SelectedScope = null;
        SelectedCollection = null;
        SelectedClass = null;
    }

    private async Task RunGuarded(string busyMessage, Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        Report(busyMessage, StatusKind.Busy);
        try
        {
            await action();

            // An action that said nothing about how it went reports success on its behalf.
            if (StatusLevel == StatusKind.Busy) StatusLevel = StatusKind.Ok;
        }
        catch (Exception ex)
        {
            Report($"Error: {ExceptionReport.Summarize(ex)}", StatusKind.Error);
            Diagnostics = ex is ClusterConnectException connectFailure
                ? connectFailure.Report
                : ExceptionReport.Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
