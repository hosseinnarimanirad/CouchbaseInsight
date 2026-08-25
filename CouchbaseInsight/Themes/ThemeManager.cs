using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace CouchbaseInsight.Themes;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Swaps the colour dictionary that every style pulls from through DynamicResource.
///
/// Only the colours are swapped — the control templates in Controls.xaml stay loaded and simply
/// repaint. Nothing is rebuilt and no binding is re-evaluated, so switching theme cannot disturb a
/// running query or lose a typed password.
///
/// The choice is remembered in <c>%APPDATA%\CouchbaseInsight\ui.json</c>, beside the connection
/// metadata. First run has no file and follows the Windows app theme instead.
/// </summary>
public static class ThemeManager
{
    private const int ThemeDictionaryIndex = 0;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CouchbaseInsight",
        "ui.json");

    private sealed class UiSettings
    {
        public string Theme { get; set; } = nameof(AppTheme.Dark);
    }

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after <see cref="Current"/> changes, for anything that holds colours the
    /// resource system cannot repaint on its own — the editor syntax highlighting, in practice.</summary>
    public static event Action<AppTheme>? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null || dictionaries.Count == 0)
            return;

        // Absolute rather than relative: a relative pack URI resolves against the entry assembly,
        // which is this exe when the app runs but the test host when it does not.
        dictionaries[ThemeDictionaryIndex] = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/CouchbaseInsight;component/Themes/{theme}.xaml",
                UriKind.Absolute)
        };

        ThemeChanged?.Invoke(theme);
    }

    public static void Toggle()
    {
        Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
        Save();
    }

    /// <summary>The remembered theme, or the Windows app theme on first run.</summary>
    public static AppTheme Preferred()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var stored = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
                if (stored is not null && Enum.TryParse<AppTheme>(stored.Theme, out var saved))
                    return saved;
            }
        }
        catch (Exception)
        {
            // Missing, corrupt or unreadable: fall through to the system theme.
        }

        return SystemTheme();
    }

    /// <summary>Matches the Windows app theme, defaulting to dark if it cannot be read.</summary>
    public static AppTheme SystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
                return light == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch (Exception)
        {
            // Not readable — fall through to the default.
        }

        return AppTheme.Dark;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(new UiSettings { Theme = Current.ToString() }));
        }
        catch (Exception)
        {
            // A preference that cannot be written is not worth failing a click over.
        }
    }
}
