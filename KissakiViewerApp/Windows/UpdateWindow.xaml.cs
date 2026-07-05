using System.Windows;
using KissakiViewer.Services;

namespace KissakiViewer.Windows;

public partial class UpdateWindow : Window
{
    private readonly string _downloadUrl;

    public UpdateWindow(ReleaseInfo info)
    {
        InitializeComponent();
        VersionLabel.Text = $"Available: {info.TagName}  ·  Current: v{AppSettingsService.AppVersion}";
        ChangelogText.Text = info.ChangeLog;
        _downloadUrl = info.DownloadUrl;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        NotNowButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading...";

        try
        {
            var progress = new Progress<int>(p =>
            {
                ProgressBar.Value = p;
                StatusText.Text = $"Downloading... {p}%";
            });
            await UpdateService.DownloadAndApplyAsync(_downloadUrl, progress);
            // App.Shutdown() is called inside DownloadAndApplyAsync — execution never reaches here
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update failed: {ex.Message}";
            UpdateButton.IsEnabled = true;
            NotNowButton.IsEnabled = true;
        }
    }

    private void NotNow_Click(object sender, RoutedEventArgs e) => Close();
}
