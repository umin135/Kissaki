using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using KissakiViewer.Core.Formats;
using KissakiViewer.Core.Rendering;
using KissakiViewer.ViewModels;
using OpenTK.Wpf;
using SixLabors.ImageSharp.Processing;

namespace KissakiViewer.Windows;

public partial class AssetViewerWindow : Window
{
    private readonly AssetViewerViewModel _vm;

    // ── OpenGL renderer ────────────────────────────────────────────────────────
    private GlMeshRenderer? _renderer;
    private Vector3D        _prevLookDir;

    // ── Tab / selection state ─────────────────────────────────────────────────
    private AssetTabItem? _subscribedTab;
    private int  _selectedSubmesh  = -1;
    private int  _selectedMaterial = -1;
    private bool _suppressSelection;

    // Per-submesh material index list: built in Rebuild3DScene, used by HighlightByMaterial
    private readonly List<int> _submeshMatIndices = [];

    // ── UV map viewer ─────────────────────────────────────────────────────────
    private readonly Dictionary<int, int>              _matUvLayers  = new();
    private readonly Dictionary<int, (float X, float Y)> _matUvTiling = new();

    private static readonly Color[] s_uvPalette =
    [
        Color.FromRgb(  0, 220, 220),
        Color.FromRgb(220, 220,   0),
        Color.FromRgb(  0, 210,  80),
        Color.FromRgb(220, 100,   0),
        Color.FromRgb(210,   0, 210),
        Color.FromRgb(255, 255, 255),
    ];

    private static readonly (string Label, Vector3D Dir, Color Col)[] s_gizmoAxes =
    {
        ("X", new Vector3D(1, 0, 0), Color.FromRgb(220, 65,  65)),
        ("Y", new Vector3D(0, 1, 0), Color.FromRgb( 90, 195, 75)),
        ("Z", new Vector3D(0, 0, 1), Color.FromRgb( 60, 130, 220)),
    };

