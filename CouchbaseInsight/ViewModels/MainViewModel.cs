using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        ClassesView.Filter = o => Matches((o as ClassEntry)?.Name, ClassFilter);

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
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
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
    public ObservableCollection<ClassEntry> Classes { get; } = new();
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
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string? _selectedCollection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _discriminator = "class";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string? _selectedClass;

    /// <summary>The row selected in the class list; <see cref="SelectedClass"/> is its name.</summary>
    [ObservableProperty] private ClassEntry? _selectedClassEntry;

    partial void OnSelectedClassEntryChanged(ClassEntry? value)
    {
        SelectedClass = value?.Name;
        if (IsOffline && value is not null) ShowOfflineSchema(value.Name);
    }

    /// <summary>What the class percentages are of: documents read, and how many had no class.</summary>
    [ObservableProperty] private string _classesCaption = "";

    [ObservableProperty] private FieldInfo? _selectedField;
    [ObservableProperty] private string _distinctValuesHeader = "Distinct values";
    [ObservableProperty] private bool _analyzeAll;

    // ---- Scan limit ----
    // How many documents Load classes, Analyze and distinct values read: the collection's first N
    // in primary-key order. On a collection with only a primary index, an unbounded query fetches
    // every document to test its WHERE, and a schema does not need all of them.

    public const int DefaultScanLimit = 50_000;

    /// <summary>Documents to read per query; empty or 0 means the whole collection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanLimitSummary))]
    private string _scanLimit = DefaultScanLimit.ToString(CultureInfo.InvariantCulture);

    public string ScanLimitSummary => TryScanLimit(out var n)
        ? n is null
            ? "documents: the whole collection is read"
            : $"documents read per query (first {n:N0} by key; 0 or empty = whole collection)"
        : "is not a whole number";

    private static bool TryParseScanLimit(string text, out int? limit)
    {
        limit = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!int.TryParse(text.Replace(",", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ||
            n < 0)
            return false;
        limit = n == 0 ? null : n;
        return true;
    }

    private bool TryScanLimit(out int? limit) => TryParseScanLimit(ScanLimit, out limit);

    /// <summary>The limit to send, or null for the whole collection. Throws on text that does not
    /// parse, so a typo is reported instead of silently reading everything.</summary>
    private int? ScanLimitValue() =>
        TryScanLimit(out var n)
            ? n
            : throw new FormatException($"Scan limit: '{ScanLimit}' is not a whole number.");

    private int? ScanLimitValueOrNull() => TryScanLimit(out var n) ? n : null;

    private static string Scanned(int? scanLimit) =>
        scanLimit is null ? "" : $" in the first {scanLimit:N0} documents";

    // ---- Whole-collection export ----
    // Reads every document of the collection in key-ordered batches and writes one JSON summary:
    // classes with counts and %, each class's min/max date, and each field's types, presence,
    // requirement, flags and distinct values. After each batch the running summary is saved, so an
    // export that stops for any reason resumes from the last finished batch.

    [ObservableProperty] private string _exportDateField = "createdts";

    // ---- Collection summary ----
    // The document count, shown as soon as a collection is picked. It comes from the collection's
    // item count rather than a scan, so it is instant on any collection.

    [ObservableProperty] private string _collectionSummary = "";

    private CancellationTokenSource? _summaryCts;

    private async Task LoadCollectionSummaryAsync()
    {
        _summaryCts?.Cancel();
        _summaryCts?.Dispose();
        var cts = _summaryCts = new CancellationTokenSource();

        if (!IsConnected || SelectedBucket is null || SelectedScope is null || SelectedCollection is null)
        {
            CollectionSummary = "";
            return;
        }

        try
        {
            CollectionSummary = "Counting documents…";
            var count = await _couchbase.CountDocumentsAsync(SelectedBucket, SelectedScope.Name, SelectedCollection);
            if (!cts.IsCancellationRequested) CollectionSummary = $"{count:N0} documents";
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                CollectionSummary = $"Could not count documents: {ExceptionReport.Summarize(ex)}";
        }
    }

    // ---- Options ----

    private AppOptions _options = AppOptions.Load();

    [RelayCommand]
    private void OpenOptions()
    {
        var window = new OptionsWindow(_options) { Owner = System.Windows.Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        try
        {
            window.Options.Save();
            _options = window.Options;
            Report($"Options saved: whole-collection reads go in batches of {_options.BatchSize:N0} documents.",
                   StatusKind.Ok);
        }
        catch (Exception ex)
        {
            Report($"Could not save the options: {ex.Message}", StatusKind.Error);
        }
    }

    // ---- Batched whole-collection reads ----
    // Load classes with no scan limit, and Analyze with "Read every document" and no scan limit,
    // read the collection in batches of the Options batch size instead of in one query: each batch
    // is the key range (after, end], so the status line can say how far along it is, the class
    // list fills in as it goes, and Cancel stops it between or inside batches, keeping what was
    // counted so far.

    /// <summary>True while a guarded operation has been running for more than a moment: the
    /// activity line under the picker shows what it is doing and for how long.</summary>
    [ObservableProperty] private bool _isActivityVisible;

    /// <summary>The live activity line: what is running, its progress, and the elapsed time.</summary>
    [ObservableProperty] private string _activityLine = "";

    /// <summary>Whether the running operation stops on Cancel (Load classes, Analyze).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelActivityCommand))]
    private bool _isActivityCancellable;

    private CancellationTokenSource? _activityCts;
    private string _activityText = "";
    private Stopwatch? _activityClock;
    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    /// <summary>Quick operations finish before this and never flash the activity line.</summary>
    private static readonly TimeSpan ShowActivityAfter = TimeSpan.FromMilliseconds(600);

    [RelayCommand(CanExecute = nameof(IsActivityCancellable))]
    private void CancelActivity() => _activityCts?.Cancel();

    /// <summary>Sets what the activity line says; the elapsed time is appended to it.</summary>
    private void SetActivity(string text)
    {
        _activityText = text;
        RenderActivity();
    }

    private void RenderActivity()
    {
        if (_activityClock is null) return;
        var e = _activityClock.Elapsed;
        var line = $"{_activityText} · {(e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"m\:ss"))}";
        ActivityLine = line;
        Report(line, StatusKind.Busy);
    }

    /// <summary>
    /// Walks the collection batch by batch: finds where each batch ends (keys only), hands the
    /// range to <paramref name="readBatch"/>, and reports progress before and after each one.
    /// <paramref name="readBatch"/> returns a short tail for the progress line ("· 12 classes so far").
    /// </summary>
    private async Task RunBatchesAsync(
        string bucket, string scope, string collection, string label,
        Func<string, string?, Task<string>> readBatch, CancellationToken ct)
    {
        var total = await _couchbase.CountDocumentsAsync(bucket, scope, collection);
        var size = _options.BatchSize;
        var batches = Math.Max(1, (int)Math.Ceiling(total / (double)size));
        var clock = Stopwatch.StartNew();
        var after = "";
        var tail = "";

        for (var n = 1; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            ShowBatchProgress(label, Math.Min(total, (long)(n - 1) * size), total, n, batches, clock.Elapsed, tail);

            var end = await _couchbase.GetBatchEndKeyAsync(bucket, scope, collection, after, size, ct);
            tail = await readBatch(after, end);
            if (end is null) break;
            after = end;
        }

        ShowBatchProgress(label, total, total, batches, batches, clock.Elapsed, tail);
    }

    private void ShowBatchProgress(
        string label, long done, long total, int batch, int batches, TimeSpan elapsed, string tail)
    {
        var percent = total == 0 ? 100 : done * 100.0 / total;
        var rate = elapsed.TotalSeconds >= 1 ? done / elapsed.TotalSeconds : 0;
        var text = $"{label}: {done:N0} / {total:N0} documents ({percent:0}%) · batch {batch} of {batches}";
        if (rate > 0) text += $" · {rate:N0} docs/s";
        if (rate > 0 && done < total)
        {
            var left = TimeSpan.FromSeconds((total - done) / rate);
            text += left.TotalHours >= 1
                ? $" · ~{(int)left.TotalHours}h {left.Minutes}m left"
                : $" · ~{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left";
        }
        SetActivity(text + tail);
    }

    /// <summary>Class counts over the whole collection, batch by batch; the class list shows the
    /// running counts after each batch. On Cancel, returns the counts so far.</summary>
    private async Task<(ClassCounts Counts, bool Cancelled)> CountClassesInBatchesAsync(
        string bucket, string scope, string collection, string discriminator, CancellationToken ct)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long withoutClass = 0;

        ClassCounts Snapshot()
        {
            var read = totals.Values.Sum() + withoutClass;
            var entries = totals
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new ClassEntry(kv.Key, kv.Value, read == 0 ? 0 : kv.Value * 100.0 / read))
                .ToList();
            return new ClassCounts(entries, read, withoutClass);
        }

        try
        {
            await RunBatchesAsync(bucket, scope, collection, "Loading classes", async (after, end) =>
            {
                var rows = await _couchbase.CountClassesInRangeAsync(
                    bucket, scope, collection, discriminator, after, end, ct);
                foreach (var (cls, n) in rows)
                {
                    if (cls is null) withoutClass += n;
                    else totals[cls] = totals.GetValueOrDefault(cls) + n;
                }

                var snapshot = Snapshot();
                Classes.Clear();
                foreach (var c in snapshot.Classes) Classes.Add(c);
                return $" · {totals.Count} class(es) so far";
            }, ct);
            return (Snapshot(), false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return (Snapshot(), true);
        }
    }

    /// <summary>Every document of one class, batch by batch. On Cancel, returns the schema of the
    /// documents read so far.</summary>
    private async Task<(ClassSchema Schema, bool Cancelled)> AnalyzeInBatchesAsync(
        string bucket, string scope, string collection, string discriminator, string className,
        CancellationToken ct)
    {
        var analyzer = new SchemaAnalyzer();
        try
        {
            await RunBatchesAsync(bucket, scope, collection, $"Analyzing '{className}'", async (after, end) =>
            {
                // Off the UI thread: a batch can hold a million documents to walk.
                await Task.Run(async () =>
                {
                    await foreach (var doc in _couchbase.ReadClassInRangeAsync(
                                       bucket, scope, collection, discriminator, className, after, end, ct))
                        analyzer.Add(doc);
                }, ct);
                return $" · {analyzer.SampleSize:N0} '{className}' document(s) so far";
            }, ct);
            return (analyzer.Build(className), false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return (analyzer.Build(className), true);
        }
    }

    // ---- Export (continued) ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isExporting;

    [ObservableProperty] private string _exportProgress = "";

    private CancellationTokenSource? _exportCts;

    /// <summary>Creation-date names seen across the Sekeh collections, in order of preference.</summary>
    private static readonly string[] DateFieldNames =
    [
        "createdts", "createdTs", "createdAt", "created_at", "createdDate", "createDate",
        "creationDate", "insertDate", "created", "timestamp", "date"
    ];

    private static string? GuessDateField(IEnumerable<string> names)
    {
        var list = names.ToList();
        foreach (var candidate in DateFieldNames)
        {
            var hit = list.FirstOrDefault(n => n.Equals(candidate, StringComparison.Ordinal))
                      ?? list.FirstOrDefault(n => n.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var batchSize = _options.BatchSize;

        var bucket = SelectedBucket!;
        var scope = SelectedScope!.Name;
        var collection = SelectedCollection!;
        var discriminator = Discriminator.Trim();
        var checkpointPath = ExportState.CheckpointPath(bucket, scope, collection, discriminator);

        var checkpoint = ExportState.Load(checkpointPath);

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"{collection}-{discriminator}-{DateTime.Now:yyyyMMdd-HHmm}.json");
        var dialog = new ExportWindow(
            $"{bucket}.{scope}.{collection} · classes by '{discriminator}'" +
            (CollectionSummary.Length > 0 ? $" · {CollectionSummary}" : ""),
            DiscriminatorFields, ExportDateField, defaultPath, batchSize, checkpoint)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        ExportState state;
        if (dialog.Resume)
        {
            state = checkpoint!;
        }
        else
        {
            state = new ExportState
            {
                Bucket = bucket,
                Scope = scope,
                Collection = collection,
                Discriminator = discriminator,
                DateField = dialog.DateField,
                BatchSize = batchSize,
                OutputPath = dialog.OutputPath,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            state.Save(checkpointPath);
        }

        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;
        IsExporting = true;
        Report($"Exporting '{collection}' to {state.OutputPath}…", StatusKind.Busy);
        ExportProgress = "Counting documents…";
        var progress = new Progress<string>(text => ExportProgress = text);

        try
        {
            var total = await _couchbase.CountDocumentsAsync(bucket, scope, collection);

            // Off the UI thread: millions of rows each resuming on the dispatcher would freeze the window.
            await Task.Run(() => RunExportAsync(state, checkpointPath, total, progress, ct), ct);

            ExportProgress = "Writing the report…";
            var countAtEnd = await _couchbase.CountDocumentsAsync(bucket, scope, collection);
            var json = state.BuildReport(countAtEnd).ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                // Persian class names and values stay readable instead of \uXXXX escapes.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            await File.WriteAllTextAsync(state.OutputPath, json, ct);
            File.Delete(checkpointPath);

            Report($"Exported '{collection}': {state.DocumentsRead:N0} documents, " +
                   $"{state.Classes.Count} classes → {state.OutputPath}", StatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            var saved = ExportState.Load(checkpointPath)?.DocumentsRead ?? 0;
            Report($"Export cancelled. {saved:N0} documents are saved; Export… again resumes it.",
                   StatusKind.Idle);
        }
        catch (Exception ex)
        {
            var saved = ExportState.Load(checkpointPath)?.DocumentsRead ?? 0;
            Report($"Export stopped by an error ({ExceptionReport.Summarize(ex)}). {saved:N0} documents " +
                   "are saved; Export… again resumes it.", StatusKind.Error);
            Diagnostics = ExceptionReport.Describe(ex);
        }
        finally
        {
            IsExporting = false;
            _exportCts.Dispose();
            _exportCts = null;
        }
    }

    private async Task RunExportAsync(
        ExportState state, string checkpointPath, long total, IProgress<string> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var resumedAt = state.DocumentsRead;

        while (true)
        {
            var inBatch = 0;
            var lastKey = state.LastKey;

            await foreach (var (key, document) in _couchbase.ReadPageAsync(
                               state.Bucket, state.Scope, state.Collection, state.LastKey, state.BatchSize, ct))
            {
                state.Add(document);
                lastKey = key;
                if (++inBatch % 10_000 == 0)
                    progress.Report(ProgressText(state, total, state.DocumentsRead - resumedAt, clock.Elapsed));
            }

            ct.ThrowIfCancellationRequested();

            // Only a finished batch is saved. A batch cut off part-way is not, so the counts on
            // disk always describe exactly the documents up to LastKey.
            state.LastKey = lastKey;
            state.BatchesDone++;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            state.Save(checkpointPath);
            progress.Report(ProgressText(state, total, state.DocumentsRead - resumedAt, clock.Elapsed));

            if (inBatch < state.BatchSize) break;   // a short batch is the last one
        }
    }

    private static string ProgressText(ExportState state, long total, long readThisRun, TimeSpan elapsed)
    {
        var rate = elapsed.TotalSeconds >= 1 ? readThisRun / elapsed.TotalSeconds : 0;
        var percent = total > 0 ? Math.Min(100, state.DocumentsRead * 100.0 / total) : 0;
        var text = $"{state.DocumentsRead:N0} / {total:N0} documents ({percent:0.0}%) · " +
                   $"batch {state.BatchesDone + 1} · {rate:N0} docs/s";
        if (rate > 0 && total > state.DocumentsRead)
        {
            var left = TimeSpan.FromSeconds((total - state.DocumentsRead) / rate);
            text += left.TotalHours >= 1
                ? $" · ~{(int)left.TotalHours}h {left.Minutes}m left"
                : $" · ~{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left";
        }
        return text;
    }

    private bool CanExport() =>
        IsConnected && !IsExporting &&
        SelectedScope is not null &&
        !string.IsNullOrWhiteSpace(SelectedCollection) &&
        !string.IsNullOrWhiteSpace(Discriminator);

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void CancelExport() => _exportCts?.Cancel();

    // ---- Opened export file ----
    // Shows a saved export in the Schema explorer instead of the cluster: the class list with the
    // export's whole-collection counts, each class's schema, and each field's saved distinct
    // values. Nothing is queried, so it works without a connection. Load classes, or picking
    // another bucket, scope or collection, goes back to the cluster.

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseExportFileCommand))]
    private bool _isOffline;

    /// <summary>"From file: …" — shown above the class list while a file is open.</summary>
    [ObservableProperty] private string _offlineSource = "";

    private readonly Dictionary<string, JsonObject> _offlineClasses = new(StringComparer.Ordinal);

    [RelayCommand]
    private void OpenExportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a collection export",
            Filter = "Collection export (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchbaseInsight", "exports")
        };
        if (!Directory.Exists(dialog.InitialDirectory)) dialog.InitialDirectory = "";
        if (dialog.ShowDialog() != true) return;

        try
        {
            var (report, unfinished) = ReadExportFile(dialog.FileName);
            ShowExport(report, Path.GetFileName(dialog.FileName), unfinished);
        }
        catch (Exception ex)
        {
            Report($"Could not open '{Path.GetFileName(dialog.FileName)}': {ExceptionReport.Summarize(ex)}",
                   StatusKind.Error);
            Diagnostics = ExceptionReport.Describe(ex);
        }
    }

    /// <summary>
    /// A finished export, or the checkpoint of an unfinished one (from the exports folder): the
    /// checkpoint is turned into the same report, covering the documents read so far.
    /// </summary>
    private static (JsonObject Report, bool Unfinished) ReadExportFile(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("The file is not a JSON object.");

        if (root["classes"] is JsonArray)
            return (root, false);

        if (root["Classes"] is JsonObject && root["Bucket"] is not null)
        {
            var state = ExportState.Load(path)
                        ?? throw new InvalidDataException("The checkpoint could not be read.");
            // Round-trip so its numbers read like a saved file's (element-backed, not CLR longs).
            var built = state.BuildReport(state.DocumentsRead).ToJsonString();
            return ((JsonObject)JsonNode.Parse(built)!, true);
        }

        throw new InvalidDataException("This is not a collection export: it has no \"classes\" list.");
    }

    private void ShowExport(JsonObject report, string fileName, bool unfinished)
    {
        CloseExportFile();
        Classes.Clear();
        SelectedClassEntry = null;
        ClearSchema();

        foreach (var node in report["classes"]!.AsArray())
        {
            if (node is not JsonObject cls) continue;
            var name = cls["class"]?.ToString() ?? "";
            _offlineClasses[name] = cls;
        }

        foreach (var (name, cls) in _offlineClasses.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            Classes.Add(new ClassEntry(
                name,
                Long(cls["count"]),
                Double(cls["percent"]),
                DescribeDateRange(cls["dateRange"] as JsonObject, report["dateField"]?.ToString())));

        var docs = Long(report["documentsRead"]);
        var keyspace = $"{report["bucket"]}.{report["scope"]}.{report["collection"]}";
        var when = report["finishedAt"]?.ToString() is { Length: >= 16 } f ? f[..16].Replace('T', ' ') + " UTC" : "";

        IsOffline = true;
        OfflineSource = unfinished
            ? $"From file: {fileName} · unfinished export, {docs:N0} documents so far"
            : $"From file: {fileName} · exported {when}";
        ClassesCaption = $"{keyspace} by '{report["discriminator"]}' · % of {docs:N0} documents (whole collection" +
                         (unfinished ? ", so far)" : ")");
        Report($"Opened {fileName}: {Classes.Count} class(es), {docs:N0} documents. " +
               "Load classes goes back to the cluster.", StatusKind.Ok);
    }

    private void ShowOfflineSchema(string className)
    {
        ClearSchema();
        if (!_offlineClasses.TryGetValue(className, out var cls)) return;

        var classCount = Long(cls["count"]);
        foreach (var node in cls["fields"]?.AsArray() ?? [])
        {
            if (node is not JsonObject f) continue;
            var flags = f["flags"]?.AsArray().Select(x => x?.ToString()).ToList() ?? [];
            Fields.Add(new FieldInfo
            {
                Name = f["name"]?.ToString() ?? "",
                Types = string.Join(", ", f["types"]?.AsArray().Select(t => t?.ToString()) ?? []),
                Occurrences = (int)Math.Min(int.MaxValue, Long(f["seen"])),
                SampleSize = (int)Math.Min(int.MaxValue, classCount),
                HasCaseDuplicate = flags.Contains("Case conflict")
            });
        }

        AnalyzedClass = className;
        SampleSize = (int)Math.Min(int.MaxValue, classCount);
        FieldCount = Fields.Count;
        RequiredCount = Fields.Count(x => x.IsRequired);
        IssueCount = Fields.Count(x => x.HasCaseDuplicate || x.HasMultipleTypes);
        Report($"'{className}' from file: {classCount:N0} document(s), {FieldCount} field(s).", StatusKind.Ok);
    }

    private void ShowOfflineDistinctValues(FieldInfo? field)
    {
        if (field is null || SelectedClass is null || !_offlineClasses.TryGetValue(SelectedClass, out var cls))
        {
            DistinctValuesHeader = "Distinct values";
            return;
        }

        var saved = cls["fields"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(f => f["name"]?.ToString() == field.Name);
        if (saved?["distinct"] is not JsonObject distinct)
        {
            DistinctValuesHeader = $"No distinct values saved for '{field.Name}' (objects and arrays have none)";
            return;
        }

        var values = distinct["values"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        foreach (var v in values)
        {
            var value = v["value"] switch
            {
                null => "(null)",
                JsonValue jv when jv.TryGetValue<string>(out var str) => str,
                var other => other.ToJsonString()
            };
            DistinctValues.Add($"{value}    ×{Long(v["count"]):N0}");
        }

        var complete = distinct["complete"]?.GetValue<bool>() ?? false;
        var total = distinct["count"] is { } c ? $"{Long(c):N0}" : $"more than {Long(distinct["moreThan"]):N0}";
        DistinctValuesHeader = complete
            ? $"All {values.Count} distinct values of '{field.Name}' (from file)"
            : $"Top {values.Count} of {total} distinct values of '{field.Name}' (from file)";
    }

    [RelayCommand(CanExecute = nameof(IsOffline))]
    private void CloseExportFile()
    {
        if (!IsOffline) return;
        IsOffline = false;
        OfflineSource = "";
        _offlineClasses.Clear();
        Classes.Clear();
        SelectedClassEntry = null;
        ClassesCaption = "";
        ClearSchema();
    }

    private static string? DescribeDateRange(JsonObject? range, string? field)
    {
        if (range is null) return null;
        var min = range["minUtc"] ?? range["min"];
        var max = range["maxUtc"] ?? range["max"];
        if (min is null || max is null) return $"{field}: not present";
        var missing = Long(range["missing"]);
        return $"{field}: {min} … {max}" + (missing > 0 ? $" ({missing:N0} without it)" : "");
    }

    private static long Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : 0;

    private static double Double(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

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
        CloseExportFile();
        var loadingText = ScanLimitValueOrNull() is { } limitText
            ? $"Loading classes from the first {limitText:N0} documents…"
            : "Loading classes…";
        await RunGuarded(loadingText, async ct =>
        {
            Classes.Clear();
            SelectedClassEntry = null;
            ClassesCaption = "";
            ClearSchema();

            var scan = ScanLimitValue();
            ClassCounts counts;
            var cancelled = false;
            if (scan is null)
                (counts, cancelled) = await CountClassesInBatchesAsync(
                    SelectedBucket!, SelectedScope!.Name, SelectedCollection!, Discriminator, ct);
            else
                counts = await _couchbase.GetClassesAsync(
                    SelectedBucket!, SelectedScope!.Name, SelectedCollection!, Discriminator, scan, ct);

            var classes = counts.Classes;
            Classes.Clear();
            foreach (var c in classes) Classes.Add(c);

            ClassesCaption =
                $"% of {counts.DocumentsRead:N0} documents read" +
                (scan is not null && counts.DocumentsRead >= scan ? " (scan limit)" : "") +
                (counts.WithoutClass > 0 ? $" · {counts.WithoutClass:N0} without '{Discriminator}'" : "");

            if (classes.Count == 0)
                Report($"No values found for discriminator '{Discriminator}'{Scanned(scan)}. " +
                       (scan is null ? "Try another field." : "Try another field or a larger scan limit."),
                       StatusKind.Error);
            else
                Report($"{classes.Count} class(es) found{Scanned(scan)}.", StatusKind.Ok);

            if (cancelled)
            {
                ClassesCaption = $"Cancelled: % of the first {counts.DocumentsRead:N0} documents only";
                Report($"Load classes cancelled after {counts.DocumentsRead:N0} documents; " +
                       "the counts shown cover those only.", StatusKind.Idle);
            }
        }, cancellable: true);
    }

    private bool CanLoadClasses() =>
        IsConnected && !IsBusy &&
        SelectedScope is not null &&
        !string.IsNullOrWhiteSpace(SelectedCollection) &&
        !string.IsNullOrWhiteSpace(Discriminator);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (IsOffline)
        {
            ShowOfflineSchema(SelectedClass!);
            return;
        }

        await RunGuarded($"Analyzing '{SelectedClass}'…", async ct =>
        {
            ClearSchema();

            int? limit = AnalyzeAll ? null : 1000;
            var scan = ScanLimitValue();
            ClassSchema schema;
            var cancelled = false;
            if (AnalyzeAll && scan is null)
                (schema, cancelled) = await AnalyzeInBatchesAsync(
                    SelectedBucket!, SelectedScope!.Name, SelectedCollection!, Discriminator, SelectedClass!, ct);
            else
            {
                var cls = SelectedClass!;
                var of = limit is null ? "" : $" of up to {limit:N0}";
                var progress = new Progress<int>(n => SetActivity($"Analyzing '{cls}': {n:N0}{of} document(s) read"));
                schema = await _couchbase.AnalyzeClassAsync(
                    SelectedBucket!, SelectedScope!.Name, SelectedCollection!,
                    Discriminator, cls, limit, scan, progress, ct);
            }

            foreach (var f in schema.Fields) Fields.Add(f);

            AnalyzedClass = schema.ClassName;
            SampleSize = schema.SampleSize;
            FieldCount = schema.Fields.Count;
            RequiredCount = schema.Fields.Count(f => f.IsRequired);
            IssueCount = schema.Fields.Count(f => f.HasCaseDuplicate || f.HasMultipleTypes);

            if (cancelled)
                Report($"Analyze cancelled: the schema shown covers the {schema.SampleSize:N0} " +
                       $"'{schema.ClassName}' document(s) read so far.", StatusKind.Idle);
            else
                Report($"Analyzed '{schema.ClassName}' over {schema.SampleSize:N0} document(s){Scanned(scan)}.",
                       StatusKind.Ok);
        }, cancellable: true);
    }

    private bool CanAnalyze() =>
        (IsConnected || IsOffline) && !IsBusy && !string.IsNullOrWhiteSpace(SelectedClass);

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
        CloseExportFile();
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
        CloseExportFile();
        Collections.Clear();
        SelectedCollection = null;
        Classes.Clear();
        ClearSchema();

        if (value is null) return;
        foreach (var c in value.Collections) Collections.Add(c);
    }

    partial void OnSelectedCollectionChanged(string? value)
    {
        CloseExportFile();
        // The discriminator is a property of the collection — offer the collection's top-level
        // fields as choices. (Discriminator must be a single top-level identifier; see
        // CouchbaseService.GetClassesAsync.)
        DiscriminatorFields.Clear();
        _summaryCts?.Cancel();
        CollectionSummary = "";
        if (value is null) return;

        _ = RunGuarded("Loading fields…", async () =>
        {
            var fields = await _couchbase.GetCollectionFieldsAsync(
                SelectedBucket!, SelectedScope!.Name, value);
            var topLevel = fields
                .Where(f => !f.Name.Contains('.'))
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var f in topLevel) DiscriminatorFields.Add(f.Name);
            ExportDateField = GuessDateField(topLevel.Select(f => f.Name)) ?? "createdts";
            _ = LoadCollectionSummaryAsync();
            Report($"{DiscriminatorFields.Count} field(s) available as discriminator.", StatusKind.Ok);
        });
    }

    partial void OnSelectedFieldChanged(FieldInfo? value)
    {
        CancelDistinctValues();
        DistinctValues.Clear();

        if (IsOffline)
        {
            ShowOfflineDistinctValues(value);
            return;
        }

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
            var scan = ScanLimitValue();
            var values = await _couchbase.GetDistinctValuesAsync(
                SelectedBucket!, SelectedScope!.Name, SelectedCollection!,
                Discriminator, SelectedClass!, fieldPath, 100, scan, ct);

            if (ct.IsCancellationRequested) return;

            DistinctValues.Clear();
            foreach (var v in values) DistinctValues.Add(v);

            DistinctValuesHeader = values.Count == 0
                ? $"No values found for '{fieldPath}'"
                : $"Top {values.Count} distinct values of '{fieldPath}'{Scanned(scan)}";
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
        CloseExportFile();
        Buckets.Clear();
        Scopes.Clear();
        Collections.Clear();
        Classes.Clear();
        ClearSchema();
        SelectedBucket = null;
        SelectedScope = null;
        SelectedCollection = null;
        SelectedClassEntry = null;
        ClassesCaption = "";
    }

    private Task RunGuarded(string busyMessage, Func<Task> action) =>
        RunGuarded(busyMessage, _ => action(), cancellable: false);

    /// <summary>
    /// Runs one operation at a time. After a moment the activity line under the picker shows what
    /// it is doing (updated through <see cref="SetActivity"/>) and the elapsed time, so a long query
    /// never looks like nothing is happening; <paramref name="cancellable"/> adds a Cancel button
    /// that cancels the token handed to <paramref name="action"/>.
    /// </summary>
    private async Task RunGuarded(string busyMessage, Func<CancellationToken, Task> action, bool cancellable)
    {
        if (IsBusy) return;
        IsBusy = true;
        Report(busyMessage, StatusKind.Busy);

        _activityCts = new CancellationTokenSource();
        var ct = _activityCts.Token;
        IsActivityCancellable = cancellable;
        _activityText = busyMessage;
        _activityClock = Stopwatch.StartNew();
        var clock = _activityClock;
        _activityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _activityTimer.Tick += (_, _) =>
        {
            if (clock.Elapsed < ShowActivityAfter) return;
            IsActivityVisible = true;
            RenderActivity();
        };
        _activityTimer.Start();

        try
        {
            await action(ct);

            // An action that said nothing about how it went reports success on its behalf.
            if (StatusLevel == StatusKind.Busy) StatusLevel = StatusKind.Ok;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Report("Cancelled.", StatusKind.Idle);
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
            _activityTimer?.Stop();
            _activityTimer = null;
            _activityClock = null;
            IsActivityVisible = false;
            IsActivityCancellable = false;
            ActivityLine = "";
            _activityCts?.Dispose();
            _activityCts = null;
            IsBusy = false;
        }
    }
}
