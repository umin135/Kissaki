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

        // Background-thread exceptions (not caught by DispatcherUnhandledException).
        // Log them before the CLR terminates the process.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogger.Exception("UnhandledException (background thread)", ex);
        };

        // Prevent OnLastWindowClose from killing the app while the launcher is closing
        // but before MainWindow is shown (no windows open in that gap).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Fire update check immediately — runs in parallel while the launcher is open.
        // ShowDialog() pumps a nested dispatcher loop, so the async continuation can
        // show UpdateWindow on top of the launcher without any extra threading tricks.
        var updateTask = UpdateService.CheckForUpdateAsync();

        var settings = SettingsService.Load();
        var launcher = new GameLauncherWindow(settings);
        _ = ShowUpdateIfAvailableAsync(updateTask, launcher);
        bool selected = launcher.ShowDialog() == true;

        if (!selected || launcher.LoadedViewModel is null)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnLastWindowClose;
        var main = new MainWindow(launcher.LoadedViewModel);
        MainWindow = main;
        main.Show();
        main.Activate();
    }

    private static async Task ShowUpdateIfAvailableAsync(Task<ReleaseInfo?> task, Window owner)
    {
        try
        {
            var info = await task;
            if (info is null) return;
            var win = new UpdateWindow(info) { Owner = owner };
            win.ShowDialog();
        }
        catch { /* network unavailable or rate-limited — silently skip */ }
    }
}
