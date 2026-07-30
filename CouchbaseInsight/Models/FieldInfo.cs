namespace CouchbaseInsight.Models;

public class FieldInfo
{
    public string Name { get; set; } = "";
    public string Types { get; set; } = "";
    public int Occurrences { get; set; }
    public int SampleSize { get; set; }

    /// <summary>True when another field exists with the same name under a different casing.</summary>
    public bool HasCaseDuplicate { get; set; }

    public double PresencePercent => SampleSize == 0 ? 0 : Occurrences * 100.0 / SampleSize;
    public bool IsRequired => SampleSize > 0 && Occurrences == SampleSize;
    public string RequirementLabel => IsRequired ? "Required" : "Optional";

    /// <summary>True when the field was observed with more than one JSON type across documents.</summary>
    public bool HasMultipleTypes => Types.Contains(',');

    /// <summary>Human-readable summary of data quality issues for this field.</summary>
    public string Issues
    {
        get
        {
            if (HasCaseDuplicate && HasMultipleTypes) return "Case conflict, Multi-type";
            if (HasCaseDuplicate) return "Case conflict";
            if (HasMultipleTypes) return "Multi-type";
            return "";
        }
    }
}
