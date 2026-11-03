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
        Options = new AppOptions { BatchSize = current.BatchSize };
        BatchSizeBox.Text = Options.BatchSize.ToString("N0", CultureInfo.InvariantCulture);
    }

    private void Reset_OnClick(object sender, RoutedEventArgs e) =>
        BatchSizeBox.Text = AppOptions.DefaultBatchSize.ToString("N0", CultureInfo.InvariantCulture);

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var text = BatchSizeBox.Text.Replace(",", "").Replace("_", "").Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
            size < AppOptions.MinBatchSize)
        {
            ErrorText.Text = $"Batch size must be a whole number of at least {AppOptions.MinBatchSize:N0}.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Options.BatchSize = size;
        DialogResult = true;
    }
}
