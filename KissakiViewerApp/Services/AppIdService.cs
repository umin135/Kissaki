using System.IO;
using System.Text.RegularExpressions;

namespace KissakiViewer.Services;

/// <summary>
/// Steam AppID를 게임 설치 경로에서 검출한다.
/// 우선순위: steam_appid.txt → appmanifest_*.acf → FNV fallback
/// </summary>
public static class AppIdService
{
    public static string GetAppId(string gameDirectory)
    {
        // 1) steam_appid.txt (많은 게임이 제공)
        string txtPath = Path.Combine(gameDirectory, "steam_appid.txt");
        if (File.Exists(txtPath))
        {
            string content = File.ReadAllText(txtPath).Trim();
            if (!string.IsNullOrEmpty(content) && content.All(char.IsAsciiDigit))
                return content;
        }

        // 2) steamapps/appmanifest_*.acf
        //    gameDir 구조: steamapps/common/<GameName>
        //    → 위로 두 단계 올라가면 steamapps/
        string? steamappsDir = TryGetSteamappsDir(gameDirectory);
        if (steamappsDir != null)
        {
            string? acfAppId = TryReadAcf(steamappsDir, gameDirectory);
            if (acfAppId != null)
                return acfAppId;
        }

        // 3) FNV32 fallback (설치 경로 정규화 해시)
        return FnvFallback(gameDirectory);
    }

    private static string? TryGetSteamappsDir(string gameDir)
    {
        // common/ → steamapps/
        try
        {
            string? common = Path.GetDirectoryName(Path.GetFullPath(gameDir));
            if (common == null) return null;
            string? steamapps = Path.GetDirectoryName(common);
            if (steamapps != null && Directory.Exists(steamapps) &&
                Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase))
                return steamapps;
        }
        catch { }
        return null;
    }

    private static string? TryReadAcf(string steamappsDir, string gameDir)
    {
        string gameName = Path.GetFileName(Path.GetFullPath(gameDir));
        foreach (string acf in Directory.EnumerateFiles(steamappsDir, "appmanifest_*.acf"))
        {
            try
            {
                string text = File.ReadAllText(acf);
                // installdir フィールドがゲームフォルダ名と一致するか確認
                Match dirMatch = Regex.Match(text, @"""installdir""\s+""([^""]+)""");
                if (!dirMatch.Success) continue;
                if (!dirMatch.Groups[1].Value.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Match idMatch = Regex.Match(text, @"""appid""\s+""(\d+)""");
                if (idMatch.Success)
                    return idMatch.Groups[1].Value;
            }
            catch { }
        }
        return null;
    }

    private static string FnvFallback(string gameDir)
    {
        // FNV-1a 32bit
        uint hash = 2166136261u;
        foreach (char c in Path.GetFullPath(gameDir).ToUpperInvariant())
        {
            hash ^= (byte)c;
            hash *= 16777619u;
        }
        return $"fnv_{hash:x8}";
    }
}
