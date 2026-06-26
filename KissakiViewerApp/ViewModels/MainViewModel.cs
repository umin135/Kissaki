using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KissakiViewer.Core;
using KissakiViewer.Core.Formats;
using KissakiViewer.Core.NameRecovery;
using Ookii.Dialogs.Wpf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KissakiViewer.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _gameDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAssets))]
    private ObservableCollection<AssetItemViewModel> _assets = [];

    [ObservableProperty]
    private ObservableCollection<AssetItemViewModel> _filteredAssets = [];

    [ObservableProperty]
    private AssetItemViewModel? _selectedAsset;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private string _statusText = "게임 디렉토리를 선택하세요.";

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _selectedTypeFilter = "전체";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _loadProgress;

    [ObservableProperty]
    private ObservableCollection<string> _typeFilters = ["전체"];

    [ObservableProperty]
    private string _previewInfo = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelPreview))]
    private G1mData? _currentG1mData;

    public bool HasModelPreview => _currentG1mData != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleSlots))]
    [NotifyPropertyChangedFor(nameof(SlotMax))]
    private int _slotCount;

    [ObservableProperty]
    private int _currentSlotIndex;

    public bool HasMultipleSlots => SlotCount > 1;
    public int  SlotMax          => Math.Max(0, SlotCount - 1);

    public bool HasAssets => Assets.Count > 0;

    // ── Internal ──────────────────────────────────────────────────────────────

    private RdbReader? _rdb;
    private FdataExtractor? _extractor;
    private string _fdataDir = string.Empty;

    // FileKtid → recovered path string (populated by name recovery)
    private Dictionary<uint, string> _nameMap = [];

    private CancellationTokenSource? _previewCts;

    // MaterialIndex → albedo BitmapSource (set when a G1M is loaded)
    private Dictionary<int, BitmapSource> _modelTextures = [];
    internal IReadOnlyDictionary<int, BitmapSource> ModelTextures => _modelTextures;

    private record SlotBitmap(int Slot, BitmapSource Bmp, string Info);
    private List<SlotBitmap> _slotBitmaps = [];

    // ── Property change reactions ─────────────────────────────────────────────

    partial void OnFilterTextChanged(string value)         => ApplyFilter();
    partial void OnSelectedTypeFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedAssetChanged(AssetItemViewModel? value) =>
        _ = LoadAssetPreviewAsync(value);

    partial void OnCurrentSlotIndexChanged(int value)
    {
        if (value >= 0 && value < _slotBitmaps.Count)
        {
            PreviewImage = _slotBitmaps[value].Bmp;
            PreviewInfo  = _slotBitmaps[value].Info;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseGameDirectory()
    {
        var dlg = new VistaFolderBrowserDialog
        {
            Description            = "Dead or Alive 6 Last Round 설치 폴더를 선택하세요",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == true)
            GameDirectory = dlg.SelectedPath;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory))
        {
            StatusText = "게임 경로를 먼저 입력하세요.";
            return;
        }

        _fdataDir = Path.Combine(GameDirectory, "fdata_package");
        string rdbPath = Path.Combine(_fdataDir, "root.rdb");
        string rdxPath = Path.Combine(_fdataDir, "root.rdx");

        if (!File.Exists(rdbPath))
        {
            StatusText = $"파일 없음: {rdbPath}";
            return;
        }

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = $"root.rdb 파싱 중... ({rdbPath})";
        Assets.Clear();
        FilteredAssets.Clear();
        PreviewImage  = null;
        SelectedAsset = null;

        List<AssetItemViewModel>? batch = null;
        string? errorMsg = null;

        try
        {
            batch = await Task.Run(() =>
            {
                var rdb       = new RdbReader(rdbPath, rdxPath);
                _extractor    = new FdataExtractor(_fdataDir);

                if (!rdb.Load())
                    throw new InvalidDataException($"RDB 로드 실패 (매직 불일치 또는 항목 없음): {rdbPath}");

                _rdb = rdb;

                // Build all VMs in background — never touch ObservableCollection from here
                var list = new List<AssetItemViewModel>(rdb.Entries.Count);
                foreach (var rec in rdb.Entries)
                {
                    string container = rdb.ResolveFdata(rec.FdataId);
                    list.Add(new AssetItemViewModel(rec, container));
                }
                return list;
            });
        }
        catch (Exception ex)
        {
            errorMsg = $"오류: {ex.Message}";
        }

        if (errorMsg is not null)
        {
            StatusText   = errorMsg;
            IsLoading    = false;
            LoadProgress = 0;
            return;
        }

        // Replace collection in a single assignment (one CollectionChanged, not N)
        if (batch is not null)
            Assets = new ObservableCollection<AssetItemViewModel>(batch);

        BuildTypeFilters();
        ApplyFilter();

        StatusText   = $"{Assets.Count:N0}개 에셋 로드됨";
        IsLoading    = false;
        LoadProgress = 100;
    }

    [RelayCommand]
    private async Task ExportSelectedAsync()
    {
        if (SelectedAsset is null || _extractor is null) return;

        var dlg = new VistaFolderBrowserDialog
        {
            Description            = "저장 폴더 선택",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != true) return;

        string outDir = dlg.SelectedPath;
        string stem   = SelectedAsset.KtidHex;
        var    rec    = SelectedAsset.Record;
        string cont   = SelectedAsset.Container;

        StatusText = "내보내는 중...";
        await Task.Run(() =>
        {
            byte[] raw = _extractor.ExtractToMemory(rec, cont);
            if (raw.Length == 0)
            {
                Application.Current.Dispatcher.Invoke(() => StatusText = "추출 실패.");
                return;
            }

            if (rec.TypeExt == ".g1t")
            {
                int n = G1tDecoder.SaveAllAsPng(raw, outDir, stem);
                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = $"{n}개 PNG 저장 완료 → {outDir}");
            }
            else
            {
                string path = Path.Combine(outDir, $"{stem}{rec.TypeExt}");
                File.WriteAllBytes(path, raw);
                Application.Current.Dispatcher.Invoke(() =>
                    StatusText = $"저장 완료 → {path}");
            }
        });
    }

    // ── Name recovery ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RecoverNamesAsync()
    {
        if (Assets.Count == 0) { StatusText = "먼저 에셋을 로드하세요."; return; }

        // Build set of all FileKtid values we want to resolve
        var knownKtids = new HashSet<uint>(Assets.Select(a => a.Record.FileKtid));

        var log  = new List<string>();
        var results = new Dictionary<uint, string>();

        IsLoading    = true;
        LoadProgress = 0;
        StatusText   = "파일명 복구 중...";

        var cts = new CancellationTokenSource();

        await Task.Run(() =>
        {
            // ── 1) 브루트포스 (small_dictionary 조합) ──────────────────────
            AppLogger.Info("[NameRecovery] Starting brute-force pass...");
            var bfResults = NameRecoveryScanner.BruteForce(knownKtids, log, cts.Token);
            foreach (var kv in bfResults) results[kv.Key] = kv.Value;
            AppLogger.Info($"[NameRecovery] Brute-force: {bfResults.Count} matches");

            Application.Current.Dispatcher.Invoke(() =>
            {
                LoadProgress = 30;
                StatusText   = $"브루트포스 완료 ({bfResults.Count}개) — 실행 파일 스캔 중...";
            });

            // ── 2) DOA6.exe 스캔 ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(GameDirectory))
            {
                // Try both common exe names
                foreach (string exeName in new[] { "DOA6LR.exe", "DOA6.exe", "DOA6_LastRound.exe", "doa6.exe", "DeadOrAlive6.exe" })
                {
                    string exePath = Path.Combine(GameDirectory, exeName);
                    if (!File.Exists(exePath)) continue;

                    AppLogger.Info($"[NameRecovery] Scanning exe: {exePath}");
                    var exeResults = NameRecoveryScanner.ScanNullTerminated(
                        exePath, knownKtids, log,
                        new Progress<int>(p =>
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                LoadProgress = 30 + p * 0.7;
                                StatusText   = $"exe 스캔 중... {p}%";
                            })),
                        cts.Token);

                    foreach (var kv in exeResults) results[kv.Key] = kv.Value;
                    AppLogger.Info($"[NameRecovery] Exe scan: {exeResults.Count} matches");
                    break;
                }
            }

            // Write log
            foreach (string line in log)
                AppLogger.Info(line);
        }, cts.Token);

        // Apply recovered names to asset VMs
        _nameMap = results;
        foreach (var a in Assets)
        {
            if (_nameMap.TryGetValue(a.Record.FileKtid, out string? name))
                a.RecoveredName = name;
        }

        // Force UI refresh
        ApplyFilter();

        StatusText   = $"파일명 복구 완료: {results.Count}개 / {Assets.Count:N0}개 해석됨  (로그: {AppLogger.LogPath})";
        IsLoading    = false;
        LoadProgress = 100;
    }

    // ── Preview dispatch ──────────────────────────────────────────────────────

    private void ClearPreview()
    {
        _slotBitmaps     = [];
        SlotCount        = 0;
        CurrentSlotIndex = 0;
        PreviewImage     = null;
        _modelTextures   = [];
        CurrentG1mData   = null;
    }

    private async Task LoadAssetPreviewAsync(AssetItemViewModel? vm)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        ClearPreview();
        if (vm is null) { PreviewInfo = string.Empty; return; }
        if (_extractor is null) { PreviewInfo = $"{vm.TypeExt}  |  {vm.SizeStr}"; return; }

        if (vm.Record.TypeExt == ".g1t" && vm.Record.Storage == StorageType.Internal)
            await LoadTexturePreviewAsync(vm, ct);
        else if (vm.Record.TypeExt == ".g1m" && vm.Record.Storage == StorageType.Internal)
            await LoadModelPreviewAsync(vm, ct);
        else if (vm.Record.TypeExt == ".mtl" && vm.Record.Storage == StorageType.Internal)
            await LoadMtlDiagnosticAsync(vm, ct);
        else
            PreviewInfo = $"{vm.TypeExt}  |  {vm.SizeStr}  |  {vm.Storage}";
    }

    // ── Model preview ─────────────────────────────────────────────────────────

    private async Task LoadModelPreviewAsync(AssetItemViewModel? vm, CancellationToken ct = default)
    {
        if (vm is null || _extractor is null) return;

        StatusText = $"모델 로딩 중: {vm.KtidHex}";
        string container = vm.Container;
        var    record    = vm.Record;

        // Snapshot assets on UI thread before entering background work
        // StorageType 필터 제거 — External 에셋도 포함해 cross-container G1T 탐색
        var colocated = Assets
            .Where(a => a.Container == container)
            .ToList();
        var g1tAssets = colocated
            .Where(a => a.TypeExt == ".g1t")
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();

        // Global FileKtid lookup (Internal + External 모두 포함)
        var allAssetsByKtid = new Dictionary<uint, AssetItemViewModel>();
        foreach (var a in Assets)
            allAssetsByKtid.TryAdd(a.Record.FileKtid, a);

        G1mData? model    = null;
        string?  errorMsg = null;
        var      textures = new Dictionary<int, BitmapSource>();

        await Task.Run(() =>
        {
            try
            {
                // ── Load G1M ────────────────────────────────────────────────
                AppLogger.Info($"ModelPreview: {vm.KtidHex} container={container}");
                byte[] raw = _extractor.ExtractToMemory(record, container);
                if (raw.Length == 0) { errorMsg = "추출 실패"; return; }

                // Verify _G1M magic (bytes: 5F 4D 31 47 = "_M1G" in LE, uint32 = 0x47314D5F)
                if (raw.Length < 4 || raw[0] != '_' || raw[1] != 'M' || raw[2] != '1' || raw[3] != 'G')
                {
                    errorMsg = $"G1M 매직 불일치: [{raw[0]:x2} {raw[1]:x2} {raw[2]:x2} {raw[3]:x2}]";
                    AppLogger.Error(errorMsg);
                    return;
                }

                model = G1mReader.Read(raw);
                if (model is null) { errorMsg = "G1M 파싱 실패"; return; }
                if (ct.IsCancellationRequested) return;

                AppLogger.Info($"  G1M: {model.Bones.Length} bones, {model.Submeshes.Length} submeshes, {model.Chunks.Count} chunks, G1MM={(model.G1mmRaw?.Length ?? 0)}B");
                foreach (var (chunkSig, chunkOff, chunkSz) in model.Chunks)
                {
                    string sigStr = new string([
                        (char)( chunkSig        & 0xFF),
                        (char)((chunkSig >>  8) & 0xFF),
                        (char)((chunkSig >> 16) & 0xFF),
                        (char)((chunkSig >> 24) & 0xFF),
                    ]);
                    AppLogger.Info($"    chunk: sig=0x{chunkSig:x8} ({sigStr}) size={chunkSz} @0x{chunkOff:x}");
                }
                foreach (var sub in model.Submeshes)
                    AppLogger.Info($"    submesh matIdx={sub.MaterialIndex} matPal=0x{sub.MatPalId:x8}: {sub.Positions.Length} verts, {sub.Indices.Length} idx, {sub.TexCoords.Length} uvs");

                // ── G1MG 헤더 덤프 (numSections 필드 위치 진단) ───────────────
                {
                    var g1mgChunk = model.Chunks.FirstOrDefault(c => c.Sig == 0x47314D47u);
                    if (g1mgChunk != default)
                    {
                        int cs = g1mgChunk.Offset;
                        int hdrLen = Math.Min(0x40, raw.Length - cs);
                        var sbH = new System.Text.StringBuilder();
                        for (int i = 0; i < hdrLen; i++)
                        {
                            if (i % 16 == 0) sbH.Append($"\n    {i:x3}: ");
                            sbH.Append($"{raw[cs + i]:x2} ");
                        }
                        uint ns2c = hdrLen >= 0x30 ? (uint)(raw[cs+0x2C]|raw[cs+0x2D]<<8|raw[cs+0x2E]<<16|raw[cs+0x2F]<<24) : 0;
                        uint ns30 = hdrLen >= 0x34 ? (uint)(raw[cs+0x30]|raw[cs+0x31]<<8|raw[cs+0x32]<<16|raw[cs+0x33]<<24) : 0;
                        AppLogger.Info($"  G1MG hdr (sections={model.G1mgSections.Count}, +0x2C={ns2c}, +0x30={ns30}):{sbH}");
                    }
                }

                // ── G1MG section IDs ────────────────────────────────────────
                foreach (var (secId, secOff, secSz) in model.G1mgSections)
                    AppLogger.Info($"  G1MG sec: id=0x{secId:x5} size={secSz} @0x{secOff:x}");

                // ── G1MG raw section dumps (0x10001, 0x10002, 0x10006, 0x10009) ──────
                foreach (var (secId, secRaw) in model.G1mgSectionRaw)
                {
                    // 0x10002 (material/texture) and 0x10009 (LOD): show up to 512B
                    int dumpLimit = (secId == 0x10009u || secId == 0x10002u) ? 512 : 128;
                    int dumpLen = Math.Min(secRaw.Length, dumpLimit);
                    AppLogger.Info($"  G1MG[0x{secId:x5}] raw ({secRaw.Length}B):");
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < dumpLen; i += 16)
                    {
                        sb.Append($"    0x{i:x4}: ");
                        for (int j = i; j < Math.Min(i + 16, dumpLen); j++)
                            sb.Append($"{secRaw[j]:x2} ");
                        AppLogger.Info(sb.ToString());
                        sb.Clear();
                    }

                    // For 0x10009: scan '@' prefixed 8-char hex KTID strings
                    if (secId == 0x10009u)
                    {
                        var ktidSet = new System.Collections.Generic.HashSet<uint>();
                        for (int i = 8; i + 9 <= secRaw.Length; i++)
                        {
                            if (secRaw[i] != '@') continue;
                            // check 8 ASCII hex chars follow
                            bool ok = true;
                            for (int k = 1; k <= 8; k++)
                            {
                                byte c = secRaw[i + k];
                                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f')))
                                { ok = false; break; }
                            }
                            if (!ok) continue;
                            string hex = System.Text.Encoding.ASCII.GetString(secRaw, i + 1, 8);
                            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint ktid))
                                ktidSet.Add(ktid);
                        }
                        var ktidInfo = ktidSet.Select(k =>
                        {
                            allAssetsByKtid.TryGetValue(k, out var avm);
                            return $"0x{k:x8}({avm?.TypeExt ?? "??"})";
                        });
                        AppLogger.Info($"  G1MG[0x10009] '@' KTIDs found ({ktidSet.Count}): {string.Join(", ", ktidInfo)}");
                    }
                }

                // ── G1MF hex dump ────────────────────────────────────────────
                if (model.G1mfRaw is { } g1mf)
                {
                    int dumpLen = Math.Min(g1mf.Length, 512);
                    AppLogger.Info($"  G1MF: {g1mf.Length}B");
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < dumpLen; i += 16)
                    {
                        sb.Append($"  G1MF 0x{i:x4}: ");
                        for (int j = i; j < Math.Min(i + 16, dumpLen); j++)
                            sb.Append($"{g1mf[j]:x2} ");
                        AppLogger.Info(sb.ToString());
                        sb.Clear();
                    }
                }

                // ── G1MM hex dump + G1T KTID 스캔 ────────────────────────────
                // G1T TypeKtid: 0xafbec60c=[0c c6 be af], 0xAD57EBBA=[ba eb 57 ad]
                if (model.G1mmRaw is { } g1mm)
                {
                    int dumpLen = Math.Min(g1mm.Length, 512);
                    AppLogger.Info($"  G1MM: {g1mm.Length}B");
                    var sb2 = new System.Text.StringBuilder();
                    for (int i = 0; i < dumpLen; i += 16)
                    {
                        sb2.Append($"  G1MM 0x{i:x4}: ");
                        for (int j = i; j < Math.Min(i + 16, dumpLen); j++)
                            sb2.Append($"{g1mm[j]:x2} ");
                        AppLogger.Info(sb2.ToString());
                        sb2.Clear();
                    }

                    // G1T TypeKtid(0xafbec60c, 0xAD57EBBA)를 G1MM 전체에서 스캔
                    var g1tFileKtidsFromG1mm = new System.Collections.Generic.HashSet<uint>();
                    for (int i = 0; i + 8 <= g1mm.Length; i += 4)
                    {
                        uint val = (uint)(g1mm[i] | g1mm[i+1]<<8 | g1mm[i+2]<<16 | g1mm[i+3]<<24);
                        if (val == 0xafbec60cu || val == 0xAD57EBBAu)
                        {
                            uint fktid = (uint)(g1mm[i+4] | g1mm[i+5]<<8 | g1mm[i+6]<<16 | g1mm[i+7]<<24);
                            g1tFileKtidsFromG1mm.Add(fktid);
                            AppLogger.Info($"  G1MM @0x{i:x4}: TypeKtid=0x{val:x8} → FileKtid=0x{fktid:x8}");
                        }
                    }
                    if (g1tFileKtidsFromG1mm.Count > 0)
                        AppLogger.Info($"  G1MM G1T 참조 발견: {string.Join(", ", g1tFileKtidsFromG1mm.Select(k => $"0x{k:x8}"))}");
                    else
                        AppLogger.Warn("  G1MM에서 G1T TypeKtid 미발견 (0xafbec60c/0xAD57EBBA)");
                }

                // ── Section 0x10002 파싱 결과 로그 ─────────────────────────────
                AppLogger.Info($"  MaterialTextures: {model.MaterialTextures.Count}개 엔트리");
                foreach (var (mi, si) in model.MaterialTextures)
                    AppLogger.Info($"    matIdx={mi} → g1tSlot={si}");

                // ── IDRK dependency KTID → G1T 참조 해석 ──────────────────────────
                uint depKtid = _extractor.ReadG1mDependencyKtid(record, container);
                AppLogger.Info($"  IDRK dependency KTID: 0x{depKtid:x8}");

                AssetItemViewModel? linkedG1tVm = null;
                if (depKtid != 0)
                {
                    allAssetsByKtid.TryGetValue(depKtid, out var depAsset);
                    if (depAsset?.TypeExt == ".g1t")
                    {
                        // dep가 바로 G1T
                        linkedG1tVm = depAsset;
                        AppLogger.Info($"  → dep=G1T: 0x{linkedG1tVm.Record.FileKtid:x8} in {linkedG1tVm.Container}");
                    }
                    else if (depAsset != null)
                    {
                        // dep가 config(.bin) → 압축 해제 후 (index, FileKtid, pad) 12B 엔트리 파싱
                        AppLogger.Info($"  → dep={depAsset.TypeExt} 0x{depAsset.Record.FileKtid:x8} in {depAsset.Container} ({depAsset.Record.FileSize}B)");
                        byte[] depRaw = [];
                        try { depRaw = _extractor.ExtractToMemory(depAsset.Record, depAsset.Container); }
                        catch (Exception ex) { AppLogger.Exception("dep extract", ex); }

                        // 포맷: 4B header + N × (u32 index, u32 fileKtid, u32 pad) = 12B/entry
                        var depG1tMap = new System.Collections.Generic.Dictionary<uint, AssetItemViewModel>();
                        if (depRaw.Length >= 16)
                        {
                            uint hdr = (uint)(depRaw[0] | depRaw[1]<<8 | depRaw[2]<<16 | depRaw[3]<<24);
                            int entryCount = (depRaw.Length - 4) / 12;
                            AppLogger.Info($"  dep header=0x{hdr:x8} entries={entryCount}");
                            for (int ei = 0; ei < entryCount; ei++)
                            {
                                int off = 4 + ei * 12;
                                uint idx    = (uint)(depRaw[off]   | depRaw[off+1]<<8 | depRaw[off+2]<<16 | depRaw[off+3]<<24);
                                uint fktid  = (uint)(depRaw[off+4] | depRaw[off+5]<<8 | depRaw[off+6]<<16 | depRaw[off+7]<<24);
                                allAssetsByKtid.TryGetValue(fktid, out var entryAsset);
                                AppLogger.Info($"    dep[{idx}] 0x{fktid:x8} = {entryAsset?.TypeExt ?? "??"}");
                                if (entryAsset?.TypeExt == ".g1t")
                                    depG1tMap.TryAdd(idx, entryAsset);
                            }
                        }

                        // 가장 많은 g1tSlot을 커버하는 G1T 선택
                        if (depG1tMap.Count > 0)
                        {
                            AppLogger.Info($"  dep G1T 파일 {depG1tMap.Count}개 발견");
                            linkedG1tVm = depG1tMap.Values.First();
                        }
                        else
                        {
                            AppLogger.Warn("  dep에서 G1T 엔트리 미발견 (모두 ??타입)");
                        }
                    }
                    else
                    {
                        AppLogger.Warn($"  → dep 0x{depKtid:x8} RDB에 없음");
                    }
                }

                // 연결 G1T 우선, 없으면 컨테이너 내 첫 번째 실제 G1T를 폴백
                var orderedG1t = g1tAssets.OrderBy(a => a.Record.FdataOffset).ToList();
                var fallbackG1tVm = orderedG1t.FirstOrDefault(g => g.Record.FileSize > 1000);
                var g1tVmToUse = linkedG1tVm ?? fallbackG1tVm;
                string g1tContainerToUse = linkedG1tVm?.Container ?? container;

                AppLogger.Info($"  G1T 후보(컨테이너): {orderedG1t.Count}개, 사용G1T={g1tVmToUse?.KtidHex ?? "없음"}" +
                               $"{(linkedG1tVm != null ? " [dep-resolved]" : fallbackG1tVm != null ? " [fallback]" : "")}");

                if (g1tVmToUse != null && !ct.IsCancellationRequested)
                {
                    byte[] g1tRaw = [];
                    try { g1tRaw = _extractor.ExtractToMemory(g1tVmToUse.Record, g1tContainerToUse); }
                    catch (Exception ex) { AppLogger.Exception($"G1T 추출 실패 {g1tVmToUse.KtidHex}", ex); }

                    if (g1tRaw.Length >= 8 && g1tRaw[0] == 'G' && g1tRaw[1] == 'T')
                    {
                        G1TFileInfo g1tInfo = G1tDecoder.Survey(g1tRaw);
                        AppLogger.Info($"  G1T Survey: texCount={g1tInfo.TexCount} ver={g1tInfo.Version} parsedSlots={g1tInfo.Textures.Length}");
                        foreach (var ti in g1tInfo.Textures)
                            AppLogger.Info($"    slot{ti.Slot}: {ti.FmtName} {ti.Width}×{ti.Height} mips={ti.MipCount} extSize={ti.ExtSize}");
                        // G1T 오프셋 테이블 덤프 (stride 진단용, 최대 12 엔트리 × 8바이트)
                        {
                            uint tb = g1tRaw.Length >= 0x10
                                ? (uint)(g1tRaw[0x0C]|g1tRaw[0x0D]<<8|g1tRaw[0x0E]<<16|g1tRaw[0x0F]<<24)
                                : 0u;
                            int tblStart = (int)tb;
                            int showEntries = Math.Min(12, (int)g1tInfo.TexCount);
                            int showBytes   = Math.Min(showEntries * 8, g1tRaw.Length - tblStart);
                            if (tblStart + showBytes <= g1tRaw.Length && showBytes > 0)
                            {
                                var sbT = new System.Text.StringBuilder();
                                for (int i = 0; i < showBytes; i++)
                                {
                                    if (i % 8 == 0) sbT.Append($"\n    [{i/8}]: ");
                                    sbT.Append($"{g1tRaw[tblStart+i]:x2} ");
                                }
                                AppLogger.Info($"  G1T offsetTable @0x{tblStart:x} (tableBase={tb}):{sbT}");
                            }
                        }

                        List<(int Slot, Image<Rgba32> Image)> allDecoded = [];
                        try { allDecoded = G1tDecoder.DecodeAll(g1tRaw); }
                        catch (Exception ex) { AppLogger.Exception("G1tDecoder.DecodeAll", ex); }

                        // slot → image 사전 (null = 이미 소비됨)
                        var slotDict = allDecoded
                            .Where(t => t.Image != null)
                            .ToDictionary(t => t.Slot, t => t.Image);
                        AppLogger.Info($"  G1T 0x{g1tVmToUse.Record.FileKtid:x8}: {slotDict.Count}개 슬롯 디코딩됨");

                        // matIdx → g1tSlot 매핑 결정
                        IEnumerable<(int matIdx, int g1tSlot)> assignments;
                        if (model.MaterialTextures.Count > 0)
                        {
                            // sec 0x10002 COLOR 텍스처 인덱스 사용
                            assignments = model.MaterialTextures.Select(x => (x.MatIdx, x.G1tSlot));
                        }
                        else
                        {
                            // 폴백: 순차 슬롯 (matIdx → slot matIdx)
                            var uniqueMats = model.Submeshes
                                .Select(s => s.MaterialIndex).Distinct().OrderBy(x => x);
                            assignments = uniqueMats.Select(m => (m, m));
                        }

                        foreach (var (matIdx, g1tSlot) in assignments)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (!slotDict.TryGetValue(g1tSlot, out var img) || img == null) continue;

                            slotDict[g1tSlot] = null!; // 소비됨 표시
                            using (img)
                            {
                                var bmp = ImageSharpToBitmapSource(img);
                                bmp.Freeze();
                                textures[matIdx] = bmp;
                                AppLogger.Info($"    matIdx={matIdx} → slot={g1tSlot} {img.Width}×{img.Height}");
                            }
                        }

                        // 미사용 이미지 해제
                        foreach (var img in slotDict.Values.Where(v => v != null))
                            img!.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                errorMsg = $"G1M 오류: {ex.Message}";
                AppLogger.Exception($"ModelPreview {vm.KtidHex}", ex);
            }
        }, ct);

        if (ct.IsCancellationRequested) return;

        if (errorMsg is not null)
        {
            StatusText = errorMsg;
            return;
        }

        if (model is not null)
        {
            int totalVerts = model.Submeshes.Sum(s => s.Positions.Length);
            int totalTris  = model.Submeshes.Sum(s => s.Indices.Length / 3);
            _modelTextures = textures;               // set before CurrentG1mData so view can read it
            CurrentG1mData = model;
            PreviewInfo  = $"{vm.KtidHex}  |  {model.Bones.Length} bones  |  {model.Submeshes.Length} submeshes  |  {totalVerts:N0} verts  |  {totalTris:N0} tris  |  {textures.Count} textures";
            StatusText   = $"{vm.KtidHex} 모델 로드 완료";
        }
    }

    // ── Texture preview ───────────────────────────────────────────────────────

    private void ClearSlots()
    {
        _slotBitmaps   = [];
        SlotCount      = 0;
        CurrentSlotIndex = 0;
        PreviewImage   = null;
    }

    private async Task LoadTexturePreviewAsync(AssetItemViewModel? vm, CancellationToken ct = default)
    {
        ClearSlots();
        if (vm is null) { PreviewInfo = string.Empty; return; }
        if (_extractor is null) return;

        StatusText = $"텍스처 로딩 중: {vm.KtidHex}";
        string container = vm.Container;
        var    record    = vm.Record;

        string? errorMsg = null;

        await Task.Run(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;
                AppLogger.Info($"Preview: {vm.KtidHex} container={container}");

                byte[] raw = _extractor.ExtractToMemory(record, container);
                if (raw.Length == 0)
                {
                    errorMsg = $"추출 실패 — 로그: {AppLogger.LogPath}";
                    return;
                }

                // Check G1T magic
                if (raw.Length < 8 || raw[0] != 'G' || raw[1] != 'T' || raw[2] != '1' || raw[3] != 'G')
                {
                    errorMsg = $"G1T 매직 불일치: [{raw[0]:x2} {raw[1]:x2} {raw[2]:x2} {raw[3]:x2}] ({raw.Length}B)";
                    AppLogger.Error(errorMsg);
                    return;
                }

                G1TFileInfo info = G1tDecoder.Survey(raw);
                AppLogger.Info($"  G1T: ver={info.Version} texCount={info.TexCount} valid={info.Valid}");
                foreach (var t in info.Textures)
                    AppLogger.Info($"    slot{t.Slot}: fmt=0x{t.FmtCode:x2}({t.FmtName}) {t.Width}×{t.Height} mips={t.MipCount} extSize={t.ExtSize}");

                var textures = G1tDecoder.DecodeAll(raw);
                if (textures.Count == 0)
                {
                    string texSummary = string.Join(", ", info.Textures.Select(t => $"slot{t.Slot}=0x{t.FmtCode:x2}"));
                    errorMsg = $"디코딩 결과 없음 — 로그: {AppLogger.LogPath}";
                    AppLogger.Error($"DecodeAll returned 0 textures. slots=[{texSummary}]");
                    return;
                }

                // Convert all decoded slots to frozen BitmapSources
                var decoded = new List<SlotBitmap>(textures.Count);
                foreach (var (slot, img) in textures)
                {
                    using (img)
                    {
                        var bmp = ImageSharpToBitmapSource(img);
                        bmp.Freeze();
                        string fmtName = info.Textures.FirstOrDefault(t => t.Slot == slot)?.FmtName ?? "?";
                        string infoStr = $"Slot {slot}  {img.Width}×{img.Height}  {fmtName}";
                        decoded.Add(new SlotBitmap(slot, bmp, infoStr));
                        AppLogger.Info($"  → decoded slot{slot} {img.Width}×{img.Height} {fmtName}");
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _slotBitmaps     = decoded;
                    SlotCount        = decoded.Count;
                    CurrentSlotIndex = 0;
                    PreviewImage     = decoded[0].Bmp;
                    PreviewInfo      = decoded[0].Info;
                    StatusText       = $"{vm.KtidHex} 로드 완료 ({decoded.Count}개 슬롯)";
                });
            }
            catch (Exception ex)
            {
                errorMsg = $"텍스처 오류: {ex.GetType().Name}: {ex.Message}";
                AppLogger.Exception($"LoadTexturePreview {vm.KtidHex}", ex);
            }
        }, ct);

        if (ct.IsCancellationRequested) return;
        if (errorMsg is not null)
            StatusText = errorMsg;
    }

    // ── Filter ────────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        string text = FilterText.Trim().ToLowerInvariant();
        string type = SelectedTypeFilter;

        FilteredAssets = new ObservableCollection<AssetItemViewModel>(
            Assets.Where(a =>
            {
                bool typeOk = type == "전체" || a.TypeExt == type;
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
        TypeFilters        = new ObservableCollection<string>(new[] { "전체" }.Concat(types));
        SelectedTypeFilter = "전체";
    }

    // ── MTL diagnostic ───────────────────────────────────────────────────────

    private async Task LoadMtlDiagnosticAsync(AssetItemViewModel vm, CancellationToken ct)
    {
        if (_extractor is null) return;

        StatusText = $"MTL 분석 중: {vm.KtidHex}";
        string container = vm.Container;
        var    record    = vm.Record;

        // Capture G1T KTID set on UI thread
        var g1tKtids = (IReadOnlySet<uint>)Assets
            .Where(a => a.TypeExt == ".g1t")
            .Select(a => a.Record.FileKtid)
            .ToHashSet();

        await Task.Run(() =>
        {
            try
            {
                byte[] raw = _extractor.ExtractToMemory(record, container);
                if (raw.Length == 0) { AppLogger.Error($"MTL {vm.KtidHex}: 추출 실패"); return; }

                string magic = MtlReader.GetMagic(raw);
                AppLogger.Info($"MTL 0x{vm.Record.FileKtid:x8}: {raw.Length}B  magic=[{magic}]");

                // Hex dump first 256 bytes
                int dumpLen = Math.Min(256, raw.Length);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < dumpLen; i += 16)
                {
                    sb.Append($"  0x{i:x4}: ");
                    for (int j = i; j < Math.Min(i + 16, dumpLen); j++)
                        sb.Append($"{raw[j]:x2} ");
                    AppLogger.Info(sb.ToString());
                    sb.Clear();
                }

                uint[] found = MtlReader.ScanForTextureKtids(raw, g1tKtids);
                AppLogger.Info($"  G1T refs: {found.Length} found: {string.Join(", ", found.Select(k => $"0x{k:x8}"))}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    PreviewInfo = $"MTL  |  {raw.Length}B  |  magic={magic}  |  G1T refs={found.Length}";
                    StatusText  = $"MTL {vm.KtidHex} 분석 완료 — 로그: {AppLogger.LogPath}";
                });
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"MTL diagnostic {vm.KtidHex}", ex);
            }
        }, ct);
    }

    // ── Conversion ────────────────────────────────────────────────────────────

    private static BitmapSource ImageSharpToBitmapSource(SixLabors.ImageSharp.Image<Rgba32> img)
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
                    bgra[i*4+0] = row[x].B;
                    bgra[i*4+1] = row[x].G;
                    bgra[i*4+2] = row[x].R;
                    bgra[i*4+3] = row[x].A;
                }
            }
        });

        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
    }
}
