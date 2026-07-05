using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using KissakiViewer.Services;

namespace KissakiViewer.Windows;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{AppSettingsService.AppVersion}";
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
