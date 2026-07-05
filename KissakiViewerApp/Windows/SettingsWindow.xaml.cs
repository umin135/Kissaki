using System.Diagnostics;
using System.IO;
using System.Windows;
using KissakiViewer.Models;
using KissakiViewer.Services;
using Ookii.Dialogs.Wpf;

namespace KissakiViewer.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        ExportDirBox.Text = AppSettingsService.GetEffectiveExportDirectory();
        UseRestoredNameCheck.IsChecked = AppSettingsService.Current.UseRestoredName;
    }

    private void BrowseExportDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new VistaFolderBrowserDialog
        {
            Description        = "Select export directory",
            UseDescriptionForTitle = true,
        };
        string current = ExportDirBox.Text.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            dlg.SelectedPath = current;

        if (dlg.ShowDialog(this) == true)
            ExportDirBox.Text = dlg.SelectedPath;
    }

    private void DeleteCache_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "All database caches will be deleted and Kissaki will restart.\n\n" +
            "Delete cache and restart Kissaki?",
            "Delete DB Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        foreach (string dir in AppSettingsService.GetCacheDirectories())
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* non-critical */ }
        }

        string? exePath = Environment.ProcessPath;
        if (exePath is not null)
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Save(new AppSettings
        {
            ExportDirectory  = ExportDirBox.Text.Trim(),
            UseRestoredName  = UseRestoredNameCheck.IsChecked == true,
        });
        Close();
    }

    private void ResetDefault_Click(object sender, RoutedEventArgs e)
    {
        ExportDirBox.Text = AppSettingsService.DefaultExportDirectory;
        UseRestoredNameCheck.IsChecked = false;
    }
}
