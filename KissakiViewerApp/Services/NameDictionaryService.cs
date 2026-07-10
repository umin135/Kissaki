using System.Globalization;
using System.IO;
using System.Text;

namespace KissakiViewer.Services;

/// <summary>
/// KTID→이름 사전 CSV 로드/저장.
/// 형식: 0xHEXKTID,path/to/name  (RDBExplorer rdb_names.csv 호환)
/// 경로: &lt;툴경로&gt;/res/dict_rdb_names/{ExeName}.csv  (게임별 분리, ExeName = EXE 파일명 확장자 제외)
/// </summary>
public static class NameDictionaryService
{
    private static readonly string DictDir =
        Path.Combine(AppContext.BaseDirectory, "res", "dict_rdb_names");

    // AppID(문자열) → EXE 파일명(확장자 제외) 마이그레이션 표
    // SteamScanner.s_knownGames와 동기화 유지
    private static readonly (string AppId, string ExeNameNoExt)[] s_legacyMigrations =
    [
        ("4144680", "DOA6LR"),
        ("3155730", "Venus Vacation PRISM - DEAD OR ALIVE Xtreme -"),
        ("1340990", "Ronin"),
        ("3123410", "Atelier_Yumia"),
        ("3473170", "Atelier_Yumia"),
        ("3920610", "FatalFrameII"),
        ("4226020", "FatalFrameII"),
        ("3681010", "nioh3"),
    ];

    public static string GetCsvPath(string exeName) =>
        Path.Combine(DictDir, $"{exeName}.csv");

    /// <summary>
    /// AppID 기반 구 CSV 파일을 ExeName 기반으로 일괄 마이그레이션.
    /// 대상 파일이 이미 존재하면 덮어쓰지 않고 건너뜀.
    /// </summary>
    public static void MigrateOldCsvFiles()
    {
        if (!Directory.Exists(DictDir)) return;
        foreach (var (appId, exeName) in s_legacyMigrations)
        {
            string oldPath = Path.Combine(DictDir, $"{appId}.csv");
            string newPath = Path.Combine(DictDir, $"{exeName}.csv");
            if (!File.Exists(oldPath)) continue;
            if (File.Exists(newPath))
            {
                AppLogger.Info($"[NameDictionary] Migration skipped (target exists): {appId}.csv → {exeName}.csv");
                continue;
            }
            try
            {
                File.Move(oldPath, newPath);
                AppLogger.Info($"[NameDictionary] Migrated {appId}.csv → {exeName}.csv");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[NameDictionary] Migration failed {appId}.csv → {exeName}.csv: {ex.Message}");
            }
        }
    }

    public static Dictionary<uint, string> Load(string csvPath)
    {
        var result = new Dictionary<uint, string>();
        try
        {
            if (!File.Exists(csvPath)) return result;
            foreach (string line in File.ReadLines(csvPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                string hex  = line[..comma].Trim();
                string name = line[(comma + 1)..].Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex[2..];
                if (uint.TryParse(hex, NumberStyles.HexNumber, null, out uint ktid))
                    result[ktid] = name;
            }
        }
        catch (Exception ex) { AppLogger.Warn($"[NameDictionary] Load failed: {ex.Message}"); }
        return result;
    }

    public static void Save(string csvPath, IReadOnlyDictionary<uint, string> names)
    {
        if (names.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
            var sb = new StringBuilder(names.Count * 48);
            foreach (var kv in names.OrderBy(x => x.Key))
                sb.AppendLine($"0x{kv.Key:X8},{kv.Value}");
            File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
            AppLogger.Info($"[NameDictionary] {names.Count} saved → {csvPath}");
        }
        catch (Exception ex) { AppLogger.Warn($"[NameDictionary] Save failed: {ex.Message}"); }
    }
}
