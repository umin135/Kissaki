using CommunityToolkit.Mvvm.ComponentModel;
using KissakiViewer.Core;

namespace KissakiViewer.ViewModels;

public sealed partial class AssetItemViewModel : ObservableObject
{
    public AssetRecord Record { get; }

    public string KtidHex    => $"0x{Record.FileKtid:x8}";
    public string TypeExt    => Record.TypeExt;
    public string Storage    => Record.Storage.ToString();
    public string SizeStr    => FormatSize(Record.FileSize);
    public string Container  { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _recoveredName;

    // Shows recovered name if available, otherwise falls back to hex KTID
    public string DisplayName => _recoveredName ?? KtidHex;

    [ObservableProperty]
    private bool _isSelected;

    public AssetItemViewModel(AssetRecord record, string container)
    {
        Record    = record;
        Container = container;
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024        => $"{bytes / 1024.0:F1} KB",
        _              => $"{bytes} B",
    };
}
