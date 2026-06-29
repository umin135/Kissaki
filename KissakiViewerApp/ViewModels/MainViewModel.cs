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

    internal IReadOnlyDictionary<ushort, List<AssetItemViewModel>> AllG1tByFid =>
        _allG1tByFid;

    internal IReadOnlyDictionary<uint, AssetItemViewModel> AllAssetsByKtid =>
        _allAssetsByKtid;

    internal IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> G1mToG1tMap =>
        _g1mToG1tMap;

    // ── Private fields ────────────────────────────────────────────────────────

    private readonly GameProfile _profile;
    private RdbReader?    _rdb;
    private FdataExtractor? _extractor;
    private Dictionary<uint, string> _nameMap = [];

    private Dictionary<ushort, List<AssetItemViewModel>> _allG1tByFid = [];
    private Dictionary<uint, AssetItemViewModel>         _allAssetsByKtid = [];
    private IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>> _g1mToG1tMap =
        new Dictionary<uint, IReadOnlyList<AssetItemViewModel>>();

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

    partial void OnFilterTextChanged(string value)              => ApplyFilter();
    partial void OnSelectedTypeFilterChanged(string value)      => ApplyFilter();
    partial void OnSelectedFolderNodeChanged(FolderNode? value) => ApplyFilter();
    partial void OnRestoreAssetNameChanged(bool value)
    {
        SelectedFolderNode = null;
        BuildFolderTree();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        string rdbPath = Path.Combine(_profile.FdataDir, "root.rdb");
        string rdxPath = Path.Combine(_profile.FdataDir, "root.rdx");

        if (!File.Exists(rdbPath))
        {
            StatusText = $"파일 없음: {rdbPath}";
            return;
        }

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = "root.rdb 파싱 중...";

        List<AssetItemViewModel>? batch = null;
        string? err = null;

        try
        {
            batch = await Task.Run(() =>
            {
                var rdb    = new RdbReader(rdbPath, rdxPath);
                _extractor = new FdataExtractor(_profile.FdataDir);

                if (!rdb.Load())
                    throw new InvalidDataException("RDB 로드 실패");

                _rdb = rdb;
                var list = new List<AssetItemViewModel>(rdb.Entries.Count);
                foreach (var rec in rdb.Entries)
                {
                    string container = rdb.ResolveFdata(rec.FdataId);
                    list.Add(new AssetItemViewModel(rec, container));
                }
                return list;
            });
        }
        catch (Exception ex) { err = ex.Message; }

        if (err != null) { StatusText = $"오류: {err}"; IsLoading = false; return; }

        Assets = new ObservableCollection<AssetItemViewModel>(batch!);

        // Build lookup tables for G1T resolution
        _allG1tByFid = Assets
            .Where(a => a.TypeExt == ".g1t")
            .GroupBy(a => a.Record.FdataId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Record.FdataOffset).ToList());

        _allAssetsByKtid = new Dictionary<uint, AssetItemViewModel>();
        foreach (var a in Assets) _allAssetsByKtid.TryAdd(a.Record.FileKtid, a);

        BuildTypeFilters();
        AppLogger.Info($"에셋 로드 완료: {Assets.Count:N0}개");
        StatusText = $"에셋 로드됨 ({Assets.Count:N0}개) — 파일명 복구 중...";

        await RunNameRecoveryAsync();

        BuildFolderTree();
        ApplyFilter();

        StatusText   = $"준비 완료: {Assets.Count:N0}개 에셋, {_nameMap.Count:N0}개 파일명 복구";
        IsLoading    = false;
        LoadProgress = 100;

        // Build G1M→G1T map in background (non-blocking)
        _ = BuildG1mMapAsync();
    }

    private async Task BuildG1mMapAsync()
    {
        if (_extractor == null) return;

        string rdbPath = Path.Combine(_profile.FdataDir, "root.rdb");
        string gameDir = _profile.GameDirectory;

        // Move Assets.ToList() + TryLoad (JSON parse + 78k dict build) off the UI thread.
        // Without this, the cache-hit path has no await and blocks the UI until it returns.
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
        BuildFolderTree();
        ApplyFilter();
        IsLoading    = false;
        LoadProgress = 100;
    }

    /// <summary>이름 사전 CSV 로드 — 시작 시 및 ReloadNamesCommand에서 호출.</summary>
    private async Task RunNameRecoveryAsync()
    {
        if (_extractor == null) return;

        StatusText   = "이름 사전 로드 중...";
        LoadProgress = 0;

        string appId   = AppIdService.GetAppId(_profile.GameDirectory);
        string csvPath = NameDictionaryService.GetCsvPath(appId);

        Dictionary<uint, string> names;

        if (File.Exists(csvPath))
        {
            // CSV 있음 → 바로 로드
            names = await Task.Run(() => NameDictionaryService.Load(csvPath));
            AppLogger.Info($"[NameDictionary] CSV 로드 (AppID={appId}): {names.Count}개");
        }
        else
        {
            // CSV 없음 → 두 소스로 이름 수집 후 저장
            var assetsCopy = Assets.ToList();
            var extractor  = _extractor;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;

            names = await Task.Run(() =>
            {
                var merged = new Dictionary<uint, string>();

                // 소스 1: G1MX/G1COX/G1P 헤더 내장 경로 (대부분 게임에서 작동)
                dispatcher?.BeginInvoke(() => StatusText = "이름 수집 중 (G1MX/G1COX/G1P)...");
                var grabbed = NameGrabberService.Grab(assetsCopy, extractor,
                    pct => dispatcher?.BeginInvoke(() => LoadProgress = pct / 2));
                foreach (var kv in grabbed) merged[kv.Key] = kv.Value;

                // 소스 2: .name 데이터베이스 파일 (DLC/온디맨드 콘텐츠, 없어도 무방)
                dispatcher?.BeginInvoke(() => StatusText = "이름 수집 중 (.name 데이터베이스)...");
                var fromName = NameBuildService.Build(assetsCopy, extractor,
                    pct => dispatcher?.BeginInvoke(() => LoadProgress = 50 + pct / 2));
                foreach (var kv in fromName) merged.TryAdd(kv.Key, kv.Value);

                if (merged.Count > 0)
                    NameDictionaryService.Save(csvPath, merged);

                return merged;
            });

            AppLogger.Info($"[NameDictionary] 수집 완료 (AppID={appId}): {names.Count}개 → {csvPath}");
        }

        _nameMap = names;
        foreach (var a in Assets)
            if (_nameMap.TryGetValue(a.Record.FileKtid, out string? n))
                a.RecoveredName = n;

        LoadProgress = 100;
        int matched = Assets.Count(a => a.RecoveredName != null);
        StatusText = names.Count > 0
            ? $"이름 복구 완료: {names.Count:N0}개 항목 / {matched:N0}개 매칭"
            : $"이름 사전 없음 (AppID={appId})";
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
        uint   ktid = vm.Record.FileKtid;
        string cont = vm.Container;
        ushort fid  = vm.Record.FdataId;

        // Priority 1: kidsobjdb map
        if (_g1mToG1tMap.TryGetValue(ktid, out var mapped) && mapped.Count > 0)
            return mapped.ToList();

        // Priority 2: co-located G1T in the same fdata container
        var colocated = _allAssetsByKtid.Values
            .Where(a => a.Container == cont && a.TypeExt == ".g1t")
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();
        if (colocated.Count > 0) return colocated;

        // Priority 3: nearest fdata ID
        if (_allG1tByFid.Count == 0) return [];
        ushort nearestFid  = 0;
        int    nearestDelta = int.MaxValue;
        foreach (var f in _allG1tByFid.Keys)
        {
            int d = Math.Abs((int)f - (int)fid);
            if (d < nearestDelta) { nearestDelta = d; nearestFid = f; }
        }
        return _allG1tByFid.TryGetValue(nearestFid, out var list) ? list : [];
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

    /// <summary>
    /// Returns the folder path used for tree building and filtering.
    /// RestoreAssetName=true  → "Content/path" or "Content (Unrecovered)/category"
    /// RestoreAssetName=false → flat category only
    /// </summary>
    private string GetEffectiveFolderPath(AssetItemViewModel asset)
    {
        if (!_restoreAssetName)
            return KtidExtension.GetCategory(asset.Record.TypeKtid);

        if (asset.RecoveredName is { } rn)
        {
            string normalized = rn.Replace('\\', '/');
            int sep = normalized.LastIndexOf('/');
            string sub = sep > 0 ? normalized[..sep] : string.Empty;
            return string.IsNullOrEmpty(sub) ? "Content" : "Content/" + sub;
        }

        return "Content (Unrecovered)/" + KtidExtension.GetCategory(asset.Record.TypeKtid);
    }

    private void BuildFolderTree()
    {
        var nodeMap = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var roots   = new List<FolderNode>();

        foreach (var asset in Assets)
        {
            string folderPath = GetEffectiveFolderPath(asset);
            if (string.IsNullOrEmpty(folderPath)) continue;

            EnsurePath(folderPath, nodeMap, roots);
        }

        int unknownCount = Assets.Count(a => string.IsNullOrEmpty(GetEffectiveFolderPath(a)));
        if (unknownCount > 0)
        {
            var unknown = new FolderNode("Unknown", string.Empty, isUnknown: true) { AssetCount = unknownCount };
            roots.Add(unknown);
        }

        // Sort roots: alphabetical, Misc last, Unknown last
        roots.Sort((a, b) =>
        {
            if (a.IsUnknown != b.IsUnknown) return a.IsUnknown ? 1 : -1;
            bool aM = a.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
            bool bM = b.Name.Equals("Misc", StringComparison.OrdinalIgnoreCase);
            if (aM != bM) return aM ? 1 : -1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var root in roots) SortChildrenRecursive(root);

        FolderTree = new ObservableCollection<FolderNode>(roots);

        // Default selection: first node
        SelectedFolderNode ??= roots.FirstOrDefault();
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

    private void ApplyFilter()
    {
        string text = FilterText.Trim().ToLowerInvariant();
        string type = SelectedTypeFilter;

        IEnumerable<AssetItemViewModel> source = SelectedFolderNode switch
        {
            null                   => Assets,
            { IsUnknown: true }    => Assets.Where(a => string.IsNullOrEmpty(GetEffectiveFolderPath(a))),
            FolderNode fn          => Assets.Where(a =>
                GetEffectiveFolderPath(a).StartsWith(fn.FullPath, StringComparison.OrdinalIgnoreCase)),
        };

        FilteredAssets = new ObservableCollection<AssetItemViewModel>(
            source.Where(a =>
            {
                bool typeOk = type == "All" || a.TypeExt == type;
                bool textOk = string.IsNullOrEmpty(text) ||
                              a.KtidHex.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                              a.TypeExt.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                              (a.RecoveredName != null && a.RecoveredName.Contains(text, StringComparison.OrdinalIgnoreCase));
                return typeOk && textOk;
            }));

        StatusText = FilteredAssets.Count == Assets.Count
            ? $"{Assets.Count:N0}개 에셋"
            : $"{FilteredAssets.Count:N0} / {Assets.Count:N0}개 에셋 (필터)";
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
