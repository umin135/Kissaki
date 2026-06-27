using CommunityToolkit.Mvvm.ComponentModel;
using KissakiViewer.Core;

namespace KissakiViewer.ViewModels;

public sealed partial class AssetItemViewModel : ObservableObject
{
    public AssetRecord Record    { get; }
    public string      KtidHex  => $"0x{Record.FileKtid:x8}";
    public string      TypeExt  => Record.TypeExt;
    public string      Storage  => Record.Storage.ToString();
    public string      SizeStr  => FormatSize(Record.FileSize);
    public string      Container { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(DisplayFileName))]
    [NotifyPropertyChangedFor(nameof(FolderPath))]
    private string? _recoveredName;

    // "kasumi_body" or "0x1234abcd"
    public string DisplayName => _recoveredName != null
        ? GetBaseName(_recoveredName)
        : KtidHex;

    // "kasumi_body.g1m" or "0x1234abcd.g1m"
    public string DisplayFileName => DisplayName + TypeExt;

    // "character/kasumi" — empty string if no recovered name or no folder
    public string FolderPath => _recoveredName != null
        ? GetFolderPart(_recoveredName)
        : string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    public AssetItemViewModel(AssetRecord record, string container)
    {
        Record    = record;
        Container = container;
    }

    private static string GetBaseName(string path)
    {
        path = path.Replace('\\', '/');
        int i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string GetFolderPart(string path)
    {
        path = path.Replace('\\', '/');
        int i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : string.Empty;
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024        => $"{bytes / 1024.0:F1} KB",
        _              => $"{bytes} B",
    };
}
