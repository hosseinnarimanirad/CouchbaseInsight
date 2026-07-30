namespace CouchbaseInsight.Models;

/// <summary>A scope together with the collections it contains.</summary>
public class ScopeInfo
{
    public string Name { get; set; } = "";
    public List<string> Collections { get; set; } = new();
}
