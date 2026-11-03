using System.Globalization;
using System.Windows;
using CouchbaseInsight.Services;

namespace CouchbaseInsight;

/// <summary>Edits a copy of <see cref="AppOptions"/>; the caller saves it when this returns true.</summary>
public partial class OptionsWindow : Window
{
    public AppOptions Options { get; }

    public OptionsWindow(AppOptions current)
    {
        InitializeComponent();
        Options = new AppOptions
        {
            BatchSize = current.BatchSize,
            ParallelReads = current.ParallelReads,
            GentleMode = current.GentleMode,
            ScanLimitEnabled = current.ScanLimitEnabled,
            ScanLimit = current.ScanLimit
        };
        Show(Options);
    }

    private void Show(AppOptions o)
    {
        BatchSizeBox.Text = o.BatchSize.ToString("N0", CultureInfo.InvariantCulture);
        ParallelBox.Text = o.ParallelReads.ToString(CultureInfo.InvariantCulture);
        GentleSwitch.IsChecked = o.GentleMode;
        ParallelBox.IsEnabled = !o.GentleMode;
        ScanLimitSwitch.IsChecked = o.ScanLimitEnabled;
        ScanLimitBox.Text = o.ScanLimit.ToString("N0", CultureInfo.InvariantCulture);
        ScanLimitBox.IsEnabled = o.ScanLimitEnabled;
    }

    private void GentleSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        // Fires while the XAML is still loading, before the box below it exists.
        if (ParallelBox is not null) ParallelBox.IsEnabled = GentleSwitch.IsChecked != true;
    }

    private void ScanLimitSwitch_OnChanged(object sender, RoutedEventArgs e)
    {
        // Fires while the XAML is still loading, before the box below it exists.
        if (ScanLimitBox is not null) ScanLimitBox.IsEnabled = ScanLimitSwitch.IsChecked == true;
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e) => Show(new AppOptions());

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryParse(BatchSizeBox.Text, out var size) || size < AppOptions.MinBatchSize)
        {
            ShowError($"Batch size must be a whole number of at least {AppOptions.MinBatchSize:N0}.");
            return;
        }

        if (!TryParse(ParallelBox.Text, out var parallel) || parallel < 1 || parallel > AppOptions.MaxParallelReads)
        {
            ShowError($"Parallel reads must be a whole number from 1 to {AppOptions.MaxParallelReads}.");
            return;
        }

        var limitOn = ScanLimitSwitch.IsChecked == true;
        var limit = Options.ScanLimit;
        if (limitOn && (!TryParse(ScanLimitBox.Text, out limit) || limit <= 0))
        {
            ShowError("Scan limit must be a whole number above 0, or switch it off.");
            return;
        }

        Options.BatchSize = size;
        Options.ParallelReads = parallel;
        Options.GentleMode = GentleSwitch.IsChecked == true;
        Options.ScanLimitEnabled = limitOn;
        Options.ScanLimit = limit;
        DialogResult = true;
    }

    private static bool TryParse(string text, out int value) =>
        int.TryParse(text.Replace(",", "").Replace("_", "").Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
