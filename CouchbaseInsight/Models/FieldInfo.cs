using System.ComponentModel;

namespace CouchbaseInsight.Models;

public class FieldInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;

    /// <summary>Ticked for the next "Analyze selected" pass.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    /// <summary>Counted over every document of the class (not only the sample).</summary>
    public bool IsAnalyzed { get; set; }

    /// <summary>Never seen as an object or an array: a value that can be counted as it is.</summary>
    public bool IsScalar => !Types.Contains("object") && !Types.Contains("array");

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
