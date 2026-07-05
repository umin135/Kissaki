using System.Collections.Concurrent;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Lazy per-session cache that finds the "master" .kidsobjdb DOK (the one with the most G1M
/// FileKtid references) and uses it to resolve:
///   - G1M → G1T files (via KTID slot table + TextureContext chain)
///   - G1M → GRP + OIDEX files (via Displayset::Model props)
///
/// Initialization is deferred to the first query.  Subsequent calls reuse the already-built
/// context; KTID slot maps are cached per-KTID after first extraction.
/// </summary>
public sealed class MasterDokCache
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const uint TypeKtidG1m  = 0x563bdef1;
    private const uint TypeKtidG1mB = 0xBEF563DDu; // StreamingMeshletModelData
    private const uint TypeKtidG1tA = 0xafbec60c;
    private const uint TypeKtidG1tB = 0xAD57EBBA;
    private const uint TypeKtidKtid = 0x8e39aa37;
    private const uint TypeKtidDok  = 0x20a6a0bb;

    private const uint PropKtidLink     = 0xad260326u;  // G1M IDOK → KTID object ID

    // Displayset::Model (TypeHash 0xd40b3c8f) — falls into _g1mMap branch (OIDResourceNameHash = G1M FK)
    private const uint TypeHashDm       = 0xd40b3c8fu;
    private const uint PropDmCsvObjId   = 0xf92c5190u;  // → TextureBindTableCSV local obj_id (KTID chain)
    private const uint PropDmGrpFk      = 0x3bbfd9a5u;  // GRP FileKtid
    private const uint PropDmOidexFk    = 0x8dfd0584u;  // OIDEX FileKtid

    private static readonly int[] PropTypeSizes = [1, 1, 2, 2, 4, 4, 0, 0, 4, 0, 16, 0, 8, 12];
    private static readonly byte[] IdokMagic    = "IDOK0000"u8.ToArray();
    private static readonly byte[] DokMagic     = "_DOK0000"u8.ToArray();

    // ── MasterContext ─────────────────────────────────────────────────────────

    private record MasterContext(
        Dictionary<uint, uint> G1mFkToKtidFk,    // G1M FileKtid → KTID FileKtid  (for G1T chain)
        Dictionary<uint, uint> ObjIdToG1tFk,     // DOK-local objId → G1T FileKtid
        Dictionary<uint, uint> G1mFkToGrpFk,     // G1M FileKtid → GRP FileKtid   (Displayset::Model)
        Dictionary<uint, uint> G1mFkToOidexFk);  // G1M FileKtid → OIDEX FileKtid

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly FdataExtractor _extractor;
    private readonly Dictionary<uint, AssetItemViewModel> _g1mMap;
    private readonly Dictionary<uint, AssetItemViewModel> _g1tMap;
    private readonly Dictionary<uint, AssetItemViewModel> _ktidMap;
    private readonly IReadOnlyDictionary<uint, AssetItemViewModel> _allAssetsByKtid;
    private readonly IReadOnlyList<AssetItemViewModel>    _dokList;

    private readonly ConcurrentDictionary<uint, Dictionary<int, uint>?> _ktidSlotCache = new();
    private Task<MasterContext?>? _initTask;
    private readonly object       _initLock = new();

    // Pre-loaded combined G1T results (from cache or post-scan merge).
    // When non-null, GetG1tFilesAsync returns from here directly — no DOK scan triggered.
    private IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>? _preloadedG1tMap;

    // ── Construction ───────────────────────────────────────────────────���──────

    public MasterDokCache(
        IReadOnlyList<AssetItemViewModel> allAssets,
        FdataExtractor extractor)
    {
        _extractor = extractor;
        _g1mMap  = allAssets.Where(a => a.Record.TypeKtid is TypeKtidG1m or TypeKtidG1mB)
                             .ToDictionary(a => a.Record.FileKtid, a => a);
        _g1tMap  = allAssets.Where(a => a.Record.TypeKtid is TypeKtidG1tA or TypeKtidG1tB)
                             .ToDictionary(a => a.Record.FileKtid, a => a);
        _ktidMap = allAssets.Where(a => a.Record.TypeKtid == TypeKtidKtid)
                             .ToDictionary(a => a.Record.FileKtid, a => a);
        _dokList = allAssets.Where(a => a.Record.TypeKtid == TypeKtidDok)
                             .OrderByDescending(a => a.Record.SizeInContainer)
                             .ToList();

        // Full by-ktid lookup needed to resolve GRP/OIDEX by FileKtid
        var all = new Dictionary<uint, AssetItemViewModel>(allAssets.Count);
        foreach (var a in allAssets) all.TryAdd(a.Record.FileKtid, a);
        _allAssetsByKtid = all;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Pre-builds the master context so the first query doesn't block.</summary>
    public Task WarmUpAsync() => EnsureMasterContextAsync();

    /// <summary>
    /// Installs pre-resolved G1T results so that <see cref="GetG1tFilesAsync"/> never
    /// triggers a DOK scan. Called after loading from cache or after a full scan + merge.
    /// </summary>
    public void PreloadG1tResults(
        IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> map)
        => _preloadedG1tMap = map;

    /// <summary>
    /// Returns the ordered G1T slot list for <paramref name="g1mFileKtid"/>,
    /// or null if not found. Triggers lazy initialization on first call unless
    /// <see cref="PreloadG1tResults"/> has been called.
    /// </summary>
    public async Task<IReadOnlyList<AssetItemViewModel>?> GetG1tFilesAsync(uint g1mFileKtid)
    {
        // Fast path: pre-loaded combined results (from cache or post-scan merge)
        if (_preloadedG1tMap != null)
        {
            _preloadedG1tMap.TryGetValue(g1mFileKtid, out var cached);
            return cached is { Count: > 0 } ? cached : null;
        }

        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return null;

        // Only trust the DOK-based G1T chain when the DOK contains Displayset::Model records
        // (GRP links). Without DM records the KTID chain resolves unreliably on non-DOA6 games
        // and produces wrong texture assignments.
        if (ctx.G1mFkToGrpFk.Count == 0) return null;

        if (!ctx.G1mFkToKtidFk.TryGetValue(g1mFileKtid, out uint ktidFk)) return null;

        var slotMap = _ktidSlotCache.GetOrAdd(ktidFk, ParseKtidSlots);
        if (slotMap is null) return null;

        return ResolveSlots(slotMap, ctx.ObjIdToG1tFk);
    }

    /// <summary>
    /// Resolves all G1M→G1T mappings from the DM DOK chain and returns them as a flat map.
    /// Returns null when no DM DOKs were found.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>?> GetAllG1tMappingsAsync()
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null || ctx.G1mFkToGrpFk.Count == 0) return null;

        var result = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();
        foreach (var (g1mFk, ktidFk) in ctx.G1mFkToKtidFk)
        {
            var slotMap = _ktidSlotCache.GetOrAdd(ktidFk, ParseKtidSlots);
            if (slotMap is null) continue;
            var resolved = ResolveSlots(slotMap, ctx.ObjIdToG1tFk);
            if (resolved != null) result[g1mFk] = resolved;
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Returns the GRP and OIDEX FileKtid maps from the master DOK.
    /// Returns (null, null) when no DM DOKs were found or the DOK has no companion records.
    /// </summary>
    public async Task<(IReadOnlyDictionary<uint, uint>? GrpMap, IReadOnlyDictionary<uint, uint>? OidexMap)>
        GetCompanionMapsAsync()
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return (null, null);
        return (ctx.G1mFkToGrpFk.Count > 0 ? ctx.G1mFkToGrpFk : null,
                ctx.G1mFkToOidexFk.Count > 0 ? ctx.G1mFkToOidexFk : null);
    }

    /// <summary>
    /// Returns the GRP and OIDEX assets linked to <paramref name="g1mFileKtid"/>
    /// via the Displayset::Model record in the master DOK.
    /// </summary>
    public async Task<(AssetItemViewModel? Grp, AssetItemViewModel? Oidex)>
        GetCompanionAssetsAsync(uint g1mFileKtid)
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return (null, null);

        AssetItemViewModel? grp = null;
        AssetItemViewModel? oidex = null;

        if (ctx.G1mFkToGrpFk.TryGetValue(g1mFileKtid, out uint grpFk))
            _allAssetsByKtid.TryGetValue(grpFk, out grp);

        if (ctx.G1mFkToOidexFk.TryGetValue(g1mFileKtid, out uint oidexFk))
            _allAssetsByKtid.TryGetValue(oidexFk, out oidex);

        return (grp, oidex);
    }

    // ── Lazy init ─────────────────────────────────────────────────────────────

    private Task<MasterContext?> EnsureMasterContextAsync()
    {
        lock (_initLock)
        {
            _initTask ??= Task.Run(BuildMasterContext);
            return _initTask;
        }
    }

    private MasterContext? BuildMasterContext()
    {
        // Scan DOKs (sorted by size, largest first) and collect ALL DM-containing DOKs.
        // Games may have multiple "singleton" DOKs (one per DLC pack, character set, etc.).
        // Merging all DM DOKs gives complete coverage across all such packages.
        // If no DM DOKs are found, fall back to the single DOK with the most G1M refs.
        const int ScanLimit = 100;

        var     dmDoks  = new List<(byte[] Data, uint FileKtid, int G1mCount)>();
        byte[]? fbData  = null;
        int     fbCount = 0;
        uint    fbKtid  = 0;

        foreach (var dok in _dokList.Take(ScanLimit))
        {
            byte[] data;
            try { data = _extractor.ExtractToMemory(dok.Record, dok.Container); }
            catch { continue; }

            var (count, hasDm) = CountG1mRefs(data);
            if (count == 0) continue;

            if (hasDm)
            {
                AppLogger.Info(
                    $"[MasterDokCache] DM DOK found: 0x{dok.Record.FileKtid:X8} " +
                    $"({count} G1M refs, {dok.Record.SizeInContainer:N0} B)");
                dmDoks.Add((data, dok.Record.FileKtid, count));
            }
            else if (count > fbCount)
            {
                fbCount = count;
                fbData  = data;
                fbKtid  = dok.Record.FileKtid;
            }
        }

        if (dmDoks.Count > 0)
            return MergeDmDoks(dmDoks);

        if (fbData != null)
        {
            AppLogger.Info(
                $"[MasterDokCache] No DM DOK, fallback: 0x{fbKtid:X8} ({fbCount} G1M refs)");
            return ParseMasterDok(fbData);
        }

        AppLogger.Warn("[MasterDokCache] Master DOK not found");
        return null;
    }

    private MasterContext? MergeDmDoks(List<(byte[] Data, uint FileKtid, int G1mCount)> dmDoks)
    {
        var g1mFkToKtidFk  = new Dictionary<uint, uint>();
        var objIdToG1tFk   = new Dictionary<uint, uint>();
        var g1mFkToGrpFk   = new Dictionary<uint, uint>();
        var g1mFkToOidexFk = new Dictionary<uint, uint>();

        foreach (var (data, _, _) in dmDoks)
        {
            var ctx = ParseMasterDok(data);
            if (ctx is null) continue;
            foreach (var kv in ctx.G1mFkToKtidFk)  g1mFkToKtidFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.ObjIdToG1tFk)    objIdToG1tFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToGrpFk)    g1mFkToGrpFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToOidexFk)  g1mFkToOidexFk.TryAdd(kv.Key, kv.Value);
        }

        AppLogger.Info(
            $"[MasterDokCache] Merged {dmDoks.Count} DM DOKs: " +
            $"{g1mFkToKtidFk.Count} G1M→KTID, {objIdToG1tFk.Count} objId→G1T, " +
            $"{g1mFkToGrpFk.Count} G1M→GRP, {g1mFkToOidexFk.Count} G1M→OIDEX");

        if (g1mFkToKtidFk.Count == 0 && g1mFkToGrpFk.Count == 0) return null;
        return new MasterContext(g1mFkToKtidFk, objIdToG1tFk, g1mFkToGrpFk, g1mFkToOidexFk);
    }

    // ── DOK parsing ───────────────────────────────────────────────────────────

    private (int Count, bool HasDm) CountG1mRefs(byte[] data)
    {
        if (data.Length < 0x1C || !data.AsSpan(0, 8).SequenceEqual(DokMagic)) return (0, false);
        int hdrSize = (int)ReadU32(data, 8);
        if (hdrSize >= data.Length) return (0, false);

        int  count = 0;
        bool hasDm = false;
        int  pos   = hdrSize;
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
            uint typeHash = ReadU32(data, pos + 0x10);
            int  numProps = (int)ReadU32(data, pos + 0x14);
            if (typeHash == TypeHashDm) hasDm = true;
            if ((uint)numProps <= 4096)
            {
                int refOff = pos + 0x18 + numProps * 12;
                if (refOff + 4 <= data.Length && _g1mMap.ContainsKey(ReadU32(data, refOff)))
                    count++;
            }
            pos += Math.Max(8, recSize);
        }
        return (count, hasDm);
    }

    private MasterContext? ParseMasterDok(byte[] data)
    {
        if (data.Length < 0x1C || !data.AsSpan(0, 8).SequenceEqual(DokMagic)) return null;
        int hdrSize = (int)ReadU32(data, 8);
        if (hdrSize >= data.Length) return null;

        var objIdToG1tFk   = new Dictionary<uint, uint>();
        var objIdToKtidFk  = new Dictionary<uint, uint>();
        var pendingG1mKtid = new List<(uint g1mFk, uint ktidOid)>();
        var g1mFkToGrpFk   = new Dictionary<uint, uint>();
        var g1mFkToOidexFk = new Dictionary<uint, uint>();

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

            if ((uint)numProps <= 4096)
            {
                int refOff = pos + 0x18 + numProps * 12;
                if (refOff + 4 <= data.Length)
                {
                    uint refFk = ReadU32(data, refOff);

                    if (_g1tMap.ContainsKey(refFk))
                    {
                        // G1T asset record: objId → G1T FileKtid
                        objIdToG1tFk[objectId] = refFk;
                    }
                    else if (_ktidMap.ContainsKey(refFk))
                    {
                        // KTID asset record: objId → KTID FileKtid
                        objIdToKtidFk[objectId] = refFk;
                    }
                    else if (_g1mMap.ContainsKey(refFk))
                    {
                        if (typeHash == TypeHashDm)
                        {
                            // Displayset::Model: OIDResourceNameHash == G1M FK.
                            // KTID chain: DM.prop[0xf92c5190] → TextureBindTableCSV local obj_id
                            //             TBTCSV.OIDResourceNameHash → KTID FK → via objIdToKtidFk
                            uint? csvOid  = ReadPropValue(data, pos, numProps, refOff, PropDmCsvObjId);
                            uint? grpFk   = ReadPropValue(data, pos, numProps, refOff, PropDmGrpFk);
                            uint? oidexFk = ReadPropValue(data, pos, numProps, refOff, PropDmOidexFk);
                            if (csvOid.HasValue)  pendingG1mKtid.Add((refFk, csvOid.Value));
                            if (grpFk.HasValue)   g1mFkToGrpFk[refFk]   = grpFk.Value;
                            if (oidexFk.HasValue) g1mFkToOidexFk[refFk] = oidexFk.Value;
                        }
                        else
                        {
                            // Regular G1M IDOK: PropKtidLink → KTID object ID
                            uint? ktidOid = ReadPropValue(data, pos, numProps, refOff, PropKtidLink);
                            if (ktidOid.HasValue) pendingG1mKtid.Add((refFk, ktidOid.Value));
                        }
                    }
                }
            }

            pos += Math.Max(8, recSize);
        }

        // Resolve pending G1M → KTID mappings (order within DOK is not guaranteed)
        var g1mFkToKtidFk = new Dictionary<uint, uint>(pendingG1mKtid.Count);
        foreach (var (g1mFk, ktidOid) in pendingG1mKtid)
        {
            if (_ktidMap.ContainsKey(ktidOid))                               // Ronin: oid IS FileKtid
                g1mFkToKtidFk[g1mFk] = ktidOid;
            else if (objIdToKtidFk.TryGetValue(ktidOid, out uint ktidFk))    // DOA6: oid → FileKtid
                g1mFkToKtidFk[g1mFk] = ktidFk;
        }

        if (g1mFkToKtidFk.Count == 0 && g1mFkToGrpFk.Count == 0) return null;

        return new MasterContext(g1mFkToKtidFk, objIdToG1tFk, g1mFkToGrpFk, g1mFkToOidexFk);
    }

    // ── KTID / slot resolution ────────────────────────────────────────────────

    private Dictionary<int, uint>? ParseKtidSlots(uint ktidFk)
    {
        if (!_ktidMap.TryGetValue(ktidFk, out var vm)) return null;
        byte[] data;
        try { data = _extractor.ExtractToMemory(vm.Record, vm.Container); }
        catch { return null; }

        var slots = new Dictionary<int, uint>(data.Length / 8);
        for (int i = 0; i + 7 < data.Length; i += 8)
            slots[(int)ReadU32(data, i)] = ReadU32(data, i + 4);

        if (slots.Count == 0) return null;
        int maxSlot = 0;
        foreach (int s in slots.Keys) if (s > maxSlot) maxSlot = s;
        return maxSlot > 65535 ? null : slots;
    }

    private IReadOnlyList<AssetItemViewModel>? ResolveSlots(
        Dictionary<int, uint> slotToObjId,
        Dictionary<uint, uint> objIdToG1tFk)
    {
        int maxSlot = 0;
        foreach (int s in slotToObjId.Keys) if (s > maxSlot) maxSlot = s;

        var ordered = new AssetItemViewModel?[maxSlot + 1];
        int hits = 0;

        foreach (var (slot, oid) in slotToObjId)
        {
            AssetItemViewModel? vm = null;
            if (objIdToG1tFk.TryGetValue(oid, out uint g1tFk))
                _g1tMap.TryGetValue(g1tFk, out vm);
            if (vm == null)
                _g1tMap.TryGetValue(oid, out vm);
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
                if (data[i + j] != magic[j]) { found = false; break; }
            if (found) return i;
        }
        return -1;
    }

    private static uint? ReadPropValue(
        byte[] data, int recordStart, int numProps, int refFkOffset, uint targetHash)
    {
        int valPos = refFkOffset;  // first property value is OIDResourceNameHash at refFkOffset
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
