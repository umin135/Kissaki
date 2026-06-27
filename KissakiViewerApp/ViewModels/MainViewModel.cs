using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KissakiViewer.Core;
using KissakiViewer.Core.Formats;
using KissakiViewer.Core.NameRecovery;
using KissakiViewer.Models;
using Ookii.Dialogs.Wpf;
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
        _profile   = profile;
        WindowTitle = $"Kissaki — {profile.Name} ({profile.GameDirectory})";
    }

    // ── Property change reactions ─────────────────────────────────────────────

    partial void OnFilterTextChanged(string _)          => ApplyFilter();
    partial void OnSelectedTypeFilterChanged(string _)  => ApplyFilter();
    partial void OnSelectedFolderNodeChanged(FolderNode? _) => ApplyFilter();

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
        BuildFolderTree();
        ApplyFilter();

        StatusText   = $"{Assets.Count:N0}개 에셋 로드됨";
        IsLoading    = false;
        LoadProgress = 100;

        // Build G1M→G1T map in background (non-blocking)
        _ = BuildG1mMapAsync();
    }

    private async Task BuildG1mMapAsync()
    {
        if (_extractor == null) return;
        StatusText = "G1M→G1T 매핑 구축 중...";
        var allAssets = Assets.ToList();
        _g1mToG1tMap = await KidsObjDbResolver.BuildAsync(allAssets, _extractor,
            new Progress<(int done, int total)>(p =>
                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = $"kidsobjdb 스캔 중... {p.done}/{p.total}")));
        Application.Current.Dispatcher.Invoke(() =>
            StatusText = $"G1M→G1T 매핑 완료 ({_g1mToG1tMap.Count:N0}개 G1M 연결)");
    }

    // ── Name recovery ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RecoverNamesAsync()
    {
        if (Assets.Count == 0) { StatusText = "먼저 에셋을 로드하세요."; return; }

        var knownKtids = new HashSet<uint>(Assets.Select(a => a.Record.FileKtid));
        var log     = new List<string>();
        var results = new Dictionary<uint, string>();
        var cts     = new CancellationTokenSource();

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = "파일명 복구 중...";

        await Task.Run(() =>
        {
            var bfResults = NameRecoveryScanner.BruteForce(knownKtids, log, cts.Token);
            foreach (var kv in bfResults) results[kv.Key] = kv.Value;
            AppLogger.Info($"[NameRecovery] BruteForce: {bfResults.Count}");

            Application.Current.Dispatcher.Invoke(() =>
            { LoadProgress = 30; StatusText = $"브루트포스 완료 ({bfResults.Count}개) — exe 스캔 중..."; });

            foreach (string exeName in new[] { "DOA6LR.exe", "DOA6.exe", "DOA6_LastRound.exe", "doa6.exe", "DeadOrAlive6.exe" })
            {
                string exePath = Path.Combine(_profile.GameDirectory, exeName);
                if (!File.Exists(exePath)) continue;

                var exeResults = NameRecoveryScanner.ScanNullTerminated(
                    exePath, knownKtids, log,
                    new Progress<int>(p => Application.Current.Dispatcher.Invoke(() =>
                    { LoadProgress = 30 + p * 0.7; StatusText = $"exe 스캔 중... {p}%"; })),
                    cts.Token);

                foreach (var kv in exeResults) results[kv.Key] = kv.Value;
                break;
            }
            foreach (string line in log) AppLogger.Info(line);
        }, cts.Token);

        _nameMap = results;
        foreach (var a in Assets)
        {
            if (_nameMap.TryGetValue(a.Record.FileKtid, out string? name))
                a.RecoveredName = name;
        }

        BuildFolderTree();
        ApplyFilter();

        StatusText   = $"파일명 복구 완료: {results.Count}개 / {Assets.Count:N0}개 (로그: {AppLogger.LogPath})";
        IsLoading    = false;
        LoadProgress = 100;
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        if (SelectedAsset is null || _extractor is null) return;

        var dlg = new VistaFolderBrowserDialog { Description = "저장 폴더 선택", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != true) return;

        string outDir = dlg.SelectedPath;
        string stem   = SelectedAsset.KtidHex;
        var    rec    = SelectedAsset.Record;
        string cont   = SelectedAsset.Container;

        StatusText = "내보내는 중...";
        await Task.Run(() =>
        {
            byte[] raw = _extractor.ExtractToMemory(rec, cont);
            if (raw.Length == 0) { Application.Current.Dispatcher.Invoke(() => StatusText = "추출 실패."); return; }

            if (rec.TypeExt == ".g1t")
            {
                int n = G1tDecoder.SaveAllAsPng(raw, outDir, stem);
                Application.Current.Dispatcher.Invoke(() => StatusText = $"{n}개 PNG 저장 완료 → {outDir}");
            }
            else
            {
                string path = Path.Combine(outDir, $"{stem}{rec.TypeExt}");
                File.WriteAllBytes(path, raw);
                Application.Current.Dispatcher.Invoke(() => StatusText = $"저장 완료 → {path}");
            }
        });
    }

    // ── Folder tree ───────────────────────────────────────────────────────────

    private void BuildFolderTree()
    {
        var nodeMap = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        var roots   = new List<FolderNode>();

        foreach (var asset in Assets)
        {
            string? folderPath = asset.FolderPath;
            if (string.IsNullOrEmpty(folderPath)) continue;

            EnsurePath(folderPath, nodeMap, roots);
        }

        int unknownCount = Assets.Count(a => string.IsNullOrEmpty(a.FolderPath));
        if (unknownCount > 0)
        {
            var unknown = new FolderNode("Unknown", string.Empty, isUnknown: true) { AssetCount = unknownCount };
            roots.Add(unknown);
        }

        FolderTree = new ObservableCollection<FolderNode>(roots);

        // Default selection: Unknown or first node
        SelectedFolderNode ??= roots.LastOrDefault();
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

    // ── Filter ────────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        string text = FilterText.Trim().ToLowerInvariant();
        string type = SelectedTypeFilter;

        IEnumerable<AssetItemViewModel> source = SelectedFolderNode switch
        {
            null                   => Assets,
            { IsUnknown: true }    => Assets.Where(a => string.IsNullOrEmpty(a.FolderPath)),
            FolderNode fn          => Assets.Where(a =>
                a.FolderPath.StartsWith(fn.FullPath, StringComparison.OrdinalIgnoreCase)),
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
