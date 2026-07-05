using System.IO;

namespace KissakiViewer.Core;

/// <summary>
/// Persists the resolved G1M→G1T, G1M→GRP, G1M→OIDEX mappings to a binary file.
/// Keyed by game directory; validated against RDB modification timestamps so that
/// game updates automatically invalidate the cache.
/// </summary>
public sealed class GameLoadCache
{
    private static readonly byte[] MagicBytes = "KSCACH01"u8.ToArray();

    public record RdbInfo(string Path, long ModTimeTicks);

    /// <summary>G1M FileKtid → sparse (slotIndex, G1T FileKtid) pairs.</summary>
    public IReadOnlyDictionary<uint, (int Slot, uint G1tFk)[]> G1mToG1tSlots { get; }

    /// <summary>G1M FileKtid → GRP FileKtid.</summary>
    public IReadOnlyDictionary<uint, uint> G1mToGrp { get; }

    /// <summary>G1M FileKtid → OIDEX FileKtid.</summary>
    public IReadOnlyDictionary<uint, uint> G1mToOidex { get; }

    private GameLoadCache(
        Dictionary<uint, (int, uint)[]> g1mToG1tSlots,
        Dictionary<uint, uint> g1mToGrp,
        Dictionary<uint, uint> g1mToOidex)
    {
        G1mToG1tSlots = g1mToG1tSlots;
        G1mToGrp      = g1mToGrp;
        G1mToOidex    = g1mToOidex;
    }

    // ── File path ─────────────────────────────────────────────────────────────

    public static string GetCacheFilePath(string gameDirectory)
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KissakiViewer", "cache");
        Directory.CreateDirectory(cacheDir);

        // FNV-1a hash of the normalised game path
        uint hash = 2166136261u;
        foreach (char c in gameDirectory.ToUpperInvariant())
        {
            hash ^= (byte)(c & 0xFF);
            hash *= 16777619u;
        }
        return Path.Combine(cacheDir, $"0x{hash:x8}.kscache");
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to load the cache from <paramref name="cacheFile"/>.
    /// Returns <c>null</c> when the file is missing, corrupt, or any RDB timestamp
    /// does not match the corresponding entry in <paramref name="rdbInfos"/>.
    /// </summary>
    public static GameLoadCache? TryLoad(string cacheFile, IReadOnlyList<RdbInfo> rdbInfos)
    {
        if (!File.Exists(cacheFile)) return null;

        try
        {
            using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (!br.ReadBytes(8).SequenceEqual(MagicBytes)) return null;

            // Validate all RDB timestamps
            int rdbCount = br.ReadInt32();
            if (rdbCount != rdbInfos.Count) return null;

            for (int i = 0; i < rdbCount; i++)
            {
                string path  = br.ReadString();
                long   ticks = br.ReadInt64();
                var    match = rdbInfos.FirstOrDefault(r =>
                    string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
                if (match is null || match.ModTimeTicks != ticks) return null;
            }

            // G1M → G1T slots (sparse)
            int g1tCount = br.ReadInt32();
            var g1mToG1t = new Dictionary<uint, (int, uint)[]>(g1tCount);
            for (int i = 0; i < g1tCount; i++)
            {
                uint g1mFk    = br.ReadUInt32();
                int  slotCnt  = br.ReadInt32();
                var  slots    = new (int, uint)[slotCnt];
                for (int s = 0; s < slotCnt; s++)
                    slots[s] = (br.ReadInt32(), br.ReadUInt32());
                g1mToG1t[g1mFk] = slots;
            }

            // G1M → GRP
            int grpCount  = br.ReadInt32();
            var g1mToGrp  = new Dictionary<uint, uint>(grpCount);
            for (int i = 0; i < grpCount; i++)
                g1mToGrp[br.ReadUInt32()] = br.ReadUInt32();

            // G1M → OIDEX
            int oidxCount  = br.ReadInt32();
            var g1mToOidex = new Dictionary<uint, uint>(oidxCount);
            for (int i = 0; i < oidxCount; i++)
                g1mToOidex[br.ReadUInt32()] = br.ReadUInt32();

            AppLogger.Info(
                $"[Cache] 캐시 유효: {g1tCount}개 G1M→G1T, " +
                $"{grpCount}개 GRP, {oidxCount}개 OIDEX");
            return new GameLoadCache(g1mToG1t, g1mToGrp, g1mToOidex);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Cache] 로드 실패 — 재생성: {ex.Message}");
            return null;
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the combined mappings.
    /// <paramref name="g1mToG1tSlots"/> maps G1M FK → sparse (slotIndex, G1T FK) pairs
    /// (null/0 G1T entries are omitted).
    /// </summary>
    public static void Save(
        string                                                    cacheFile,
        IReadOnlyList<RdbInfo>                                    rdbInfos,
        IReadOnlyDictionary<uint, IReadOnlyList<(int Slot, uint G1tFk)>> g1mToG1tSlots,
        IReadOnlyDictionary<uint, uint>                           g1mToGrp,
        IReadOnlyDictionary<uint, uint>                           g1mToOidex)
    {
        string tmpFile = cacheFile + ".tmp";
        try
        {
            using var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(MagicBytes);

            bw.Write(rdbInfos.Count);
            foreach (var info in rdbInfos)
            {
                bw.Write(info.Path);
                bw.Write(info.ModTimeTicks);
            }

            bw.Write(g1mToG1tSlots.Count);
            foreach (var (g1mFk, slots) in g1mToG1tSlots)
            {
                bw.Write(g1mFk);
                bw.Write(slots.Count);
                foreach (var (slot, g1tFk) in slots)
                {
                    bw.Write(slot);
                    bw.Write(g1tFk);
                }
            }

            bw.Write(g1mToGrp.Count);
            foreach (var (k, v) in g1mToGrp)  { bw.Write(k); bw.Write(v); }

            bw.Write(g1mToOidex.Count);
            foreach (var (k, v) in g1mToOidex) { bw.Write(k); bw.Write(v); }
        }
        catch
        {
            try { File.Delete(tmpFile); } catch { }
            throw;
        }

        // Atomic replace
        if (File.Exists(cacheFile)) File.Delete(cacheFile);
        File.Move(tmpFile, cacheFile);
    }
}
