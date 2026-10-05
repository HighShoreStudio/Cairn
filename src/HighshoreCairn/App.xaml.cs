using System.Windows;
using System.Windows.Threading;
using HighshoreCairn.Services;
using HighshoreCairn.ViewModels;
using HighshoreCairn.Views;

namespace HighshoreCairn;

/// <summary>Application entry point: wires services, the root ViewModel and the main window.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            Directory.CreateDirectory(AppPaths.Root);

            var accounts = new AccountService(new DpapiProtector());
            var projects = new ProjectService();
            var dialogs = new DialogService();
            var main = new MainViewModel(accounts, projects, dialogs, new ThemeService(), new SettingsService(), new SoundService())
            {
                DocConverter = new DocConverter(),
                Icons = Helpers.IconArt.Library
            };

            var window = new MainWindow { DataContext = main };
            ThemeService.Track(window);
            main.ExitRequested = window.Close;
            // Closing the window closes the project too: open documents are saved, the timer stops.
            window.Closing += (_, _) => main.Shutdown();
            MainWindow = window;

            main.Start();
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Cairn could not start.\n\n" + ex, "Cairn",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Data is saved after every change, so it is safe to keep the app running.
        MessageBox.Show("An unexpected error occurred:\n\n" + e.Exception.Message, "Cairn",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
