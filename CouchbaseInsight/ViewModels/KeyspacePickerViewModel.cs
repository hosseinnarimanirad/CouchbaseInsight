using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;

namespace CouchbaseInsight.ViewModels;

/// <summary>
/// Bucket › scope › collection, on its own: the lists, the selection, and loading a bucket's
/// scopes. It does not use the window's busy gate, so it never waits on (or blocks) a long Schema
/// load; a newer bucket choice simply supersedes a scope listing still on its way.
/// </summary>
public partial class KeyspacePickerViewModel : ObservableObject
{
    private readonly ICouchbaseService _couchbase;
    private readonly Action<string, StatusKind> _report;
    private int _scopeRequest;

    public KeyspacePickerViewModel(
        ICouchbaseService couchbase, ObservableCollection<string> buckets, Action<string, StatusKind> report)
    {
        _couchbase = couchbase;
        Buckets = buckets;
        _report = report;
    }

    /// <summary>The connection's buckets; shared, since they belong to the connection, not to a picker.</summary>
    public ObservableCollection<string> Buckets { get; }

    public ObservableCollection<ScopeInfo> Scopes { get; } = new();
    public ObservableCollection<string> Collections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Keyspace))]
    private string? _selectedBucket;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Keyspace))]
    private ScopeInfo? _selectedScope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Keyspace))]
    private string? _selectedCollection;

    /// <summary>`bucket`.`scope`.`collection`, or empty until all three are picked.</summary>
    public string Keyspace =>
        SelectedBucket is null || SelectedScope is null || string.IsNullOrEmpty(SelectedCollection)
            ? ""
            : SqlText.Keyspace(SelectedBucket, SelectedScope.Name, SelectedCollection);

    /// <summary>Completes when the scopes of the last chosen bucket are listed (or failed to be).</summary>
    private Task _scopesLoaded = Task.CompletedTask;

    partial void OnSelectedBucketChanged(string? value)
    {
        Scopes.Clear();
        Collections.Clear();
        SelectedScope = null;
        SelectedCollection = null;
        _scopesLoaded = value is null ? Task.CompletedTask : LoadScopesAsync(value, ++_scopeRequest);
    }

    partial void OnSelectedScopeChanged(ScopeInfo? value)
    {
        Collections.Clear();
        SelectedCollection = null;
        if (value is null) return;
        foreach (var c in value.Collections) Collections.Add(c);
    }

    private async Task LoadScopesAsync(string bucket, int request)
    {
        try
        {
            var scopes = await _couchbase.GetScopesAsync(bucket);
            if (request != _scopeRequest) return; // another bucket was picked meanwhile

            foreach (var s in scopes) Scopes.Add(s);
            // Same default as the Schema picker: the bucket's own scopes before _default and _system.
            SelectedScope = Scopes.FirstOrDefault(sc => !sc.Name.StartsWith('_')) ?? Scopes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (request == _scopeRequest)
                _report($"Could not list the scopes of '{bucket}': {ExceptionReport.Summarize(ex)}", StatusKind.Error);
        }
    }

    /// <summary>Picks a whole path, waiting for the bucket's scopes; a part that does not exist is left unpicked.</summary>
    public async Task SelectAsync(string bucket, string scope, string collection)
    {
        if (SelectedBucket != bucket) SelectedBucket = Buckets.Contains(bucket) ? bucket : null;
        await _scopesLoaded;
        if (SelectedBucket != bucket) return;

        SelectedScope = Scopes.FirstOrDefault(s => s.Name == scope);
        if (SelectedScope is not null && Collections.Contains(collection))
            SelectedCollection = collection;
    }

    /// <summary>Back to nothing picked, e.g. on disconnect.</summary>
    public void Reset()
    {
        ++_scopeRequest;
        SelectedBucket = null;
        Scopes.Clear();
        Collections.Clear();
    }
}
