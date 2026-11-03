using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CouchbaseInsight.Themes;
using CouchbaseInsight.ViewModels;

namespace CouchbaseInsight;

public partial class MainWindow : Window
{
    // Guards against the two-way password sync below echoing back on itself.
    private bool _syncingPassword;

    // True when DWM draws the window edge itself (Windows 11); false means WPF draws it.
    private bool _nativeEdge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateChrome();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme _) => UpdateEdgeColour();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        UpdateEdgeColour();
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

        // Restored: a 1px edge of our own, unless DWM already draws one.
        BorderThickness = maximised
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1)
            : new Thickness(_nativeEdge ? 0 : 1);
        SetResourceReference(BorderBrushProperty, maximised ? "Brush.Window" : "Brush.WindowEdge");

        MaximiseButton.Content = maximised ? "" : "";
        MaximiseButton.ToolTip = maximised ? "Restore" : "Maximise";
    }

    /// <summary>
    /// Paints the window edge in the theme's Brush.WindowEdge. Windows 11 takes it as the DWM
    /// border colour, which follows the rounded corners; older Windows rejects the attribute, and
    /// the window's own BorderBrush draws a square 1px edge instead. When maximised the brush is
    /// set to the window background, so the inset in UpdateChrome stays invisible.
    /// </summary>
    private void UpdateEdgeColour()
    {
        var edge = (TryFindResource("Brush.WindowEdge") as SolidColorBrush)?.Color ?? Colors.Gray;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int colorRef = edge.R | (edge.G << 8) | (edge.B << 16);
            _nativeEdge = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int)) == 0;
        }

        UpdateChrome();
    }

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
