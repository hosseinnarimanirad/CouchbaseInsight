using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

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
    [ObservableProperty] private string _distinctValuesHeader = "Distinct Values";
    [ObservableProperty] private bool _analyzeAll;
    [ObservableProperty] private string _schemaSummary = "";

    // ---- Query ----
    [ObservableProperty] private string _selectExpression = "*";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    private string _fromKeyspace = "";

    [ObservableProperty] private string _whereClause = "";
    [ObservableProperty] private string _queryResult = "";
    [ObservableProperty] private string _queryResultCountLabel = "";

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
        Status = $"Saved connection '{name}'.";
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
        Status = $"Deleted connection '{profile.Name}'.";
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

            Status = $"Connected. {Buckets.Count} bucket(s) found.";
        });
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
            Status = "Disconnected.";
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

            Status = classes.Count == 0
                ? $"No values found for discriminator '{Discriminator}'. Try another field."
                : $"{classes.Count} class(es) found.";
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

            var required = schema.Fields.Count(f => f.IsRequired);
            var caseDups = schema.Fields.Count(f => f.HasCaseDuplicate);
            var multiType = schema.Fields.Count(f => f.HasMultipleTypes);

            var summary = $"'{schema.ClassName}': {schema.Fields.Count} field(s) " +
                          $"({required} required, {schema.Fields.Count - required} optional) " +
                          $"from {schema.SampleSize} doc(s).";

            if (caseDups > 0) summary += $" | {caseDups} case conflict(s)";
            if (multiType > 0) summary += $" | {multiType} multi-type field(s)";

            SchemaSummary = summary;
            Status = "Analysis complete.";
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
            Status = $"Synced — {QueryFields.Count} field(s) discovered.";
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
        Status = "Running query…";
        QueryResult = "";
        QueryResultCountLabel = "";
        try
        {
            var (count, json) = await _couchbase.RunQueryAsync(BuildQuery(), ct);
            QueryResult = json;
            QueryResultCountLabel = $"Count: {count}";
            Status = $"Query complete — {count} row(s).";
        }
        catch (OperationCanceledException)
        {
            Status = "Query cancelled.";
        }
        catch (Exception ex)
        {
            QueryResult = $"Error:\n{ex.Message}";
            Status = "Query failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunQuery() =>
        IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(FromKeyspace);

    private string BuildQuery()
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
            Status = $"{Scopes.Count} scope(s) in '{value}'.";
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
            Status = $"{DiscriminatorFields.Count} field(s) available as discriminator.";
        });
    }

    partial void OnSelectedFieldChanged(FieldInfo? value)
    {
        CancelDistinctValues();
        DistinctValues.Clear();

        if (value is null || SelectedBucket is null || SelectedScope is null ||
            SelectedCollection is null || SelectedClass is null)
        {
            DistinctValuesHeader = "Distinct Values";
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

    private void ClearSchema()
    {
        CancelDistinctValues();
        Fields.Clear();
        DistinctValues.Clear();
        SelectedField = null;
        SchemaSummary = "";
        DistinctValuesHeader = "Distinct Values";
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
        Status = busyMessage;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Status = $"Error: {ExceptionReport.Summarize(ex)}";
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
