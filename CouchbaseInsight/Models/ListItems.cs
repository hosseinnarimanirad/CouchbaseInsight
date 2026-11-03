namespace CouchbaseInsight.Models;

/// <summary>One line of the values pane: the value with its count, and a document that holds it.</summary>
public sealed record DistinctValueItem(string Text, string? ExampleKey)
{
    // The list's tooltip and Copy read the item as text.
    public override string ToString() => ExampleKey is null ? Text : $"{Text}    (e.g. {ExampleKey})";
}

/// <summary>One document of a class's sample: its key, and its body as indented JSON.</summary>
public sealed record SampleDocumentItem(string Key, string Json, string? Label = null)
{
    public string Display => Label is null ? Key : $"{Label}: {Key}";
    public override string ToString() => Key;
}
