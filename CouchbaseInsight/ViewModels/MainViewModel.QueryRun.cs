using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

/// <summary>
/// The guard in front of statements that change data. A statement is a write unless its first
/// keyword is a known read (<see cref="StatementClassifier"/>). Writes need two things: the
/// <see cref="AllowWrites"/> switch, off at launch and again on every connect or disconnect, and a
/// confirm card that shows the full statement and the cluster it goes to. Reads go with the
/// server's read-only flag, so a write that slipped past the classifier is refused by the cluster.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The switch beside Run. Never remembered: each session, and each connection, starts read-only.</summary>
    [ObservableProperty] private bool _allowWrites;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunQueryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmWriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelWriteCommand))]
    private bool _isWriteConfirmVisible;

    /// <summary>The statement the confirm card is asking about, exactly as it will be sent.</summary>
    [ObservableProperty] private string _pendingWriteStatement = "";

    /// <summary>The confirm card's "this goes to …" line.</summary>
    [ObservableProperty] private string _pendingWriteTarget = "";

    /// <summary>The pill over the result: "12 rows · 3.2ms", or "3 documents changed · 5.1ms".</summary>
    [ObservableProperty] private string _queryResultSummary = "";

    /// <summary>Whether what Run would send now is a write; drives the badge beside Run.</summary>
    public bool IsCurrentQueryWrite =>
        !string.IsNullOrWhiteSpace(QueryPreview) &&
        StatementClassifier.Classify(QueryPreview) == QueryKind.Write;

    partial void OnIsConnectedChanged(bool value) => AllowWrites = false;

    private void RequestWrite(string statement)
    {
        if (!AllowWrites)
        {
            var keyword = StatementClassifier.FirstKeyword(statement) ?? "This statement";
            Report($"{keyword} changes data and was not run. Turn on 'Allow writes' to run it.", StatusKind.Error);
            return;
        }

        PendingWriteStatement = statement;
        PendingWriteTarget = string.IsNullOrWhiteSpace(ConnectionSummary)
            ? "the connected cluster"
            : ConnectionSummary;
        IsWriteConfirmVisible = true;
    }

    // Enabled only while the card shows, so the window-wide Escape binding passes the key on otherwise.
    [RelayCommand(CanExecute = nameof(IsWriteConfirmVisible))]
    private async Task ConfirmWriteAsync()
    {
        if (!IsWriteConfirmVisible) return;
        var statement = PendingWriteStatement;
        CloseWriteConfirm();

        // Things can change while the card is open (a disconnect, the switch turned off).
        if (!IsConnected || !AllowWrites)
        {
            Report("The write was not run: the connection or the 'Allow writes' switch changed.", StatusKind.Error);
            return;
        }

        await ExecuteQueryAsync(statement, readOnly: false);
    }

    [RelayCommand(CanExecute = nameof(IsWriteConfirmVisible))]
    private void CancelWrite()
    {
        if (!IsWriteConfirmVisible) return;
        CloseWriteConfirm();
        Report("Write not run.", StatusKind.Idle);
    }

    private void CloseWriteConfirm()
    {
        IsWriteConfirmVisible = false;
        PendingWriteStatement = "";
        PendingWriteTarget = "";
    }

    private static string DescribeOutcome(QueryOutcome outcome, bool readOnly)
    {
        var parts = new List<string>();
        if (!readOnly)
            parts.Add(outcome.MutationCount is { } n
                ? $"{n:N0} document{(n == 1 ? "" : "s")} changed"
                : "changed: not reported");
        if (readOnly || outcome.Count > 0)
            parts.Add($"{outcome.Count:N0} row{(outcome.Count == 1 ? "" : "s")}");
        if (!string.IsNullOrEmpty(outcome.Elapsed))
            parts.Add(outcome.Elapsed);
        return string.Join(" · ", parts);
    }
}
