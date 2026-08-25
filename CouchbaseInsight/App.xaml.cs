using System.Windows;
using Microsoft.Extensions.Configuration;
using CouchbaseInsight.Models;
using CouchbaseInsight.Services;
using CouchbaseInsight.Themes;
using CouchbaseInsight.ViewModels;

namespace CouchbaseInsight;

public partial class App : Application
{
    private CouchbaseService? _couchbase;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window exists, so the first frame is already in the right theme rather than
        // flashing the dictionary that App.xaml happened to merge.
        ThemeManager.Apply(ThemeManager.Preferred());

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<App>(optional: true)
            .Build();

        var store = new ConnectionStore(new WindowsCredentialStore());
        var profiles = store.Load();

        // First-run seed: migrate the single appsettings/user-secrets connection into a saved
        // profile so existing users keep their configured connection.
        if (profiles.Count == 0)
        {
            var settings = config.GetSection("Couchbase").Get<ConnectionSettings>();
            if (settings is not null && !string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                profiles.Add(new ConnectionProfile
                {
                    Name = "Default",
                    ConnectionString = settings.ConnectionString,
                    Username = settings.Username,
                    Password = settings.Password,
                    UseNetworkResolutionExternal = settings.UseNetworkResolutionExternal
                });
                store.Save(profiles);
            }
        }

        _couchbase = new CouchbaseService();
        var window = new MainWindow
        {
            DataContext = new MainViewModel(_couchbase, store, profiles)
        };
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_couchbase is not null)
            await _couchbase.DisposeAsync();
        base.OnExit(e);
    }
}
