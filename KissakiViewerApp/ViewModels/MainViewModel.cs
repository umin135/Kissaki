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
    private string _statusText = "에셋 로딩 중...";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _selectedTypeFilter = "All";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private ObservableCollection<string> _typeFilters = ["All"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderTree))]
    private ObservableCollection<FolderNode> _folderTree = [];

    [ObservableProperty]
    private FolderNode? _selectedFolderNode;

    [ObservableProperty]
    private bool _isGridView;

    [ObservableProperty]
    private bool _restoreAssetName = true;

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

    internal IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> G1mToG1tMap =>
        _g1mToG1tMap;

    // ── Private fields ────────────────────────────────────────────────────────

    private readonly GameProfile _profile;
    private RdbReader?    _rdb;
    private string        _primaryRdbPath = string.Empty;
    private FdataExtractor? _extractor;
    private Dictionary<uint, string> _nameMap = [];

    private Dictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>> _allG1tByFid = [];
    private Dictionary<uint, AssetItemViewModel>         _allAssetsByKtid = [];
    private IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> _g1mToG1tMap =
        new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();

    private CancellationTokenSource? _filterCts;

    // ── Construction ─────────────────────────────────────────────────────────

    public MainViewModel(GameProfile profile)
    {
        _profile    = profile;
        WindowTitle = $"Kissaki — {profile.Name} ({profile.GameDirectory})";

        AppLogger.LogAdded += line =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                (Action)(() => ConsoleLog.Add(line)));
    }

    // ── Property change reactions ─────────────────────────────────────────────

    partial void OnFilterTextChanged(string value)              => ScheduleFilter();
    partial void OnSelectedTypeFilterChanged(string value)      => ScheduleFilter();
    partial void OnSelectedFolderNodeChanged(FolderNode? value) => ScheduleFilter();
    partial void OnRestoreAssetNameChanged(bool value)
    {
        SelectedFolderNode = null;
        _ = BuildFolderTreeAsync();
    }

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
            StatusText = $"RDB 파일 없음: {fdataDir}";
            return;
        }

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = rdbFiles.Length == 1
            ? $"{Path.GetFileName(rdbFiles[0])} 파싱 중..."
            : $"RDB 파싱 중... ({rdbFiles.Length}개 파일)";

        List<AssetItemViewModel>?                                          batch    = null;
        Dictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>>?   g1tByFid = null;
        Dictionary<uint, AssetItemViewModel>?                              byKtid   = null;
        string? err = null;

        try
        {
            var r = await Task.Run(() =>
            {
                _extractor = new FdataExtractor(fdataDir);
                var list   = new List<AssetItemViewModel>();

                foreach (var rdbFile in rdbFiles)
                {
                    string rdxFile = Path.ChangeExtension(rdbFile, ".rdx");
                    string rdbName = Path.GetFileName(rdbFile);
                    var    reader  = new RdbReader(rdbFile, rdxFile);

                    if (!reader.Load())
                    {
                        AppLogger.Warn($"[RDB] 로드 실패: {rdbName}");
                        continue;
                    }

                    if (_rdb == null)
                    {
                        _rdb = reader;
                        _primaryRdbPath = rdbFile;
                    }

                    foreach (var rec in reader.Entries)
                    {
                        string container = reader.ResolveFdata(rec.FdataId);
                        list.Add(new AssetItemViewModel(rec, container, rdbName));
                    }

                    AppLogger.Info($"[RDB] {rdbName}: {reader.Entries.Count:N0}개");
                }

                if (_rdb == null)
                    throw new InvalidDataException("로드된 RDB 없음");

                // G1T index keyed by (rdbName, fdataId) so proximity search stays within one RDB.
                var fid = list
                    .Where(a => a.TypeExt == ".g1t")
                    .GroupBy(a => (a.RdbName, a.Record.FdataId))
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Record.FdataOffset).ToList());

                var ktid = new Dictionary<uint, AssetItemViewModel>(list.Count);
                foreach (var a in list) ktid.TryAdd(a.Record.FileKtid, a);

                return (List: list, G1tByFid: fid, ByKtid: ktid);
            });
            batch    = r.List;
            g1tByFid = r.G1tByFid;
            byKtid   = r.ByKtid;
        }
        catch (Exception ex) { err = ex.Message; }

        if (err != null) { StatusText = $"오류: {err}"; IsLoading = false; return; }

        Assets           = new ObservableCollection<AssetItemViewModel>(batch!);
        _allG1tByFid     = g1tByFid!;
        _allAssetsByKtid = byKtid!;

        BuildTypeFilters();
        AppLogger.Info($"에셋 로드 완료: {Assets.Count:N0}개");

        FilteredAssets = new ObservableCollection<AssetItemViewModel>(batch!);
        StatusText     = $"{batch!.Count:N0}개 에셋";

        IsLoading    = false;
        LoadProgress = 100;
    }

    /// <summary>
    /// Slow background work called from <c>MainWindow.Window_Loaded</c> after the browser is visible.
    /// Runs name recovery, folder tree build, and G1M→G1T map construction without blocking the UI.
    /// </summary>
    public async Task LoadBackgroundAsync()
    {
        if (_extractor == null) return;

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = "이름 사전 로드 중...";

        await RunNameRecoveryAsync();
        LoadProgress = 30;

        await BuildFolderTreeAsync();
        LoadProgress = 60;

        await BuildG1mMapAsync();

        StatusText   = $"준비 완료: {Assets.Count:N0}개 에셋, {_nameMap.Count:N0}개 파일명 복구";
        IsLoading    = false;
        LoadProgress = 100;
    }

    private async Task BuildG1mMapAsync()
    {
        if (_extractor == null) return;

        string rdbPath = _primaryRdbPath;
        string gameDir = _profile.GameDirectory;

        // Move Assets.ToList() + TryLoad (JSON parse + dict build) off the UI thread.
        var (allAssets, cached, hasCached) = await Task.Run(() =>
        {
            var assets = Assets.ToList();
            bool hit   = TextureMapCache.TryLoad(gameDir, rdbPath, assets, out var c);
            return (assets, c, hit);
        });

        if (hasCached)
        {
            _g1mToG1tMap = cached;
            AppLogger.Info($"[G1mMap] 캐시 로드: {_g1mToG1tMap.Count:N0}개 G1M 연결");
            StatusText = $"G1M→G1T 매핑 완료 (캐시, {_g1mToG1tMap.Count:N0}개)";
            return;
        }

        StatusText   = "G1M→G1T 매핑 구축 중...";
        _g1mToG1tMap = await KidsObjDbResolver.BuildAsync(allAssets, _extractor);
        StatusText   = $"G1M→G1T 매핑 완료 ({_g1mToG1tMap.Count:N0}개 G1M 연결)";

        TextureMapCache.Save(gameDir, rdbPath, _g1mToG1tMap);
    }

    // ── Name recovery ─────────────────────────────────────────────────────────

    /// <summary>CSV 파일을 다시 읽어 이름 사전을 갱신한다 (View 메뉴에서 수동 실행).</summary>
    [RelayCommand]
    private async Task ReloadNamesAsync()
    {
        if (Assets.Count == 0) { StatusText = "먼저 에셋을 로드하세요."; return; }
        IsLoading    = true;
        LoadProgress = 0;
        await RunNameRecoveryAsync();
        await BuildFolderTreeAsync();
        IsLoading    = false;
        LoadProgress = 100;
    }

    /// <summary>이름 사전 CSV 로드 — 시작 시 및 ReloadNamesCommand에서 호출.</summary>
    private async Task RunNameRecoveryAsync()
    {
        if (_extractor == null) return;

        string appId   = AppIdService.GetAppId(_profile.GameDirectory);
        string csvPath = NameDictionaryService.GetCsvPath(appId);

        Dictionary<uint, string> names;

        if (File.Exists(csvPath))
        {
            names = await Task.Run(() => NameDictionaryService.Load(csvPath));
            AppLogger.Info($"[NameDictionary] CSV 로드 (AppID={appId}): {names.Count}개");
        }
        else
        {
            names = [];
            AppLogger.Info($"[NameDictionary] CSV 없음 (AppID={appId})");
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
        AppLogger.Info($"[NameDictionary] {names.Count}개 로드, {matched}개 적용");
    }

    // ── Save Model (FBX) ─────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSaveModel))]
    private async Task SaveModelAsync(AssetItemViewModel? vm)
    {
        vm ??= SelectedAsset;
        if (vm == null || _extractor == null) return;

        string exportDir = Path.Combine(AppContext.BaseDirectory, "export");
        string stem      = vm.RecoveredName is string rn ? Path.GetFileNameWithoutExtension(rn) : vm.KtidHex;

        StatusText = $"모델 내보내는 중... {stem}";

        string? savedPath = null;
        int     texCount  = 0;
        string? err       = null;

        var extractor = _extractor;
        await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(exportDir);

                // 1. Parse G1M
                byte[]   raw   = extractor.ExtractToMemory(vm.Record, vm.Container);
                G1mData? model = G1mReader.Read(raw);
                if (model == null) { err = "G1M 파싱 실패"; return; }

                // 2. Resolve associated G1T files
                var g1tFiles = ResolveG1tFilesForModel(vm);
                AppLogger.Info($"[SaveModel] G1T files resolved: {g1tFiles.Count}");

                // 3. Determine how many texture slots live in one G1T file
                int texPerFile = 1;
                if (g1tFiles.Count > 0)
                {
                    try
                    {
                        var fi = g1tFiles[0];
                        var firstInfo = G1tDecoder.Survey(extractor.ExtractToMemory(fi.Record, fi.Container));
                        if (firstInfo.Version == "1600")
                        {
                            int rc = firstInfo.Textures.Count(t => t.FmtCode != 0);
                            if (rc > 1) texPerFile = rc;
                        }
                    }
                    catch { }
                }

                // 4. Collect which global G1T slots are needed
                var neededSlots = model.MaterialTextures.Count > 0
                    ? model.MaterialTextures.Select(x => x.G1tSlot).Distinct().ToList()
                    : model.Submeshes.Select(s => s.MaterialIndex).Where(m => m >= 0).Distinct().ToList();

                // 5. Group by G1T file index, extract and save PNGs
                var matTexPaths = new Dictionary<int, string>(); // matIdx → relative .png name

                foreach (var grp in neededSlots.GroupBy(s => s / texPerFile))
                {
                    int fileIdx = grp.Key;
                    if (fileIdx >= g1tFiles.Count) continue;

                    byte[] g1tRaw;
                    try { g1tRaw = extractor.ExtractToMemory(g1tFiles[fileIdx].Record, g1tFiles[fileIdx].Container); }
                    catch { continue; }
                    if (g1tRaw.Length < 8 || g1tRaw[0] != 'G') continue;

                    List<(int Slot, Image<Rgba32> Image)> decoded;
                    try { decoded = G1tDecoder.DecodeAll(g1tRaw); }
                    catch { continue; }

                    var neededInternal = new HashSet<int>(grp.Select(s => s % texPerFile));

                    foreach (var (intSlot, img) in decoded)
                    {
                        if (img == null) continue;

                        if (!neededInternal.Contains(intSlot)) { img.Dispose(); continue; }

                        int globalSlot = fileIdx * texPerFile + intSlot;
                        string texName = $"{stem}_g1t{globalSlot}.png";
                        string texPath = Path.Combine(exportDir, texName);

                        try
                        {
                            using (img) img.SaveAsPng(texPath);
                            texCount++;
                            AppLogger.Info($"[SaveModel] Saved texture: {texName}");

                            // Map every material that references this slot
                            foreach (var (matIdx, slot, _, _, _, _) in model.MaterialTextures)
                                if (slot == globalSlot)
                                    matTexPaths.TryAdd(matIdx, texName);
                        }
                        catch (Exception ex2)
                        {
                            img.Dispose();
                            AppLogger.Warn($"[SaveModel] PNG save failed ({texName}): {ex2.Message}");
                        }
                    }
                }

                // 6. Export geometry (OBJ/FBX) with texture references embedded in MTL
                savedPath = G1mFbxExporter.Export(model, exportDir, stem, matTexPaths);
            }
            catch (Exception ex)
            {
                err = ex.Message;
                AppLogger.Error($"[SaveModel] {ex}");
            }
        });

        StatusText = err != null
            ? $"내보내기 오류: {err}"
            : $"저장 완료 → {savedPath}  ({texCount}개 텍스처)";
    }

    private bool CanSaveModel(AssetItemViewModel? vm)
        => (vm ?? SelectedAsset)?.TypeExt == ".g1m" && _extractor != null;

    private List<AssetItemViewModel> ResolveG1tFilesForModel(AssetItemViewModel vm)
    {
        uint   ktid    = vm.Record.FileKtid;
        string cont    = vm.Container;
        ushort fid     = vm.Record.FdataId;
        string rdbName = vm.RdbName;

        // Priority 1: kidsobjdb map
        if (_g1mToG1tMap.TryGetValue(ktid, out var mapped) && mapped.Count > 0)
            return mapped.ToList();

        // Priority 2: co-located G1T in the same fdata container
        var colocated = _allAssetsByKtid.Values
            .Where(a => a.Container == cont && a.TypeExt == ".g1t")
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();
        if (colocated.Count > 0) return colocated;

        // Priority 3: nearest fdata ID within the same RDB (cross-RDB proximity is meaningless).
        if (_allG1tByFid.Count == 0) return [];
        var nearestKey  = ((string Rdb, ushort Fid))default;
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

    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        if (SelectedAsset is null || _extractor is null) return;

        var    rec      = SelectedAsset.Record;
        string cont     = SelectedAsset.Container;
        string fileName = BuildExportFileName(SelectedAsset);
        string outDir   = Path.Combine(AppContext.BaseDirectory, "export");
        string outPath  = Path.Combine(outDir, fileName);

        StatusText = $"내보내는 중... {fileName}";

        string? err = null;
        await Task.Run(() =>
        {
            try
            {
                byte[] raw = _extractor.ExtractToMemory(rec, cont);
                if (raw.Length == 0) { err = "빈 데이터 (추출 실패)"; return; }

                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(outPath, raw);
                AppLogger.Info($"[Export] {outPath} ({raw.Length:N0} bytes)");
            }
            catch (Exception ex)
            {
                err = ex.Message;
                AppLogger.Error($"[Export] {ex.Message}");
            }
        });

        StatusText = err is null
            ? $"저장 완료 → {outPath}"
            : $"내보내기 실패: {err}";
    }

    private static string BuildExportFileName(AssetItemViewModel vm)
    {
        string ext = vm.Record.TypeExt;

        // Use recovered name stem when available, sanitised for filesystem use
        if (vm.RecoveredName is { Length: > 0 } rn)
        {
            string stem = rn
                .Replace('/', '_')
                .Replace('\\', '_')
                .Trim('_');
            // Strip surrounding bracket wrappers like CE1Resource...[name]
            int lb = stem.LastIndexOf('[');
            int rb = stem.LastIndexOf(']');
            if (lb >= 0 && rb > lb)
                stem = stem[(lb + 1)..rb];

            return stem + ext;
        }

        return vm.KtidHex + ext;
    }

    // ── Folder tree ───────────────────────────────────────────────────────────

    // Instance overload: reads _restoreAssetName from UI thread. Do NOT call from Task.Run.
    private string GetEffectiveFolderPath(AssetItemViewModel asset) =>
        GetEffectiveFolderPath(asset, _restoreAssetName);

    // Static overload: safe to call from any thread with an explicitly captured value.
    private static string GetEffectiveFolderPath(AssetItemViewModel asset, bool restoreNames)
    {
        // Each RDB file is a top-level folder in the tree (e.g. "root.rdb", "system.rdb").
        string prefix = asset.RdbName + "/";

        if (!restoreNames)
            return prefix + KtidExtension.GetCategory(asset.Record.TypeKtid);

        if (asset.RecoveredName is { } rn)
        {
            string normalized = rn.Replace('\\', '/');
            int sep = normalized.LastIndexOf('/');
            string sub = sep > 0 ? normalized[..sep] : string.Empty;
            return string.IsNullOrEmpty(sub) ? prefix + "Content" : prefix + "Content/" + sub;
        }

        return prefix + "Content (Unrecovered)/" + KtidExtension.GetCategory(asset.Record.TypeKtid);
    }

    private async Task BuildFolderTreeAsync()
    {
        var assets       = Assets.ToList();
        bool restoreNames = RestoreAssetName;

        var r = await Task.Run(() =>
        {
            var nodeMap  = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
            var rootList = new List<FolderNode>();

            foreach (var asset in assets)
            {
                string folderPath = GetEffectiveFolderPath(asset, restoreNames);
                if (string.IsNullOrEmpty(folderPath)) continue;
                EnsurePath(folderPath, nodeMap, rootList);
            }

            int unkCount = assets.Count(a => string.IsNullOrEmpty(GetEffectiveFolderPath(a, restoreNames)));

            rootList.Sort((a, b) =>
            {
                if (a.IsUnknown != b.IsUnknown) return a.IsUnknown ? 1 : -1;
                bool aM = a.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
                bool bM = b.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
                if (aM != bM) return aM ? 1 : -1;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var root in rootList) SortChildrenRecursive(root);

            return (Roots: rootList, UnknownCount: unkCount);
        });

        if (r.UnknownCount > 0)
        {
            var unknown = new FolderNode("Unknown", string.Empty, isUnknown: true) { AssetCount = r.UnknownCount };
            r.Roots.Add(unknown);
        }

        FolderTree         = new ObservableCollection<FolderNode>(r.Roots);
        SelectedFolderNode ??= r.Roots.FirstOrDefault();
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
        string text        = FilterText.Trim().ToLowerInvariant();
        string type        = SelectedTypeFilter;
        var    folder      = SelectedFolderNode;
        var    assets      = Assets;          // stable reference — not modified after LoadAsync
        bool   restoreNames = RestoreAssetName;

        List<AssetItemViewModel> filtered;
        try
        {
            filtered = await Task.Run(() =>
            {
                IEnumerable<AssetItemViewModel> source = folder switch
                {
                    null               => (IEnumerable<AssetItemViewModel>)assets,
                    { IsUnknown: true } => assets.Where(a =>
                        string.IsNullOrEmpty(GetEffectiveFolderPath(a, restoreNames))),
                    FolderNode fn      => assets.Where(a =>
                    {
                        string fp = GetEffectiveFolderPath(a, restoreNames);
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
            ? $"{total:N0}개 에셋"
            : $"{filtered.Count:N0} / {total:N0}개 에셋 (필터)";
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
