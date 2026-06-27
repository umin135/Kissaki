using KissakiViewer.ViewModels;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Builds a G1M → [G1T] mapping by parsing .kidsobjdb DOK files.
///
/// The DOK chain:
///   1. Parse each IDOK record → build object_id→G1T map and collect KTID ref
///   2. Parse the KTID file (slot, object_id) pairs
///   3. slot → object_id → G1T AssetItemViewModel → ordered list by slot index
///
/// IDOK record layout (all offsets from start of "IDOK0000" magic):
///   +0x00  "IDOK0000" magic (8B)
///   +0x08  rec_size (u32)   — total size including magic
///   +0x0C  object_id (u32)  — unique ID in this DOK, used by KTID slot map
///   +0x10  object_type (u32)
///   +0x14  num_props (u32)
///   +0x18  properties[num_props × 12B]  — {type_hint(u32), count(u32), schema_hash(u32)}
///   +0x18 + num_props*12: ref_fk (u32)  — RDB FileKtid of the referenced asset
/// </summary>
public static class KidsObjDbResolver
{
    private const uint TypeKtidG1m        = 0x563bdef1;
    private const uint TypeKtidG1tA       = 0xafbec60c;
    private const uint TypeKtidG1tB       = 0xAD57EBBA;
    private const uint TypeKtidKtid       = 0x8e39aa37;
    private const uint TypeKtidKidsObjDb  = 0x20a6a0bb;

    private static readonly byte[] IdokMagic = "IDOK0000"u8.ToArray();
    private static readonly byte[] DokMagic  = "_DOK0000"u8.ToArray();

