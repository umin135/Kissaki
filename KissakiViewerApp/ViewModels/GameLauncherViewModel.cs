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
        StatusText = "Steam 라이브러리 스캔 중...";

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
        StatusText = added > 0 ? $"{added}개 게임 추가됨." : "새로운 게임을 찾지 못했습니다.";
        IsScanning = false;
    }

    [RelayCommand]
    private void New()
    {
        var dlg = new VistaFolderBrowserDialog
        {
            Description            = "KatanaEngine 게임 폴더를 선택하세요 (fdata_package 포함)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != true) return;

        string dir = dlg.SelectedPath;
        if (!Directory.Exists(Path.Combine(dir, "fdata_package")))
        {
            StatusText = "fdata_package 폴더가 없습니다. KatanaEngine 게임 경로를 선택하세요.";
            return;
        }

        bool exists = Games.Any(g => string.Equals(g.GameDirectory, dir, StringComparison.OrdinalIgnoreCase));
        if (exists) { StatusText = "이미 목록에 있는 게임입니다."; return; }

        GameProfile? profile = SteamScanner.DetectFromDirectory(dir);
        if (profile == null) { StatusText = "게임 정보를 인식하지 못했습니다."; return; }

        Games.Add(profile);
        SelectedGame = profile;
        PersistSettings();
        StatusText = $"추가됨: {profile.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedGame is null) return;
        Games.Remove(SelectedGame);
        SelectedGame = null;
        PersistSettings();
        StatusText = "제거됨.";
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
