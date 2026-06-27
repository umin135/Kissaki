using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KissakiViewer.Core.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace KissakiViewer.ViewModels;

// ── Slot helper (mirrors what MainViewModel used to have) ─────────────────────
internal record SlotBitmap(int Slot, BitmapSource Bmp, string Info);

// ── Per-tab model ─────────────────────────────────────────────────────────────
public sealed partial class AssetTabItem : ObservableObject
{
    public AssetItemViewModel Asset { get; }
    public string Header => Asset.DisplayFileName;

    // ── Loading state ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isLoading  = true;
    [ObservableProperty] private string _statusText = "로딩 중...";
    [ObservableProperty] private bool   _isSelected;

    // ── Texture preview ────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleSlots))]
    [NotifyPropertyChangedFor(nameof(SlotMax))]
    private int _slotCount;

    [ObservableProperty] private int          _currentSlotIndex;
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string        _previewInfo = string.Empty;

    public bool HasMultipleSlots => SlotCount > 1;
    public int  SlotMax          => Math.Max(0, SlotCount - 1);

    internal List<SlotBitmap> SlotBitmaps { get; set; } = [];

    partial void OnCurrentSlotIndexChanged(int value)
    {
        if (value >= 0 && value < SlotBitmaps.Count)
        {
            PreviewImage = SlotBitmaps[value].Bmp;
            PreviewInfo  = SlotBitmaps[value].Info;
        }
    }

    // ── Model preview ──────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModelPreview))]
    private G1mData? _g1mData;

    public bool HasModelPreview => G1mData != null;

    private Dictionary<int, BitmapSource> _modelTextures = [];
    public  IReadOnlyDictionary<int, BitmapSource> ModelTextures => _modelTextures;
    internal void SetModelTextures(Dictionary<int, BitmapSource> tex) => _modelTextures = tex;

    public AssetTabItem(AssetItemViewModel asset) => Asset = asset;
}

