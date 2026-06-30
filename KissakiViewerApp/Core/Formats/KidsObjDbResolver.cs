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

    // G1M IDOK property that contains the object_id of its associated KTID IDOK
    private const uint PropKtidLink = 0xad260326u;

    // Byte sizes for IDOK property value types (index = type id; 0-2 and unknown → 4 as fallback)
    private static readonly int[] PropTypeSizes = [4, 4, 4, 1, 2, 4, 8, 1, 4, 8, 4, 4];

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
                    var objIdToG1t    = new Dictionary<uint, AssetItemViewModel>();
                    var objIdToKtidFk = new Dictionary<uint, uint>();  // ktid_oid -> ktid_fk
                    var g1mToKtidOid  = new Dictionary<uint, uint>();  // g1m_fk   -> ktid_oid
                    var ktidFileFks   = new List<uint>();               // fallback list
                    var g1mFks        = new List<uint>();

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
                            {
                                objIdToG1t[objectId] = g1tVm;
                            }
                            else if (ktidMap.ContainsKey(refFk))
                            {
                                ktidFileFks.Add(refFk);
                                objIdToKtidFk[objectId] = refFk;
                            }
                            else if (g1mMap.ContainsKey(refFk))
                            {
                                g1mFks.Add(refFk);
                                // Read prop PropKtidLink to find the associated KTID's object_id
                                uint? ktidOid = ReadPropValue(data, pos, numProps, refOffset, PropKtidLink);
                                if (ktidOid.HasValue)
                                    g1mToKtidOid[refFk] = ktidOid.Value;
                            }
                        }

                        int advance = recSize >= 8 ? recSize : 8;
                        pos += advance;
                    }

                    if (g1mFks.Count == 0) goto fallback;

                    // Attempt per-G1M KTID chain mapping
                    if (objIdToG1t.Count > 0)
                    {
                        // Cache parsed KTID results within this DOK so the same file is only
                        // extracted and decompressed once regardless of how many G1Ms reference it.
                        // null = already tried but invalid (extraction failed, empty, or maxSlot > 65535, no hits).
                        var ktidResultCache = new Dictionary<uint, AssetItemViewModel?[]?>();

                        bool anyMapped = false;
                        foreach (uint g1mFk in g1mFks)
                        {
                            // Build prioritized candidate list:
                            //   1. PropKtidLink-resolved KTID (per-G1M, most authoritative)
                            //   2. IDRK header dependency field at 0x74
                            //   3. All other KTID files found in this DOK (last resort)
                            var candidates = new List<uint>(ktidFileFks.Count + 2);

                            bool hasPropLink = g1mToKtidOid.TryGetValue(g1mFk, out uint ktidOid);
                            if (hasPropLink)
                            {
                                if (objIdToKtidFk.TryGetValue(ktidOid, out uint resolvedFk))
                                    candidates.Add(resolvedFk);
                                else if (ktidMap.ContainsKey(ktidOid))
                                    candidates.Add(ktidOid);  // DOA6: PropKtidLink stores FileKtid directly
                            }

                            if (g1mMap.TryGetValue(g1mFk, out var g1mVm))
                            {
                                uint depKtid = extractor.ReadG1mDependencyKtid(g1mVm.Record, g1mVm.Container);
                                if (depKtid != 0 && ktidMap.ContainsKey(depKtid) && !candidates.Contains(depKtid))
                                    candidates.Add(depKtid);
                            }

                            foreach (uint fk in ktidFileFks)
                                if (!candidates.Contains(fk))
                                    candidates.Add(fk);

                            if (candidates.Count == 0) continue;

                            // Try candidates in priority order.
                            // Use the FIRST candidate that has valid slot range and at least one G1T hit.
                            // Do NOT compare hit counts across candidates — priority order is authoritative.
                            AssetItemViewModel?[]? resolvedOrdered = null;

                            foreach (uint candidateFk in candidates)
                            {
                                if (!ktidMap.TryGetValue(candidateFk, out var ktidVm)) continue;

                                if (!ktidResultCache.TryGetValue(candidateFk, out AssetItemViewModel?[]? cachedResult))
                                {
                                    // First time seeing this KTID in this DOK — extract, parse, resolve, cache.
                                    byte[] ktidData;
                                    try { ktidData = extractor.ExtractToMemory(ktidVm.Record, ktidVm.Container); }
                                    catch { ktidResultCache[candidateFk] = null; continue; }

                                    // KTID format: [(slot u32, fileKtid-or-objId u32), ...]
                                    var slotToObjId = new Dictionary<int, uint>();
                                    for (int i = 0; i + 7 < ktidData.Length; i += 8)
                                        slotToObjId[(int)ReadU32(ktidData, i)] = ReadU32(ktidData, i + 4);

                                    if (slotToObjId.Count == 0) { ktidResultCache[candidateFk] = null; continue; }

                                    int maxSlot = 0;
                                    foreach (int s in slotToObjId.Keys)
                                        if (s > maxSlot) maxSlot = s;

                                    // Huge slot values indicate a non-texture KTID — skip it.
                                    if (maxSlot > 65535) { ktidResultCache[candidateFk] = null; continue; }

                                    var ordered = new AssetItemViewModel?[maxSlot + 1];
                                    int hits = 0;
                                    for (int s = 0; s <= maxSlot; s++)
                                    {
                                        if (!slotToObjId.TryGetValue(s, out uint oid)) continue;
                                        // DOA6: KTID stores DOK object_id → objIdToG1t
                                        // Ronin: KTID stores G1T FileKtid directly → g1tMap
                                        if (!objIdToG1t.TryGetValue(oid, out var vm))
                                            g1tMap.TryGetValue(oid, out vm);
                                        if (vm != null) { ordered[s] = vm; hits++; }
                                    }

                                    cachedResult = hits > 0 ? ordered : null;
                                    ktidResultCache[candidateFk] = cachedResult;
                                }

                                if (cachedResult is null) continue;

                                resolvedOrdered = cachedResult;
                                break;  // First valid candidate wins
                            }

                            if (resolvedOrdered is null)
                            {
                                var tried = string.Join(",", candidates.Take(4).Select(x => $"0x{x:X8}"));
                                AppLogger.Warn($"[KidsObjDb] G1M 0x{g1mFk:X8}: {candidates.Count}개 후보 시도 — G1T 없음 (tried=[{tried}])");
                                continue;
                            }

                            var list = resolvedOrdered.Select(v => v ?? default!).ToList();
                            while (list.Count > 0 && list[^1] is null) list.RemoveAt(list.Count - 1);

                            if (list.Count > 0)
                            {
                                lock (result) result[g1mFk] = list.AsReadOnly();
                                anyMapped = true;
                            }
                        }
                        // Only skip fallback if every G1M in this DOK was resolved by KTID chain
                        if (anyMapped && g1mFks.TrueForAll(fk => result.ContainsKey(fk)))
                            goto nextFile;
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
                catch (Exception ex)
                {
                    AppLogger.Error($"[KidsObjDb] DOK 0x{kobj.Record.FileKtid:X8} 처리 예외: {ex.GetType().Name}: {ex.Message}");
                }

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

    /// <summary>
    /// Reads a single u32 property value from an IDOK record by matching <paramref name="targetHash"/>.
    /// Returns null if the property is absent or cannot be read.
    /// </summary>
    private static uint? ReadPropValue(byte[] data, int recordStart, int numProps, int refFkOffset, uint targetHash)
    {
        int valPos = refFkOffset + 4;  // property values start after ref_fk
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
