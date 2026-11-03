using System.ComponentModel;

namespace CouchbaseInsight.Models;

/// <summary>
/// One class in the left-hand list: its discriminator value, how many documents carry it, what
/// share of the documents that is, and how far its analysis has got (<see cref="Status"/>), which
/// changes as fields are listed and analyzed without the list being rebuilt.
/// </summary>
public sealed class ClassEntry : INotifyPropertyChanged
{
    public const string Counted = "counted";
    public const string Sampled = "sampled";
    public const string PartlyAnalyzed = "partly analyzed";
    public const string Analyzed = "analyzed";
    public const string Loaded = "loaded";

    public ClassEntry(string name, long count, double percent, string? detail = null, string status = Loaded)
    {
        Name = name;
        Count = count;
        Percent = percent;
        _detail = detail;
        _status = status;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public long Count { get; }
    public double Percent { get; }

    private string? _detail;
    public string? Detail
    {
        get => _detail;
        set { _detail = value; Changed(nameof(Detail)); Changed(nameof(Tip)); }
    }

    private string _status;

    /// <summary>counted · sampled · partly analyzed · analyzed (two-step), or loaded (Deep load / file).</summary>
    public string Status
    {
        get => _status;
        set { _status = value; Changed(nameof(Status)); Changed(nameof(Glyph)); Changed(nameof(Tip)); }
    }

    /// <summary>The mark before the name: ○ counted, ◐ fields listed, ◑ some fields analyzed, ● done.</summary>
    public string Glyph => Status switch
    {
        Counted => "○",
        Sampled => "◐",
        PartlyAnalyzed => "◑",
        _ => "●"
    };

    public string StatusText => Status switch
    {
        Counted => "Counted only: select it (or Analyze all classes) to list its fields",
        Sampled => "Fields listed from a sample; none analyzed yet",
        PartlyAnalyzed => "Some of its fields analyzed over every document",
        Analyzed => "All of its scalar fields analyzed over every document",
        _ => "Summarised from the documents read"
    };

    public string CountText => $"{Count:N0} · {Percent:0.#}%";

    public string Tip => $"{CountText}\n{StatusText}" + (Detail is null ? "" : $"\n{Detail}");

    // The ListBox's type-to-select and accessibility read the item as text.
    public override string ToString() => Name;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
