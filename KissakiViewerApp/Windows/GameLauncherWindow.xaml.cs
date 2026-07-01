using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KissakiViewer.Models;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Windows;

public partial class GameLauncherWindow : Window
{
    /// <summary>ViewModel passed to MainWindow — created immediately on Select, not pre-loaded.</summary>
    public MainViewModel? LoadedViewModel { get; private set; }

    private readonly GameLauncherViewModel _vm;

    public GameLauncherWindow(Models.KissakiSettings settings)
    {
        InitializeComponent();
        _vm = new GameLauncherViewModel(settings);
        DataContext = _vm;
        _vm.SelectRequested += OnSelectRequested;
    }

    private void OnSelectRequested(object? sender, EventArgs e)
    {
        if (_vm.SelectedGame is null) return;
        // Create the VM immediately and close — MainWindow handles all loading in Window_Loaded.
        LoadedViewModel = new MainViewModel(_vm.SelectedGame);
        DialogResult    = true;
        Close();
    }

    private void GameListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only act when the click originated from a ListViewItem.
        // Without this, clicking empty space below items (or the scroll bar) with a previously
        // selected game would silently close the launcher.
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null && hit is not ListViewItem)
        {
            var parent = VisualTreeHelper.GetParent(hit);
            if (parent == null) return;   // reached the visual root without hitting an item
            hit = parent;
        }
        if (hit is not ListViewItem) return;

        if (_vm.SelectedGame != null && _vm.SelectCommand.CanExecute(null))
            _vm.SelectCommand.Execute(null);
    }
}