    public static async Task<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>>
        BuildAsync(
            IReadOnlyList<AssetItemViewModel> allAssets,
            FdataExtractor extractor,
            IProgress<(int done, int total)>? progress = null)
    {
        var g1mMap = allAssets
            .Where(a => a.Record.TypeKtid is TypeKtidG1m)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var g1tMap = allAssets
            .Where(a => a.Record.TypeKtid is TypeKtidG1tA or TypeKtidG1tB)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var ktidMap = allAssets
            .Where(a => a.Record.TypeKtid is TypeKtidKtid)
            .ToDictionary(a => a.Record.FileKtid, a => a);

        var kobjList = allAssets
            .Where(a => a.Record.TypeKtid == TypeKtidKidsObjDb)
            .ToList();

        var result = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();

        int done = 0;
        int total = kobjList.Count;

        await Task.Run(() =>
        {
            foreach (var kobj in kobjList)
            {
                try
                {
                    byte[] data = extractor.ExtractToMemory(kobj.Record, kobj.Container);
                    if (data.Length < 28) continue;

                    // Verify DOK magic
                    if (!data.AsSpan(0, 8).SequenceEqual(DokMagic)) continue;

                    int hdrSize = (int)ReadU32(data, 8);
                    if (hdrSize >= data.Length) continue;

                    // Parse all IDOK records
                    var objIdToG1t  = new Dictionary<uint, AssetItemViewModel>();
                    var ktidFileFks = new List<uint>();
                    var g1mFks      = new List<uint>();

                    int pos = hdrSize;
                    while (pos + 16 <= data.Length)
                    {
                        // Scan for IDOK magic
                        if (!data.AsSpan(pos, 8).SequenceEqual(IdokMagic))
                        {
                            int next = FindMagic(data, pos + 1, IdokMagic);
                            if (next < 0) break;
                            pos = next;
                            continue;
                        }

                        int recSize = (int)ReadU32(data, pos + 8);
                        if (recSize < 20 || pos + recSize > data.Length + 4)
                        { pos += 8; continue; }

                        uint objectId = ReadU32(data, pos + 0x0C);
                        int numProps  = (int)ReadU32(data, pos + 0x14);
                        if (numProps > 4096) { pos += 8; continue; }

                        int refOffset = pos + 0x18 + numProps * 12;
                        if (refOffset + 4 <= data.Length)
                        {
                            uint refFk = ReadU32(data, refOffset);
                            if (g1tMap.TryGetValue(refFk, out var g1tVm))
                                objIdToG1t[objectId] = g1tVm;
                            else if (ktidMap.ContainsKey(refFk))
                                ktidFileFks.Add(refFk);
                            else if (g1mMap.ContainsKey(refFk))
                                g1mFks.Add(refFk);
                        }

                        int advance = recSize >= 8 ? recSize : 8;
                        pos += advance;
                    }

                    if (g1mFks.Count == 0) goto fallback;

                    // Attempt chain-based mapping via KTID file
                    if (ktidFileFks.Count > 0 && objIdToG1t.Count > 0)
                    {
                        // Parse the KTID texture binding table
                        uint ktidFk = ktidFileFks[0];
                        if (!ktidMap.TryGetValue(ktidFk, out var ktidVm)) goto fallback;

                        byte[] ktidData;
                        try { ktidData = extractor.ExtractToMemory(ktidVm.Record, ktidVm.Container); }
                        catch { goto fallback; }

                        // KTID file format: [(u32 slot_index, u32 object_id), ...]
                        var slotToObjId = new Dictionary<int, uint>();
                        for (int i = 0; i + 7 < ktidData.Length; i += 8)
                        {
                            int  slot  = (int)ReadU32(ktidData, i);
                            uint objId = ReadU32(ktidData, i + 4);
                            slotToObjId[slot] = objId;
                        }

                        if (slotToObjId.Count == 0) goto fallback;

                        int maxSlot = 0;
                        foreach (int s in slotToObjId.Keys)
                            if (s > maxSlot) maxSlot = s;

                        var orderedG1ts = new AssetItemViewModel?[maxSlot + 1];
                        bool anyMapped = false;
                        for (int s = 0; s <= maxSlot; s++)
                        {
                            if (slotToObjId.TryGetValue(s, out uint oid) &&
                                objIdToG1t.TryGetValue(oid, out var vm))
                            {
                                orderedG1ts[s] = vm;
                                anyMapped = true;
                            }
                        }

                        if (anyMapped)
                        {
                            // Build compact list preserving slot-index positions
                            // (null slots become null entries, trimmed at end)
                            var list = orderedG1ts
                                .Select(v => v ?? default!)
                                .ToList();

                            // Remove trailing nulls
                            while (list.Count > 0 && list[^1] is null) list.RemoveAt(list.Count - 1);

                            if (list.Count > 0)
                            {
                                lock (result)
                                {
                                    foreach (uint g1mFk in g1mFks)
                                        result[g1mFk] = list.AsReadOnly();
                                }
                                goto nextFile;
                            }
                        }
                    }

                    fallback:
                    // Fall back to scan-order if DOK chain failed
                    if (objIdToG1t.Count > 0 && g1mFks.Count > 0)
                    {
                        // Keep scan order (same as old behavior)
                        var fallbackList = objIdToG1t.Values
                            .DistinctBy(v => v.Record.FileKtid)
                            .ToList();
                        if (fallbackList.Count > 0)
                        {
                            lock (result)
                            {
                                foreach (uint g1mFk in g1mFks)
                                    if (!result.ContainsKey(g1mFk))
                                        result[g1mFk] = fallbackList.AsReadOnly();
                            }
                        }
                    }
                    else if (g1mFks.Count > 0)
                    {
                        // Old brute-force fallback for non-DOK kidsobjdb
                        var scanned = ScanBruteForce(data, g1tMap, g1mMap);
                        if (scanned.Count > 0)
                        {
                            lock (result)
                            {
                                foreach (uint g1mFk in g1mFks)
                                    if (!result.ContainsKey(g1mFk))
                                        result[g1mFk] = scanned.AsReadOnly();
                            }
                        }
                    }

                    nextFile:;
                }
                catch { }

                progress?.Report((++done, total));
            }
        });

        return result;
    }

    /// <summary>Old brute-force scan for files that don't parse as DOK.</summary>
    private static List<AssetItemViewModel> ScanBruteForce(
        byte[] data,
        Dictionary<uint, AssetItemViewModel> g1tMap,
        Dictionary<uint, AssetItemViewModel> g1mMap)
    {
        var g1mFks   = new List<uint>();
        var g1tSeen  = new Dictionary<uint, int>();

        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            uint v = ReadU32(data, i);
            if (g1mMap.ContainsKey(v) && !g1mFks.Contains(v)) g1mFks.Add(v);
            if (g1tMap.ContainsKey(v) && !g1tSeen.ContainsKey(v)) g1tSeen[v] = g1tSeen.Count;
        }

        return g1mFks.Count == 0 ? []
            : g1tSeen.OrderBy(kv => kv.Value).Select(kv => g1tMap[kv.Key]).ToList();
    }

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

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
