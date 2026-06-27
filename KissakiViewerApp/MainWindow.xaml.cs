using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KissakiViewer.Models;
using KissakiViewer.ViewModels;
using KissakiViewer.Windows;

namespace KissakiViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private AssetViewerWindow? _viewerWindow;
    private double _savedConsoleHeight = 160;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = _vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Populate with log lines already accumulated during the launch load phase
        foreach (var line in _vm.ConsoleLog)
            ConsoleTextBox.AppendText(line + "\n");
        if (_vm.ConsoleLog.Count > 0)
            ConsoleTextBox.ScrollToEnd();

        // Auto-scroll and append when new lines arrive (e.g. from BuildG1mMapAsync)
        _vm.ConsoleLog.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not { Count: > 0 }) return;
            foreach (var item in args.NewItems)
                ConsoleTextBox.AppendText((string)item + "\n");
            ConsoleTextBox.ScrollToEnd();
        };
    }

    private void ConsoleToggle_Click(object sender, RoutedEventArgs e)
    {
        var splitterRow = RootGrid.RowDefinitions[2];
        var consoleRow  = RootGrid.RowDefinitions[3];
        if (consoleRow.ActualHeight > 26)
        {
            _savedConsoleHeight    = consoleRow.ActualHeight;
            consoleRow.Height      = new GridLength(26);
            splitterRow.Height     = new GridLength(0);
            ConsoleToggleBtn.Content = "▸";
        }
        else
        {
            consoleRow.Height      = new GridLength(_savedConsoleHeight);
            splitterRow.Height     = new GridLength(5);
            ConsoleToggleBtn.Content = "▾";
        }
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _vm.SelectedFolderNode = e.NewValue as FolderNode;
    }

    private void AssetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var asset = _vm.SelectedAsset;
        if (asset == null || _vm.Extractor == null) return;
        OpenInViewer(asset);
    }

    private void OpenInViewer(AssetItemViewModel asset)
    {
        if (_viewerWindow == null || !_viewerWindow.IsVisible)
        {
            _viewerWindow = new AssetViewerWindow(
                _vm.Extractor!,
                _vm.AllG1tByFid,
                _vm.AllAssetsByKtid,
                () => _vm.G1mToG1tMap);
            _viewerWindow.Owner = this;
            _viewerWindow.Show();
        }

        _viewerWindow.OpenAsset(asset);
        _viewerWindow.Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewerWindow?.Close();
        base.OnClosed(e);
    }
}
