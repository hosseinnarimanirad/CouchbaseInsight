using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
        ICouchbaseService couchbase, IConnectionStore store, IEnumerable<ConnectionProfile> profiles,
        ISavedQueryStore queryStore)
    {
        _couchbase = couchbase;
        _store = store;
        QueryEditor = new QueryEditorViewModel(couchbase, Buckets, Report);
        QueryEditor.PropertyChanged += OnQueryEditorChanged;
        // Before a connection is selected below: selecting one lists its saved queries.
        InitSavedQueries(queryStore);

        foreach (var p in profiles) SavedConnections.Add(p);

        // Filtering runs through the default views, so the lists the window binds to and the views
        // filtered here are the same object — there is no second copy to keep in step.
        ClassesView = CollectionViewSource.GetDefaultView(Classes);
        ClassesView.Filter = o => Matches((o as ClassEntry)?.Name, ClassFilter);

        FieldsView = CollectionViewSource.GetDefaultView(Fields);
        FieldsView.Filter = o => Matches((o as FieldInfo)?.Name, FieldFilter);

        ApplyScanLimitDefaults();
        _couchbase.GentleMode = _options.GentleMode;

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
    [NotifyCanExecuteChangedFor(nameof(CopyConnectionStringCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(DeepLoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(CountExactlyCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeAllClassesCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeepLoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeAllClassesCommand))]
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
    public ObservableCollection<DistinctValueItem> DistinctValues { get; } = new();

    /// <summary>The documents of the selected class's sample (or a Deep load's oldest/newest).</summary>
    public ObservableCollection<SampleDocumentItem> SampleDocuments { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenValueExampleCommand))]
    private DistinctValueItem? _selectedDistinctValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySampleKeyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSampleInQueryCommand))]
    private SampleDocumentItem? _selectedSampleDocument;

    [ObservableProperty] private string _sampleDocumentsHeader = "Sample documents";

    /// <summary>The collection's first documents by key, shown in the documents pane while no class
    /// is selected.</summary>
    private IReadOnlyList<SampleDocumentItem> _collectionPreview = [];

    public const int PreviewSize = 10;

    private CancellationTokenSource? _previewCts;

    private async Task LoadCollectionPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        var cts = _previewCts = new CancellationTokenSource();
        _collectionPreview = [];
        ShowCollectionPreview();
        if (!IsConnected || SelectedBucket is null || SelectedScope is null || SelectedCollection is null) return;

        var collection = SelectedCollection;
        try
        {
            SampleDocumentsHeader = $"Loading the first {PreviewSize} documents of '{collection}'…";
            var docs = await _couchbase.GetFirstDocumentsAsync(SelectedBucket, SelectedScope.Name, collection, PreviewSize, cts.Token);
            if (cts.IsCancellationRequested) return;
            _collectionPreview = docs.Select(d => new SampleDocumentItem(
                d.Key, d.Document.ValueKind == JsonValueKind.Undefined ? "(not JSON)" : PrettyJson(JsonNode.Parse(d.Document.GetRawText())))).ToList();
            ShowCollectionPreview();
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                SampleDocumentsHeader = $"Could not load the first documents: {ExceptionReport.Summarize(ex)}";
        }
    }

    /// <summary>Shows the collection preview in the documents pane, unless a class is on screen.</summary>
    private void ShowCollectionPreview()
    {
        if (!string.IsNullOrEmpty(AnalyzedClass)) return;
        SampleDocuments.Clear();
        foreach (var d in _collectionPreview) SampleDocuments.Add(d);
        SampleDocumentsHeader = _collectionPreview.Count == 0
            ? "Sample documents"
            : $"First {_collectionPreview.Count} documents of '{SelectedCollection}' (by key)";
        SelectedSampleDocument = SampleDocuments.FirstOrDefault();
    }

    private static string PrettyJson(JsonNode? doc) => doc?.ToJsonString(JsonText.Readable) ?? "";

    /// <summary>Tab positions in the window, in XAML order: Query first, the default.</summary>
    private const int QueryTab = 0;

    /// <summary>The window's tab: <see cref="QueryTab"/> or the Schema explorer.</summary>
    [ObservableProperty] private int _selectedTabIndex = QueryTab;

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
    private string? _selectedBucket;

    [ObservableProperty]
    private ScopeInfo? _selectedScope;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeepLoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string? _selectedCollection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadClassesCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeepLoadCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private string _discriminator = "class";

    [ObservableProperty]
    private string? _selectedClass;

    /// <summary>The row selected in the class list; <see cref="SelectedClass"/> is its name.</summary>
    [ObservableProperty] private ClassEntry? _selectedClassEntry;

    // Everything a class shows was gathered by Load: selecting one reads nothing.
    partial void OnSelectedClassEntryChanged(ClassEntry? value)
    {
        SelectedClass = value?.Name;
        if (value is null) { ClearSchema(); return; }
        ShowClassSchema(value.Name);
        if (_twoStep is not null && !_twoStep.Samples.ContainsKey(value.Name))
            _ = SampleClassAsync(value.Name);
    }

    /// <summary>What the class percentages are of: documents read, and how many had no class.</summary>
    [ObservableProperty] private string _classesCaption = "";

    [ObservableProperty] private FieldInfo? _selectedField;
    [ObservableProperty] private string _distinctValuesHeader = "Distinct values";

    // ---- Scan limit ----
    // How many documents Load reads: the collection's first N in primary-key order, or all of them.
    // A schema rarely needs every document, and reading fewer is what keeps Load quick.

    /// <summary>On: Load reads the first <see cref="ScanLimit"/> documents. Off: the whole collection.
    /// Starts from Options; the switch beside Load changes it for the session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanLimitSummary))]
    private bool _isScanLimitOn = true;

    /// <summary>Documents Load reads while the scan limit is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanLimitSummary))]
    private string _scanLimit = AppOptions.DefaultScanLimitValue.ToString("N0", CultureInfo.InvariantCulture);

    public string ScanLimitSummary => !IsScanLimitOn
        ? "off: Deep load and Analyze read the whole collection"
        : TryScanLimit(out var n) && n is not null
            ? $"documents: Deep load and Analyze read the first {n:N0} by key"
            : "must be a whole number above 0 (or switch the limit off)";

    private static bool TryParseScanLimit(string text, out int? limit)
    {
        limit = null;
        if (!int.TryParse(text.Replace(",", "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ||
            n <= 0)
            return false;
        limit = n;
        return true;
    }

    /// <summary>The limit in force: null (whole collection) when the switch is off.</summary>
    private bool TryScanLimit(out int? limit)
    {
        limit = null;
        return !IsScanLimitOn || TryParseScanLimit(ScanLimit, out limit);
    }

    /// <summary>The switch is remembered: flipping it is saved to options.json at once, so the next
    /// session starts the way this one was left.</summary>
    partial void OnIsScanLimitOnChanged(bool value)
    {
        if (_options.ScanLimitEnabled == value) return;   // set from Options, nothing new to save
        _options.ScanLimitEnabled = value;
        try
        {
            _options.Save();
        }
        catch (Exception ex)
        {
            Report($"Could not remember the scan limit switch: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Puts the scan-limit switch and number back to what Options says.</summary>
    private void ApplyScanLimitDefaults()
    {
        IsScanLimitOn = _options.ScanLimitEnabled;
        ScanLimit = _options.ScanLimit.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The limit to send, or null for the whole collection. Throws on text that does not
    /// parse, so a typo is reported instead of silently reading everything.</summary>
    private int? ScanLimitValue() =>
        TryScanLimit(out var n)
            ? n
            : throw new FormatException($"Scan limit: '{ScanLimit}' is not a whole number above 0. Fix it, or switch the limit off.");

    private int? ScanLimitValueOrNull() => TryScanLimit(out var n) ? n : null;

    private static string Scanned(int? scanLimit) =>
        scanLimit is null ? "" : $" in the first {scanLimit:N0} documents";

    // ---- Date field ----

    /// <summary>The field whose first/last value Load reports per class (when its documents were
    /// created). Guessed when a collection is picked: createdts, createdAt, created_at, …</summary>
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
            ApplyScanLimitDefaults();
            _couchbase.GentleMode = _options.GentleMode;
            Report($"Options saved: batches of {_options.BatchSize:N0} documents, " +
                   (_options.GentleMode ? "gentle mode on (one at a time, paused); " : $"{_options.ParallelReads} at once; ") +
                   "scan limit " +
                   (_options.ScanLimitEnabled ? $"on, {_options.ScanLimit:N0}." : "off (whole collection)."),
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

    /// <summary>How far a batched read is, 0–100; null when unknown (a single query).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityPercent))]
    private double? _activityPercent;

    public bool HasActivityPercent => ActivityPercent is not null;

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

    // ---- Load ----
    // One pass over the scan limit (or the whole collection) gathers everything the explorer
    // shows: per class its count, %, first/last date, and per field its types, presence, flags and
    // most frequent values. Selecting a class or a field afterwards reads nothing, and Export saves
    // the same summary. Documents come in key-ordered pages of the Options batch size; after each
    // page the running summary is saved, so a long load that is cancelled or fails can resume.

    /// <summary>The summary of the last Load, for Export. Null when nothing is loaded or a file is shown.</summary>
    private ExportState? _loaded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _hasLoaded;

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

    [RelayCommand(CanExecute = nameof(CanLoadClasses))]
    private async Task DeepLoadAsync()
    {
        int? scan;
        try { scan = ScanLimitValue(); }
        catch (FormatException ex) { Report(ex.Message, StatusKind.Error); return; }

        var bucket = SelectedBucket!;
        var scope = SelectedScope!.Name;
        var collection = SelectedCollection!;
        var discriminator = Discriminator.Trim();
        var coverage = scan is null ? "the whole collection" : $"the first {scan:N0} documents";
        var checkpointPath = ExportState.CheckpointPath(bucket, scope, collection, discriminator, scan);

        var state = ExportState.Load(checkpointPath);
        if (state is not null)
        {
            var answer = System.Windows.MessageBox.Show(
                $"A load of '{collection}' ({coverage}, classes by '{discriminator}') stopped after " +
                $"{state.DocumentsRead:N0} documents; its progress was saved " +
                $"{state.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.\n\nResume it? No starts over.",
                "Resume load?", System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);
            if (answer == System.Windows.MessageBoxResult.Cancel) return;
            if (answer == System.Windows.MessageBoxResult.No) state = null;
        }

        state ??= new ExportState
        {
            Bucket = bucket,
            Scope = scope,
            Collection = collection,
            Discriminator = discriminator,
            DateField = ExportDateField.Trim(),
            BatchSize = _options.BatchSize,
            ParallelReads = _options.EffectiveParallelReads,
            MaxDocuments = scan,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        state.ParallelReads = _options.EffectiveParallelReads;
        state.PauseBetweenPages = _options.EffectivePause;
        ClearLoaded();
        await RunGuarded($"Loading '{collection}'…", async ct =>
        {
            var count = await _couchbase.CountDocumentsAsync(bucket, scope, collection, ct);
            var total = scan is { } m ? Math.Min(count, m) : count;
            var resumedAt = state.DocumentsRead;
            var clock = Stopwatch.StartNew();
            IProgress<(string Text, double Percent)> progress = new Progress<(string Text, double Percent)>(p =>
            {
                ActivityPercent = p.Percent;
                SetActivity(p.Text);
            });

            var cancelled = false;
            try
            {
                // Off the UI thread: a page can hold a million documents to walk. The progress text
                // is built on the reading thread, the only one touching the summary while it grows.
                await Task.Run(() => state.ReadAllAsync(_couchbase, checkpointPath,
                    () => progress.Report(LoadProgress(state, collection, total,
                                                       state.DocumentsRead - resumedAt, clock.Elapsed)),
                    ct), ct);
                TryDelete(checkpointPath);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                cancelled = true;
            }

            var countNow = await _couchbase.CountDocumentsAsync(bucket, scope, collection);
            _report = state.BuildReport(countNow);
            _loaded = state;
            HasLoaded = true;
            ShowReport(_report);
            CanCountExactly = state.DocumentsRead < countNow;

            var expected = scan is { } cap ? Math.Min(countNow, cap) : countNow;
            var check = cancelled ? ""
                : state.DocumentsRead == expected ? " · ✓ matches the collection count"
                : $" · ⚠ expected {expected:N0} (documents were added or removed during the read)";
            ClassesCaption = cancelled
                ? $"Cancelled: % of the first {state.DocumentsRead:N0} documents only"
                : $"% of {state.DocumentsRead:N0} documents ({coverage}){check}";

            Report(cancelled
                    ? $"Load cancelled after {state.DocumentsRead:N0} documents; what is shown covers those. " +
                      "Load again to resume."
                    : $"Loaded {Classes.Count} class(es) from {state.DocumentsRead:N0} documents. " +
                      "Select a class to see its schema.",
                cancelled ? StatusKind.Idle : StatusKind.Ok);
        }, cancellable: true);
    }

    private bool CanLoadClasses() =>
        IsConnected && !IsBusy &&
        SelectedScope is not null &&
        !string.IsNullOrWhiteSpace(SelectedCollection) &&
        !string.IsNullOrWhiteSpace(Discriminator);

    private static (string Text, double Percent) LoadProgress(
        ExportState state, string collection, long total, long readThisRun, TimeSpan elapsed)
    {
        var done = state.DocumentsRead;
        var percent = total == 0 ? 100 : Math.Min(100, done * 100.0 / total);
        var batches = Math.Max(1, (int)Math.Ceiling(total / (double)state.BatchSize));
        var batch = Math.Min(batches, state.BatchesDone + 1);
        var rate = elapsed.TotalSeconds >= 1 ? readThisRun / elapsed.TotalSeconds : 0;

        var text = $"Reading '{collection}': {done:N0} / {total:N0} documents ({percent:0}%) · " +
                   $"batch {batch} of {batches}";
        if (rate > 0) text += $" · {rate:N0} docs/s";
        if (rate > 0 && done < total)
        {
            var left = TimeSpan.FromSeconds((total - done) / rate);
            text += left.TotalHours >= 1
                ? $" · ~{(int)left.TotalHours}h {left.Minutes}m left"
                : $" · ~{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left";
        }
        text += $" · {state.Classes.Count} class(es)";

        // Where the time goes: the page's wait for its first row is the server preparing it; the
        // rest is the server fetching and sending rows, or this app summarising them.
        var reading = state.ReadingTime.TotalSeconds;
        if (reading >= 2)
        {
            var appShare = state.ProcessingTime.TotalSeconds / reading * 100;
            var perDoc = readThisRun > 0 ? state.BytesRead / (double)readThisRun / 1024 : 0;
            var mbps = state.BytesRead / reading / (1024 * 1024);
            text += $"\nfirst row of this batch after {state.LastFirstRowWait.TotalSeconds:0.0} s · " +
                    $"app busy {appShare:0}%, waiting on the server/network {100 - appShare:0}% · " +
                    $"{state.RangesActive} range(s) streaming · ≈{perDoc:0.0} KB/doc · ≈{mbps:0.00} MB/s";
        }
        return (text, percent);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception) { /* a stale checkpoint only means a resume offer next time */ }
    }

    // ---- Two-step analysis ----
    // Step 1, Load: every class with its exact count, counted on the server over the whole
    // collection; no document is downloaded. Opening a class then downloads a small sample of it to
    // list its fields. Step 2, Analyze selected: the ticked fields are counted on the server over
    // every document of the class in one pass, and only the counts come back. Deep load (above) is
    // the other way: download documents and summarise everything in the app.

    private TwoStepState? _twoStep;

    /// <summary>Ticked field names. One list for every class: Analyze counts these fields in all
    /// classes at once, so a tick holds whichever class is on screen.</summary>
    private readonly HashSet<string> _tickedFields = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeSelectedCommand))]
    private int _selectedFieldCount;

    /// <summary>True when the explorer shows a two-step summary, so the tick boxes do something.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeFieldCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeAllClassesCommand))]
    private bool _isTwoStep;

    /// <summary>The legend under the class list for a two-step summary.</summary>
    private const string StatusLegend = "○ counted · ◐ fields listed · ◑ partly analyzed · ● analyzed";

    [RelayCommand(CanExecute = nameof(CanLoadClasses))]
    private async Task LoadClassesAsync()
    {
        var bucket = SelectedBucket!;
        var scope = SelectedScope!.Name;
        var collection = SelectedCollection!;
        var discriminator = Discriminator.Trim();

        ClearLoaded();
        await RunGuarded($"Counting the classes of '{collection}'…", async ct =>
        {
            Dictionary<string, long> counts;
            var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            try
            {
                counts = await CountAllClassesAsync(bucket, scope, collection, discriminator, "Counting classes", ct, keys);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                Report("Load cancelled.", StatusKind.Idle);
                return;
            }

            var state = new TwoStepState
            {
                Bucket = bucket, Scope = scope, Collection = collection, Discriminator = discriminator
            };
            foreach (var (k, v) in counts) state.Counts[k] = v;
            foreach (var (k, v) in keys) state.SampleKeys[k] = v;
            state.Total = counts.Values.Sum();
            state.CountedAt = DateTimeOffset.UtcNow;

            _twoStep = state;
            IsTwoStep = true;

            // Every class's sample right away, fetched by the keys just collected (a direct lookup,
            // no search), so selecting a class shows its fields at once. Cancel keeps the counts and
            // the samples taken so far; a class without one is sampled when it is selected.
            var ordered = counts.OrderByDescending(c => c.Value).Select(c => c.Key).ToList();
            var sampled = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                ActivityPercent = i * 100.0 / ordered.Count;
                SetActivity($"Listing fields from a sample of each class: {i + 1} of {ordered.Count} ('{ordered[i]}')");
                try
                {
                    await SampleClassCoreAsync(ordered[i], ct);
                    sampled++;
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }

            _report = state.BuildReport();
            HasLoaded = true;
            ShowReport(_report);
            ClassesCaption = $"% of all {state.Total:N0} documents (exact) · {StatusLegend}";
            Report(sampled == ordered.Count
                    ? $"Counted {counts.Count} class(es) over {state.Total:N0} documents and listed their fields. " +
                      "Tick fields and Analyze selected, or press Analyze all classes for everything."
                    : $"Counted {counts.Count} class(es); fields listed for {sampled} before Cancel. The others " +
                      "are listed when selected.",
                sampled == ordered.Count ? StatusKind.Ok : StatusKind.Idle);
        }, cancellable: true);
    }

    /// <summary>
    /// Every class's document count over the whole collection, counted on the server range by range
    /// (several ranges at once): only one row per class comes back per range. With
    /// <paramref name="sampleKeys"/>, also up to <see cref="TwoStepState.SampleSize"/> keys per class.
    /// </summary>
    private async Task<Dictionary<string, long>> CountAllClassesAsync(
        string bucket, string scope, string collection, string discriminator, string label, CancellationToken ct,
        Dictionary<string, List<string>>? sampleKeys = null)
    {
        var count = await _couchbase.CountDocumentsAsync(bucket, scope, collection, ct);
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        var sync = new object();
        var clock = Stopwatch.StartNew();
        IProgress<(int Done, int Expected)> progress = new Progress<(int Done, int Expected)>(p =>
        {
            long docs; int classes;
            lock (sync) { docs = totals.Values.Sum(); classes = totals.Count; }
            ActivityPercent = Math.Min(100, p.Done * 100.0 / p.Expected);
            var rate = clock.Elapsed.TotalSeconds >= 1 ? docs / clock.Elapsed.TotalSeconds : 0;
            SetActivity($"{label}: batch {p.Done} of ~{p.Expected} ({ActivityPercent:0}%) · {docs:N0} of " +
                        $"{count:N0} documents counted · {classes} class(es)" + (rate > 0 ? $" · {rate:N0} docs/s" : ""));
        });
        SetActivity($"{label}: {count:N0} documents, starting…");

        await RangePlanner.RunAsync(
            count, cap: null, _options.BatchSize, _options.EffectiveParallelReads,
            (after, n) => _couchbase.GetBatchEndKeyAsync(bucket, scope, collection, after, n, ct),
            async (after, end) =>
            {
                var rows = await _couchbase.CountClassesInRangeAsync(
                    bucket, scope, collection, discriminator, after, end,
                    sampleKeys is null ? 0 : TwoStepState.KeysPerRange, ct);
                lock (sync)
                    foreach (var (cls, n, keys) in rows)
                    {
                        var key = cls ?? ExportState.NoClass;
                        totals[key] = totals.GetValueOrDefault(key) + n;
                        if (sampleKeys is null) continue;
                        if (!sampleKeys.TryGetValue(key, out var list)) sampleKeys[key] = list = new List<string>();
                        foreach (var k in keys)
                            if (list.Count < TwoStepState.SampleSize) list.Add(k);
                    }
            },
            (done, expected) => progress.Report((done, expected)),
            ct,
            _options.EffectivePause);

        return totals;
    }

    /// <summary>Lists a class's fields from a sample (once per class), then shows them.</summary>
    private async Task SampleClassAsync(string className)
    {
        await RunGuarded($"Listing the fields of '{className}'…", async ct =>
        {
            try
            {
                await SampleClassCoreAsync(className, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                Report("Sampling cancelled.", StatusKind.Idle);
                return;
            }

            RefreshTwoStepReport();
            if (SelectedClass == className) ShowClassSchema(className);
            var sample = _twoStep!.Samples[className];
            Report($"'{className}': {sample.Size:N0} document(s) sampled, {sample.Fields.Count} field(s). " +
                   "Tick fields (or use Scalars) and Analyze selected.", StatusKind.Ok);
        }, cancellable: true);
    }

    /// <summary>The sample itself: fetched by the keys Load kept (no search), or, when there are
    /// none, the first documents of the class the server finds.</summary>
    private async Task SampleClassCoreAsync(string className, CancellationToken ct)
    {
        var state = _twoStep!;
        var docs = state.SampleKeys.TryGetValue(className, out var keys) && keys.Count > 0
            ? await _couchbase.GetDocumentsByKeysAsync(state.Bucket, state.Scope, state.Collection, keys, ct)
            : await _couchbase.GetClassSampleAsync(
                state.Bucket, state.Scope, state.Collection, state.Discriminator,
                className == ExportState.NoClass ? null : className, TwoStepState.SampleSize, ct);

        var analyzer = new SchemaAnalyzer();
        foreach (var (_, d) in docs) analyzer.Add(d);
        state.Samples[className] = new TwoStepState.ClassSample
        {
            Size = docs.Count,
            Fields = analyzer.Build(className).Fields,
            At = DateTimeOffset.UtcNow,
            Documents = docs.Take(TwoStepState.DocumentsKept)
                            .Where(d => d.Document.ValueKind != JsonValueKind.Undefined)
                            .Select(d => (d.Key, d.Document.GetRawText()))
                            .ToList()
        };
        // Found by searching (no keys from Load): its keys still give the key shapes.
        if (!state.SampleKeys.ContainsKey(className))
            state.SampleKeys[className] = docs.Select(d => d.Key).ToList();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeSelected))]
    private Task AnalyzeSelectedAsync() => AnalyzeFieldsAsync(_tickedFields.Order(StringComparer.Ordinal).ToList());

    private bool CanAnalyzeSelected() => IsTwoStep && IsConnected && !IsBusy && SelectedFieldCount > 0;

    /// <summary>The button on a field's row: that one field, in every class, then its values.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeField))]
    private async Task AnalyzeFieldAsync(FieldInfo? field)
    {
        if (field is null) return;
        var name = field.Name;
        await AnalyzeFieldsAsync([name]);
        SelectedField = Fields.FirstOrDefault(f => f.Name == name);
    }

    private bool CanAnalyzeField(FieldInfo? field) => IsTwoStep && IsConnected && !IsBusy;

    /// <summary>
    /// Everything for every class: lists the fields of each class not yet sampled (by key, from the
    /// keys Load kept), ticks every field that never holds an object or an array, and counts them all
    /// in one pass. Export then holds the whole collection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyzeAll))]
    private async Task AnalyzeAllClassesAsync()
    {
        int? scan;
        try { scan = ScanLimitValue(); }
        catch (FormatException ex) { Report(ex.Message, StatusKind.Error); return; }
        var state = _twoStep!;

        await RunGuarded("Analyzing every class…", async ct =>
        {
            var toSample = state.Counts.Keys.Where(k => !state.Samples.ContainsKey(k))
                                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < toSample.Count; i++)
            {
                ActivityPercent = i * 100.0 / toSample.Count;
                SetActivity($"Listing fields: class {i + 1} of {toSample.Count} ('{toSample[i]}')");
                try
                {
                    await SampleClassCoreAsync(toSample[i], ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    RefreshTwoStepReport();
                    Report("Analyze all classes cancelled while listing fields; the classes listed so far keep " +
                           "their fields.", StatusKind.Idle);
                    return;
                }
            }
            RefreshTwoStepReport();

            // A field that is an object or an array in any class is left out: its values are large
            // and rarely worth counting. Its own scalar members (e.g. linkedCard.provider) stay in.
            var fields = state.Samples.Values.SelectMany(sm => sm.Fields)
                .GroupBy(f => f.Name, StringComparer.Ordinal)
                .Where(g => g.All(f => f.IsScalar))
                .Select(g => g.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            foreach (var f in fields) _tickedFields.Add(f);
            SelectedFieldCount = _tickedFields.Count;
            foreach (var f in Fields) f.IsSelected = _tickedFields.Contains(f.Name);

            if (fields.Count == 0)
            {
                Report("No field with plain values was found in the samples.", StatusKind.Idle);
                return;
            }

            var summary = await AnalyzeFieldsCoreAsync(fields, scan, ct);
            if (summary is null) return;
            AfterAnalysis();
            Report($"Every class analyzed: {summary} Export now holds the whole collection.", StatusKind.Ok);
        }, cancellable: true);
    }

    private bool CanAnalyzeAll() => IsTwoStep && IsConnected && !IsBusy;

    /// <summary>Counts the given fields in every class (see <see cref="AnalyzeFieldsCoreAsync"/>).</summary>
    private async Task AnalyzeFieldsAsync(IReadOnlyList<string> fields)
    {
        if (_twoStep is null || fields.Count == 0) return;
        int? scan;
        try { scan = ScanLimitValue(); }
        catch (FormatException ex) { Report(ex.Message, StatusKind.Error); return; }

        var what = fields.Count == 1 ? $"'{fields[0]}'" : $"{fields.Count} fields";
        await RunGuarded($"Analyzing {what} in every class…", async ct =>
        {
            var summary = await AnalyzeFieldsCoreAsync(fields, scan, ct);
            if (summary is null) return;
            AfterAnalysis();
            Report(summary, StatusKind.Ok);
        }, cancellable: true);
    }

    /// <summary>
    /// Counts the given fields in every class in one pass over the collection, or over its first N
    /// documents by key while the scan limit is on: the server groups by class, field and value and
    /// returns only counts, plus how many documents of each class it read. Returns a one-line
    /// summary, or null when cancelled (nothing is changed then).
    /// </summary>
    private async Task<string?> AnalyzeFieldsCoreAsync(IReadOnlyList<string> fields, int? scan, CancellationToken ct)
    {
        var state = _twoStep!;
        var coverage = scan is null ? "every document" : $"the first {scan:N0} documents";
        var what = fields.Count == 1 ? $"'{fields[0]}'" : $"{fields.Count} fields";

        var count = await _couchbase.CountDocumentsAsync(state.Bucket, state.Scope, state.Collection, ct);
        var stats = new Dictionary<(string Class, string Field), TwoStepState.FieldStats>();
        var docsPerClass = new Dictionary<string, long>(StringComparer.Ordinal);
        var sync = new object();
        var windowed = true;   // falls back to plain grouping if the server rejects window functions
        var clock = Stopwatch.StartNew();
        IProgress<(int Done, int Expected)> progress = new Progress<(int Done, int Expected)>(p =>
        {
            long docs;
            lock (sync) docs = docsPerClass.Values.Sum();
            ActivityPercent = Math.Min(100, p.Done * 100.0 / p.Expected);
            SetActivity($"Analyzing {what} in every class over {coverage}: batch {p.Done} of ~{p.Expected} " +
                        $"({ActivityPercent:0}%) · {docs:N0} documents read");
        });
        ActivityPercent = 0;
        SetActivity($"Analyzing {what} in every class over {coverage}: starting…");

        Task<IReadOnlyList<FieldValueRow>> Count(string after, string? end, bool w) =>
            _couchbase.CountFieldValuesInRangeAsync(
                state.Bucket, state.Scope, state.Collection, state.Discriminator,
                fields, after, end, TwoStepState.TopPerRange, w, ct);

        try
        {
            await RangePlanner.RunAsync(
                count, scan, _options.BatchSize, _options.EffectiveParallelReads,
                (after, n) => _couchbase.GetBatchEndKeyAsync(state.Bucket, state.Scope, state.Collection, after, n, ct),
                async (after, end) =>
                {
                    IReadOnlyList<FieldValueRow> rows;
                    bool w;
                    lock (sync) w = windowed;
                    try
                    {
                        rows = await Count(after, end, w);
                    }
                    catch (Exception ex) when (w && !ct.IsCancellationRequested)
                    {
                        // Older servers without window functions: count every value instead.
                        lock (sync) windowed = false;
                        Diagnostics = "The server did not accept the windowed count; every value is " +
                                      "returned and totalled in the app instead.\n\n" + ExceptionReport.Describe(ex);
                        rows = await Count(after, end, false);
                    }

                    lock (sync)
                        foreach (var group in rows.GroupBy(r => (Class: r.Class ?? ExportState.NoClass, r.Field)))
                        {
                            if (group.Key.Field == CouchbaseService.DocumentsField)
                            {
                                docsPerClass[group.Key.Class] =
                                    docsPerClass.GetValueOrDefault(group.Key.Class) + group.Sum(r => r.Count);
                                continue;
                            }
                            if (!stats.TryGetValue(group.Key, out var s))
                                stats[group.Key] = s = new TwoStepState.FieldStats();
                            s.Merge(group.ToList());
                        }
                },
                (done, expected) => progress.Report((done, expected)),
                ct,
                _options.EffectivePause);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Report("Analyze cancelled; nothing was changed.", StatusKind.Idle);
            return null;
        }

        // A ticked field no document of a class holds is recorded as analyzed and absent, for every
        // class whose sample lists it.
        var now = DateTimeOffset.UtcNow;
        foreach (var (cls, docs) in docsPerClass)
        {
            if (!state.Analyses.TryGetValue(cls, out var byField))
                state.Analyses[cls] = byField = new Dictionary<string, TwoStepState.FieldStats>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var s = stats.GetValueOrDefault((cls, field));
                if (s is null && (!state.Samples.TryGetValue(cls, out var sample) ||
                                  sample.Fields.All(f => f.Name != field)))
                    continue;   // neither held nor listed: nothing worth showing
                s ??= new TwoStepState.FieldStats();
                s.Base = docs;
                s.ScanLimit = scan;
                s.At = now;
                byField[field] = s;
            }
        }

        var approx = stats.Count(kv => kv.Value.Truncated);
        return $"Analyzed {what} in {docsPerClass.Count} class(es) over {docsPerClass.Values.Sum():N0} documents " +
               $"in {clock.Elapsed:m\\:ss}" +
               (approx > 0 ? $"; {approx} class/field pair(s) have too many distinct values for exact top values." : ".");
    }

    /// <summary>After an analysis: the new numbers on screen, keeping the class and field shown.</summary>
    private void AfterAnalysis()
    {
        var keep = SelectedField?.Name;
        RefreshTwoStepReport();
        if (SelectedClass is { } shown)
        {
            ShowClassSchema(shown);
            SelectedField = Fields.FirstOrDefault(f => f.Name == keep);
        }
    }

    [RelayCommand]
    private void SelectScalarFields() => SetSelection(f => f.IsScalar);

    [RelayCommand]
    private void SelectAllFields() => SetSelection(_ => true);

    [RelayCommand]
    private void SelectNoFields() => SetSelection(_ => false);

    /// <summary>Ticks or unticks the fields of the class on screen (other ticks stay as they are).</summary>
    private void SetSelection(Func<FieldInfo, bool> pick)
    {
        foreach (var f in Fields) f.IsSelected = pick(f);
    }

    private void OnFieldPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FieldInfo.IsSelected) || sender is not FieldInfo f) return;
        if (f.IsSelected) _tickedFields.Add(f.Name);
        else _tickedFields.Remove(f.Name);
        SelectedFieldCount = _tickedFields.Count;
    }

    private void RefreshTwoStepReport()
    {
        if (_twoStep is null) return;
        _report = _twoStep.BuildReport();
        _reportClasses.Clear();
        foreach (var node in _report["classes"]!.AsArray())
            if (node is JsonObject cls) _reportClasses[cls["class"]?.ToString() ?? ""] = cls;

        var dateField = _report["dateField"]?.ToString();
        foreach (var entry in Classes)
            if (_reportClasses.TryGetValue(entry.Name, out var cls))
            {
                entry.Status = cls["status"]?.ToString() ?? ClassEntry.Loaded;
                entry.Detail = DescribeClass(cls, dateField, cls["exactCount"] is JsonValue);
            }
    }

    // ---- Export ----

    /// <summary>Saves what the last Load gathered. Reads nothing: the summary is already in memory.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var report = _report!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the collection summary",
            // bucket.scope.collection-yyyyMMdd-HHmm: the file covers every class of the collection.
            FileName = $"{report["bucket"]}.{report["scope"]}.{report["collection"]}-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // Persian class names and values stay readable instead of \uXXXX escapes.
            var json = report.ToJsonString(JsonText.Readable);
            await File.WriteAllTextAsync(dialog.FileName, json);
            Report($"Saved {Long(report["classCount"]):N0} class(es), {Long(report["documentsRead"]):N0} documents, " +
                   $"to {dialog.FileName}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            Report($"Could not save: {ExceptionReport.Summarize(ex)}", StatusKind.Error);
        }
    }

    private bool CanExport() => HasLoaded && !IsBusy;

    // ---- Exact counts ----
    // A Load with a scan limit describes the first N documents. This optional step counts every
    // class over the whole collection on the server, one key range of the batch size at a time
    // (see BatchPlanner): only one row per class comes back per range, so the network carries
    // counts, not documents. The server still goes through every document, so it takes a while on
    // a large collection; the schema and values stay those of the documents Load read.

    /// <summary>True when the last Load read only part of the collection, so exact counts would add something.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CountExactlyCommand))]
    private bool _canCountExactly;

    [RelayCommand(CanExecute = nameof(CanRunExactCount))]
    private async Task CountExactlyAsync()
    {
        var state = _loaded!;
        await RunGuarded($"Counting every class of '{state.Collection}'…", async ct =>
        {
            var count = await _couchbase.CountDocumentsAsync(state.Bucket, state.Scope, state.Collection, ct);
            Dictionary<string, long> totals;
            long withoutClass = 0;

            try
            {
                totals = await CountAllClassesAsync(
                    state.Bucket, state.Scope, state.Collection, state.Discriminator, "Counting every class", ct);
                withoutClass = totals.GetValueOrDefault(ExportState.NoClass);
                totals.Remove(ExportState.NoClass);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                Report("Exact count cancelled; the counts shown are still those of the documents read.",
                       StatusKind.Idle);
                return;
            }

            if (withoutClass > 0) totals[ExportState.NoClass] = withoutClass;
            state.ExactCounts = totals;
            state.ExactWithoutClass = withoutClass;
            state.ExactTotal = totals.Values.Sum();
            state.ExactCountedAt = DateTimeOffset.UtcNow;

            _report = state.BuildReport(count);
            ShowReport(_report);
            CanCountExactly = false;

            var onlyCounted = totals.Keys.Count(k => !state.Classes.ContainsKey(k));
            ClassesCaption = $"% of all {state.ExactTotal:N0} documents (exact count) · " +
                             $"schema from the {state.DocumentsRead:N0} read";
            Report($"Counted {totals.Count} class(es) over {state.ExactTotal:N0} documents" +
                   (onlyCounted > 0
                       ? $"; {onlyCounted} of them are not in the documents read, so they have counts only."
                       : "."),
                   StatusKind.Ok);
        }, cancellable: true);
    }

    private bool CanRunExactCount() => CanCountExactly && IsConnected && !IsBusy && _loaded is not null;

    // ---- Shown summary: from Load, or from a saved file ----
    // The class list, the schema and the distinct values are all read from one report (the JSON an
    // export writes). Load builds it from the documents it read; Open export… reads it from a file.

    /// <summary>True while the summary on screen came from a file rather than the cluster.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloseExportFileCommand))]
    private bool _isOffline;

    /// <summary>"From file: …" — shown above the class list while a file is open.</summary>
    [ObservableProperty] private string _offlineSource = "";

    private JsonObject? _report;
    private readonly Dictionary<string, JsonObject> _reportClasses = new(StringComparer.Ordinal);

    [RelayCommand]
    private void OpenExportFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a collection summary",
            Filter = "Collection summary (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchbaseInsight", "exports")
        };
        if (!Directory.Exists(dialog.InitialDirectory)) dialog.InitialDirectory = "";
        if (dialog.ShowDialog() != true) return;

        var fileName = Path.GetFileName(dialog.FileName);
        try
        {
            var (report, unfinished) = ReadExportFile(dialog.FileName);
            ClearLoaded();
            _report = report;
            ShowReport(report);

            var docs = Long(report["documentsRead"]);
            var keyspace = $"{report["bucket"]}.{report["scope"]}.{report["collection"]}";
            var when = report["finishedAt"]?.ToString() is { Length: >= 16 } f ? f[..16].Replace('T', ' ') + " UTC" : "";
            IsOffline = true;
            OfflineSource = unfinished
                ? $"From file: {fileName} · unfinished, {docs:N0} documents so far"
                : $"From file: {fileName} · saved {when}";
            ClassesCaption = $"{keyspace} by '{report["discriminator"]}' · % of {docs:N0} documents";
            Report($"Opened {fileName}: {Classes.Count} class(es), {docs:N0} documents. " +
                   "Load goes back to the cluster.", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            Report($"Could not open '{fileName}': {ExceptionReport.Summarize(ex)}", StatusKind.Error);
            Diagnostics = ExceptionReport.Describe(ex);
        }
    }

    /// <summary>
    /// A saved summary, or the checkpoint of an unfinished load (from the exports folder): the
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

        throw new InvalidDataException("This is not a collection summary: it has no \"classes\" list.");
    }

    /// <summary>Fills the class list from a report.</summary>
    private void ShowReport(JsonObject report)
    {
        _reportClasses.Clear();
        Classes.Clear();
        SelectedClassEntry = null;
        ClearSchema();

        foreach (var node in report["classes"]!.AsArray())
        {
            if (node is not JsonObject cls) continue;
            _reportClasses[cls["class"]?.ToString() ?? ""] = cls;
        }

        var dateField = report["dateField"]?.ToString();
        foreach (var (name, cls) in _reportClasses.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var exact = cls["exactCount"] is JsonValue;
            Classes.Add(new ClassEntry(
                name,
                exact ? Long(cls["exactCount"]) : Long(cls["count"]),
                exact ? Double(cls["exactPercent"]) : Double(cls["percent"]),
                DescribeClass(cls, dateField, exact),
                cls["status"]?.ToString() ?? ClassEntry.Loaded));
        }
    }

    private void ShowClassSchema(string className)
    {
        ClearSchema();
        if (!_reportClasses.TryGetValue(className, out var cls)) return;

        var classCount = Long(cls["count"]);
        var sampleSize = cls["sampleSize"] is JsonValue ? Long(cls["sampleSize"]) : classCount;

        foreach (var node in cls["fields"]?.AsArray() ?? [])
        {
            if (node is not JsonObject f) continue;
            var flags = f["flags"]?.AsArray().Select(x => x?.ToString()).ToList() ?? [];
            // Presence is over the class when the field was analyzed, else over the sample.
            var basis = f["base"] is JsonValue ? Long(f["base"]) : sampleSize;
            var field = new FieldInfo
            {
                Name = f["name"]?.ToString() ?? "",
                Types = string.Join(", ", f["types"]?.AsArray().Select(t => t?.ToString()) ?? []),
                Occurrences = (int)Math.Min(int.MaxValue, Long(f["seen"])),
                SampleSize = (int)Math.Min(int.MaxValue, basis),
                HasCaseDuplicate = flags.Contains("Case conflict"),
                IsAnalyzed = f["analyzed"]?.GetValue<bool>() == true
            };
            field.IsSelected = _tickedFields.Contains(field.Name);
            field.PropertyChanged += OnFieldPropertyChanged;
            Fields.Add(field);
        }
        SelectedFieldCount = _tickedFields.Count;

        if (cls["inSample"]?.GetValue<bool>() == false)
            Report($"'{className}' was only counted: none of its documents were among those read. " +
                   "Load with a larger scan limit (or none) to see its schema.", StatusKind.Idle);

        ShowSampleDocuments(cls);

        AnalyzedClass = className;
        SampleSize = (int)Math.Min(int.MaxValue, sampleSize);
        FieldCount = Fields.Count;
        RequiredCount = Fields.Count(x => x.IsRequired);
        IssueCount = Fields.Count(x => x.HasCaseDuplicate || x.HasMultipleTypes);
    }

    private void ShowDistinctValues(FieldInfo? field)
    {
        if (field is null || SelectedClass is null || !_reportClasses.TryGetValue(SelectedClass, out var cls))
        {
            DistinctValuesHeader = "Distinct values";
            return;
        }

        var saved = cls["fields"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(f => f["name"]?.ToString() == field.Name);
        if (saved?["distinct"] is not JsonObject distinct)
        {
            DistinctValuesHeader = _twoStep is not null
                ? $"Press the button on the '{field.Name}' row, or tick it and Analyze selected, to count its values"
                : $"No values kept for '{field.Name}' (objects and arrays have none)";
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
            DistinctValues.Add(new DistinctValueItem(
                $"{value}    ×{Long(v["count"]):N0}",
                v["exampleKey"] is JsonValue ek && ek.TryGetValue<string>(out var exampleKey) ? exampleKey : null));
        }

        var complete = distinct["complete"]?.GetValue<bool>() ?? false;
        var total = distinct["count"] is { } c ? $"{Long(c):N0}" : $"more than {Long(distinct["moreThan"]):N0}";
        var otherCount = Long(distinct["otherCount"]);
        var missing = Long(saved["missing"]);
        DistinctValuesHeader =
            (complete
                ? $"All {values.Count} distinct values of '{field.Name}'"
                : $"Top {values.Count} of {total} distinct values of '{field.Name}'") +
            (otherCount > 0 ? $" · {otherCount:N0} other" : "") +
            (missing > 0 ? $" · missing in {missing:N0}" : "") +
            (distinct["over"] is JsonValue
                ? distinct["scanLimit"] is JsonValue
                    ? $" · over the {Long(distinct["over"]):N0} documents of the class in the first {Long(distinct["scanLimit"]):N0}"
                    : $" · over all {Long(distinct["over"]):N0} documents of the class"
                : "") +
            (distinct["exact"]?.GetValue<bool>() == false ? " · approximate (too many distinct values)" : "") +
            (distinct["min"] is { } mn && distinct["max"] is { } mx ? $" · min {Shorten(mn)} · max {Shorten(mx)}" : "");
    }

    /// <summary>Fills the Sample documents pane: a two-step sample's documents, or a Deep load's
    /// oldest and newest document of the class.</summary>
    private void ShowSampleDocuments(JsonObject cls)
    {
        SampleDocuments.Clear();

        if (cls["sampleDocuments"] is JsonArray docs)
            foreach (var d in docs.OfType<JsonObject>())
                SampleDocuments.Add(new SampleDocumentItem(d["key"]?.ToString() ?? "", PrettyJson(d["doc"])));
        else if (cls["samples"] is JsonObject samples)
            foreach (var which in new[] { "oldest", "newest" })
                if (samples[which] is JsonObject d && d["key"] is not null)
                    SampleDocuments.Add(new SampleDocumentItem(d["key"]!.ToString(), PrettyJson(d["doc"]), which));

        var shapes = cls["keyPatterns"] is JsonArray kp && kp.Count > 0
            ? " · keys " + string.Join(", ", kp.OfType<JsonObject>().Take(3).Select(k => $"{k["pattern"]}"))
            : "";
        SampleDocumentsHeader = $"Sample documents ({SampleDocuments.Count}){shapes}";
        SelectedSampleDocument = SampleDocuments.FirstOrDefault();
    }

    /// <summary>The keyspace of the documents on screen: the loaded or opened summary's, or, before
    /// any Load (the collection preview), the bucket, scope and collection picked in the Schema
    /// explorer. Null while any part is missing.</summary>
    private (string Bucket, string Scope, string Collection)? ShownKeyspaceParts()
    {
        var (bucket, scope, collection) = _report is not null
            ? (_report["bucket"]?.ToString(), _report["scope"]?.ToString(), _report["collection"]?.ToString())
            : (SelectedBucket, SelectedScope?.Name, SelectedCollection);
        return string.IsNullOrEmpty(bucket) || string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(collection)
            ? null
            : (bucket, scope, collection);
    }

    /// <summary>The same, escaped for a statement; empty while any part is missing.</summary>
    private string ShownKeyspace() =>
        ShownKeyspaceParts() is { } k ? SqlText.Keyspace(k.Bucket, k.Scope, k.Collection) : "";

    /// <summary>Puts a by-key statement for the document in the Query tab, points the Query tab's
    /// picker at the same collection (so its field list matches), and switches to it.</summary>
    private void OpenInQuery(string key)
    {
        QueryEditor.Load($"SELECT META(c).id, c.*\nFROM {ShownKeyspace()} AS c\nUSE KEYS {JsonSerializer.Serialize(key)}");
        if (ShownKeyspaceParts() is { } k)
            _ = QueryEditor.Picker.SelectAsync(k.Bucket, k.Scope, k.Collection);
        SelectedTabIndex = QueryTab;
        Report($"Query tab: document '{key}'. Press Run (F5).", StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasSampleDocument))]
    private void CopySampleKey() => CopyToClipboard(SelectedSampleDocument!.Key, "Key");

    [RelayCommand(CanExecute = nameof(HasSampleDocument))]
    private void OpenSampleInQuery() => OpenInQuery(SelectedSampleDocument!.Key);

    private bool HasSampleDocument() => SelectedSampleDocument is not null;

    /// <summary>Opens a document that holds the selected value.</summary>
    [RelayCommand(CanExecute = nameof(HasValueExample))]
    private void OpenValueExample() => OpenInQuery(SelectedDistinctValue!.ExampleKey!);

    private bool HasValueExample() => SelectedDistinctValue?.ExampleKey is not null && _report is not null;

    private static string Shorten(JsonNode node)
    {
        var text = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    [RelayCommand(CanExecute = nameof(IsOffline))]
    private void CloseExportFile() => ClearLoaded();

    /// <summary>Clears whatever summary is shown, loaded or opened from a file.</summary>
    private void ClearLoaded()
    {
        _twoStep = null;
        IsTwoStep = false;
        _tickedFields.Clear();
        SelectedFieldCount = 0;
        _loaded = null;
        HasLoaded = false;
        CanCountExactly = false;
        _report = null;
        _reportClasses.Clear();
        IsOffline = false;
        OfflineSource = "";
        Classes.Clear();
        SelectedClassEntry = null;
        ClassesCaption = "";
        ClearSchema();
    }

    /// <summary>The class tooltip under its counts: how much of it was read, its dates, TTL and key shapes.</summary>
    private static string? DescribeClass(JsonObject cls, string? dateField, bool exact)
    {
        var lines = new List<string>();
        if (exact)
            lines.Add(cls["inSample"]?.GetValue<bool>() == true
                ? $"exact count; schema from {Long(cls["count"]):N0} document(s) read"
                : "exact count only: none of its documents were among those read");
        if (DescribeDateRange(cls["dateRange"] as JsonObject, dateField) is { } dates) lines.Add(dates);
        if (Long(cls["ttlCount"]) is > 0 and var ttl)
            lines.Add($"with a TTL: {ttl:N0} ({Double(cls["ttlPercent"]):0.#}%)");
        if (cls["lastWriteUtc"]?.ToString() is { Length: > 0 } lastWrite) lines.Add($"last write: {lastWrite}");
        if (cls["keyPatterns"] is JsonArray patterns && patterns.Count > 0)
            lines.Add("keys: " + string.Join(", ", patterns.OfType<JsonObject>().Take(3)
                .Select(k => $"{k["pattern"]} ×{Long(k["count"]):N0}")));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? DescribeDateRange(JsonObject? range, string? field)
    {
        if (range is null || string.IsNullOrEmpty(field)) return null;
        var min = range["minUtc"] ?? range["min"];
        var max = range["maxUtc"] ?? range["max"];
        if (min is null || max is null) return $"{field}: not present";
        var missing = Long(range["missing"]);
        return $"{field}: {min} … {max}" + (missing > 0 ? $" ({missing:N0} without it)" : "");
    }

    private static long Long(JsonNode? node) => JsonNumber.ToLong(node);

    private static double Double(JsonNode? node) => JsonNumber.ToDouble(node);

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

    /// <summary>The Query tab's editor and its own collection picker.</summary>
    public QueryEditorViewModel QueryEditor { get; }

    /// <summary>The statement Run sends and Save stores: the editor's text, as it is.</summary>
    private string CurrentStatement => QueryEditor.Statement;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyQueryResultCommand))]
    private string _queryResult = "";

    [ObservableProperty] private int _queryRowCount = -1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelQueryCommand))]
    private bool _isQueryRunning;

    private CancellationTokenSource? _queryCts;

    private void OnQueryEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueryEditorViewModel.Statement)) return;
        RunQueryCommand.NotifyCanExecuteChanged();
        OnQueryEditStateChanged();
    }

    // ---- Commands ----

    // Switching the saved-connection dropdown loads its values into the editable fields.
    partial void OnSelectedConnectionChanged(ConnectionProfile? value)
    {
        ReloadSavedQueries();
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
        // Say which it was: a name already in the list (any letter case) updates that connection.
        Report(existing is null
                ? $"Saved new connection '{name}' ({SavedConnections.Count} saved)."
                : $"Updated the saved connection '{existing.Name}' (a connection with that name already existed).",
            StatusKind.Ok);
    }

    [RelayCommand(CanExecute = nameof(HasConnectionString))]
    private void CopyConnectionString() => CopyToClipboard(ConnectionString, "Connection string");

    private bool HasConnectionString() => !string.IsNullOrWhiteSpace(ConnectionString);

    private bool CanSaveConnection() =>
        !string.IsNullOrWhiteSpace(ConnectionName) &&
        !string.IsNullOrWhiteSpace(ConnectionString);

    [RelayCommand(CanExecute = nameof(CanDeleteConnection))]
    private void DeleteConnection()
    {
        var profile = SelectedConnection!;

        // Its saved queries go with it; offer to export them first, so they can be imported later.
        if (!ConfirmDeletingConnectionQueries(profile.Name, out var exportNote))
            return;

        SavedConnections.Remove(profile);
        _store.Save(SavedConnections);
        var removed = DeleteConnectionQueries(profile.Name);
        SelectedConnection = SavedConnections.FirstOrDefault();
        Report($"Deleted connection '{profile.Name}'" +
               (removed > 0 ? $" and its {removed} saved quer{(removed == 1 ? "y" : "ies")}" : "") +
               $".{exportNote}", StatusKind.Ok);
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

        // Start on the first bucket; its scopes then load and the first one is picked too. Done
        // after the connect has finished, because a selection made while it is still running
        // would find the window busy and load nothing.
        else if (SelectedBucket is null && Buckets.Count > 0)
        {
            SelectedBucket = Buckets[0];
            // The Query tab starts on the same default, then goes its own way.
            QueryEditor.Picker.SelectedBucket ??= Buckets[0];
        }
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

    /// <summary>
    /// F5. A read runs at once, with the server's read-only flag on. A write runs only while
    /// <see cref="AllowWrites"/> is on, and then only after the confirm card (see
    /// MainViewModel.QueryRun.cs) has shown the statement and the cluster it is about to change.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunQuery))]
    private async Task RunQueryAsync()
    {
        var statement = CurrentStatement;
        if (StatementClassifier.Classify(statement) == QueryKind.Read)
        {
            await ExecuteQueryAsync(statement, readOnly: true);
            return;
        }

        RequestWrite(statement);
    }

    private async Task ExecuteQueryAsync(string statement, bool readOnly)
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;

        IsBusy = true;
        IsQueryRunning = true;
        Report(readOnly ? "Running query…" : "Running write…", StatusKind.Busy);
        QueryResult = "";
        QueryRowCount = -1;
        QueryResultSummary = "";
        try
        {
            var outcome = await _couchbase.RunQueryAsync(statement, readOnly, ct);
            QueryResult = outcome.Json;
            QueryRowCount = outcome.Count;
            QueryResultSummary = DescribeOutcome(outcome, readOnly);
            Report(readOnly
                    ? $"Query complete — {outcome.Count} row(s)."
                    : $"Write complete — {outcome.MutationCount?.ToString("N0") ?? "?"} document(s) changed.",
                StatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            // Cancelling only stops the wait here; a write the server already started can finish.
            Report(readOnly
                    ? "Query cancelled."
                    : "Write cancelled here — the cluster may still have applied some or all of it. Check before running it again.",
                readOnly ? StatusKind.Idle : StatusKind.Error);
        }
        catch (Exception ex)
        {
            QueryResult = $"// {ExceptionReport.Summarize(ex)}" + Environment.NewLine + Environment.NewLine +
                          ExceptionReport.Describe(ex);
            Report(readOnly ? "Query failed." : "Write failed.", StatusKind.Error);
        }
        finally
        {
            IsQueryRunning = false;
            IsBusy = false;
        }
    }

    private bool CanRunQuery() =>
        IsConnected && !IsBusy && !IsWriteConfirmVisible &&
        !string.IsNullOrWhiteSpace(CurrentStatement);

    [RelayCommand(CanExecute = nameof(CanCancelQuery))]
    private void CancelQuery() => _queryCts?.Cancel();

    private bool CanCancelQuery() => IsQueryRunning;

    [RelayCommand(CanExecute = nameof(CanCopyQueryResult))]
    private void CopyQueryResult() => CopyToClipboard(QueryResult, "Result");

    private bool CanCopyQueryResult() => !string.IsNullOrWhiteSpace(QueryResult);

    [RelayCommand]
    private void CopyDiagnostics() => CopyToClipboard(Diagnostics, "Diagnostics");

    [RelayCommand]
    private void CopyDistinctValues() =>
        CopyToClipboard(string.Join(Environment.NewLine, DistinctValues.Select(v => v.ToString())), "Values");

    // ---- Selection reactions ----

    partial void OnSelectedBucketChanged(string? value)
    {
        ClearLoaded();
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

            // The first scope of the bucket, passing over Couchbase's own _default and _system
            // when the bucket has scopes of its own: those are the ones worth looking at.
            SelectedScope = Scopes.FirstOrDefault(sc => !sc.Name.StartsWith('_')) ?? Scopes.FirstOrDefault();
            Report($"{Scopes.Count} scope(s) in '{value}'.", StatusKind.Ok);
        });
    }

    partial void OnSelectedScopeChanged(ScopeInfo? value)
    {
        ClearLoaded();
        Collections.Clear();
        SelectedCollection = null;
        Classes.Clear();
        ClearSchema();

        if (value is null) return;
        foreach (var c in value.Collections) Collections.Add(c);
    }

    partial void OnSelectedCollectionChanged(string? value)
    {
        ClearLoaded();
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
            _ = LoadCollectionPreviewAsync();
            Report($"{DiscriminatorFields.Count} field(s) available as discriminator.", StatusKind.Ok);
        });
    }

    partial void OnSelectedFieldChanged(FieldInfo? value)
    {
        DistinctValues.Clear();
        SelectedDistinctValue = null;
        ShowDistinctValues(value);
        // Clicking a field asks for its values, so bring that tab forward.
        if (value is not null) DetailTabIndex = 0;
    }

    /// <summary>The tab under the field list: 0 = distinct values, 1 = sample documents.</summary>
    [ObservableProperty] private int _detailTabIndex;

    // ---- Helpers ----

    private static string Escape(string id) => SqlText.Identifier(id);

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

    /// <summary>The cluster for the title bar, from the connection string the SDK was given. No
    /// bucket: the Schema explorer and the Query tab each pick their own.</summary>
    private string DescribeTarget()
    {
        var seeds = ConnectionStringInspector.Inspect(ConnectionString).Seeds;
        var host = seeds.Count > 0 ? seeds[0].Host : ConnectionString;
        var extra = seeds.Count > 1 ? $" +{seeds.Count - 1}" : "";
        return $"{host}{extra}";
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
        ShowCollectionPreview();
    }

    private void CancelDistinctValues()
    {
        _distinctValuesCts?.Cancel();
        _distinctValuesCts?.Dispose();
        _distinctValuesCts = null;
    }

    private void ResetHierarchy()
    {
        ClearLoaded();
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
        QueryEditor.Picker.Reset();
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
            ActivityPercent = null;
            _activityCts?.Dispose();
            _activityCts = null;
            IsBusy = false;
        }
    }
}
