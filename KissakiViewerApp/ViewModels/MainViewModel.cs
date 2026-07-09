using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KissakiViewer.Core;
using KissakiViewer.Core.Formats;
using KissakiViewer.Models;
using KissakiViewer.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KissakiViewer.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Observable ────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssets))]
    private ObservableCollection<AssetItemViewModel> _assets = [];

    [ObservableProperty]
    private ObservableCollection<AssetItemViewModel> _filteredAssets = [];

    [ObservableProperty]
    private AssetItemViewModel? _selectedAsset;

    [ObservableProperty]
    private string _statusText = "Loading assets...";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _loadProgress;

    /// <summary>
    /// True during first-time DOK scanning (cache miss or invalidation).
    /// The XAML overlay blocks UI interaction while this is active.
    /// </summary>
    [ObservableProperty]
    private bool _isInitializing;

    [ObservableProperty]
    private ObservableCollection<string> _typeFilters = ["All"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderTree))]
    private ObservableCollection<FolderNode> _folderTree = [];

    [ObservableProperty]
    private FolderNode? _selectedFolderNode;

    [ObservableProperty]
    private bool _isGridView;

    /// <summary>Live log lines streamed from AppLogger — bound to the console panel.</summary>
    public ObservableCollection<string> ConsoleLog { get; } = [];

    public bool HasAssets     => Assets.Count > 0;
    public bool HasFolderTree => FolderTree.Count > 0;
    public string WindowTitle { get; }

    // ── Internal state exposed for AssetViewerWindow ─────────────────────────

    internal FdataExtractor? Extractor => _extractor;

    internal IReadOnlyDictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>> AllG1tByFid =>
        _allG1tByFid;

    internal IReadOnlyDictionary<uint, AssetItemViewModel> AllAssetsByKtid =>
        _allAssetsByKtid;

    internal MasterDokCache? MasterDokCache => _masterDokCache;

    // ── Private fields ────────────────────────────────────────────────────────

    private readonly GameProfile _profile;
    private RdbReader?    _rdb;
    private string        _primaryRdbPath = string.Empty;
    private FdataExtractor? _extractor;
    private Dictionary<uint, string> _nameMap = [];

    private Dictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>> _allG1tByFid = [];
    private Dictionary<uint, AssetItemViewModel>         _allAssetsByKtid = [];
    private MasterDokCache?                              _masterDokCache;
    private IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>? _g1mToG1tMap;

    // Companion maps: populated from cache or from MasterDokCache DM chain after warmup.
    // Null when the game has no DM-record-containing DOK (no companion info available).
    private IReadOnlyDictionary<uint, uint>? _cachedG1mToGrpFk;
    private IReadOnlyDictionary<uint, uint>? _cachedG1mToOidexFk;
    private IReadOnlyDictionary<uint, uint>? _cachedG1mToRigbinFk;

    // RDB infos used for cache validation (actual files loaded, may be Kashira backups).
    private List<Core.GameLoadCache.RdbInfo> _loadedRdbInfos = [];

    private CancellationTokenSource? _filterCts;

    // ── Construction ─────────────────────────────────────────────────────────

    public MainViewModel(GameProfile profile)
    {
        _profile    = profile;
        WindowTitle = $"Kissaki v{AppSettingsService.AppVersion} — {profile.Name} ({profile.GameDirectory})";

        AppLogger.LogAdded += line =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => ConsoleLog.Add(line)));
    }

    // ── Property change reactions ─────────────────────────────────────────────

    partial void OnFilterTextChanged(string value)              => ScheduleFilter();
    partial void OnSelectedTypeFilterChanged(string value)      => ScheduleFilter();
    partial void OnSelectedFolderNodeChanged(FolderNode? value) => ScheduleFilter();

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fast phase: parse RDB and build the asset list. Completes in seconds even for large games.
    /// The launcher awaits this before closing. Slow work (names, folder tree, G1M map) runs
    /// in <see cref="LoadBackgroundAsync"/> after the browser window is visible.
    /// </summary>
    public async Task LoadAsync()
    {
        string fdataDir = _profile.FdataDir;
        var rdbFiles = Directory.Exists(fdataDir)
            ? Directory.GetFiles(fdataDir, "*.rdb")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        if (rdbFiles.Length == 0)
        {
            StatusText = $"No RDB files found in: {fdataDir}";
            return;
        }

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = rdbFiles.Length == 1
            ? $"Parsing {Path.GetFileName(rdbFiles[0])}..."
            : $"Parsing RDB files... ({rdbFiles.Length} files)";

        ObservableCollection<AssetItemViewModel>?                          loadedAssets = null;
        Dictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>>?   g1tByFid     = null;
        Dictionary<uint, AssetItemViewModel>?                              byKtid       = null;
        string? err = null;

        try
        {
            var r = await Task.Run(() =>
            {
                var extractor = new FdataExtractor(fdataDir);
                _extractor = extractor;
                var list     = new List<AssetItemViewModel>();
                var rdbInfos = new List<Core.GameLoadCache.RdbInfo>();

                // Check for Kashira mod-manager backup directory.
                // If a backup copy of an RDB exists there, load the backup (original, unmodded)
                // for the asset index while fdata files remain at their original location.
                string kashiraBackup = Path.Combine(_profile.GameDirectory, "_Kashira", "backup");

                foreach (var rdbFile in rdbFiles)
                {
                    string rdbName  = Path.GetFileName(rdbFile);
                    string rdxName  = Path.ChangeExtension(rdbName, ".rdx");

                    string backupRdb = Path.Combine(kashiraBackup, rdbName);
                    string backupRdx = Path.Combine(kashiraBackup, rdxName);

                    string actualRdb = File.Exists(backupRdb) ? backupRdb : rdbFile;
                    string actualRdx = File.Exists(backupRdx)
                        ? backupRdx
                        : Path.ChangeExtension(rdbFile, ".rdx");

                    if (actualRdb != rdbFile)
                        AppLogger.Info($"[RDB] Kashira backup detected, using backup of {rdbName}");

                    // Record the file that was actually used for cache invalidation.
                    long modTicks = File.GetLastWriteTimeUtc(actualRdb).Ticks;
                    rdbInfos.Add(new Core.GameLoadCache.RdbInfo(actualRdb, modTicks));

                    var rdxFile = actualRdx;
                    var reader  = new RdbReader(actualRdb, rdxFile);

                    if (!reader.Load())
                    {
                        AppLogger.Warn($"[RDB] Failed to load: {rdbName}");
                        continue;
                    }

                    if (_rdb == null)
                    {
                        _rdb = reader;
                        _primaryRdbPath = actualRdb;
                    }

                    foreach (var rec in reader.Entries)
                    {
                        string container = reader.ResolveFdata(rec.FdataId);
                        list.Add(new AssetItemViewModel(rec, container, rdbName));
                    }

                    AppLogger.Info($"[RDB] {rdbName}: {reader.Entries.Count:N0} entries");
                }

                if (_rdb == null)
                    throw new InvalidDataException("No RDB files loaded");

                // G1T index keyed by (rdbName, fdataId) so proximity search stays within one RDB.
                var fid = list
                    .Where(a => a.TypeExt == ".g1t")
                    .GroupBy(a => (a.RdbName, a.Record.FdataId))
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Record.FdataOffset).ToList());

                var ktid = new Dictionary<uint, AssetItemViewModel>(list.Count);
                foreach (var a in list) ktid.TryAdd(a.Record.FileKtid, a);

                var masterDok = new MasterDokCache(list, extractor);

                var assets = new ObservableCollection<AssetItemViewModel>(list);

                return (Assets: assets, G1tByFid: fid, ByKtid: ktid, RdbInfos: rdbInfos,
                        MasterDok: masterDok);
            });
            loadedAssets    = r.Assets;
            g1tByFid        = r.G1tByFid;
            byKtid          = r.ByKtid;
            _loadedRdbInfos = r.RdbInfos;
            _masterDokCache = r.MasterDok;
        }
        catch (Exception ex) { err = ex.Message; }

        if (err != null) { StatusText = $"Error: {err}"; IsLoading = false; return; }

        Assets           = loadedAssets!;
        _allG1tByFid     = g1tByFid!;
        _allAssetsByKtid = byKtid!;

        BuildTypeFilters();
        AppLogger.Info($"Assets loaded: {Assets.Count:N0}");

        FilteredAssets = Assets;
        StatusText   = $"{Assets.Count:N0} assets";
        IsLoading    = false;
        LoadProgress = 100;
    }

    /// <summary>
    /// Slow background work called from <c>MainWindow.Window_Loaded</c> after the browser is visible.
    /// On first run (cache miss) shows the initialization overlay and builds the full DOK mapping,
    /// then saves to cache. On subsequent runs loads instantly from cache.
    /// </summary>
    public async Task LoadBackgroundAsync()
    {
        AppLogger.Info($"[LoadBG] Starting (extractor={_extractor != null})");
        if (_extractor == null) return;

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = "Loading name dictionary...";

        await RunNameRecoveryAsync();
        LoadProgress = 20;

        AppLogger.Info("[LoadBG] Building folder tree...");
        await BuildFolderTreeAsync();
        LoadProgress = 40;
        AppLogger.Info("[LoadBG] Folder tree built. Checking cache...");

        string cacheFile = Core.GameLoadCache.GetCacheFilePath(_profile.GameDirectory);
        var    cached    = Core.GameLoadCache.TryLoad(cacheFile, _loadedRdbInfos);

        if (cached != null)
        {
            // ── Fast path: restore mappings from cache ────────────────────────
            StatusText = "Loading G1M→G1T map from cache...";

            var combinedG1tMap = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();
            foreach (var (g1mFk, slots) in cached.G1mToG1tSlots)
            {
                int maxSlot = slots.Length > 0 ? slots.Max(s => s.Slot) : -1;
                if (maxSlot < 0) continue;
                var arr = new AssetItemViewModel?[maxSlot + 1];
                int hits = 0;
                foreach (var (slot, g1tFk) in slots)
                    if (g1tFk != 0 && _allAssetsByKtid.TryGetValue(g1tFk, out var g1tVm))
                    { arr[slot] = g1tVm; hits++; }
                if (hits > 0) combinedG1tMap[g1mFk] = arr.ToList().AsReadOnly();
            }
            _g1mToG1tMap          = combinedG1tMap;
            _cachedG1mToGrpFk     = cached.G1mToGrp.Count    > 0 ? cached.G1mToGrp    : null;
            _cachedG1mToOidexFk   = cached.G1mToOidex.Count  > 0 ? cached.G1mToOidex  : null;
            _cachedG1mToRigbinFk  = cached.G1mToRigbin.Count > 0 ? cached.G1mToRigbin : null;

            // Preload into MasterDokCache so AssetViewerViewModel also benefits.
            _masterDokCache?.PreloadG1tResults(_g1mToG1tMap);

            AppLogger.Info(
                $"[Cache] Restored: {combinedG1tMap.Count} G1M→G1T, " +
                $"{cached.G1mToGrp.Count} GRP, {cached.G1mToOidex.Count} OIDEX");
        }
        else
        {
            // ── Slow path: full DOK scan — show overlay to block UI ───────────
            IsInitializing = true;
            try
            {
                StatusText = "Building G1M→G1T map... (first load — subsequent starts use cache)";

                var snapshot = Assets.ToList();
                var ext      = _extractor;

                // Run MasterDokCache (CE singleton / DM DOK search) and KidsObjDbResolver
                // (full DOK scan, all games) in parallel.
                var masterWarmUp = _masterDokCache?.WarmUpAsync() ?? Task.CompletedTask;
                var resolverTask = KidsObjDbResolver.BuildAsync(
                    snapshot, ext,
                    progress: new Progress<(int done, int total)>(p =>
                        LoadProgress = 40 + (int)(p.done / (double)Math.Max(p.total, 1) * 55)));

                await Task.WhenAll(masterWarmUp, resolverTask);

                // Start with KidsObjDbResolver results (covers all games)
                var combinedMap = new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>(
                    resolverTask.Result);

                // Merge / override with MasterDokCache DM-chain results (higher quality when available)
                if (_masterDokCache != null)
                {
                    var dmMap = await _masterDokCache.GetAllG1tMappingsAsync();
                    if (dmMap != null)
                        foreach (var (k, v) in dmMap) combinedMap[k] = v;
                }

                _g1mToG1tMap = combinedMap;

                // Extract GRP/OIDEX maps for bundle export
                if (_masterDokCache != null)
                {
                    LoadProgress = 96;
                    StatusText   = "Building GRP/OIDEX maps...";
                    var (grpMap, oidexMap, rigbinMap) = await _masterDokCache.GetCompanionMapsAsync();
                    _cachedG1mToGrpFk    = grpMap;
                    _cachedG1mToOidexFk  = oidexMap;
                    _cachedG1mToRigbinFk = rigbinMap;
                }

                // Preload into MasterDokCache for AssetViewerViewModel
                _masterDokCache?.PreloadG1tResults(_g1mToG1tMap);

                // Save to cache
                LoadProgress = 97;
                StatusText   = "Saving cache...";
                try
                {
                    var slotsForCache = combinedMap.ToDictionary(
                        kv => kv.Key,
                        kv => (IReadOnlyList<(int Slot, uint G1tFk)>)kv.Value
                            .Select((vm, slot) => (slot, vm?.Record.FileKtid ?? 0u))
                            .Where(x => x.Item2 != 0)
                            .ToList());

                    Core.GameLoadCache.Save(
                        cacheFile, _loadedRdbInfos,
                        slotsForCache,
                        _cachedG1mToGrpFk    ?? new Dictionary<uint, uint>(),
                        _cachedG1mToOidexFk  ?? new Dictionary<uint, uint>(),
                        _cachedG1mToRigbinFk ?? new Dictionary<uint, uint>());

                    AppLogger.Info($"[Cache] Saved: {combinedMap.Count} G1M→G1T entries");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Cache] Save failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[LoadBG] Slow path failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                IsInitializing = false;
            }
        }

        StatusText   = $"Ready: {Assets.Count:N0} assets, {_nameMap.Count:N0} names recovered";
        IsLoading    = false;
        LoadProgress = 100;
    }

    // ── Name recovery ─────────────────────────────────────────────────────────

    private async Task RunNameRecoveryAsync()
    {
        if (_extractor == null) return;

        AppLogger.Info("[NameRecovery] Getting AppId...");
        string appId   = AppIdService.GetAppId(_profile.GameDirectory);
        AppLogger.Info($"[NameRecovery] AppId={appId}, checking CSV...");
        string csvPath = NameDictionaryService.GetCsvPath(appId);

        Dictionary<uint, string> names;

        if (File.Exists(csvPath))
        {
            names = await Task.Run(() => NameDictionaryService.Load(csvPath));
            AppLogger.Info($"[NameDictionary] CSV loaded (AppID={appId}): {names.Count} entries");
        }
        else if (appId == "4144680") // DOA6LR — auto-recover from kidsscndb chain
        {
            AppLogger.Info("[Doa6Name] No CSV found — running DOA6LR name recovery...");
            StatusText = "Recovering names (DOA6LR)...";
            names = await Doa6NameRecovery.BuildAsync(_allAssetsByKtid, _extractor!);
            if (names.Count > 0)
                await Task.Run(() => NameDictionaryService.Save(csvPath, names));
        }
        else
        {
            names = [];
            AppLogger.Info($"[NameDictionary] No CSV found (AppID={appId})");
        }

        _nameMap = names;

        // Apply names off the UI thread. PropertyChanged from a background thread is safe in WPF —
        // the binding engine marshals updates to the dispatcher automatically.
        var snapshot = Assets.ToList();
        await Task.Run(() =>
        {
            foreach (var a in snapshot)
                if (_nameMap.TryGetValue(a.Record.FileKtid, out string? n))
                    a.RecoveredName = n;
        });

        int matched = snapshot.Count(a => a.RecoveredName != null);
        AppLogger.Info($"[NameDictionary] {names.Count} loaded, {matched} applied");
    }

    // ── G1M companion file discovery ─────────────────────────────────────────

    // TypeKtid values that are bundled alongside a G1M in the same fdata block group.
    private static readonly HashSet<uint> s_g1mCompanionTypeKtids =
    [
        0x8e39aa37u, 0xBE144B78u, // .ktid
        0xb340861au, 0x5153729bu, // .mtl
        0x56efe45cu, 0xbbf9b49du, // .grp
        0x133d2c3bu,              // unknown adjacent type observed in DOA6 bundles
    ];

    private static readonly HashSet<uint> s_g1mTypeKtids = [0x563bdef1u, 0xBEF563DDu];

    // G1M_fk - OidDelta == OID_fk  (verified 100% across DOA6LR + FF2, fk arithmetic)
    private const uint OidDelta = 0x0E05C687u;

    /// <summary>
    /// Finds companion files (KTID, MTL, GRP, …) for a G1M by scanning the same fdata container.
    /// Collects all companion-typed assets whose fdata offset falls between this G1M and the next
    /// G1M — skipping over any non-companion intermediary types without stopping.
    /// DOA6 fdata layout: [G1M] → [KTID] → [MTL] → … → [next G1M]
    /// </summary>
    private List<AssetItemViewModel> ResolveCompanionFiles(AssetItemViewModel vm)
    {
        var inSameFdata = _allAssetsByKtid.Values
            .Where(a => a.Container == vm.Container)
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();

        int g1mIdx = inSameFdata.FindIndex(a => a.Record.FileKtid == vm.Record.FileKtid);
        if (g1mIdx < 0) return [];

        ulong g1mOffset = vm.Record.FdataOffset;

        // Upper bound: the next G1M's offset (exclusive).
        // Prevents picking up companions that belong to a later bundle.
        ulong upperBound = ulong.MaxValue;
        for (int i = g1mIdx + 1; i < inSameFdata.Count; i++)
        {
            if (s_g1mTypeKtids.Contains(inSameFdata[i].Record.TypeKtid))
            {
                upperBound = inSameFdata[i].Record.FdataOffset;
                break;
            }
        }

        // Collect every companion-typed asset between this G1M and the next G1M.
        // Non-companion intermediary files (g1co, rigbin, xf1g …) are simply skipped.
        return inSameFdata
            .Where(a => s_g1mCompanionTypeKtids.Contains(a.Record.TypeKtid)
                     && a.Record.FdataOffset >  g1mOffset
                     && a.Record.FdataOffset <  upperBound)
            .ToList();
    }

    /// <summary>
    /// Resolves the .oid bone-binding file for a G1M using two strategies:
    /// 1. Direct arithmetic:  oid_fk = g1m_fk - OidDelta  (base models, 100% hit rate)
    /// 2. Name-based fallback: for variation G1Ms that share a base model's skeleton
    ///    (e.g. ARD_COS_001.g1m → ARD_COS_000.oid), by decrementing the trailing _NNN suffix.
    /// Returns null when no OID can be resolved (face/hair meshes driven by a parent skeleton).
    /// </summary>
    private AssetItemViewModel? ResolveOidForG1m(AssetItemViewModel vm)
    {
        // Primary: arithmetic lookup
        uint oidFk = unchecked(vm.Record.FileKtid - OidDelta);
        if (_allAssetsByKtid.TryGetValue(oidFk, out var oidVm))
        {
            // Name verification ��� only when both names are recovered; skip silently otherwise.
            if (vm.RecoveredName is { } g1mName && oidVm.RecoveredName is { } oidName)
            {
                string g1mBase = Path.GetFileNameWithoutExtension(g1mName);
                string oidBase = Path.GetFileNameWithoutExtension(oidName);
                if (!string.Equals(g1mBase, oidBase, StringComparison.OrdinalIgnoreCase))
                    AppLogger.Warn($"[Bundle] OID name mismatch: G1M={g1mName} OID={oidName} (arithmetic 0x{oidFk:x8})");
            }
            return oidVm;
        }

        // Fallback: variation G1M — try base model (e.g. ARD_COS_001 → ARD_COS_000)
        string? recovered = vm.RecoveredName;
        if (recovered is null) return null;

        string baseName = Path.GetFileNameWithoutExtension(recovered);
        var m = Regex.Match(baseName, @"^(.+?)_(\d+)$");
        if (!m.Success) return null;

        string prefix   = m.Groups[1].Value;
        string numStr   = m.Groups[2].Value;
        int    number   = int.Parse(numStr);
        int    padWidth = numStr.Length;

        for (int n = number - 1; n >= 0; n--)
        {
            string candidateOidName = $"{prefix}_{n.ToString().PadLeft(padWidth, '0')}.oid";
            var candidate = _allAssetsByKtid.Values.FirstOrDefault(a =>
                a.TypeExt == ".oid" &&
                a.RecoveredName != null &&
                string.Equals(a.RecoveredName, candidateOidName, StringComparison.OrdinalIgnoreCase));
            if (candidate != null)
            {
                AppLogger.Info($"[Bundle] OID fallback: {recovered} → {candidateOidName}");
                return candidate;
            }
        }

        return null;
    }

    private async Task<List<AssetItemViewModel>> ResolveG1tFilesForModelAsync(AssetItemViewModel vm)
    {
        uint   ktid    = vm.Record.FileKtid;
        string cont    = vm.Container;
        ushort fid     = vm.Record.FdataId;
        string rdbName = vm.RdbName;

        // Priority 1: MasterDokCache — CE singleton DM-chain (DOA6 only; returns null for other games)
        if (_masterDokCache != null)
        {
            var mapped = await _masterDokCache.GetG1tFilesAsync(ktid);
            if (mapped is { Count: > 0 }) return mapped.ToList();
        }

        // Priority 1.5: KidsObjDbResolver — full DOK scan, all games
        if (_g1mToG1tMap != null && _g1mToG1tMap.TryGetValue(ktid, out var resolved) && resolved.Count > 0)
            return resolved.ToList();

        // Priority 2: co-located G1T in the same fdata container
        var colocated = _allAssetsByKtid.Values
            .Where(a => a.Container == cont && a.TypeExt == ".g1t")
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();
        if (colocated.Count > 0) return colocated;

        // Priority 3: nearest fdata ID within the same RDB
        if (_allG1tByFid.Count == 0) return [];
        var nearestKey   = ((string Rdb, ushort Fid))default;
        int nearestDelta = int.MaxValue;
        foreach (var key in _allG1tByFid.Keys)
        {
            if (key.Rdb != rdbName) continue;
            int d = Math.Abs((int)key.Fid - (int)fid);
            if (d < nearestDelta) { nearestDelta = d; nearestKey = key; }
        }
        return nearestDelta < int.MaxValue && _allG1tByFid.TryGetValue(nearestKey, out var list)
            ? list : [];
    }

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the export directory for a given asset.
    /// Single:  export/&lt;GameExe&gt;/&lt;rdbName&gt;/
    /// Bundle:  export/&lt;GameExe&gt;/&lt;rdbName&gt;/0x{ktid}/
    /// </summary>
    private static string ExportFileName(AssetItemViewModel vm)
    {
        if (!AppSettingsService.Current.UseRestoredName || vm.RecoveredName is null)
            return $"0x{vm.Record.FileKtid:x8}{vm.TypeExt}";
        string dispName = vm.DisplayName;
        string baseName = string.Equals(Path.GetExtension(dispName), vm.TypeExt, StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(dispName)
            : dispName;
        return baseName + vm.TypeExt;
    }

    private string GetExportDir(AssetItemViewModel vm, bool bundle)
    {
        string gameExe = Path.GetFileNameWithoutExtension(_profile.ExeName);
        string rdbBase = Path.GetFileNameWithoutExtension(vm.RdbName);
        string baseDir = Path.Combine(AppSettingsService.GetEffectiveExportDirectory(), gameExe, rdbBase);
        string subFolder = AppSettingsService.Current.UseRestoredName && vm.RecoveredName != null
            ? vm.DisplayName
            : $"0x{vm.Record.FileKtid:x8}";
        return bundle ? Path.Combine(baseDir, subFolder) : baseDir;
    }

    /// <summary>Single-file raw export: exports only the selected asset (any type).</summary>
    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        if (SelectedAsset is null || _extractor is null) return;
        var vm = SelectedAsset;

        string exportDir = GetExportDir(vm, bundle: false);
        string name      = ExportFileName(vm);

        StatusText = $"Exporting... {name}";
        var extractor = _extractor;
        string? err = null;

        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(exportDir);
                string outPath = Path.Combine(exportDir, name);

                // Use streaming extraction for large raw assets (e.g. 1 GB SRST) to avoid OOM.
                if ((long)vm.Record.FileSize > FdataExtractor.StreamingThreshold)
                {
                    long bytes = extractor.ExtractToFile(vm.Record, vm.Container, outPath);
                    if (bytes < 0) err = "Streaming extraction failed";
                }
                else
                {
                    byte[] raw = extractor.ExtractToMemory(vm.Record, vm.Container);
                    if (raw.Length == 0) { err = "Empty data (extraction failed)"; return; }
                    File.WriteAllBytes(outPath, raw);
                    AppLogger.Info($"[Export] {name} ({raw.Length:N0} B)");
                }
            }
            catch (Exception ex) { err = ex.Message; AppLogger.Error($"[Export] {ex.Message}"); }
        });

        StatusText = err is null
            ? $"Saved → {Path.Combine(exportDir, name)}"
            : $"Export failed: {err}";
    }

    /// <summary>
    /// Bundle export for G1M assets: exports the G1M + companion files (KTID, MTL, GRP …)
    /// + all linked G1T files into a subfolder named by the G1M hash.
    /// </summary>
    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        if (SelectedAsset is null || _extractor is null || !SelectedAsset.IsG1m) return;
        var vm = SelectedAsset;

        string exportDir = GetExportDir(vm, bundle: true);
        StatusText = $"Exporting bundle... 0x{vm.Record.FileKtid:x8}";

        var extractor = _extractor;
        int count  = 0;
        string? err = null;

        // Resolve G1T (always) + companion files (only when companion maps are available)
        var g1tFiles = await ResolveG1tFilesForModelAsync(vm);
        List<AssetItemViewModel> companions = [];
        AssetItemViewModel? grpVm = null, oidexVm = null, rigbinVm = null;
        if (_cachedG1mToGrpFk != null)
        {
            companions = ResolveCompanionFiles(vm);  // KTID, MTL, GRP (fdata window)
            uint fk = vm.Record.FileKtid;
            if (_cachedG1mToGrpFk.TryGetValue(fk, out uint grpFk))
                _allAssetsByKtid.TryGetValue(grpFk, out grpVm);
            if (_cachedG1mToOidexFk != null && _cachedG1mToOidexFk.TryGetValue(fk, out uint oidexFk))
                _allAssetsByKtid.TryGetValue(oidexFk, out oidexVm);
            if (_cachedG1mToRigbinFk != null && _cachedG1mToRigbinFk.TryGetValue(fk, out uint rigbinFk))
                _allAssetsByKtid.TryGetValue(rigbinFk, out rigbinVm);
        }

        // OID: arithmetic (base models) + name-based fallback (variation G1Ms)
        var oidVm = ResolveOidForG1m(vm);

        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(exportDir);

                var toExport = new List<AssetItemViewModel> { vm };
                toExport.AddRange(companions);
                if (grpVm    != null) toExport.Add(grpVm);
                if (oidexVm  != null) toExport.Add(oidexVm);
                if (rigbinVm != null) toExport.Add(rigbinVm);
                if (oidVm    != null) toExport.Add(oidVm);
                toExport.AddRange(g1tFiles);

                foreach (var asset in toExport.DistinctBy(a => a.Record.FileKtid))
                {
                    string name = ExportFileName(asset);
                    byte[] raw  = extractor.ExtractToMemory(asset.Record, asset.Container);
                    File.WriteAllBytes(Path.Combine(exportDir, name), raw);
                    AppLogger.Info($"[Bundle] {name} ({raw.Length:N0} B)");
                    count++;
                }
            }
            catch (Exception ex) { err = ex.Message; AppLogger.Error($"[Bundle] {ex.Message}"); }
        });

        StatusText = err is null
            ? $"Bundle saved → {exportDir}  ({count} files)"
            : $"Bundle export failed: {err}";
    }

    // ── Folder tree ───────────────────────────────────────────────────────────

    private static string GetEffectiveFolderPath(AssetItemViewModel asset)
    {
        string prefix = asset.RdbName + "/";

        if (asset.RecoveredName is { } rn)
        {
            string normalized = rn.Replace('\\', '/');
            int sep = normalized.LastIndexOf('/');
            string sub = sep > 0 ? normalized[..sep] : string.Empty;
            return string.IsNullOrEmpty(sub) ? prefix + "Content" : prefix + "Content/" + sub;
        }

        return prefix + "Content (Unrecovered)";
    }

    private async Task BuildFolderTreeAsync()
    {
        AppLogger.Info($"[FolderTree] 0: entering, Assets.Count={Assets.Count}");
        var assets = Assets.ToList();
        AppLogger.Info($"[FolderTree] 1: ToList done ({assets.Count}), queuing Task.Run");

        var r = await Task.Run(() =>
        {
            AppLogger.Info("[FolderTree] 2: Task.Run started");
            var nodeMap  = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            var rootList = new List<FolderNode>();

            foreach (var asset in assets)
            {
                string folderPath = GetEffectiveFolderPath(asset);
                if (string.IsNullOrEmpty(folderPath)) continue;
                EnsurePath(folderPath, nodeMap, rootList);
            }

            int unkCount = assets.Count(a => string.IsNullOrEmpty(GetEffectiveFolderPath(a)));
            AppLogger.Info($"[FolderTree] 3: loop done — {rootList.Count} roots, {nodeMap.Count} nodes, {unkCount} unknown");

            rootList.Sort((a, b) =>
            {
                if (a.IsUnknown != b.IsUnknown) return a.IsUnknown ? 1 : -1;
                bool aM = a.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
                bool bM = b.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
                if (aM != bM) return aM ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var root in rootList) SortChildrenRecursive(root);

            AppLogger.Info("[FolderTree] 4: Task.Run done");
            return (Roots: rootList, UnknownCount: unkCount);
        });

        AppLogger.Info($"[FolderTree] 5: continuation on UI thread, unknownCount={r.UnknownCount}");
        if (r.UnknownCount > 0)
        {
            var unknown = new FolderNode("Unknown", string.Empty, isUnknown: true) { AssetCount = r.UnknownCount };
            r.Roots.Add(unknown);
        }

        FolderTree = new ObservableCollection<FolderNode>(r.Roots);
        AppLogger.Info($"[FolderTree] 6: FolderTree set ({r.Roots.Count} roots)");
        AppLogger.Info("[FolderTree] 7: done");
    }

    private static FolderNode EnsurePath(string path,
        Dictionary<string, FolderNode> nodeMap, List<FolderNode> roots)
    {
        if (nodeMap.TryGetValue(path, out var existing)) return existing;

        string normalized = path.Replace('\\', '/');
        int sep = normalized.LastIndexOf('/');

        string name = sep >= 0 ? normalized[(sep + 1)..] : normalized;
        var node = new FolderNode(name, normalized);
        nodeMap[normalized] = node;

        if (sep > 0)
        {
            var parent = EnsurePath(normalized[..sep], nodeMap, roots);
            parent.Children.Add(node);
        }
        else
        {
            roots.Add(node);
        }

        return node;
    }

    private static void SortChildrenRecursive(FolderNode node)
    {
        var sorted = node.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        foreach (var child in sorted) node.Children.Add(child);
        foreach (var child in node.Children) SortChildrenRecursive(child);
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    private void ScheduleFilter()
    {
        _filterCts?.Cancel();
        _filterCts = new CancellationTokenSource();
        _ = ApplyFilterAsync(_filterCts.Token);
    }

    private async Task ApplyFilterAsync(CancellationToken ct = default)
    {
        string text   = FilterText.Trim().ToLowerInvariant();
        string type   = SelectedTypeFilter;
        var    folder = SelectedFolderNode;
        var    assets = Assets;          // stable reference — not modified after LoadAsync

        // Fast path: no filter active — reuse Assets directly, no new OC or List allocation.
        // This avoids the ~1.4 MB LOH allocation (List<> + OC<> backing array) that would
        // otherwise trigger a Gen2 GC pause and block the WPF dispatcher.
        if (string.IsNullOrEmpty(text) && type == "All" && folder == null)
        {
            if (!ct.IsCancellationRequested)
            {
                FilteredAssets = assets;
                StatusText = $"{assets.Count:N0} assets";
            }
            return;
        }

        List<AssetItemViewModel> filtered;
        try
        {
            filtered = await Task.Run(() =>
            {
                IEnumerable<AssetItemViewModel> source = folder switch
                {
                    null               => (IEnumerable<AssetItemViewModel>)assets,
                    { IsUnknown: true } => assets.Where(a =>
                        string.IsNullOrEmpty(GetEffectiveFolderPath(a))),
                    FolderNode fn      => assets.Where(a =>
                    {
                        string fp = GetEffectiveFolderPath(a);
                        return fp.Equals(fn.FullPath, StringComparison.OrdinalIgnoreCase) ||
                               fp.StartsWith(fn.FullPath + "/", StringComparison.OrdinalIgnoreCase);
                    }),
                };

                return source.Where(a =>
                {
                    bool typeOk = type == "All" || a.TypeExt == type;
                    bool textOk = string.IsNullOrEmpty(text) ||
                                  a.KtidHex.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                  a.TypeExt.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                  (a.RecoveredName != null &&
                                   a.RecoveredName.Contains(text, StringComparison.OrdinalIgnoreCase));
                    return typeOk && textOk;
                }).ToList();
            }, ct);
        }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        FilteredAssets = new ObservableCollection<AssetItemViewModel>(filtered);
        int total = Assets.Count;
        StatusText = filtered.Count == total
            ? $"{total:N0} assets"
            : $"{filtered.Count:N0} / {total:N0} assets (filtered)";
    }

    private void BuildTypeFilters()
    {
        var types = Assets.Select(a => a.TypeExt).Distinct().OrderBy(x => x).ToList();
        TypeFilters        = new ObservableCollection<string>(new[] { "All" }.Concat(types));
        SelectedTypeFilter = "All";
    }

    // ── BitmapSource helper (for future use if needed) ────────────────────────

    internal static BitmapSource ImageSharpToBitmapSource(Image<Rgba32> img)
    {
        int    w    = img.Width, h = img.Height;
        byte[] bgra = new byte[w * h * 4];

        img.ProcessPixelRows(accessor =>
        {
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++, i++)
                {
                    bgra[i * 4 + 0] = row[x].B;
                    bgra[i * 4 + 1] = row[x].G;
                    bgra[i * 4 + 2] = row[x].R;
                    bgra[i * 4 + 3] = row[x].A;
                }
            }
        });

        return BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
    }
}
