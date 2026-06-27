using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using KissakiViewer.Models;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Windows;

public partial class GameLauncherWindow : Window
{
    public GameProfile? SelectedGame { get; private set; }

    /// <summary>Pre-loaded ViewModel passed to MainWindow after successful load.</summary>
    public MainViewModel? LoadedViewModel { get; private set; }

    private readonly GameLauncherViewModel _vm;
    private PropertyChangedEventHandler?          _vmPropChangedHandler;
    private NotifyCollectionChangedEventHandler?  _logChangedHandler;

    public GameLauncherWindow(Models.KissakiSettings settings)
    {
        InitializeComponent();
        _vm = new GameLauncherViewModel(settings);
        DataContext = _vm;
        _vm.SelectRequested += OnSelectRequested;
    }

    private async void OnSelectRequested(object? sender, EventArgs e)
    {
        SelectedGame = _vm.SelectedGame;
        if (SelectedGame is null) return;

        // Switch to loading phase
        GameListView.Visibility      = Visibility.Collapsed;
        LoadingPhasePanel.Visibility = Visibility.Visible;

        // Disable all buttons during load
        foreach (var child in ((System.Windows.Controls.Grid)Content).Children)
            if (child is System.Windows.Controls.Border b &&
                b.Child is System.Windows.Controls.Grid g)
                g.IsEnabled = false;

        var mainVm = new MainViewModel(SelectedGame);

        // Wire status/progress updates into the loading panel
        // Always marshal to UI thread — PropertyChanged can fire from background tasks
        _vmPropChangedHandler = (s, args) => Dispatcher.BeginInvoke(() =>
        {
            if (args.PropertyName == nameof(MainViewModel.StatusText))
                LoadStatusBlock.Text = mainVm.StatusText;
            if (args.PropertyName == nameof(MainViewModel.LoadProgress))
                LoadProgressBar.Value = mainVm.LoadProgress;
        });
        mainVm.PropertyChanged += _vmPropChangedHandler;

        // Stream log lines into the loading console as plain text
        _logChangedHandler = (s, args) =>
        {
            if (args.NewItems is not { Count: > 0 }) return;
            foreach (var item in args.NewItems)
                LoadingConsoleTextBox.AppendText((string)item + "\n");
            LoadingConsoleTextBox.ScrollToEnd();
        };
        mainVm.ConsoleLog.CollectionChanged += _logChangedHandler;

        await mainVm.LoadAsync();

        // Unsubscribe before closing — prevents callbacks from BuildG1mMapAsync
        // hitting controls on an already-closing window.
        mainVm.ConsoleLog.CollectionChanged -= _logChangedHandler;
        mainVm.PropertyChanged              -= _vmPropChangedHandler;

        LoadedViewModel = mainVm;
        DialogResult    = true;
        Close();
    }

    private void GameListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedGame != null && _vm.SelectCommand.CanExecute(null))
            _vm.SelectCommand.Execute(null);
    }
}
