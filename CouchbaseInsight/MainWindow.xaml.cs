using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
}
