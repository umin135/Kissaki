using System.Runtime.CompilerServices;
using System.Text;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Katana Engine G1M name recovery via kidsscndb → CharacterSetting → RenderSetting → CE G1M chain.
/// Triggered automatically when no name CSV exists for the current game.
///
/// All three driver DOKs are Katana Engine common singletons — their FileKtids are identical
/// across every Katana title (verified against fdata_dump/rdb_common.csv), so this chain works
/// for any game that ships them. When a DOK is absent, BuildAsync returns [] and the caller
/// simply falls back to no recovery.
///
/// Chain (FKs verified against DOA6LR KAS_COS_006→0x0957FBEC):
///   ProjectCommonResource.motor.kidsscndb  FK=0x6D011726 — CharacterNameArray (prop 0x4D269345) + HashArray (prop 0x8F9F2DA6)
///   CE1CommonResource.motor.kidsobjdb      FK=0x2082AD97 — type=0xD186CEEB: hash[i] → prop 0x68A6F779 → RenderSetting oid
///                                                          type=0xC4B9B28D: RS oid → prop 0x0B6E1578 → CE G1M IDOK oid
///   CharacterEditor.kidssingletondb        FK=0xB290631C — type=0xD40B3C8F: CE G1M IDOK oid → prop 0x8AB68B3F → G1M FileKtid
///
/// Coverage (DOA6LR): ~1632/1650 names (98.9%). The 18 failures are DLC-only or non-model entries.
/// </summary>
public static class KatanaNameRecovery
{
    private const uint FkScndb  = 0x6D011726u;  // ProjectCommonResource.motor.kidsscndb
    private const uint FkBigDok = 0x2082AD97u;  // CE1CommonResource.motor.kidsobjdb
    private const uint FkCeDok  = 0xB290631Cu;  // CharacterEditor.kidssingletondb

    private const uint TypeCharacterSetting = 0xD186CEEBu;
    private const uint TypeRenderSetting    = 0xC4B9B28Du;
    private const uint TypeCeG1mIdok        = 0xD40B3C8Fu;

    private const uint PropCharacterNameArray = 0x4D269345u;
    private const uint PropHashArray          = 0x8F9F2DA6u;
    private const uint PropRenderSettingOid   = 0x68A6F779u;
    private const uint PropCeG1mIdokOid       = 0x0B6E1578u;
    private const uint PropG1mFileKtid        = 0x8AB68B3Fu;

    private static readonly int[] PropTypeSizes = [1, 1, 2, 2, 4, 4, 0, 0, 4, 0, 16, 0, 8, 12];
    private static readonly byte[] IdokMagic    = "IDOK0000"u8.ToArray();

