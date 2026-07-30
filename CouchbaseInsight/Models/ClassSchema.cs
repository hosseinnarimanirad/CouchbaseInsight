namespace CouchbaseInsight.Models;

/// <summary>
/// The inferred schema for one "class" (discriminator value) within a collection.
/// </summary>
public class ClassSchema
{
    public string ClassName { get; set; } = "";
    public int SampleSize { get; set; }
    public List<FieldInfo> Fields { get; set; } = new();
}
