using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KissakiViewer.ViewModels;

public sealed partial class FolderNode : ObservableObject
{
    public string Name      { get; }
    public string FullPath  { get; }
    public bool   IsUnknown { get; }

    [ObservableProperty]
    private int _assetCount;

    public string DisplayName => IsUnknown
        ? $"Unknown ({AssetCount})"
        : Name;

    public ObservableCollection<FolderNode> Children { get; } = [];

    public FolderNode(string name, string fullPath, bool isUnknown = false)
    {
        Name      = name;
        FullPath  = fullPath;
        IsUnknown = isUnknown;
    }
}
