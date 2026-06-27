using System.IO;

namespace KissakiViewer.Models;

public sealed class GameProfile
{
    public string Name          { get; set; } = string.Empty;
    public string ExeName       { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public uint   SteamAppId    { get; set; }

    public string FdataDir => Path.Combine(GameDirectory, "fdata_package");
    public string ExePath  => Path.Combine(GameDirectory, ExeName);
}
