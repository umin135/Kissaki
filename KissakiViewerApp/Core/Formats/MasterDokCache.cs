using System.Collections.Concurrent;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Lazy per-session cache that finds the "master" .kidsobjdb DOK (the one with the most G1M
/// FileKtid references) and uses it to resolve:
///   - G1M → G1T files (via KTID slot table + TextureContext chain)
///   - G1M → GRP + OIDEX files (via Displayset::Model props)
///   - G1M → MPR KTID + KTS (via scndb → CE1Common → MaterialEditor chain)
///
/// Initialization is deferred to the first query.  Subsequent calls reuse the already-built
/// context; KTID slot maps are cached per-KTID after first extraction.
/// </summary>
public sealed class MasterDokCache
{
    // ── TypeKtid constants ────────────────────────────────────────────────────

    private const uint TypeKtidG1m  = 0x563bdef1;
    private const uint TypeKtidG1mB = 0xBEF563DDu; // StreamingMeshletModelData
    private const uint TypeKtidG1tA = 0xafbec60c;
    private const uint TypeKtidG1tB = 0xAD57EBBA;
    private const uint TypeKtidKtid = 0x8e39aa37;
    private const uint TypeKtidDok  = 0x20a6a0bb;

    // ── Singleton DOK FileKtid constants ─────────────────────────────────────

    private const uint ScndbFk     = 0x6D011726u;  // Scene DB — costume name registry
    private const uint Ce1CommonFk = 0x2082AD97u;  // CharacterEditor1Common — MPR variation table
    private const uint MatEditorFk = 0xD956E4A2u;  // MaterialEditor — MPR KTID + KTS resolver

    // ── IDOK typeHash constants ───────────────────────────────────────────────

    private const uint TypeHashDm          = 0xd40b3c8fu;  // Displayset::Model (CE singleton)
    private const uint TypeHashScndbEntry  = 0xDAC911D7u;  // scndb costume array entry
    private const uint TypeHashCharSetting = 0xD186CEEBu;  // CE1Common CharacterSetting per costume
    private const uint TypeHashMatBind     = 0xA8F14404u;  // MaterialEditor MaterialBindEntry
    private const uint TypeHashTexBind     = 0x3059B9C3u;  // MaterialEditor/CE TextureBindTableCSV

    // ── Prop hash constants ───────────────────────────────────────────────────

    private const uint PropKtidLink   = 0xad260326u;  // G1M IDOK → KTID obj ID; value coincides with Character.sid FK in system.rdb
    private const uint PropDmG1mFk    = 0x8ab68b3fu;  // DM: G1M FileKtid (OIDResourceNameHash)
    private const uint PropDmCsvObjId = 0xf92c5190u;  // DM: → TextureBindTableCSV local obj_id
    private const uint PropDmGrpFk    = 0x3bbfd9a5u;  // DM: GRP FileKtid
    private const uint PropDmSidFk    = 0x07aaf542u;  // DM: SID (physics sim) FileKtid
    private const uint PropDmOidexFk  = 0x8dfd0584u;  // DM: OIDEX FileKtid
    private const uint PropDmRigbinFk = 0x1b4ff321u;  // DM: rigbin FileKtid

    // scndb props
    private const uint PropScndbNames = 0x4D269345u;  // Byte[]: null-separated ASCII name list
    private const uint PropScndbOids  = 0x8F9F2DA6u;  // UInt32[]: corresponding cos_oid array

    // CE1Common props
    private const uint PropMiArray = 0x24C114F6u;  // UInt32[]: MI obj_id array (slot×variation)

    // MaterialEditor props
    private const uint PropKtsFk      = 0x0A3D837Bu;  // MaterialBindEntry: KTS FileKtid
    private const uint PropTbcOid     = 0xF92C5190u;  // MaterialBindEntry: TextureBindTableCSV oid
    private const uint PropMprKtidFk  = 0x7A1E1EF8u;  // TextureBindTableCSV: KTID FileKtid

    private static readonly int[] PropTypeSizes = [1, 1, 2, 2, 4, 4, 0, 0, 4, 0, 16, 0, 8, 12];
    private static readonly byte[] IdokMagic    = "IDOK0000"u8.ToArray();
    private static readonly byte[] DokMagic     = "_DOK0000"u8.ToArray();

    // ── MasterContext ─────────────────────────────────────────────────────────

    private record MasterContext(
        Dictionary<uint, uint>       G1mFkToKtidFk,
        Dictionary<uint, uint>       ObjIdToG1tFk,
        Dictionary<uint, uint>       G1mFkToGrpFk,
        Dictionary<uint, uint>       G1mFkToOidexFk,
        Dictionary<uint, uint>       G1mFkToRigbinFk,
        Dictionary<uint, List<uint>> G1mFkToAllLinkedFks,
        // MPR costume chain (scndb → CE1Common → MaterialEditor)
        Dictionary<string, uint>     CostumeNameToOid,
        Dictionary<uint, List<uint>> CosOidToMiObjIds,
        Dictionary<uint, uint>       MiObjIdToMprKtidFk,
        Dictionary<uint, uint>       MiObjIdToKtsFk,
        // MaterialEditor G1T mapping: DOK-local objId → G1T FileKtid
        Dictionary<uint, uint>       MatEdObjIdToG1tFk
    );

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

    private IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>? _preloadedG1tMap;

    // ── Construction ──────────────────────────────────────────────────────────

    public MasterDokCache(
        IReadOnlyList<AssetItemViewModel> allAssets,
        FdataExtractor extractor)
    {
        _extractor = extractor;

        var g1mList  = new List<AssetItemViewModel>();
        var g1tList  = new List<AssetItemViewModel>();
        var ktidList = new List<AssetItemViewModel>();
        var dokList  = new List<AssetItemViewModel>();

        foreach (var a in allAssets)
        {
            switch (a.Record.TypeKtid)
            {
                case TypeKtidG1m or TypeKtidG1mB: g1mList.Add(a);  break;
                case TypeKtidG1tA or TypeKtidG1tB: g1tList.Add(a); break;
                case TypeKtidKtid:                 ktidList.Add(a); break;
                case TypeKtidDok:                  dokList.Add(a);  break;
            }
        }

        _g1mMap  = new Dictionary<uint, AssetItemViewModel>(g1mList.Count);
        foreach (var a in g1mList)  _g1mMap.TryAdd(a.Record.FileKtid, a);

        _g1tMap  = new Dictionary<uint, AssetItemViewModel>(g1tList.Count);
        foreach (var a in g1tList)  _g1tMap.TryAdd(a.Record.FileKtid, a);

        _ktidMap = new Dictionary<uint, AssetItemViewModel>(ktidList.Count);
        foreach (var a in ktidList) _ktidMap.TryAdd(a.Record.FileKtid, a);

        dokList.Sort((a, b) => b.Record.SizeInContainer.CompareTo(a.Record.SizeInContainer));
        _dokList = dokList;

        var all = new Dictionary<uint, AssetItemViewModel>(allAssets.Count);
        foreach (var a in allAssets) all.TryAdd(a.Record.FileKtid, a);
        _allAssetsByKtid = all;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public Task WarmUpAsync() => EnsureMasterContextAsync();

    public void PreloadG1tResults(
        IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> map)
        => _preloadedG1tMap = map;

    public async Task<IReadOnlyList<AssetItemViewModel>?> GetG1tFilesAsync(uint g1mFileKtid)
    {
        if (_preloadedG1tMap != null)
        {
            _preloadedG1tMap.TryGetValue(g1mFileKtid, out var cached);
            return cached is { Count: > 0 } ? cached : null;
        }

        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return null;

        if (ctx.G1mFkToGrpFk.Count == 0) return null;
        if (!ctx.G1mFkToKtidFk.TryGetValue(g1mFileKtid, out uint ktidFk)) return null;

        var slotMap = _ktidSlotCache.GetOrAdd(ktidFk, ParseKtidSlots);
        if (slotMap is null) return null;

        return ResolveSlots(slotMap, ctx.ObjIdToG1tFk);
    }

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

    public async Task<(IReadOnlyDictionary<uint, uint>? GrpMap,
                       IReadOnlyDictionary<uint, uint>? OidexMap,
                       IReadOnlyDictionary<uint, uint>? RigbinMap)>
        GetCompanionMapsAsync()
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return (null, null, null);
        return (ctx.G1mFkToGrpFk.Count    > 0 ? ctx.G1mFkToGrpFk    : null,
                ctx.G1mFkToOidexFk.Count  > 0 ? ctx.G1mFkToOidexFk  : null,
                ctx.G1mFkToRigbinFk.Count > 0 ? ctx.G1mFkToRigbinFk : null);
    }

    public async Task<IReadOnlyList<AssetItemViewModel>> GetAllLinkedAssetsAsync(uint g1mFileKtid)
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null || !ctx.G1mFkToAllLinkedFks.TryGetValue(g1mFileKtid, out var fks))
            return [];

        var result = new List<AssetItemViewModel>(fks.Count);
        foreach (uint fk in fks)
            if (_allAssetsByKtid.TryGetValue(fk, out var a))
                result.Add(a);
        return result;
    }

    public async Task<(AssetItemViewModel? Grp, AssetItemViewModel? Oidex, AssetItemViewModel? Rigbin)>
        GetCompanionAssetsAsync(uint g1mFileKtid)
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return (null, null, null);

        AssetItemViewModel? grp = null, oidex = null, rigbin = null;

        if (ctx.G1mFkToGrpFk.TryGetValue(g1mFileKtid, out uint grpFk))
            _allAssetsByKtid.TryGetValue(grpFk, out grp);
        if (ctx.G1mFkToOidexFk.TryGetValue(g1mFileKtid, out uint oidexFk))
            _allAssetsByKtid.TryGetValue(oidexFk, out oidex);
        if (ctx.G1mFkToRigbinFk.TryGetValue(g1mFileKtid, out uint rigbinFk))
            _allAssetsByKtid.TryGetValue(rigbinFk, out rigbin);

        return (grp, oidex, rigbin);
    }

    /// <summary>
    /// Returns MPR KTID FileKtids for the given costume name.
    /// Chain: scndb (name → cos_oid) → CE1Common (cos_oid → MI[]) → MaterialEditor (MI → KTID FK).
    /// </summary>
    public async Task<IReadOnlyList<uint>> GetMprKtidFksAsync(string costumeName)
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return [];
        if (!ctx.CostumeNameToOid.TryGetValue(costumeName, out uint cosOid)) return [];
        if (!ctx.CosOidToMiObjIds.TryGetValue(cosOid, out var miOids)) return [];

        var result = new List<uint>();
        var seen   = new HashSet<uint>();
        foreach (uint miOid in miOids)
            if (ctx.MiObjIdToMprKtidFk.TryGetValue(miOid, out uint fk) && seen.Add(fk))
                result.Add(fk);
        return result;
    }

    /// <summary>
    /// Returns KTS FileKtids for the given costume name (same chain as GetMprKtidFksAsync).
    /// </summary>
    public async Task<IReadOnlyList<uint>> GetKtsFksAsync(string costumeName)
    {
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null) return [];
        if (!ctx.CostumeNameToOid.TryGetValue(costumeName, out uint cosOid)) return [];
        if (!ctx.CosOidToMiObjIds.TryGetValue(cosOid, out var miOids)) return [];

        var result = new List<uint>();
        var seen   = new HashSet<uint>();
        foreach (uint miOid in miOids)
            if (ctx.MiObjIdToKtsFk.TryGetValue(miOid, out uint fk) && seen.Add(fk))
                result.Add(fk);
        return result;
    }

    /// <summary>
    /// Resolves G1T files referenced by MPR KTID files.
    /// MPR KTID values are MaterialEditor DOK-local objIds, not FileKtids directly.
    /// Lookup path: MPR KTID (slot, objId) → MatEdObjIdToG1tFk[objId] → G1T FileKtid.
    /// </summary>
    public async Task<IReadOnlyList<AssetItemViewModel>> GetMprG1tFilesAsync(IReadOnlyList<uint> mprKtidFks)
    {
        if (mprKtidFks.Count == 0) return [];
        var ctx = await EnsureMasterContextAsync();
        if (ctx is null || ctx.MatEdObjIdToG1tFk.Count == 0) return [];

        var result = new List<AssetItemViewModel>();
        var seen   = new HashSet<uint>();

        foreach (uint mprFk in mprKtidFks)
        {
            if (!_ktidMap.TryGetValue(mprFk, out var ktidVm)) continue;
            byte[] kd;
            try { kd = _extractor.ExtractToMemory(ktidVm.Record, ktidVm.Container); }
            catch { continue; }

            for (int i = 0; i + 7 < kd.Length; i += 8)
            {
                uint objId = ReadU32(kd, i + 4);
                if (objId == 0) continue;
                if (ctx.MatEdObjIdToG1tFk.TryGetValue(objId, out uint g1tFk)
                    && seen.Add(g1tFk)
                    && _g1tMap.TryGetValue(g1tFk, out var g1tVm))
                    result.Add(g1tVm);
            }
        }

        AppLogger.Info($"[MasterDokCache] GetMprG1tFilesAsync: {result.Count} G1T from {mprKtidFks.Count} MPR KTIDs");
        return result;
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

        // Parse MPR costume chain singletons (DOA6-specific, fail gracefully if absent)
        var costumeNameToOid    = ParseScndb();
        var cosOidToMiObjIds    = ParseCe1Common();
        var (miToMprKtid, miToKts, matEdObjToG1t) = ParseMaterialEditor();

        if (dmDoks.Count > 0)
            return MergeDmDoks(dmDoks, costumeNameToOid, cosOidToMiObjIds, miToMprKtid, miToKts, matEdObjToG1t);

        if (fbData != null)
        {
            AppLogger.Info(
                $"[MasterDokCache] No DM DOK, fallback: 0x{fbKtid:X8} ({fbCount} G1M refs)");
            var ctx = ParseMasterDok(fbData);
            if (ctx is null) return null;
            return ctx with
            {
                CostumeNameToOid   = costumeNameToOid,
                CosOidToMiObjIds   = cosOidToMiObjIds,
                MiObjIdToMprKtidFk = miToMprKtid,
                MiObjIdToKtsFk     = miToKts,
                MatEdObjIdToG1tFk  = matEdObjToG1t,
            };
        }

        AppLogger.Warn("[MasterDokCache] Master DOK not found");
        return null;
    }

    private MasterContext? MergeDmDoks(
        List<(byte[] Data, uint FileKtid, int G1mCount)> dmDoks,
        Dictionary<string, uint>     costumeNameToOid,
        Dictionary<uint, List<uint>> cosOidToMiObjIds,
        Dictionary<uint, uint>       miToMprKtid,
        Dictionary<uint, uint>       miToKts,
        Dictionary<uint, uint>       matEdObjToG1t)
    {
        var g1mFkToKtidFk       = new Dictionary<uint, uint>();
        var objIdToG1tFk        = new Dictionary<uint, uint>();
        var g1mFkToGrpFk        = new Dictionary<uint, uint>();
        var g1mFkToOidexFk      = new Dictionary<uint, uint>();
        var g1mFkToRigbinFk     = new Dictionary<uint, uint>();
        var g1mFkToAllLinkedFks = new Dictionary<uint, List<uint>>();

        foreach (var (data, _, _) in dmDoks)
        {
            var ctx = ParseMasterDok(data);
            if (ctx is null) continue;
            foreach (var kv in ctx.G1mFkToKtidFk)      g1mFkToKtidFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.ObjIdToG1tFk)        objIdToG1tFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToGrpFk)        g1mFkToGrpFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToOidexFk)      g1mFkToOidexFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToRigbinFk)     g1mFkToRigbinFk.TryAdd(kv.Key, kv.Value);
            foreach (var kv in ctx.G1mFkToAllLinkedFks) g1mFkToAllLinkedFks.TryAdd(kv.Key, kv.Value);
        }

        AppLogger.Info(
            $"[MasterDokCache] Merged {dmDoks.Count} DM DOKs: " +
            $"{g1mFkToKtidFk.Count} G1M→KTID, {objIdToG1tFk.Count} objId→G1T, " +
            $"{g1mFkToGrpFk.Count} G1M→GRP, {g1mFkToOidexFk.Count} G1M→OIDEX, " +
            $"{g1mFkToRigbinFk.Count} G1M→rigbin, {g1mFkToAllLinkedFks.Count} G1M→AllLinked");
        AppLogger.Info(
            $"[MasterDokCache] Costume chain: {costumeNameToOid.Count} names, " +
            $"{cosOidToMiObjIds.Count} CE1Common entries, {miToMprKtid.Count} MPR KTID, {miToKts.Count} KTS");

        if (g1mFkToKtidFk.Count == 0 && g1mFkToGrpFk.Count == 0) return null;

        return new MasterContext(
            g1mFkToKtidFk, objIdToG1tFk,
            g1mFkToGrpFk, g1mFkToOidexFk, g1mFkToRigbinFk, g1mFkToAllLinkedFks,
            costumeNameToOid, cosOidToMiObjIds, miToMprKtid, miToKts, matEdObjToG1t);
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

        var objIdToG1tFk        = new Dictionary<uint, uint>();
        var objIdToKtidFk       = new Dictionary<uint, uint>();
        var pendingG1mKtid      = new List<(uint g1mFk, uint ktidOid)>();
        var g1mFkToGrpFk        = new Dictionary<uint, uint>();
        var g1mFkToOidexFk      = new Dictionary<uint, uint>();
        var g1mFkToRigbinFk     = new Dictionary<uint, uint>();
        var g1mFkToAllLinkedFks = new Dictionary<uint, List<uint>>();

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
                        objIdToG1tFk[objectId] = refFk;
                    }
                    else if (_ktidMap.ContainsKey(refFk))
                    {
                        objIdToKtidFk[objectId] = refFk;
                    }
                    else if (_g1mMap.ContainsKey(refFk))
                    {
                        if (typeHash == TypeHashDm)
                        {
                            uint? csvOid   = ReadPropValue(data, pos, numProps, refOff, PropDmCsvObjId);
                            uint? grpFk    = ReadPropValue(data, pos, numProps, refOff, PropDmGrpFk);
                            uint? oidexFk  = ReadPropValue(data, pos, numProps, refOff, PropDmOidexFk);
                            uint? rigbinFk = ReadPropValue(data, pos, numProps, refOff, PropDmRigbinFk);
                            if (csvOid.HasValue)              pendingG1mKtid.Add((refFk, csvOid.Value));
                            if (grpFk.HasValue)               g1mFkToGrpFk[refFk]    = grpFk.Value;
                            if (oidexFk.HasValue)             g1mFkToOidexFk[refFk]  = oidexFk.Value;
                            if (rigbinFk is { } rb && rb != 0) g1mFkToRigbinFk[refFk] = rb;

                            var linked = ReadAllLinkedFileKtids(data, pos, numProps, refOff, refFk);
                            if (linked.Count > 0) g1mFkToAllLinkedFks[refFk] = linked;
                        }
                        else
                        {
                            uint? ktidOid = ReadPropValue(data, pos, numProps, refOff, PropKtidLink);
                            if (ktidOid.HasValue) pendingG1mKtid.Add((refFk, ktidOid.Value));
                        }
                    }
                }
            }

            pos += Math.Max(8, recSize);
        }

        var g1mFkToKtidFk = new Dictionary<uint, uint>(pendingG1mKtid.Count);
        foreach (var (g1mFk, ktidOid) in pendingG1mKtid)
        {
            if (_ktidMap.ContainsKey(ktidOid))
                g1mFkToKtidFk[g1mFk] = ktidOid;
            else if (objIdToKtidFk.TryGetValue(ktidOid, out uint ktidFk))
                g1mFkToKtidFk[g1mFk] = ktidFk;
        }

        if (g1mFkToKtidFk.Count == 0 && g1mFkToGrpFk.Count == 0) return null;

        return new MasterContext(
            g1mFkToKtidFk, objIdToG1tFk,
            g1mFkToGrpFk, g1mFkToOidexFk, g1mFkToRigbinFk, g1mFkToAllLinkedFks,
            [], [], [], [], []);  // MPR/MatEd fields filled by BuildMasterContext
    }

    // ── Singleton DB parsers ──────────────────────────────────────────────────

    /// <summary>scndb (0x6D011726): costume name → cos_oid</summary>
    private Dictionary<string, uint> ParseScndb()
    {
        if (!_allAssetsByKtid.TryGetValue(ScndbFk, out var vm)) return [];
        byte[] data;
        try { data = _extractor.ExtractToMemory(vm.Record, vm.Container); }
        catch { return []; }

        if (data.Length < 0x1C || !data.AsSpan(0, 8).SequenceEqual(DokMagic)) return [];
        int hdrSize = (int)ReadU32(data, 8);

        var result = new Dictionary<string, uint>(2048, StringComparer.Ordinal);
        int pos = hdrSize;
        while (pos + 0x18 <= data.Length)
        {
            if (!data.AsSpan(pos, 8).SequenceEqual(IdokMagic))
            {
                int next = FindMagic(data, pos + 1, IdokMagic);
                if (next < 0) break;
                pos = next; continue;
            }
            int  recSize  = (int)ReadU32(data, pos + 8);
            uint typeHash = ReadU32(data, pos + 0x10);
            int  numProps = (int)ReadU32(data, pos + 0x14);

            if (typeHash == TypeHashScndbEntry && (uint)numProps <= 4096)
            {
                byte[]? namesRaw = null;
                uint[]? oidsArr  = null;
                int valPos = pos + 0x18 + numProps * 12;

                for (int i = 0; i < numProps; i++)
                {
                    int doff = pos + 0x18 + i * 12;
                    if (doff + 12 > data.Length) break;
                    uint pt  = ReadU32(data, doff);
                    uint pc  = ReadU32(data, doff + 4);
                    uint ph  = ReadU32(data, doff + 8);
                    int  sz  = pt < (uint)PropTypeSizes.Length ? PropTypeSizes[pt] : 0;
                    int  vb  = sz * (int)pc;

                    if (ph == PropScndbNames && pt <= 1 && vb > 0 && valPos + vb <= data.Length)
                    {
                        namesRaw = data[valPos..(valPos + vb)];
                    }
                    else if (ph == PropScndbOids && (pt == 4 || pt == 5) && pc > 0 && valPos + vb <= data.Length)
                    {
                        oidsArr = new uint[pc];
                        for (int j = 0; j < (int)pc; j++)
                            oidsArr[j] = ReadU32(data, valPos + j * 4);
                    }
                    valPos += vb;
                }

                if (namesRaw != null && oidsArr != null)
                {
                    int nameIdx = 0, start = 0;
                    for (int i = 0; i <= namesRaw.Length && nameIdx < oidsArr.Length; i++)
                    {
                        if (i == namesRaw.Length || namesRaw[i] == 0)
                        {
                            if (i > start)
                            {
                                string name = System.Text.Encoding.ASCII.GetString(namesRaw, start, i - start);
                                result.TryAdd(name, oidsArr[nameIdx]);
                                nameIdx++;
                            }
                            start = i + 1;
                        }
                    }
                }
            }
            pos += Math.Max(8, recSize);
        }

        AppLogger.Info($"[MasterDokCache] scndb: {result.Count} costume names");
        return result;
    }

    /// <summary>CE1Common (0x2082AD97): cos_oid → non-zero MI obj_id list</summary>
    private Dictionary<uint, List<uint>> ParseCe1Common()
    {
        if (!_allAssetsByKtid.TryGetValue(Ce1CommonFk, out var vm)) return [];
        byte[] data;
        try { data = _extractor.ExtractToMemory(vm.Record, vm.Container); }
        catch { return []; }

        if (data.Length < 0x1C || !data.AsSpan(0, 8).SequenceEqual(DokMagic)) return [];
        int hdrSize = (int)ReadU32(data, 8);

        var result = new Dictionary<uint, List<uint>>(2048);
        int pos = hdrSize;
        while (pos + 0x18 <= data.Length)
        {
            if (!data.AsSpan(pos, 8).SequenceEqual(IdokMagic))
            {
                int next = FindMagic(data, pos + 1, IdokMagic);
                if (next < 0) break;
                pos = next; continue;
            }
            int  recSize  = (int)ReadU32(data, pos + 8);
            uint oid      = ReadU32(data, pos + 0x0C);
            uint typeHash = ReadU32(data, pos + 0x10);
            int  numProps = (int)ReadU32(data, pos + 0x14);

            if (typeHash == TypeHashCharSetting && (uint)numProps <= 4096)
            {
                int valPos = pos + 0x18 + numProps * 12;
                for (int i = 0; i < numProps; i++)
                {
                    int doff = pos + 0x18 + i * 12;
                    if (doff + 12 > data.Length) break;
                    uint pt = ReadU32(data, doff);
                    uint pc = ReadU32(data, doff + 4);
                    uint ph = ReadU32(data, doff + 8);
                    int  sz = pt < (uint)PropTypeSizes.Length ? PropTypeSizes[pt] : 0;

                    if (ph == PropMiArray && (pt == 4 || pt == 5) && pc > 0)
                    {
                        var miList = new List<uint>((int)pc);
                        for (int j = 0; j < (int)pc; j++)
                        {
                            int vp = valPos + j * 4;
                            if (vp + 4 > data.Length) break;
                            uint mi = ReadU32(data, vp);
                            if (mi != 0) miList.Add(mi);
                        }
                        if (miList.Count > 0) result[oid] = miList;
                        break;
                    }
                    valPos += sz * (int)pc;
                }
            }
            pos += Math.Max(8, recSize);
        }

        AppLogger.Info($"[MasterDokCache] CE1Common: {result.Count} costume entries");
        return result;
    }

    /// <summary>
    /// MaterialEditor (0xD956E4A2): scan MaterialBindEntry + TextureBindTableCSV,
    /// build miObjId → MPR KTID FK and miObjId → KTS FK maps.
    /// Chain: MaterialBindEntry.prop[0xF92C5190] → TBC oid → TextureBindTableCSV.prop[0x7A1E1EF8] → KTID FK
    /// Also builds objIdToG1tFk via ParseMasterDok logic (no TypeHash filter) so that
    /// MPR KTID (slot, objId) pairs can be resolved to G1T FileKtids.
    /// </summary>
    private (Dictionary<uint, uint> MiToMprKtid, Dictionary<uint, uint> MiToKts,
             Dictionary<uint, uint> ObjIdToG1tFk) ParseMaterialEditor()
    {
        if (!_allAssetsByKtid.TryGetValue(MatEditorFk, out var vm))
            return ([], [], []);
        byte[] data;
        try { data = _extractor.ExtractToMemory(vm.Record, vm.Container); }
        catch { return ([], [], []); }

        if (data.Length < 0x1C || !data.AsSpan(0, 8).SequenceEqual(DokMagic))
            return ([], [], []);
        int hdrSize = (int)ReadU32(data, 8);

        // miOid → (ktsFk, tbcOid)
        var mbeMap = new Dictionary<uint, (uint KtsFk, uint TbcOid)>(16384);
        // tbcOid → mprKtidFk
        var tbcMap = new Dictionary<uint, uint>(16384);
        // DOK-local objId → G1T FileKtid (ParseMasterDok-style, no TypeHash filter)
        var objIdToG1tFk = new Dictionary<uint, uint>(65536);

        int pos = hdrSize;
        while (pos + 0x18 <= data.Length)
        {
            if (!data.AsSpan(pos, 8).SequenceEqual(IdokMagic))
            {
                int next = FindMagic(data, pos + 1, IdokMagic);
                if (next < 0) break;
                pos = next; continue;
            }
            int  recSize  = (int)ReadU32(data, pos + 8);
            uint oid      = ReadU32(data, pos + 0x0C);
            uint typeHash = ReadU32(data, pos + 0x10);
            int  numProps = (int)ReadU32(data, pos + 0x14);

            if ((uint)numProps <= 4096)
            {
                int refOff = pos + 0x18 + numProps * 12;

                // ParseMasterDok-style: check first value at refOff for G1T FileKtid
                if (refOff + 4 <= data.Length)
                {
                    uint refFk = ReadU32(data, refOff);
                    if (_g1tMap.ContainsKey(refFk))
                        objIdToG1tFk[oid] = refFk;
                }

                if (typeHash == TypeHashMatBind)
                {
                    uint? ktsFk  = ReadPropValue(data, pos, numProps, refOff, PropKtsFk);
                    uint? tbcOid = ReadPropValue(data, pos, numProps, refOff, PropTbcOid);
                    if (tbcOid is { } t && t != 0)
                        mbeMap[oid] = (ktsFk ?? 0, t);
                }
                else if (typeHash == TypeHashTexBind)
                {
                    uint? ktidFk = ReadPropValue(data, pos, numProps, refOff, PropMprKtidFk);
                    if (ktidFk is { } k && k != 0)
                        tbcMap[oid] = k;
                }
            }
            pos += Math.Max(8, recSize);
        }

        // Combine: miOid → mprKtidFk (via tbcOid)
        var miToMprKtid = new Dictionary<uint, uint>(mbeMap.Count);
        var miToKts     = new Dictionary<uint, uint>(mbeMap.Count);
        foreach (var (miOid, (ktsFk, tbcOid)) in mbeMap)
        {
            if (tbcMap.TryGetValue(tbcOid, out uint mprKtidFk))
                miToMprKtid[miOid] = mprKtidFk;
            if (ktsFk != 0)
                miToKts[miOid] = ktsFk;
        }

        AppLogger.Info(
            $"[MasterDokCache] MaterialEditor: {mbeMap.Count} MBE, {tbcMap.Count} TBC " +
            $"→ {miToMprKtid.Count} MPR KTID, {miToKts.Count} KTS, {objIdToG1tFk.Count} G1T mapped");
        return (miToMprKtid, miToKts, objIdToG1tFk);
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

    private List<uint> ReadAllLinkedFileKtids(
        byte[] data, int recordStart, int numProps, int refFkOffset, uint excludeFk)
    {
        var result = new List<uint>();
        var seen   = new HashSet<uint>();
        int valPos = refFkOffset;

        for (int i = 0; i < numProps; i++)
        {
            int doff = recordStart + 0x18 + i * 12;
            if (doff + 12 > data.Length) break;
            uint propType  = ReadU32(data, doff + 0);
            uint propCount = ReadU32(data, doff + 4);
            int  typeSize  = propType < (uint)PropTypeSizes.Length ? PropTypeSizes[propType] : 4;

            if ((propType == 4 || propType == 5) && typeSize == 4 && propCount > 0)
            {
                for (uint j = 0; j < propCount; j++)
                {
                    int vp = valPos + (int)(j * 4);
                    if (vp + 4 > data.Length) break;
                    uint val = ReadU32(data, vp);
                    if (val != 0 && val != excludeFk && seen.Add(val)
                        && _allAssetsByKtid.ContainsKey(val))
                        result.Add(val);
                }
            }

            valPos += typeSize * (int)propCount;
        }
        return result;
    }

    private static uint? ReadPropValue(
        byte[] data, int recordStart, int numProps, int refFkOffset, uint targetHash)
    {
        int valPos = refFkOffset;
        for (int i = 0; i < numProps; i++)
        {
            int doff = recordStart + 0x18 + i * 12;
            if (doff + 12 > data.Length) break;
            uint propType  = ReadU32(data, doff + 0);
            uint propCount = ReadU32(data, doff + 4);
            uint propHash  = ReadU32(data, doff + 8);
            int  typeSize  = propType < (uint)PropTypeSizes.Length ? PropTypeSizes[propType] : 4;
            int  valBytes  = (int)(propCount * typeSize);
            if (propHash == targetHash && propCount >= 1 && typeSize == 4 && valPos + 4 <= data.Length)
                return ReadU32(data, valPos);
            valPos += valBytes;
        }
        return null;
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
