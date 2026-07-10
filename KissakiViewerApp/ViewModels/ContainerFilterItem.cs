using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace KissakiViewer.ViewModels;

/// <summary>
/// One fdata container entry shown in the container-filter popup.
/// IsEnabled changes call back into MainViewModel.ScheduleFilter.
/// </summary>
public sealed class ContainerFilterItem : INotifyPropertyChanged
{
    public string Key       { get; }   // "0x00000001.fdata"
    public string Display   { get; }   // "0x00000001"
    public string RdbSource { get; }   // "root" / "system"
    public int    AssetCount { get; }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            OnPropertyChanged();
            OnChanged?.Invoke();
        }
    }

    internal Action? OnChanged { get; set; }

    public ContainerFilterItem(string key, string rdbName, int assetCount)
    {
        Key        = key;
        Display    = key.EndsWith(".fdata", StringComparison.OrdinalIgnoreCase)
                     ? key[..^6]   // strip ".fdata"
                     : key;
        RdbSource  = Path.GetFileNameWithoutExtension(rdbName);  // "root" / "system"
        AssetCount = assetCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
