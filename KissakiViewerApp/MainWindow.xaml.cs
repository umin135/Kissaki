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

    public MainWindow(GameProfile profile)
    {
        InitializeComponent();
        _vm = new MainViewModel(profile);
        DataContext = _vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = _vm.LoadAsync();
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
