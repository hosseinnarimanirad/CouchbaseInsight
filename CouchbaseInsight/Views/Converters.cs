using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CouchbaseInsight.Views;

/// <summary>
/// The view-only converters. All of them are layout or logic — never colour, which belongs to the
/// theme dictionaries and reaches controls through DynamicResource.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Visible when the bound string is null, empty or whitespace. Drives empty-state text.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound string has content.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound count is zero — the empty state behind a list or grid.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound count is one or more.</summary>
public sealed class PositiveToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The parent path of a dotted field name, trailing dot included — <c>address.geo.lat</c> becomes
/// <c>address.geo.</c>, and a top-level field becomes the empty string. The schema grid renders it
/// dimmed in front of the leaf, which is how a nested field is legible in a flat list: the eye
/// reads the ten leaves down the column instead of ten near-identical prefixes.
/// </summary>
public sealed class PathPrefixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string ?? string.Empty;
        var cut = path.LastIndexOf('.');
        return cut < 0 ? string.Empty : path[..(cut + 1)];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>The last segment of a dotted field name. Pairs with <see cref="PathPrefixConverter"/>.</summary>
public sealed class PathLeafConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string ?? string.Empty;
        var cut = path.LastIndexOf('.');
        return cut < 0 ? path : path[(cut + 1)..];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Splits the analyzer's comma-joined type list into its parts, so each one can be drawn as its own
/// badge. <c>FieldInfo.Types</c> is built with <c>string.Join(", ", …)</c>; nothing else produces a
/// comma, since an array's element types are joined with <c>|</c> inside the angle brackets.
/// </summary>
public sealed class CsvToListConverter : IValueConverter
{
    private static readonly string[] Separator = [","];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        return text.Split(Separator, StringSplitOptions.RemoveEmptyEntries)
                   .Select(part => part.Trim())
                   .ToArray();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A 0–100 percentage as a width in device pixels, scaled by the track width passed as the
/// converter parameter. Below one pixel the fill is clamped up rather than to zero: a field seen in
/// 3 of 5000 documents still has to draw something, or "rare" and "absent" look identical.
/// </summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value switch
        {
            double d => d,
            int i => i,
            _ => 0d
        };

        var track = parameter is string s &&
                    double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var width)
            ? width
            : 54d;

        var scaled = Math.Clamp(percent, 0, 100) / 100d * track;
        return percent > 0 ? Math.Max(scaled, 2d) : 0d;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
