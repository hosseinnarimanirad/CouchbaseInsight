namespace CouchbaseInsight.Models;

/// <summary>One class in the left-hand list: its discriminator value, how many of the documents
/// read carry it, and what share of those documents that is.</summary>
public sealed record ClassEntry(string Name, long Count, double Percent, string? Detail = null)
{
    public string CountText => $"{Count:N0} · {Percent:0.#}%";

    /// <summary>Tooltip: the counts, plus the date range when the entry came from an export file.</summary>
    public string Tip => Detail is null ? CountText : $"{CountText}\n{Detail}";

    // The ListBox's type-to-select and accessibility read the item as text.
    public override string ToString() => Name;
}

/// <summary>The result of Load classes: the classes, and the totals their percentages are of.</summary>
public sealed record ClassCounts(IReadOnlyList<ClassEntry> Classes, long DocumentsRead, long WithoutClass);
