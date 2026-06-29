using System.Windows;
using System.Windows.Input;
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
        if (_vm.SelectedGame != null && _vm.SelectCommand.CanExecute(null))
            _vm.SelectCommand.Execute(null);
    }
}
