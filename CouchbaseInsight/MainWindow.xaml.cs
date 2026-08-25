using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CouchbaseInsight.Themes;
using CouchbaseInsight.ViewModels;

namespace CouchbaseInsight;

public partial class MainWindow : Window
{
    // Guards against the two-way password sync below echoing back on itself.
    private bool _syncingPassword;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateChrome();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateChrome();
        UpdateThemeButton();

        if (DataContext is not MainViewModel vm) return;

        // Sync the initial password (from the loaded profile) into the PasswordBox, which
        // can't be data-bound, and keep it in sync when the profile is switched.
        SetPasswordBox(vm.Password);
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    // VM -> PasswordBox: fired when switching saved connections sets Password programmatically.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Password) && DataContext is MainViewModel vm)
            SetPasswordBox(vm.Password);
    }

    // PasswordBox -> VM: push user-typed password into the ViewModel.
    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        if (DataContext is MainViewModel vm && sender is PasswordBox box)
            vm.Password = box.Password;
    }

    private void SetPasswordBox(string password)
    {
        if (PasswordBox.Password == password) return;
        _syncingPassword = true;
        try { PasswordBox.Password = password; }
        finally { _syncingPassword = false; }
    }

    // ---- Window chrome ----
    //
    // The window draws its own caption, so the three buttons on the right and the maximised-state
    // inset are this class's job rather than the shell's.

    private void Minimise_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximiseRestore_OnClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A WindowChrome window maximises to the monitor's full rectangle, which puts the outer few
    /// pixels of the content — the resize border's worth — off the edge of the screen. The inset
    /// gives them back, and the button glyph follows the state.
    /// </summary>
    private void UpdateChrome()
    {
        var maximised = WindowState == WindowState.Maximized;

        BorderThickness = maximised
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1)
            : new Thickness(0);

        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }

    private void ThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeButton();
    }

    // The glyph names the theme you would switch to, not the one you are in: a sun on a dark
    // window, a moon on a light one.
    private void UpdateThemeButton()
    {
        var dark = ThemeManager.Current == AppTheme.Dark;
        ThemeButton.Content = dark ? "" : "";
        ThemeButton.ToolTip = dark ? "Switch to the light theme" : "Switch to the dark theme";
    }
}
