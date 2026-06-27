using System.Windows;
using System.Windows.Input;
using KissakiViewer.Models;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Windows;

public partial class GameLauncherWindow : Window
{
    public GameProfile? SelectedGame { get; private set; }

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
        SelectedGame = _vm.SelectedGame;
        DialogResult = true;
        Close();
    }

    private void GameListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedGame != null && _vm.SelectCommand.CanExecute(null))
            _vm.SelectCommand.Execute(null);
    }
}
