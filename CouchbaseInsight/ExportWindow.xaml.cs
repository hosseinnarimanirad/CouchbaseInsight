using System.IO;
using System.Windows;
using CouchbaseInsight.Services;

namespace CouchbaseInsight;

/// <summary>
/// Asks what a collection export needs before it starts: the date field, where to save, and —
/// when an unfinished export of the same collection is on disk — whether to resume it. Resuming
/// keeps that export's own date field, file and batch size, so those inputs lock while it is chosen.
/// </summary>
public partial class ExportWindow : Window
{
    private readonly ExportState? _checkpoint;
    private readonly int _batchSize;
    private readonly string _newDateField;
    private readonly string _newPath;

    /// <summary>True: continue <c>checkpoint</c>. False: start a new export with the fields below.</summary>
    public bool Resume => _checkpoint is not null && ResumeBox.IsChecked == true;

    public string DateField => DateFieldBox.Text.Trim();
    public string OutputPath => PathBox.Text.Trim();

    public ExportWindow(
        string target, IEnumerable<string> fields, string guessedDateField, string defaultPath,
        int batchSize, ExportState? checkpoint)
    {
        InitializeComponent();
        _checkpoint = checkpoint;
        _batchSize = batchSize;
        _newDateField = guessedDateField;
        _newPath = defaultPath;

        TargetText.Text = target;
        foreach (var f in fields) DateFieldBox.Items.Add(f);

        if (checkpoint is not null)
        {
            ResumePanel.Visibility = Visibility.Visible;
            ResumeText.Text =
                $"An unfinished export of this collection stopped after {checkpoint.DocumentsRead:N0} documents " +
                $"({checkpoint.BatchesDone} batch(es)), last saved {checkpoint.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}.";
        }

        ApplyMode();
    }

    private void Resume_OnChanged(object sender, RoutedEventArgs e) => ApplyMode();

    /// <summary>Resume shows the checkpoint's own settings, locked; a new export shows editable defaults.</summary>
    private void ApplyMode()
    {
        // The switch's Checked fires while the XAML is still loading, before the later controls exist.
        if (StartButton is null) return;
        var resume = Resume;

        DateFieldBox.Text = resume ? _checkpoint!.DateField : _newDateField;
        PathBox.Text = resume ? _checkpoint!.OutputPath : _newPath;
        DateFieldBox.IsEnabled = PathBox.IsEnabled = BrowseButton.IsEnabled = !resume;

        var size = resume ? _checkpoint!.BatchSize : _batchSize;
        BatchText.Text = resume
            ? $"Continues in batches of {size:N0} documents from where it stopped."
            : $"Reads every document in batches of {size:N0} (change it in Options). Progress is saved after " +
              "each batch: cancel or close the app, and Export… again resumes it.";
        StartButton.Content = resume ? "Resume export" : "Start export";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Browse_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the collection summary to",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = SafeDirectory(OutputPath),
            Filter = "JSON (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void Start_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Resume)
        {
            var problem = string.IsNullOrWhiteSpace(OutputPath) ? "Choose where to save the export."
                : SafeDirectory(OutputPath) is null ? "The folder of that path does not exist."
                : null;
            if (problem is not null)
            {
                ErrorText.Text = problem;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }

    private static string? SafeDirectory(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
