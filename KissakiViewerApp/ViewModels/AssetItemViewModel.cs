using CommunityToolkit.Mvvm.ComponentModel;
using KissakiViewer.Core;
using KissakiViewer.Core.Formats;

namespace KissakiViewer.ViewModels;

public sealed partial class AssetItemViewModel : ObservableObject
{
    public AssetRecord Record    { get; }
    public string      KtidHex  => $"0x{Record.FileKtid:x8}";
    public string      TypeExt  => Record.TypeExt;
    public bool        IsG1m    => Record.TypeKtid is 0x563bdef1u or 0xBEF563DDu;
    public string      Storage  => Record.Storage.ToString();
    public ulong       FileSize => Record.FileSize;
    public string      SizeStr  => FormatSize(Record.FileSize);
    public string      Container { get; }
    /// <summary>Source RDB filename (e.g. "root.rdb", "system.rdb"). Used as top-level folder in the tree.</summary>
    public string      RdbName  { get; }

    /// <summary>
    /// True when the RDB indexes this asset but its .fdata container is not on disk.
    /// Retail builds keep index entries for containers that were never shipped
    /// (e.g. every .name entry in DOA6), so these assets can be listed but not extracted.
    /// </summary>
    public bool        IsContainerMissing { get; }

    /// <summary>Row tooltip explaining why the asset cannot be extracted (null when available).</summary>
    public string?     UnavailableReason => IsContainerMissing
        ? $"Container not installed: {Container}\nThis asset is listed in the RDB index, but its data is not in the game folder and cannot be extracted."
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(DisplayFileName))]
    [NotifyPropertyChangedFor(nameof(FolderPath))]
    private string? _recoveredName;

    // "kasumi_body" or "0x1234abcd"
    public string DisplayName => RecoveredName != null
        ? GetBaseName(RecoveredName)
        : KtidHex;

    // "kasumi_body.g1m" or "0x1234abcd.g1m"
    public string DisplayFileName => DisplayName + TypeExt;

    // "character/kasumi" — TypeKtid category if no recovered name with path
    // Note: embedded paths may use backslashes (Windows-style); check both separators
    public string FolderPath
    {
        get
        {
            if (RecoveredName is null) return KtidExtension.GetCategory(Record.TypeKtid);
            string normalized = RecoveredName.Replace('\\', '/');
            int sep = normalized.LastIndexOf('/');
            return sep > 0 ? normalized[..sep] : KtidExtension.GetCategory(Record.TypeKtid);
        }
    }

    [ObservableProperty]
    private bool _isSelected;

    public AssetItemViewModel(AssetRecord record, string container, string rdbName = "root.rdb",
                              bool isContainerMissing = false)
    {
        Record             = record;
        Container          = container;
        RdbName            = rdbName;
        IsContainerMissing = isContainerMissing;
    }

    private static string GetBaseName(string path)
    {
        path = path.Replace('\\', '/');
        int i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string FormatSize(ulong bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024        => $"{bytes / 1024.0:F1} KB",
        _              => $"{bytes} B",
    };
}
