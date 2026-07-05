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

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Wire console auto-scroll before any loading starts.
        _vm.ConsoleLog.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not { Count: > 0 }) return;
            foreach (var item in args.NewItems)
                ConsoleTextBox.AppendText((string)item + "\n");
            ConsoleTextBox.ScrollToEnd();
        };

        // Phase 1 — fast: parse RDB and populate the asset list.
        await _vm.LoadAsync();

        // Phase 2 — slow: name recovery, folder tree, G1M→G1T map.
        // The browser window is already visible and responsive at this point.
        await _vm.LoadBackgroundAsync();
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
                _vm.MasterDokCache);
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
