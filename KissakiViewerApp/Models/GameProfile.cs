using System.IO;

namespace KissakiViewer.Models;

public sealed class GameProfile
{
    public string Name          { get; set; } = string.Empty;
    public string ExeName       { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public uint   SteamAppId    { get; set; }

    // Cached directory containing root.rdb / root.rdx, discovered at scan time.
    // Falls back to <GameDirectory>/fdata_package if empty (backward compat).
    private string _fdataDir = string.Empty;
    public string FdataDir
    {
        get => string.IsNullOrEmpty(_fdataDir)
                   ? Path.Combine(GameDirectory, "fdata_package")
                   : _fdataDir;
        set => _fdataDir = value;
    }

    public string ExePath => Path.Combine(GameDirectory, ExeName);
}
