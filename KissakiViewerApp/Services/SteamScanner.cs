using System.IO;
using Microsoft.Win32;
using KissakiViewer.Models;

namespace KissakiViewer.Services;

public static class SteamScanner
{
    // Known KatanaEngine games: (AppId, Display Name, exe file name)
    // Only games confirmed to use the RDB/FDATA format (KTGL 2.x+).
    // Atelier Ryza 1/2/3 and DOA6 (non-LR) use an older format with no root.rdb — excluded.
    private static readonly (uint AppId, string Name, string ExeName)[] s_knownGames =
    [
        (4144680, "DEAD OR ALIVE 6 Last Round",                               "DOA6LR.exe"),
        (3155730, "Venus Vacation PRISM - DEAD OR ALIVE Xtreme -",            "Venus Vacation PRISM - DEAD OR ALIVE Xtreme -.exe"),
        (1340990, "Rise of the Ronin",                                         "Ronin.exe"),
        (3123410, "Atelier Yumia: The Alchemist of Memories & the Envisioned Land",        "Atelier_Yumia.exe"),
        (3473170, "Atelier Yumia: The Alchemist of Memories & the Envisioned Land - Demo", "Atelier_Yumia.exe"),
        (3920610, "FATAL FRAME II: Crimson Butterfly REMAKE",                 "FatalFrameII.exe"),
        (4226020, "FATAL FRAME II: Crimson Butterfly REMAKE DEMO",            "FatalFrameII.exe"),
        (3681010, "Nioh 3",                                                    "nioh3.exe"),
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
                string acfPath = Path.Combine(libPath, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acfPath)) continue;

                string? installDir = ReadAcfInstallDir(acfPath);
                if (installDir == null) continue;

                string gamePath = Path.Combine(commonPath, installDir);
                if (!Directory.Exists(gamePath)) continue;

                string? rdbDir = FindRdbDir(gamePath);
                if (rdbDir == null) continue;

                // For ACF-verified known games, prefer the expected exe name but fall back to
                // any .exe in the game directory — the game identity is guaranteed by the AppID.
                string? foundExe = FindExe(gamePath, exeName)
                                ?? FindAnyExe(gamePath);

                results.Add(new GameProfile
                {
                    Name          = name,
                    ExeName       = foundExe != null ? Path.GetFileName(foundExe) : exeName,
                    GameDirectory = gamePath,
                    SteamAppId    = appId,
                    FdataDir      = rdbDir,
                });
            }
        }

        return results;
    }

    public static GameProfile? DetectFromDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string? rdbDir = FindRdbDir(dir);
        if (rdbDir == null) return null;

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
                    FdataDir      = rdbDir,
                };
            }
        }

        // Unknown KatanaEngine game — use folder name + any exe
        string fallbackExe = FindAnyExe(dir) ?? "";
        return new GameProfile
        {
            Name          = Path.GetFileName(dir),
            ExeName       = Path.GetFileName(fallbackExe),
            GameDirectory = dir,
            SteamAppId    = 0,
            FdataDir      = rdbDir,
        };
    }

    /// <summary>
    /// Scans <paramref name="gameDir"/> and all its immediate subdirectories for
    /// root.rdb + root.rdx and returns the first directory that contains both.
    /// Returns null if not found.
    /// </summary>
    public static string? FindRdbDir(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return null;

        // Check game root itself first
        if (HasRdb(gameDir)) return gameDir;

        // Scan all immediate subdirectories
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(gameDir))
                if (HasRdb(sub)) return sub;
        }
        catch { }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasRdb(string dir) =>
        File.Exists(Path.Combine(dir, "root.rdb")) &&
        File.Exists(Path.Combine(dir, "root.rdx"));

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
            return Directory.GetFiles(dir, "*.exe")
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), preferredExe,
                                                   StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static string? FindAnyExe(string dir)
    {
        try { return Directory.GetFiles(dir, "*.exe").FirstOrDefault(); }
        catch { return null; }
    }
}
