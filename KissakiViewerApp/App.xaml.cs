using System.Windows;
using KissakiViewer.Services;
using KissakiViewer.Windows;

namespace KissakiViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Exception("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        // Prevent OnLastWindowClose from killing the app while the launcher is closing
        // but before MainWindow is shown (no windows open in that gap).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = SettingsService.Load();
        var launcher = new GameLauncherWindow(settings);
        bool selected = launcher.ShowDialog() == true;

        if (!selected || launcher.LoadedViewModel is null)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        var main = new MainWindow(launcher.LoadedViewModel);
        MainWindow = main;   // ensure WPF tracks this as the main window
        main.Show();
        main.Activate();
    }
}
