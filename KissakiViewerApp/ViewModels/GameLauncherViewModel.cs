using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KissakiViewer.Models;
using KissakiViewer.Services;
using Ookii.Dialogs.Wpf;

namespace KissakiViewer.ViewModels;

public sealed partial class GameLauncherViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectCommand))]
    private GameProfile? _selectedGame;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    public ObservableCollection<GameProfile> Games { get; } = [];

    private readonly KissakiSettings _settings;

    public GameLauncherViewModel(KissakiSettings settings)
    {
        _settings = settings;
        foreach (var g in settings.Games)
            Games.Add(g);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        StatusText = "Scanning Steam libraries...";

        List<GameProfile> found = await Task.Run(SteamScanner.Scan);

        int added = 0;
        foreach (var p in found)
        {
            bool exists = Games.Any(g =>
                string.Equals(g.GameDirectory, p.GameDirectory, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                Games.Add(p);
                added++;
            }
        }

        PersistSettings();
        StatusText = added > 0 ? $"{added} game(s) added." : "No new games found.";
        IsScanning = false;
    }

    [RelayCommand]
    private void New()
    {
        var dlg = new VistaFolderBrowserDialog
        {
            Description            = "Select KatanaEngine game folder (containing root.rdb)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != true) return;

        string dir = dlg.SelectedPath;
        if (SteamScanner.FindRdbDir(dir) == null)
        {
            StatusText = "root.rdb not found. Please select a KatanaEngine game folder.";
            return;
        }

        bool exists = Games.Any(g => string.Equals(g.GameDirectory, dir, StringComparison.OrdinalIgnoreCase));
        if (exists) { StatusText = "This game is already in the list."; return; }

        GameProfile? profile = SteamScanner.DetectFromDirectory(dir);
        if (profile == null) { StatusText = "Could not detect game information."; return; }

        Games.Add(profile);
        SelectedGame = profile;
        PersistSettings();
        StatusText = $"Added: {profile.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedGame is null) return;
        Games.Remove(SelectedGame);
        SelectedGame = null;
        PersistSettings();
        StatusText = "Removed.";
    }
    private bool CanRemove() => SelectedGame != null;

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void Select()
    {
        SelectRequested?.Invoke(this, EventArgs.Empty);
    }
    private bool CanSelect() => SelectedGame != null;

    public event EventHandler? SelectRequested;

    private void PersistSettings()
    {
        _settings.Games = [.. Games];
        SettingsService.Save(_settings);
    }
}
