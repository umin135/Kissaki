using KissakiViewer.ViewModels;
using System.IO;
using System.Text.Json;

namespace KissakiViewer.Services;

/// <summary>
/// Persists the G1M?�G1T KTID map to AppData so BuildAsync is skipped on subsequent launches.
/// Cache is invalidated when root.rdb changes (size or write-time).
/// </summary>
public static class TextureMapCache
{
    private record CacheFile(string RdbStamp, Dictionary<string, List<string>> Map);

    private static string CachePath(string gameDir)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string id      = GameId(gameDir);
        return Path.Combine(appData, "KissakiViewer", "cache", $"g1m_map_{id}.json");
    }

    /// <summary>
    /// Try to load a cached map. Returns false if missing or stale.
    /// On success, resolves KTID values back to AssetItemViewModels via allAssets.
    /// </summary>
    public static bool TryLoad(
        string gameDir,
        string rdbPath,
        IEnumerable<AssetItemViewModel> allAssets,
        out IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> map)
    {
        map = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();
        try
        {
            string path = CachePath(gameDir);
            if (!File.Exists(path)) return false;

            string json   = File.ReadAllText(path);
            var    cache  = JsonSerializer.Deserialize<CacheFile>(json);
            if (cache is null) return false;

            string stamp = RdbStamp(rdbPath);
            if (cache.RdbStamp != stamp) return false;

            // Build lookup: FileKtid ??AssetItemViewModel
            var byKtid = allAssets.ToDictionary(a => a.Record.FileKtid, a => a);

            var result = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();
            foreach (var (g1mHex, g1tHexList) in cache.Map)
            {
                if (!TryParseHex(g1mHex, out uint g1mKtid)) continue;
                var g1tList = new List<AssetItemViewModel>();
                foreach (string h in g1tHexList)
                {
                    if (TryParseHex(h, out uint g1tKtid) && byKtid.TryGetValue(g1tKtid, out var vm))
                        g1tList.Add(vm);
                }
                if (g1tList.Count > 0)
                    result[g1mKtid] = g1tList;
            }

            map = result;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Saves the G1M?�G1T map to the cache file.</summary>
    public static void Save(
        string gameDir,
        string rdbPath,
        IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> map)
    {
        try
        {
            string cachePath = CachePath(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            var serializable = map.ToDictionary(
                kv => $"0x{kv.Key:X8}",
                kv => (kv.Value ?? []).Where(vm => vm != null)
                    .Select(vm => $"0x{vm.Record.FileKtid:X8}").ToList());

            var cache = new CacheFile(RdbStamp(rdbPath), serializable);
            string json = JsonSerializer.Serialize(cache,
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[TextureMapCache] Save failed: {ex.Message}");
        }
    }

    private static string RdbStamp(string rdbPath)
    {
        if (!File.Exists(rdbPath)) return "missing";
        var fi = new FileInfo(rdbPath);
        return $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
    }

    private static string GameId(string gameDir)
    {
        uint h = 2166136261u;
        foreach (char c in gameDir.ToUpperInvariant())
            h = (h ^ c) * 16777619u;
        return $"{h:x8}";
    }

    private static bool TryParseHex(string s, out uint value)
    {
        string trimmed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