    public AssetViewerWindow(
        FdataExtractor extractor,
        IReadOnlyDictionary<(string Rdb, ushort Fid), List<AssetItemViewModel>> allG1tByFid,
        IReadOnlyDictionary<uint,   AssetItemViewModel>       allAssetsByKtid,
        MasterDokCache? masterDokCache)
    {
        InitializeComponent();
        _vm = new AssetViewerViewModel(extractor, allG1tByFid, allAssetsByKtid, masterDokCache);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRenderGizmo;

        _renderer = new GlMeshRenderer();

        try
        {
            GlControl.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3 });
            GlControl.Render += OnGlRender;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[GL] OpenGL context init failed: {ex.Message}");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderGizmo;
        // Renderer disposal: GL context may still be valid at this point.
        // Resources are also freed by the driver when the process exits.
        try { _renderer?.Dispose(); } catch { }
        _renderer = null;
    }

    private void OnGlRender(TimeSpan delta)
    {
        int w = (int)Math.Max(1, GlControl.ActualWidth);
        int h = (int)Math.Max(1, GlControl.ActualHeight);
        _renderer?.Render(w, h);
    }

    // ── Gizmo: update per composition frame ───────────────────────────────────

    private void OnRenderGizmo(object? sender, EventArgs e)
    {
        // Drive continuous GL rendering from the WPF composition loop
        GlControl.InvalidateVisual();

        if (_renderer == null || _vm.SelectedTab?.PreviewMode != PreviewMode.Model) return;
        var look = _renderer.LookWpf;
        if (look != _prevLookDir)
        {
            _prevLookDir = look;
            DrawGizmo(look, _renderer.UpWpf);
        }
    }

    private void DrawGizmo(Vector3D look, Vector3D up)
    {
        GizmoCanvas.Children.Clear();

        look.Normalize();
        var right   = Vector3D.CrossProduct(look, up); right.Normalize();
        var upOrtho = Vector3D.CrossProduct(right, look); upOrtho.Normalize();

        const double CX = 45, CY = 45, ALEN = 30, R_POS = 10, R_NEG = 7;

        GizmoCanvas.Children.Add(new Ellipse
        {
            Width = 90, Height = 90,
            Fill = new SolidColorBrush(Color.FromArgb(150, 18, 18, 20)),
        });

        var pts = new List<(double x, double y, double depth, bool isPos, int axis)>(6);
        for (int i = 0; i < s_gizmoAxes.Length; i++)
        {
            var d = s_gizmoAxes[i].Dir;
            double sx    = Vector3D.DotProduct(d, right)    * ALEN;
            double sy    = -Vector3D.DotProduct(d, upOrtho) * ALEN;
            double depth = Vector3D.DotProduct(d, look);
            pts.Add((CX + sx, CY + sy,  depth, true,  i));
            pts.Add((CX - sx, CY - sy, -depth, false, i));
        }
        pts.Sort((a, b) => b.depth.CompareTo(a.depth));

        foreach (var (_, dir, col) in s_gizmoAxes)
        {
            double sx = Vector3D.DotProduct(dir, right)    * ALEN;
            double sy = -Vector3D.DotProduct(dir, upOrtho) * ALEN;
            GizmoCanvas.Children.Add(new Line
            {
                X1 = CX, Y1 = CY, X2 = CX + sx, Y2 = CY + sy,
                StrokeThickness = 1.8,
                Stroke = new SolidColorBrush(col),
            });
        }

        foreach (var (x, y, _, isPos, axisIdx) in pts)
        {
            var (label, _, col) = s_gizmoAxes[axisIdx];
            double r      = isPos ? R_POS : R_NEG;
            var fillColor = isPos ? col : Color.FromArgb(180, col.R, col.G, col.B);

            var circle = new Ellipse
            {
                Width  = r * 2, Height = r * 2,
                Fill   = new SolidColorBrush(fillColor),
                Effect = isPos ? new DropShadowEffect
                {
                    Color = col, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.45,
                } : null,
            };
            Canvas.SetLeft(circle, x - r);
            Canvas.SetTop (circle, y - r);
            GizmoCanvas.Children.Add(circle);

            if (isPos)
            {
                circle.IsHitTestVisible = false;
                var tb = new TextBlock
                {
                    Text            = label,
                    FontSize        = 8.5,
                    FontWeight      = FontWeights.Bold,
                    Foreground      = Brushes.White,
                    IsHitTestVisible = false,
                };
                tb.Measure(new Size(20, 20));
                Canvas.SetLeft(tb, x - tb.DesiredSize.Width  / 2);
                Canvas.SetTop (tb, y - tb.DesiredSize.Height / 2);
                GizmoCanvas.Children.Add(tb);
            }
        }

        var dot = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White, IsHitTestVisible = false };
        Canvas.SetLeft(dot, CX - 2);
        Canvas.SetTop (dot, CY - 2);
        GizmoCanvas.Children.Add(dot);
    }

    // ── Public entry point ────────────────────────────────────────────────────

    public void OpenAsset(AssetItemViewModel vm) => _vm.OpenAsset(vm);

    // ── Tab / VM property change wiring ──────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssetViewerViewModel.SelectedTab)) return;

        if (_subscribedTab != null && _subscribedTab != _vm.SelectedTab)
        {
            _subscribedTab.PropertyChanged -= OnTabPropertyChanged;
            UnsubscribeItemVisibility(_subscribedTab);
            _subscribedTab = null;
        }

        var tab = _vm.SelectedTab;
        if (tab == null) { ClearScene(); return; }

        if (_subscribedTab != tab)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            _subscribedTab = tab;
        }

        if (tab.G1mData != null)
            Rebuild3DScene(tab.G1mData, tab.ModelTextures);
        else if (!tab.IsLoading)
            ClearScene();
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not AssetTabItem tab || tab != _vm.SelectedTab) return;

        if (e.PropertyName == nameof(AssetTabItem.G1mData))
        {
            if (tab.G1mData != null) Rebuild3DScene(tab.G1mData, tab.ModelTextures);
            else                     ClearScene();
        }
        else if (e.PropertyName == nameof(AssetTabItem.ShowBones))
        {
            RebuildBoneOverlay(tab.G1mData, tab.ShowBones);
        }
        else if (e.PropertyName == nameof(AssetTabItem.SelectedLodGroup))
        {
            if (tab.G1mData?.LodGroupCount > 1)
                ApplyLodFilter(tab.SelectedLodGroup);
        }
    }

    private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AssetTabItem tab)
            _vm.SelectedTab = tab;
    }

    private void SubTab_Visual_Click(object sender, MouseButtonEventArgs e)
        => _vm.SwitchToVisualCommand.Execute(null);

    private void SubTab_Metadata_Click(object sender, MouseButtonEventArgs e)
        => _vm.SwitchToMetadataCommand.Execute(null);

    private void DetailScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    // ── Detail panel: selection handlers ────────────────────────────────────

    private void OnSubmeshSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (_selectedMaterial >= 0) { HighlightByMaterial(_selectedMaterial, false); _selectedMaterial = -1; }
        if (_selectedSubmesh  >= 0) { HighlightSubmesh(_selectedSubmesh,  false);     _selectedSubmesh  = -1; }

        _suppressSelection = true;
        MaterialListBox.SelectedItem = null;
        _suppressSelection = false;

        var item = SubmeshListBox.SelectedItem as SubmeshItemVM;
        _selectedSubmesh = item?.Index ?? -1;
        if (_selectedSubmesh >= 0) HighlightSubmesh(_selectedSubmesh, true);
        DrawUvMap(-1, _selectedSubmesh);
    }

    private void OnMaterialSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        if (_selectedSubmesh  >= 0) { HighlightSubmesh(_selectedSubmesh, false);     _selectedSubmesh  = -1; }
        if (_selectedMaterial >= 0) { HighlightByMaterial(_selectedMaterial, false); _selectedMaterial = -1; }

        _suppressSelection = true;
        SubmeshListBox.SelectedItem = null;
        _suppressSelection = false;

        var item = MaterialListBox.SelectedItem as MaterialSlotItemVM;
        _selectedMaterial = item?.Index ?? -1;
        if (_selectedMaterial >= 0) HighlightByMaterial(_selectedMaterial, true);
        DrawUvMap(_selectedMaterial);
    }

    // ── Item visibility event ─────────────────────────────────────────────────

    private void OnSubmeshItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is SubmeshItemVM item && e.PropertyName == nameof(SubmeshItemVM.IsVisible))
            SetSubmeshVisible(item.Index, item.IsVisible);
    }

    private void SubscribeItemVisibility(AssetTabItem tab)
    {
        foreach (var item in tab.SubmeshItems)
            item.PropertyChanged += OnSubmeshItemPropertyChanged;
    }

    private void UnsubscribeItemVisibility(AssetTabItem tab)
    {
        foreach (var item in tab.SubmeshItems)
            item.PropertyChanged -= OnSubmeshItemPropertyChanged;
    }

    // ── 3D scene: visibility and highlight ───────────────────────────────────

    private void SetSubmeshVisible(int idx, bool visible) => _renderer?.SetSubmeshVisible(idx, visible);

    private void HighlightSubmesh(int idx, bool highlight)
        => _renderer?.SetHighlight(highlight ? new[] { idx } : null);

    private void HighlightByMaterial(int matIdx, bool highlight)
    {
        if (!highlight) { _renderer?.SetHighlight(null); return; }
        var indices = Enumerable.Range(0, _submeshMatIndices.Count)
            .Where(i => _submeshMatIndices[i] == matIdx);
        _renderer?.SetHighlight(indices);
    }

    private void ApplyLodFilter(int lodGroup)
    {
        var tab = _vm.SelectedTab;
        bool hasLods = tab?.G1mData?.LodGroupCount > 1;
        _renderer?.ApplyLodFilter(lodGroup, hasLods);
    }

    // ── Bone overlay ──────────────────────────────────────────────────────────

    private void RebuildBoneOverlay(G1mData? model, bool showBones)
        => _renderer?.SetBoneOverlay(model, showBones);

    // ── 3D Scene ─────────────────────────────────────────────────────────────

    private void ClearScene()
    {
        if (_subscribedTab != null) UnsubscribeItemVisibility(_subscribedTab);
        _submeshMatIndices.Clear();
        _selectedSubmesh  = -1;
        _selectedMaterial = -1;

        _suppressSelection = true;
        if (SubmeshListBox  != null) SubmeshListBox.SelectedItem  = null;
        if (MaterialListBox != null) MaterialListBox.SelectedItem = null;
        _suppressSelection = false;

        _renderer?.Clear();
        GizmoCanvas.Children.Clear();
        _matUvLayers.Clear();
        _matUvTiling.Clear();
        if (UvMapCanvas  != null) UvMapCanvas.Children.Clear();
        if (UvMapImage   != null) UvMapImage.Source = null;
    }

    private void Rebuild3DScene(G1mData model, IReadOnlyDictionary<int, BitmapSource> textures)
    {
        if (_subscribedTab != null) UnsubscribeItemVisibility(_subscribedTab);
        _submeshMatIndices.Clear();
        _selectedSubmesh  = -1;
        _selectedMaterial = -1;

        _suppressSelection = true;
        if (SubmeshListBox  != null) SubmeshListBox.SelectedItem  = null;
        if (MaterialListBox != null) MaterialListBox.SelectedItem = null;
        _suppressSelection = false;

        // Build UV channel lookup (used by UV map viewer)
        _matUvLayers.Clear();
        _matUvTiling.Clear();
        foreach (var (matIdx, _, uvLayer, texType, tileX, tileY) in model.MaterialTextures)
        {
            if (texType == 1)
            {
                _matUvLayers.TryAdd(matIdx, uvLayer);
                _matUvTiling.TryAdd(matIdx, (tileX > 0 ? tileX : 1f, tileY > 0 ? tileY : 1f));
            }
        }

        // Compute shell outline scale from model extents
        float shellScale = 0.01f;
        {
            float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
            float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
            foreach (var sm in model.Submeshes)
                foreach (var p in sm.Positions)
                {
                    if (p.X < mnX) mnX = p.X; if (p.X > mxX) mxX = p.X;
                    if (p.Y < mnY) mnY = p.Y; if (p.Y > mxY) mxY = p.Y;
                    if (p.Z < mnZ) mnZ = p.Z; if (p.Z > mxZ) mxZ = p.Z;
                }
            if (mnX < mxX)
            {
                float maxDim = Math.Max(Math.Max(mxX - mnX, mxY - mnY), mxZ - mnZ);
                shellScale = maxDim * 0.0025f;
            }
        }

        // Record per-submesh material indices for highlight-by-material
        foreach (var sm in model.Submeshes)
            _submeshMatIndices.Add(sm.MaterialIndex);

        // Queue upload to GPU (applied on next GL Render callback)
        _renderer?.LoadModel(model, textures, new Dictionary<int, int>(_matUvLayers), shellScale);

        var tab = _vm.SelectedTab;

        // Apply LOD filter immediately
        if (model.LodGroupCount > 1 && tab != null)
            _renderer?.ApplyLodFilter(tab.SelectedLodGroup, true);

        // Re-subscribe submesh visibility events
        if (tab != null)
        {
            _subscribedTab = tab;
            SubscribeItemVisibility(tab);
        }

        RebuildBoneOverlay(model, tab?.ShowBones ?? false);

        DrawGizmo(_renderer?.LookWpf ?? new Vector3D(0, 0, -1),
                  _renderer?.UpWpf   ?? new Vector3D(0, 1,  0));
    }

    // ── GLWpfControl mouse events ─────────────────────────────────────────────

    private void GlControl_MouseDown(object sender, MouseButtonEventArgs e)
    {
        GlControl.Focus();
        GlControl.CaptureMouse();
        bool left = e.ChangedButton == MouseButton.Left;
        var pos   = e.GetPosition(GlControl);
        _renderer?.MouseDown(left, (float)pos.X, (float)pos.Y);
        e.Handled = true;
    }

    private void GlControl_MouseUp(object sender, MouseButtonEventArgs e)
    {
        GlControl.ReleaseMouseCapture();
        bool left = e.ChangedButton == MouseButton.Left;
        _renderer?.MouseUp(left);
        e.Handled = true;
    }

    private void GlControl_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(GlControl);
        _renderer?.MouseMove((float)pos.X, (float)pos.Y);
        e.Handled = true;
    }

    private void GlControl_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _renderer?.MouseWheel(e.Delta);
        e.Handled = true;
    }

    // ── HDR loader (kept for future skybox support) ───────────────────────────

    private static BitmapSource? LoadAndPrepareHdr(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            while (ReadHdrLine(fs).Length > 0) { }
            var res  = ReadHdrLine(fs).Split(' ');
            int srcH = int.Parse(res[1]), srcW = int.Parse(res[3]);

            var rgbeRow  = new byte[srcW * 4];
            var floatRgb = new float[srcW * srcH * 3];
            for (int y = 0; y < srcH; y++)
            {
                ReadRgbeScanline(fs, rgbeRow, srcW);
                for (int x = 0; x < srcW; x++)
                {
                    int qi = x * 4, pi = (y * srcW + x) * 3;
                    int e  = rgbeRow[qi + 3];
                    if (e != 0)
                    {
                        float s = MathF.Pow(2f, e - 128) / 255f;
                        floatRgb[pi]     = rgbeRow[qi]     * s;
                        floatRgb[pi + 1] = rgbeRow[qi + 1] * s;
                        floatRgb[pi + 2] = rgbeRow[qi + 2] * s;
                    }
                }
            }

            const int OUT_W = 512, OUT_H = 256;
            float scaleX = (float)srcW / OUT_W, scaleY = (float)srcH / OUT_H;
            using var ldrImg = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(OUT_W, OUT_H);
            ldrImg.ProcessPixelRows(acc =>
            {
                for (int oy = 0; oy < OUT_H; oy++)
                {
                    var row = acc.GetRowSpan(oy);
                    int sy0 = (int)(oy * scaleY), sy1 = Math.Max(sy0 + 1, Math.Min((int)((oy + 1) * scaleY), srcH));
                    for (int ox = 0; ox < OUT_W; ox++)
                    {
                        int sx0 = (int)(ox * scaleX), sx1 = Math.Max(sx0 + 1, Math.Min((int)((ox + 1) * scaleX), srcW));
                        float r = 0, g = 0, b = 0; int cnt = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                            for (int sx = sx0; sx < sx1; sx++)
                            { int pi = (sy * srcW + sx) * 3; r += floatRgb[pi]; g += floatRgb[pi+1]; b += floatRgb[pi+2]; cnt++; }
                        if (cnt > 0) { r /= cnt; g /= cnt; b /= cnt; }
                        row[ox] = new SixLabors.ImageSharp.PixelFormats.Rgba32(
                            (byte)(r / (1 + r) * 255), (byte)(g / (1 + g) * 255), (byte)(b / (1 + b) * 255), 255);
                    }
                }
            });

            ldrImg.Mutate(ctx => ctx.GaussianBlur(14f));

            int stride = OUT_W * 4;
            var pixBuf = new byte[OUT_H * stride];
            ldrImg.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < OUT_H; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < OUT_W; x++)
                    { int off = y * stride + x * 4; pixBuf[off] = row[x].B; pixBuf[off+1] = row[x].G; pixBuf[off+2] = row[x].R; pixBuf[off+3] = 255; }
                }
            });
            var bmp = BitmapSource.Create(OUT_W, OUT_H, 96, 96, PixelFormats.Bgra32, null, pixBuf, stride);
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) { AppLogger.Warn($"[Skybox] HDR load failed: {ex.Message}"); return null; }
    }

    private static string ReadHdrLine(System.IO.Stream s)
    {
        var sb = new System.Text.StringBuilder();
        int c;
        while ((c = s.ReadByte()) > 0 && c != '\n')
            if (c != '\r') sb.Append((char)c);
        return sb.ToString();
    }

    private static void ReadRgbeScanline(System.IO.Stream s, byte[] buf, int w)
    {
        int b0 = s.ReadByte(), b1 = s.ReadByte(), b2 = s.ReadByte(), b3 = s.ReadByte();
        bool isNewRle = b0 == 0x02 && b1 == 0x02 && (b2 & 0x80) == 0 && ((b2 << 8) | b3) == w;
        if (isNewRle)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int i = 0;
                while (i < w)
                {
                    int code = s.ReadByte();
                    if (code > 128) { byte val = (byte)s.ReadByte(); int cnt = code - 128; while (cnt-- > 0 && i < w) buf[i++ * 4 + ch] = val; }
                    else            { while (code-- > 0 && i < w) buf[i++ * 4 + ch] = (byte)s.ReadByte(); }
                }
            }
        }
        else
        {
            buf[0] = (byte)b0; buf[1] = (byte)b1; buf[2] = (byte)b2; buf[3] = (byte)b3;
            int rem = (w - 1) * 4, off = 4;
            while (rem > 0) { int n = s.Read(buf, off, rem); if (n <= 0) break; off += n; rem -= n; }
        }
    }

    // ── UV Map viewer ─────────────────────────────────────────────────────────

    private void DrawUvMap(int matIdx, int smOnly = -1)
    {
        UvMapCanvas.Children.Clear();
        UvMapImage.Source = null;

        var tab = _vm.SelectedTab;
        if (tab?.G1mData == null) return;
        var model = tab.G1mData;
        const double sz = 202.0;

        int bgMat = matIdx >= 0 ? matIdx
            : smOnly >= 0 && smOnly < model.Submeshes.Length ? model.Submeshes[smOnly].MaterialIndex
            : -1;
        if (bgMat >= 0 && tab.ModelTextures.TryGetValue(bgMat, out var bgBmp))
            UvMapImage.Source = bgBmp;

        int ci = 0;
        for (int si = 0; si < model.Submeshes.Length; si++)
        {
            if (smOnly >= 0 && si != smOnly) continue;
            var sm = model.Submeshes[si];
            if (matIdx >= 0 && sm.MaterialIndex != matIdx) continue;

            int uvCh  = _matUvLayers.TryGetValue(sm.MaterialIndex, out var c) ? c : 0;
            var uvSrc = uvCh < sm.AllTexCoords.Length ? sm.AllTexCoords[uvCh] : sm.TexCoords;
            if (uvSrc.Length == 0 || sm.Indices.Length < 3) continue;

            int n     = uvSrc.Length;
            var col   = s_uvPalette[ci++ % s_uvPalette.Length];
            var brush = new SolidColorBrush(Color.FromArgb(204, col.R, col.G, col.B));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int fi = 0; fi + 2 < sm.Indices.Length; fi += 3)
                {
                    int i0 = sm.Indices[fi], i1 = sm.Indices[fi+1], i2 = sm.Indices[fi+2];
                    if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;
                    ctx.BeginFigure(new Point(uvSrc[i0].X * sz, uvSrc[i0].Y * sz), false, true);
                    ctx.LineTo(new Point(uvSrc[i1].X * sz, uvSrc[i1].Y * sz), true, false);
                    ctx.LineTo(new Point(uvSrc[i2].X * sz, uvSrc[i2].Y * sz), true, false);
                }
            }
            geo.Freeze();

            UvMapCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo, Stroke = brush, StrokeThickness = 0.5, Fill = null,
            });
        }
    }
}