    public static async Task<Dictionary<uint, string>> BuildAsync(
        IReadOnlyDictionary<uint, AssetItemViewModel> assetsByKtid,
        FdataExtractor extractor)
    {
        if (!assetsByKtid.TryGetValue(FkScndb,  out var scndbAsset)  ||
            !assetsByKtid.TryGetValue(FkBigDok, out var bigDokAsset) ||
            !assetsByKtid.TryGetValue(FkCeDok,  out var ceDokAsset))
        {
            AppLogger.Warn("[KatanaName] Required DOK FKs not found — name recovery skipped");
            return [];
        }

        // Parallel extraction
        var t1 = Task.Run(() => extractor.ExtractToMemory(scndbAsset.Record,  scndbAsset.Container));
        var t2 = Task.Run(() => extractor.ExtractToMemory(bigDokAsset.Record, bigDokAsset.Container));
        var t3 = Task.Run(() => extractor.ExtractToMemory(ceDokAsset.Record,  ceDokAsset.Container));
        await Task.WhenAll(t1, t2, t3);

        byte[] scndbData  = t1.Result;
        byte[] bigDokData = t2.Result;
        byte[] ceDokData  = t3.Result;

        if (scndbData.Length == 0 || bigDokData.Length == 0 || ceDokData.Length == 0)
        {
            AppLogger.Warn("[KatanaName] Extraction failed for one or more DOK files");
            return [];
        }

        AppLogger.Info($"[KatanaName] Extracted: scndb={scndbData.Length}B  bigDok={bigDokData.Length}B  ceDok={ceDokData.Length}B");

        // Parallel parsing
        var p1 = Task.Run(() => ParseScndb(scndbData));
        var p2 = Task.Run(() => ParseBigDok(bigDokData));
        var p3 = Task.Run(() => ParseCeDok(ceDokData));
        await Task.WhenAll(p1, p2, p3);

        var (names, hashes) = p1.Result;
        var (csToRs, rsToCeOid) = p2.Result;
        var ceOidToG1mFk = p3.Result;

        if (names.Length == 0)
        {
            AppLogger.Warn("[KatanaName] scndb returned no names");
            return [];
        }

        AppLogger.Info(
            $"[KatanaName] Parsed: scndb={names.Length}  csToRs={csToRs.Count}  rsToCeOid={rsToCeOid.Count}  ceOid→FK={ceOidToG1mFk.Count}");

        // Assemble chain
        var result = new Dictionary<uint, string>();
        int noRs = 0, noCeOid = 0, noG1mFk = 0;
        int count = Math.Min(names.Length, hashes.Length);

        for (int i = 0; i < count; i++)
        {
            if (!csToRs.TryGetValue(hashes[i], out uint rsOid))   { noRs++;    continue; }
            if (!rsToCeOid.TryGetValue(rsOid, out uint ceOid))    { noCeOid++; continue; }
            if (!ceOidToG1mFk.TryGetValue(ceOid, out uint g1mFk)) { noG1mFk++; continue; }
            result[g1mFk] = names[i] + ".g1m";
        }

        int g1mCount   = result.Count;
        int companions = DeriveCompanions(result, assetsByKtid);

        AppLogger.Info(
            $"[KatanaName] Result: {g1mCount}/{count} G1M mapped  (noRS={noRs}  noCeOid={noCeOid}  noG1mFk={noG1mFk}); +{companions} companions derived");
        return result;
    }

    // Model-companion extensions that share a G1M's base name (per fdata_dump Displayset::Model
    // derivation). Textures (.g1t) are NOT here — they carry unknown suffixes and are resolved via
    // the KTID→G1T chain instead.
    private static readonly string[] CompanionExts =
        ["ktid", "mtl", "grp", "oid", "oidex", "swg", "rigbin"];

