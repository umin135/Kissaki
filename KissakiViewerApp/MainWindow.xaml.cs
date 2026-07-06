using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    // Column sort state
    private static readonly Dictionary<string, string> ColSortProp = new()
    {
        ["Name"]      = "DisplayName",
        ["Hash"]      = "KtidHex",
        ["Type"]      = "TypeExt",
        ["Container"] = "Container",
        ["Size"]      = "FileSize",
    };
    private GridViewColumnHeader? _lastSortHeader;
    private string?               _lastSortProp;
    private ListSortDirection     _sortDir = ListSortDirection.Ascending;

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

        // Re-apply sort when FilteredAssets is replaced by search/filter
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.FilteredAssets) && _lastSortProp != null)
                ApplySort(AssetListView.ItemsSource, _lastSortProp, _sortDir);
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

    private void AssetListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;

        // Strip any existing arrow to get the base label
        string label = (header.Content?.ToString() ?? "").TrimEnd().TrimEnd('↑', '↓').TrimEnd();
        if (!ColSortProp.TryGetValue(label, out string? prop)) return;

        if (_lastSortHeader == header)
        {
            _sortDir = _sortDir == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            // Restore previous header to plain label
            if (_lastSortHeader != null)
            {
                string prev = (_lastSortHeader.Content?.ToString() ?? "").TrimEnd().TrimEnd('↑', '↓').TrimEnd();
                _lastSortHeader.Content = prev;
            }
            _lastSortHeader = header;
            _lastSortProp   = prop;
            _sortDir        = ListSortDirection.Ascending;
        }

        header.Content = label + (_sortDir == ListSortDirection.Ascending ? "  ↑" : "  ↓");
        ApplySort(AssetListView.ItemsSource, prop, _sortDir);
    }

    private static void ApplySort(IEnumerable? source, string prop, ListSortDirection dir)
    {
        if (source == null) return;
        var view = CollectionViewSource.GetDefaultView(source);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(prop, dir));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        new HelpWindow { Owner = this }.ShowDialog();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewerWindow?.Close();
        base.OnClosed(e);
    }
}
