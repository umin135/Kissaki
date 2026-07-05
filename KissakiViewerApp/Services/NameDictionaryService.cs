using System.Globalization;
using System.IO;
using System.Text;

namespace KissakiViewer.Services;

/// <summary>
/// KTID→이름 사전 CSV 로드/저장.
/// 형식: 0xHEXKTID,path/to/name  (RDBExplorer rdb_names.csv 호환)
/// 경로: &lt;툴경로&gt;/res/dict_rdb_names/{AppID}.csv  (게임별 분리)
/// </summary>
public static class NameDictionaryService
{
    private static readonly string DictDir =
        Path.Combine(AppContext.BaseDirectory, "res", "dict_rdb_names");

    public static string GetCsvPath(string appId) =>
        Path.Combine(DictDir, $"{appId}.csv");

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
