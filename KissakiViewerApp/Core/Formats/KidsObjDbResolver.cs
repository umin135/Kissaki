using System.Collections.Concurrent;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Builds a G1M → [G1T] mapping by pre-scanning all .kidsobjdb DOK files.
///
/// Three-phase algorithm:
///   Phase 1 — parse every DOK in parallel; collect IDOK records, build DokContexts
///   Phase 2 — extract each unique KTID file exactly once (global dedup)
///   Phase 3 — resolve G1M → G1T via KTID slot maps (CPU-only, no I/O)
///
/// IDOK record layout (offsets from "IDOK0000" magic):
///   +0x00  "IDOK0000" magic (8B)
///   +0x08  rec_size (u32)
///   +0x0C  prop_Name / object_id (u32)   — DOK-local unique ID
///   +0x10  prop_TypeName (u32)            — asset type hash
///   +0x14  prop_PropsCount (u32)
///   +0x18  properties[prop_PropsCount × 12B]  — {type(u32), arraySize(u32), hash(u32)}
///   +0x18 + prop_PropsCount*12: first property value = OIDResourceNameHash (FileKtid)
/// </summary>
public static class KidsObjDbResolver
{
    private const uint TypeKtidG1m       = 0x563bdef1;
    private const uint TypeKtidG1mB      = 0xBEF563DDu; // StreamingMeshletModelData
    private const uint TypeKtidG1tA      = 0xafbec60c;
    private const uint TypeKtidG1tB      = 0xAD57EBBA;
    private const uint TypeKtidKtid      = 0x8e39aa37;
    private const uint TypeKtidKidsObjDb = 0x20a6a0bb;

    private const uint PropKtidLink = 0xad260326u;

    // Bytes per element per type (from Python tool infoPerTypeNum; 6/7/9/11 unknown → 0)
    private static readonly int[] PropTypeSizes = [1, 1, 2, 2, 4, 4, 0, 0, 4, 0, 16, 0, 8, 12];
    //                                             0  1  2  3  4  5  6  7  8  9  10  11  12  13

    private static readonly byte[] IdokMagic = "IDOK0000"u8.ToArray();
    private static readonly byte[] DokMagic  = "_DOK0000"u8.ToArray();

    /// <summary>
    /// DOK-local context produced by Phase 1.
    /// ObjIdToG1tFk: DOK-local IDOK objectId → G1T FileKtid  (DOA6: KTID slots reference by objId)
    /// G1mFkToKtidFk: G1M FileKtid → KTID FileKtid           (resolved from PropKtidLink)
    /// </summary>
    private record DokContext(
        Dictionary<uint, uint> ObjIdToG1tFk,
        Dictionary<uint, uint> G1mFkToKtidFk);