    /// <summary>
    /// For each recovered "&lt;base&gt;.g1m", forward-hashes every model companion (&lt;base&gt;.ktid /
    /// .mtl / .grp / …) via <see cref="KatanaHasher"/> and adds a name for any hash that resolves to
    /// a real asset. Only runs on the no-CSV recovery path, so a curated per-game CSV always wins.
    /// Returns the number of companion names added.
    /// </summary>
    private static int DeriveCompanions(
        Dictionary<uint, string> result,
        IReadOnlyDictionary<uint, AssetItemViewModel> assetsByKtid)
    {
        // Snapshot base names first — we mutate `result` while deriving.
        var bases = new List<string>(result.Count);
        foreach (string name in result.Values)
            bases.Add(name.EndsWith(".g1m", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);

        int added = 0;
        foreach (string b in bases)
        {
            foreach (string ext in CompanionExts)
            {
                string cand = $"{b}.{ext}";
                uint fk = KatanaHasher.Hash(cand);
                if (assetsByKtid.ContainsKey(fk) && result.TryAdd(fk, cand))
                    added++;
            }
        }
        return added;
    }

    private static (string[] names, uint[] hashes) ParseScndb(byte[] data)
    {
        var span = new ReadOnlySpan<byte>(data);
        int pos = 0;

        while (true)
        {
            int idx = span[pos..].IndexOf(IdokMagic);
            if (idx < 0) break;
            int p = pos + idx;
            pos = p + IdokMagic.Length;

            if (p + 24 > data.Length) continue;
            int nProps   = (int)ReadU32(data, p + 20);
            int metaBase = p + 24;
            int vp       = metaBase + nProps * 12;

            byte[]? nameArr = null;
            byte[]? hashArr = null;

            for (int i = 0; i < nProps && metaBase + i * 12 + 12 <= data.Length; i++)
            {
                int mo    = metaBase + i * 12;
                int ptype = (int)ReadU32(data, mo);
                int asize = (int)ReadU32(data, mo + 4);
                uint ph   = ReadU32(data, mo + 8);
                int sz    = (ptype < PropTypeSizes.Length ? PropTypeSizes[ptype] : 0) * asize;
                if (sz > 0 && vp + sz <= data.Length)
                {
                    if      (ph == PropCharacterNameArray) nameArr = data[vp..(vp + sz)];
                    else if (ph == PropHashArray)          hashArr = data[vp..(vp + sz)];
                }
                vp += sz;
            }

            if (nameArr == null || hashArr == null) continue;

            var names  = Encoding.ASCII.GetString(nameArr)
                             .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var hashes = new uint[hashArr.Length / 4];
            for (int j = 0; j < hashes.Length; j++)
                hashes[j] = ReadU32(hashArr, j * 4);
            return (names, hashes);
        }
        return ([], []);
    }

    private static (Dictionary<uint, uint> csToRs, Dictionary<uint, uint> rsToCeOid) ParseBigDok(byte[] data)
    {
        var csToRs    = new Dictionary<uint, uint>();
        var rsToCeOid = new Dictionary<uint, uint>();
        var span      = new ReadOnlySpan<byte>(data);
        int pos       = 0;

        while (true)
        {
            int idx = span[pos..].IndexOf(IdokMagic);
            if (idx < 0) break;
            int p = pos + idx;
            pos = p + IdokMagic.Length;

            if (p + 24 > data.Length) continue;
            uint oid     = ReadU32(data, p + 12);
            uint typNm   = ReadU32(data, p + 16);
            int nProps   = (int)ReadU32(data, p + 20);
            int metaBase = p + 24;
            int vp       = metaBase + nProps * 12;

            if (typNm == TypeCharacterSetting)
            {
                for (int i = 0; i < nProps && metaBase + i * 12 + 12 <= data.Length; i++)
                {
                    int mo    = metaBase + i * 12;
                    int ptype = (int)ReadU32(data, mo);
                    int asize = (int)ReadU32(data, mo + 4);
                    uint ph   = ReadU32(data, mo + 8);
                    int sz    = (ptype < PropTypeSizes.Length ? PropTypeSizes[ptype] : 0) * asize;
                    if (sz == 4 && ph == PropRenderSettingOid && vp + 4 <= data.Length)
                        csToRs[oid] = ReadU32(data, vp);
                    vp += sz;
                }
            }
            else if (typNm == TypeRenderSetting)
            {
                for (int i = 0; i < nProps && metaBase + i * 12 + 12 <= data.Length; i++)
                {
                    int mo    = metaBase + i * 12;
                    int ptype = (int)ReadU32(data, mo);
                    int asize = (int)ReadU32(data, mo + 4);
                    uint ph   = ReadU32(data, mo + 8);
                    int sz    = (ptype < PropTypeSizes.Length ? PropTypeSizes[ptype] : 0) * asize;
                    if (sz == 4 && ph == PropCeG1mIdokOid && vp + 4 <= data.Length)
                        rsToCeOid[oid] = ReadU32(data, vp);
                    vp += sz;
                }
            }
        }
        return (csToRs, rsToCeOid);
    }

    private static Dictionary<uint, uint> ParseCeDok(byte[] data)
    {
        var result = new Dictionary<uint, uint>();
        var span   = new ReadOnlySpan<byte>(data);
        int pos    = 0;

        while (true)
        {
            int idx = span[pos..].IndexOf(IdokMagic);
            if (idx < 0) break;
            int p = pos + idx;
            pos = p + IdokMagic.Length;

            if (p + 24 > data.Length) continue;
            uint oid   = ReadU32(data, p + 12);
            uint typNm = ReadU32(data, p + 16);
            if (typNm != TypeCeG1mIdok) continue;

            int nProps   = (int)ReadU32(data, p + 20);
            int metaBase = p + 24;
            int vp       = metaBase + nProps * 12;

            for (int i = 0; i < nProps && metaBase + i * 12 + 12 <= data.Length; i++)
            {
                int mo    = metaBase + i * 12;
                int ptype = (int)ReadU32(data, mo);
                int asize = (int)ReadU32(data, mo + 4);
                uint ph   = ReadU32(data, mo + 8);
                int sz    = (ptype < PropTypeSizes.Length ? PropTypeSizes[ptype] : 0) * asize;
                if (sz == 4 && ph == PropG1mFileKtid && vp + 4 <= data.Length)
                    result[oid] = ReadU32(data, vp);
                vp += sz;
            }
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
}
