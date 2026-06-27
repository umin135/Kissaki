using System.IO;
using Microsoft.Win32;
using KissakiViewer.Models;

namespace KissakiViewer.Services;

public static class SteamScanner
{
    // Known KatanaEngine games: (AppId, Display Name, exe file name)
    private static readonly (uint AppId, string Name, string ExeName)[] s_knownGames =
    [
        (838380,  "DEAD OR ALIVE 6",                              "DOA6.exe"),
        (4144680, "DEAD OR ALIVE 6 Last Round",                   "DOA6LR.exe"),
        (3920610, "FATAL FRAME II: Crimson Butterfly REMAKE",     "FatalFrame2Remake.exe"),
        (1121560, "Atelier Ryza: Ever Darkness & the Secret Hideout",  "Atelier_Ryza.exe"),
        (1257290, "Atelier Ryza 2: Lost Legends & the Secret Fairy",   "Atelier_Ryza2.exe"),
        (1999770, "Atelier Ryza 3: Alchemist of the End & the Secret Key", "Atelier_Ryza3.exe"),
        (3681010, "Nioh 3",                                        "nioh3.exe"),
    ];

    public static List<GameProfile> Scan()
    {
        var results = new List<GameProfile>();
        var libraryPaths = GetSteamLibraryPaths();

        foreach (string libPath in libraryPaths)
        {
            string commonPath = Path.Combine(libPath, "steamapps", "common");
            if (!Directory.Exists(commonPath)) continue;

            foreach (var (appId, name, exeName) in s_knownGames)
            {
                // Try to find the game folder by scanning the acf manifest
                string acfPath = Path.Combine(libPath, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acfPath)) continue;

                string? installDir = ReadAcfInstallDir(acfPath);
                if (installDir == null) continue;

                string gamePath = Path.Combine(commonPath, installDir);
                if (!Directory.Exists(gamePath)) continue;

                // Verify fdata_package exists (KatanaEngine marker)
                if (!Directory.Exists(Path.Combine(gamePath, "fdata_package"))) continue;

                // Find the exe (try multiple possible names)
                string? foundExe = FindExe(gamePath, exeName);
                if (foundExe == null) continue;

                results.Add(new GameProfile
                {
                    Name          = name,
                    ExeName       = Path.GetFileName(foundExe),
                    GameDirectory = gamePath,
                    SteamAppId    = appId,
                });
            }
        }

        return results;
    }

    public static GameProfile? DetectFromDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        if (!Directory.Exists(Path.Combine(dir, "fdata_package"))) return null;

        // Try to match by exe name
        foreach (var (appId, name, exeName) in s_knownGames)
        {
            string? found = FindExe(dir, exeName);
            if (found != null)
            {
                return new GameProfile
                {
                    Name          = name,
                    ExeName       = Path.GetFileName(found),
                    GameDirectory = dir,
                    SteamAppId    = appId,
                };
            }
        }

        // Unknown KatanaEngine game — use folder name
        string fallbackExe = Directory.GetFiles(dir, "*.exe").FirstOrDefault() ?? "";
        return new GameProfile
        {
            Name          = Path.GetFileName(dir),
            ExeName       = Path.GetFileName(fallbackExe),
            GameDirectory = dir,
            SteamAppId    = 0,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<string> GetSteamLibraryPaths()
    {
        var paths = new List<string>();

        string? steamPath = GetSteamRootPath();
        if (steamPath != null)
        {
            paths.Add(steamPath);

            // Parse libraryfolders.vdf to find additional library locations
            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                foreach (string line in File.ReadLines(vdfPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    {
                        string? libPath = ExtractVdfValue(trimmed);
                        if (libPath != null && Directory.Exists(libPath))
                            paths.Add(libPath);
                    }
                }
            }
        }

        return paths;
    }

    private static string? GetSteamRootPath()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch { return null; }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            foreach (string line in File.ReadLines(acfPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                    return ExtractVdfValue(trimmed);
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractVdfValue(string line)
    {
        // Format: "key"    "value"
        int second = line.IndexOf('"', 1);
        if (second < 0) return null;
        int start = line.IndexOf('"', second + 1);
        if (start < 0) return null;
        int end = line.IndexOf('"', start + 1);
        if (end < 0) return null;
        return line.Substring(start + 1, end - start - 1).Replace("\\\\", "\\");
    }

    private static string? FindExe(string dir, string preferredExe)
    {
        string path = Path.Combine(dir, preferredExe);
        if (File.Exists(path)) return path;

        // Try case-insensitive match
        try
        {
            string? found = Directory.GetFiles(dir, "*.exe")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), preferredExe,
                                                   StringComparison.OrdinalIgnoreCase));
            return found;
        }
        catch { return null; }
    }
}