// ── ViewModel ─────────────────────────────────────────────────────────────────
public sealed partial class AssetViewerViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTabs))]
    private ObservableCollection<AssetTabItem> _tabs = [];

    private AssetTabItem? _selectedTab;
    public AssetTabItem? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return;
            if (_selectedTab != null) _selectedTab.IsSelected = false;
            _selectedTab = value;
            if (_selectedTab != null) _selectedTab.IsSelected = true;
            OnPropertyChanged();
        }
    }

    public bool HasTabs => Tabs.Count > 0;

    private readonly FdataExtractor _extractor;
    private readonly IReadOnlyDictionary<ushort, List<AssetItemViewModel>> _allG1tByFid;
    private readonly IReadOnlyDictionary<uint,  AssetItemViewModel>        _allAssetsByKtid;
    // Func so that late-built kidsobjdb map (populated after asset load) is always current
    private readonly Func<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>> _getG1mMap;

    public AssetViewerViewModel(
        FdataExtractor extractor,
        IReadOnlyDictionary<ushort, List<AssetItemViewModel>> allG1tByFid,
        IReadOnlyDictionary<uint,   AssetItemViewModel>       allAssetsByKtid,
        Func<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>> getG1mMap)
    {
        _extractor       = extractor;
        _allG1tByFid     = allG1tByFid;
        _allAssetsByKtid = allAssetsByKtid;
        _getG1mMap       = getG1mMap;
        _tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    public void OpenAsset(AssetItemViewModel vm)
    {
        uint ktid = vm.Record.FileKtid;
        var existing = Tabs.FirstOrDefault(t => t.Asset.Record.FileKtid == ktid);
        if (existing != null) { SelectedTab = existing; return; }

        var tab = new AssetTabItem(vm);
        Tabs.Add(tab);
        SelectedTab = tab;
        _ = LoadTabAsync(tab);
    }

    [RelayCommand]
    private void CloseTab(AssetTabItem? tab)
    {
        if (tab == null) return;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Max(0, idx - 1)] : null;
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private async Task LoadTabAsync(AssetTabItem tab)
    {
        var ct = new CancellationToken(); // Future: track per-tab CTS
        try
        {
            string ext = tab.Asset.TypeExt;
            if (ext == ".g1t")
                await LoadTextureAsync(tab, ct);
            else if (ext == ".g1m")
                await LoadModelAsync(tab, ct);
            else
            {
                tab.StatusText = $"{ext} — 미리보기 미지원";
                tab.IsLoading  = false;
            }
        }
        catch (Exception ex)
        {
            tab.StatusText = $"오류: {ex.Message}";
            tab.IsLoading  = false;
        }
    }

    // ── Texture loading (mirrors MainViewModel.LoadTexturePreviewAsync) ────────

    private async Task LoadTextureAsync(AssetTabItem tab, CancellationToken ct)
    {
        var vm   = tab.Asset;
        var rec  = vm.Record;
        var cont = vm.Container;

        await Task.Run(() =>
        {
            byte[] raw = _extractor.ExtractToMemory(rec, cont);
            if (raw.Length == 0) { SetStatus(tab, "추출 실패"); return; }
            if (raw.Length < 8 || raw[0] != 'G' || raw[1] != 'T')
            { SetStatus(tab, "G1T 매직 불일치"); return; }

            var info     = G1tDecoder.Survey(raw);
            var textures = G1tDecoder.DecodeAll(raw);
            if (textures.Count == 0) { SetStatus(tab, "디코딩 실패"); return; }

            var slots = new List<SlotBitmap>(textures.Count);
            foreach (var (slot, img) in textures)
            {
                using (img)
                {
                    var bmp = MainViewModel.ImageSharpToBitmapSource(img);
                    bmp.Freeze();
                    string fmtName = info.Textures.FirstOrDefault(t => t.Slot == slot)?.FmtName ?? "?";
                    slots.Add(new SlotBitmap(slot, bmp, $"Slot {slot}  {img.Width}×{img.Height}  {fmtName}"));
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                tab.SlotBitmaps      = slots;
                tab.SlotCount        = slots.Count;
                tab.CurrentSlotIndex = 0;
                tab.PreviewImage     = slots[0].Bmp;
                tab.PreviewInfo      = slots[0].Info;
                tab.StatusText       = $"로드 완료 ({slots.Count}개 슬롯)";
                tab.IsLoading        = false;
            });
        }, ct);
    }

    // ── Model loading (mirrors MainViewModel.LoadModelPreviewAsync) ───────────

    private async Task LoadModelAsync(AssetTabItem tab, CancellationToken ct)
    {
        var vm   = tab.Asset;
        var rec  = vm.Record;
        var cont = vm.Container;

        // Priority 1: kidsobjdb mapping (most accurate — cross-container)
        var kidsG1ts = _getG1mMap().TryGetValue(rec.FileKtid, out var mapped)
            ? mapped.ToList()
            : null;

        // Priority 2: co-located G1T files in same fdata container
        var colocated = _allAssetsByKtid.Values
            .Where(a => a.Container == cont && a.TypeExt == ".g1t")
            .OrderBy(a => a.Record.FdataOffset)
            .ToList();

        G1mData?                 model    = null;
        Dictionary<int, BitmapSource> textures = [];

        await Task.Run(() =>
        {
            byte[] raw = _extractor.ExtractToMemory(rec, cont);
            if (raw.Length == 0 || raw[0] != '_' || raw[1] != 'M') { SetStatus(tab, "추출 실패"); return; }

            model = G1mReader.Read(raw);
            if (model == null) { SetStatus(tab, "G1M 파싱 실패"); return; }
            if (ct.IsCancellationRequested) return;

            // Resolve G1T: kidsobjdb map → co-located → proximity fallback
            List<AssetItemViewModel> g1tFileList =
                kidsG1ts is { Count: > 0 }  ? kidsG1ts  :
                colocated.Count > 0         ? colocated :
                ResolveG1tByProximity(rec.FdataId);

            if (g1tFileList.Count == 0) return;

            // Determine textures-per-file
            int texPerFile = 1;
            var g1tRawCache = new Dictionary<int, byte[]>();
            try
            {
                byte[] first = _extractor.ExtractToMemory(g1tFileList[0].Record, g1tFileList[0].Container);
                g1tRawCache[0] = first;
                var firstInfo = G1tDecoder.Survey(first);
                if (firstInfo.Version == "1600")
                {
                    int rc = firstInfo.Textures.Count(t => t.FmtCode != 0);
                    if (rc > 1) texPerFile = rc;
                }
            }
            catch { }

            IEnumerable<int> neededSlots = model.MaterialTextures.Count > 0
                ? model.MaterialTextures.Select(x => x.G1tSlot)
                : model.Submeshes.Select(s => s.MaterialIndex).Distinct();

            var distinctSlots = neededSlots.Distinct()
                .Where(s => s >= 0 && s / texPerFile < g1tFileList.Count)
                .OrderBy(s => s).ToList();
            if (distinctSlots.Count == 0) distinctSlots = [0];

            var slotDict = new Dictionary<int, SixLabors.ImageSharp.Image<Rgba32>>();

            foreach (var fileGroup in distinctSlots.GroupBy(s => s / texPerFile))
            {
                if (ct.IsCancellationRequested) break;
                int fileIdx = fileGroup.Key;
                var neededInternal = new Dictionary<int, int>();
                foreach (int gs in fileGroup) neededInternal.TryAdd(gs % texPerFile, gs);

                if (!g1tRawCache.TryGetValue(fileIdx, out byte[]? g1tRaw))
                {
                    var g1tVm = fileIdx < g1tFileList.Count ? g1tFileList[fileIdx] : null;
                    if (g1tVm is null) continue;
                    try { g1tRaw = _extractor.ExtractToMemory(g1tVm.Record, g1tVm.Container); }
                    catch { continue; }
                    g1tRawCache[fileIdx] = g1tRaw;
                }
                if (g1tRaw!.Length < 8 || g1tRaw[0] != 'G') continue;

                List<(int Slot, SixLabors.ImageSharp.Image<Rgba32> Image)> decoded;
                try { decoded = G1tDecoder.DecodeAll(g1tRaw); }
                catch { continue; }

                foreach (var (internalSlot, img) in decoded)
                {
                    if (img == null) continue;
                    if (neededInternal.TryGetValue(internalSlot, out int globalSlot))
                        slotDict[globalSlot] = img;
                    else
                        img.Dispose();
                }
            }

            // Build matIdx → BitmapSource
            IEnumerable<(int matIdx, int g1tSlot)> assignments = model.MaterialTextures.Count > 0
                ? model.MaterialTextures.Select(x => (x.MatIdx, x.G1tSlot))
                : model.Submeshes.Select(s => (s.MaterialIndex, s.MaterialIndex)).Distinct();

            var texBitmaps = new Dictionary<int, BitmapSource>();
            foreach (var (matIdx, g1tSlot) in assignments)
            {
                if (!slotDict.TryGetValue(g1tSlot, out var img) || img == null) continue;
                slotDict[g1tSlot] = null!;
                using (img)
                {
                    var bmp = MainViewModel.ImageSharpToBitmapSource(img);
                    bmp.Freeze();
                    texBitmaps[matIdx] = bmp;
                }
            }
            foreach (var img in slotDict.Values.Where(v => v != null)) img!.Dispose();

            textures = texBitmaps;
        }, ct);

        if (ct.IsCancellationRequested || model == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            tab.SetModelTextures(textures);
            tab.G1mData    = model;
            tab.IsLoading  = false;
            int v = model.Submeshes.Sum(s => s.Positions.Length);
            int t = model.Submeshes.Sum(s => s.Indices.Length / 3);
            tab.StatusText = $"{vm.KtidHex} | {model.Bones.Length} bones | {model.Submeshes.Length} submeshes | {v:N0} verts | {textures.Count} textures";
            tab.PreviewInfo = tab.StatusText;
        });
    }

    private List<AssetItemViewModel> ResolveG1tByProximity(ushort g1mFid)
    {
        if (_allG1tByFid.Count == 0) return [];
        ushort nearestFid = 0;
        int nearestDelta  = int.MaxValue;
        foreach (var fid in _allG1tByFid.Keys)
        {
            int d = Math.Abs((int)fid - (int)g1mFid);
            if (d < nearestDelta) { nearestDelta = d; nearestFid = fid; }
        }
        return nearestDelta < int.MaxValue && _allG1tByFid.TryGetValue(nearestFid, out var list)
            ? list
            : [];
    }

    private static void SetStatus(AssetTabItem tab, string msg)
        => Application.Current.Dispatcher.Invoke(() => { tab.StatusText = msg; tab.IsLoading = false; });
}