    public static async Task<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>>
        BuildAsync(
            IReadOnlyList<AssetItemViewModel> allAssets,
            FdataExtractor extractor,
            IProgress<(int done, int total)>? progress = null)
    {
        var g1mMap = allAssets
            .Where(a => a.Record.TypeKtid is TypeKtidG1m or TypeKtidG1mB)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var g1tMap = allAssets
            .Where(a => a.Record.TypeKtid is TypeKtidG1tA or TypeKtidG1tB)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var ktidMap = allAssets
            .Where(a => a.Record.TypeKtid == TypeKtidKtid)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var kobjList = allAssets
            .Where(a => a.Record.TypeKtid == TypeKtidKidsObjDb)
            .ToList();

        // ── Phase 1: parse all DOKs in parallel ──────────────────────────────

        var dokContexts = new ConcurrentBag<DokContext>();
        int done  = 0;
        int total = kobjList.Count;

        await Parallel.ForEachAsync(kobjList, async (kobj, ct) =>
        {
            await Task.Run(() =>
            {
                try
                {
                    byte[] data = extractor.ExtractToMemory(kobj.Record, kobj.Container);
                    var ctx = ParseDok(data, g1mMap, g1tMap, ktidMap);
                    if (ctx != null) dokContexts.Add(ctx);
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        $"[KidsObjDb] Phase1 DOK 0x{kobj.Record.FileKtid:X8}: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
            }, ct);
        });

        AppLogger.Info(
            $"[KidsObjDb] Phase1 done: {dokContexts.Count}/{total} DOK valid, " +
            $"{dokContexts.Sum(c => c.G1mFkToKtidFk.Count)} G1M candidates");

        // ── Phase 2: extract unique KTID files globally (each at most once) ──

        var uniqueKtidFks = new HashSet<uint>(
            dokContexts.SelectMany(ctx => ctx.G1mFkToKtidFk.Values));

        var globalKtidSlots = new ConcurrentDictionary<uint, Dictionary<int, uint>?>();

        await Parallel.ForEachAsync(uniqueKtidFks, async (ktidFk, ct) =>
        {
            await Task.Run(() =>
            {
                globalKtidSlots[ktidFk] = ParseKtidSlots(ktidFk, ktidMap, extractor);
            }, ct);
        });

        AppLogger.Info(
            $"[KidsObjDb] Phase2 done: {uniqueKtidFks.Count} KTID processed, " +
            $"{globalKtidSlots.Count(kv => kv.Value != null)} valid");

        // ── Phase 3: resolve G1M → G1T (CPU only, no I/O) ───────────────────

        var result = new ConcurrentDictionary<uint, IReadOnlyList<AssetItemViewModel>>();

        Parallel.ForEach(dokContexts, ctx =>
        {
            foreach (var (g1mFk, ktidFk) in ctx.G1mFkToKtidFk)
            {
                if (result.ContainsKey(g1mFk)) continue;
                if (!globalKtidSlots.TryGetValue(ktidFk, out var slotMap) || slotMap is null)
                    continue;

                var resolved = ResolveSlots(slotMap, ctx.ObjIdToG1tFk, g1tMap);
                if (resolved != null)
                    result.TryAdd(g1mFk, resolved);
            }
        });

        AppLogger.Info($"[KidsObjDb] Phase3 done: {result.Count} G1M→G1T mappings resolved");
        return result;
    }

    // ── Phase 1 helper ────────────────────────────────────────────────────────

    private static DokContext? ParseDok(
        byte[] data,
        Dictionary<uint, AssetItemViewModel> g1mMap,
        Dictionary<uint, AssetItemViewModel> g1tMap,
        Dictionary<uint, AssetItemViewModel> ktidMap)
    {
        if (data.Length < 0x1C) return null;
        if (!data.AsSpan(0, 8).SequenceEqual(DokMagic)) return null;

        int hdrSize = (int)ReadU32(data, 8);
        if (hdrSize >= data.Length) return null;

        var objIdToG1tFk   = new Dictionary<uint, uint>();
        var objIdToKtidFk  = new Dictionary<uint, uint>();
        var pendingG1mKtid = new List<(uint g1mFk, uint ktidOid)>();

        int pos = hdrSize;
        while (pos + 16 <= data.Length)
        {
            if (!data.AsSpan(pos, 8).SequenceEqual(IdokMagic))
            {
                int next = FindMagic(data, pos + 1, IdokMagic);
                if (next < 0) break;
                pos = next;
                continue;
            }

            int  recSize  = (int)ReadU32(data, pos + 8);
            uint objectId = ReadU32(data, pos + 0x0C);
            uint typeHash = ReadU32(data, pos + 0x10);
            int  numProps = (int)ReadU32(data, pos + 0x14);

            if ((uint)numProps > 4096)
            { pos += Math.Max(8, recSize); continue; }

            int refOffset = pos + 0x18 + numProps * 12;
            if (refOffset + 4 <= data.Length)
            {
                uint refFk = ReadU32(data, refOffset);

                // Classify by which asset map contains refFk (= OIDResourceNameHash = FileKtid).
                // FileKtid membership is game-agnostic; typeHash is NOT used for classification
                // because it may vary between DOA6 and Ronin for the same asset type.
                if (g1tMap.ContainsKey(refFk))
                {
                    objIdToG1tFk[objectId] = refFk;
                }
                else if (ktidMap.ContainsKey(refFk))
                {
                    objIdToKtidFk[objectId] = refFk;
                }
                else if (g1mMap.ContainsKey(refFk))
                {
                    uint? ktidOid = ReadPropValue(data, pos, numProps, refOffset, PropKtidLink);
                    if (ktidOid.HasValue)
                        pendingG1mKtid.Add((refFk, ktidOid.Value));
                }
            }

            pos += Math.Max(8, recSize);
        }

        if (pendingG1mKtid.Count == 0) return null;

        var g1mFkToKtidFk = new Dictionary<uint, uint>(pendingG1mKtid.Count);
        foreach (var (g1mFk, ktidOid) in pendingG1mKtid)
        {
            if (ktidMap.ContainsKey(ktidOid))                               // Ronin: oid IS FileKtid
                g1mFkToKtidFk[g1mFk] = ktidOid;
            else if (objIdToKtidFk.TryGetValue(ktidOid, out uint ktidFk))  // DOA6: oid → FileKtid via IDOK
                g1mFkToKtidFk[g1mFk] = ktidFk;
        }

        return g1mFkToKtidFk.Count > 0
            ? new DokContext(objIdToG1tFk, g1mFkToKtidFk)
            : null;
    }

    private static Dictionary<int, uint>? ParseKtidSlots(
        uint ktidFk,
        Dictionary<uint, AssetItemViewModel> ktidMap,
        FdataExtractor extractor)
    {
        if (!ktidMap.TryGetValue(ktidFk, out var ktidVm)) return null;

        byte[] ktidData;
        try { ktidData = extractor.ExtractToMemory(ktidVm.Record, ktidVm.Container); }
        catch { return null; }

        var slotToObjId = new Dictionary<int, uint>(ktidData.Length / 8);
        for (int i = 0; i + 7 < ktidData.Length; i += 8)
            slotToObjId[(int)ReadU32(ktidData, i)] = ReadU32(ktidData, i + 4);

        if (slotToObjId.Count == 0) return null;

        int maxSlot = 0;
        foreach (int s in slotToObjId.Keys)
            if (s > maxSlot) maxSlot = s;
        if (maxSlot > 65535) return null;

        return slotToObjId;
    }

    private static IReadOnlyList<AssetItemViewModel>? ResolveSlots(
        Dictionary<int, uint> slotToObjId,
        Dictionary<uint, uint> objIdToG1tFk,
        Dictionary<uint, AssetItemViewModel> g1tMap)
    {
        int maxSlot = 0;
        foreach (int s in slotToObjId.Keys)
            if (s > maxSlot) maxSlot = s;

        var ordered = new AssetItemViewModel?[maxSlot + 1];
        int hits = 0;

        foreach (var (slot, oid) in slotToObjId)
        {
            AssetItemViewModel? vm = null;
            // DOA6: KTID value = DOK-local objId → resolve via ObjIdToG1tFk then g1tMap
            if (objIdToG1tFk.TryGetValue(oid, out uint g1tFk))
                g1tMap.TryGetValue(g1tFk, out vm);
            // Ronin: KTID value = G1T FileKtid directly
            if (vm == null)
                g1tMap.TryGetValue(oid, out vm);

            if (vm != null) { ordered[slot] = vm; hits++; }
        }

        if (hits == 0) return null;

        var list = ordered.Select(v => v ?? default!).ToList();
        while (list.Count > 0 && list[^1] is null) list.RemoveAt(list.Count - 1);
        return list.Count > 0 ? list.AsReadOnly() : null;
    }

    // ── Shared utilities ──────────────────────────────────────────────────────

    private static int FindMagic(byte[] data, int start, byte[] magic)
    {
        int end = data.Length - magic.Length;
        for (int i = start; i <= end; i++)
        {
            bool found = true;
            for (int j = 0; j < magic.Length; j++)
            {
                if (data[i + j] != magic[j]) { found = false; break; }
            }
            if (found) return i;
        }
        return -1;
    }

    private static uint? ReadPropValue(byte[] data, int recordStart, int numProps, int refFkOffset, uint targetHash)
    {
        int valPos = refFkOffset + 4;  // first property value = OIDResourceNameHash; start after it
        for (int i = 0; i < numProps; i++)
        {
            int doff = recordStart + 0x18 + i * 12;
            if (doff + 12 > data.Length) break;
            uint propType  = ReadU32(data, doff + 0);
            uint propCount = ReadU32(data, doff + 4);
            uint propHash  = ReadU32(data, doff + 8);
            int  typeSize  = propType < (uint)PropTypeSizes.Length ? PropTypeSizes[propType] : 4;
            int  valBytes  = (int)(propCount * typeSize);

            if (propHash == targetHash && propCount == 1 && typeSize == 4 && valPos + 4 <= data.Length)
                return ReadU32(data, valPos);

            valPos += valBytes;
        }
        return null;
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
