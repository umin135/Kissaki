using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using HelixToolkit.Wpf;
using KissakiViewer.Core.Formats;
using KissakiViewer.ViewModels;
using SixLabors.ImageSharp.Processing;

namespace KissakiViewer.Windows;

public partial class AssetViewerWindow : Window
{
    private readonly AssetViewerViewModel _vm;

    // ── Gizmo / skybox state ─────────────────────────────────────────────────
    private Rect3D    _lastBounds = Rect3D.Empty;
    private Vector3D  _prevLookDir;
    private BitmapSource? _skyboxTex;

    // ── 3D scene tracking ─────────────────────────────────────────────────────
    // Per-submesh: (geometry, front material, _, material index)  — BackMaterial always null (backface culled)
    private readonly List<(GeometryModel3D? Geo, Material? FrontMat, Material? BackMat, int MatIdx)> _submeshGeos = [];
    private AssetTabItem? _subscribedTab;  // tab whose items we hold PropertyChanged refs for
    private int  _selectedSubmesh  = -1;
    private int  _selectedMaterial = -1;
    private bool _suppressSelection;

    // ── UV map viewer ─────────────────────────────────────────────────────────
    private readonly Dictionary<int, int>              _matUvLayers  = new();
    private readonly Dictionary<int, (float X, float Y)> _matUvTiling = new();

    private static readonly Color[] s_uvPalette =
    [
        Color.FromRgb(  0, 220, 220),  // cyan
        Color.FromRgb(220, 220,   0),  // yellow
        Color.FromRgb(  0, 210,  80),  // green
        Color.FromRgb(220, 100,   0),  // orange
        Color.FromRgb(210,   0, 210),  // magenta
        Color.FromRgb(255, 255, 255),  // white
    ];

    // Highlight material: translucent cyan emissive
    private static readonly Material s_highlightMat = new DiffuseMaterial(
        new SolidColorBrush(Color.FromArgb(255, 30, 200, 255)) { Opacity = 0.85 });

    private static readonly (string Label, Vector3D Dir, Color Col)[] s_gizmoAxes =
    {
        ("X", new Vector3D(1, 0, 0), Color.FromRgb(220, 65,  65)),
        ("Y", new Vector3D(0, 1, 0), Color.FromRgb( 90, 195, 75)),
        ("Z", new Vector3D(0, 0, 1), Color.FromRgb( 60, 130, 220)),
    };

    public AssetViewerWindow(
        FdataExtractor extractor,
        IReadOnlyDictionary<ushort, List<AssetItemViewModel>> allG1tByFid,
        IReadOnlyDictionary<uint,   AssetItemViewModel>       allAssetsByKtid,
        Func<IReadOnlyDictionary<uint, IReadOnlyList<AssetItemViewModel>>> getG1mMap)
    {
        InitializeComponent();
        _vm = new AssetViewerViewModel(extractor, allG1tByFid, allAssetsByKtid, getG1mMap);
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnRenderGizmo;
            _skyboxTex = LoadAndPrepareHdr(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images", "defaultHDR.hdr"));
            BuildSkybox(_skyboxTex); // null → fallback dark color
            BuildWorldAxes(50);
            // Hide cursor during right-click rotation (SizeAll is the default and distracting)
            Viewport3D.CameraController.RotateCursor = Cursors.None;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRenderGizmo;
    }

    // ── Gizmo: per-frame update ───────────────────────────────────────────────

    private void OnRenderGizmo(object? sender, EventArgs e)
    {
        if (Viewport3D.Camera is ProjectionCamera cam && cam.LookDirection != _prevLookDir)
        {
            _prevLookDir = cam.LookDirection;
            DrawGizmo();
        }
    }

    private void DrawGizmo()
    {
        GizmoCanvas.Children.Clear();
        if (Viewport3D.Camera is not ProjectionCamera cam) return;

        var look = cam.LookDirection; look.Normalize();
        var up   = cam.UpDirection;
        var right   = Vector3D.CrossProduct(look, up); right.Normalize();
        var upOrtho = Vector3D.CrossProduct(right, look); upOrtho.Normalize();

        const double CX = 45, CY = 45, ALEN = 30, R_POS = 10, R_NEG = 7;

        // Background disc
        GizmoCanvas.Children.Add(new Ellipse
        {
            Width = 90, Height = 90,
            Fill = new SolidColorBrush(Color.FromArgb(150, 18, 18, 20)),
        });

        // Collect all 6 endpoints and sort back-to-front for correct overdraw
        var pts = new List<(double x, double y, double depth, bool isPos, int axis)>(6);
        for (int i = 0; i < s_gizmoAxes.Length; i++)
        {
            var d = s_gizmoAxes[i].Dir;
            double sx    = Vector3D.DotProduct(d, right)   * ALEN;
            double sy    = -Vector3D.DotProduct(d, upOrtho) * ALEN;
            double depth = Vector3D.DotProduct(d, look);
            pts.Add((CX + sx, CY + sy,  depth, true,  i));
            pts.Add((CX - sx, CY - sy, -depth, false, i));
        }
        pts.Sort((a, b) => b.depth.CompareTo(a.depth));

        // Axis lines
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

        // Circles in depth order
        foreach (var (x, y, _, isPos, axisIdx) in pts)
        {
            var (label, dir, col) = s_gizmoAxes[axisIdx];
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
            System.Windows.Controls.Canvas.SetLeft(circle, x - r);
            System.Windows.Controls.Canvas.SetTop (circle, y - r);
            GizmoCanvas.Children.Add(circle);

            if (isPos)
            {
                circle.IsHitTestVisible = false;
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text            = label,
                    FontSize        = 8.5,
                    FontWeight      = FontWeights.Bold,
                    Foreground      = Brushes.White,
                    IsHitTestVisible = false,
                };
                tb.Measure(new System.Windows.Size(20, 20));
                System.Windows.Controls.Canvas.SetLeft(tb, x - tb.DesiredSize.Width  / 2);
                System.Windows.Controls.Canvas.SetTop (tb, y - tb.DesiredSize.Height / 2);
                GizmoCanvas.Children.Add(tb);
            }
        }

        // Centre dot
        var dot = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White, IsHitTestVisible = false };
        System.Windows.Controls.Canvas.SetLeft(dot, CX - 2);
        System.Windows.Controls.Canvas.SetTop (dot, CY - 2);
        GizmoCanvas.Children.Add(dot);
    }

    public void OpenAsset(AssetItemViewModel vm)
    {
        _vm.OpenAsset(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssetViewerViewModel.SelectedTab)) return;

        // Detach from previous tab (if different from the newly subscribed one)
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
            if (tab.G1mData != null)
                Rebuild3DScene(tab.G1mData, tab.ModelTextures);
            else
                ClearScene();
        }
        else if (e.PropertyName == nameof(AssetTabItem.ShowBones))
        {
            RebuildBoneOverlay(tab.G1mData, tab.ShowBones);
        }
    }

    private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AssetTabItem tab)
            _vm.SelectedTab = tab;
    }

    // ── Detail panel: selection handlers ─────────────────────────────────────

    private void OnSubmeshSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        // Undo previous material highlight
        if (_selectedMaterial >= 0) { HighlightByMaterial(_selectedMaterial, false); _selectedMaterial = -1; }
        if (_selectedSubmesh  >= 0) { HighlightSubmesh(_selectedSubmesh,  false); _selectedSubmesh  = -1; }

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

        // Undo previous highlights
        if (_selectedSubmesh  >= 0) { HighlightSubmesh(_selectedSubmesh, false);      _selectedSubmesh  = -1; }
        if (_selectedMaterial >= 0) { HighlightByMaterial(_selectedMaterial, false);  _selectedMaterial = -1; }

        _suppressSelection = true;
        SubmeshListBox.SelectedItem = null;
        _suppressSelection = false;

        var item = MaterialListBox.SelectedItem as MaterialSlotItemVM;
        _selectedMaterial = item?.Index ?? -1;
        if (_selectedMaterial >= 0) HighlightByMaterial(_selectedMaterial, true);
        DrawUvMap(_selectedMaterial);
    }

    // ── Detail panel: item visibility event ──────────────────────────────────

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

    // ── 3D scene helpers: visibility and highlight ────────────────────────────

    private void SetSubmeshVisible(int idx, bool visible)
    {
        if ((uint)idx >= (uint)_submeshGeos.Count) return;
        var (geo, frontMat, _, _) = _submeshGeos[idx];
        if (geo == null) return;
        bool highlighted = idx == _selectedSubmesh;
        geo.Material = visible ? (highlighted ? s_highlightMat : frontMat) : null;
    }

    private void HighlightSubmesh(int idx, bool highlight)
    {
        if ((uint)idx >= (uint)_submeshGeos.Count) return;
        var (geo, frontMat, _, _) = _submeshGeos[idx];
        if (geo == null) return;
        bool visible = geo.Material != null || !highlight;
        if (!visible && !highlight) return;
        geo.Material = highlight ? s_highlightMat : frontMat;
    }

    private void HighlightByMaterial(int matIdx, bool highlight)
    {
        for (int i = 0; i < _submeshGeos.Count; i++)
        {
            var (geo, frontMat, _, m) = _submeshGeos[i];
            if (geo == null || m != matIdx) continue;
            geo.Material = highlight ? s_highlightMat : frontMat;
        }
    }

    // ── Bone overlay ──────────────────────────────────────────────────────────

    private void RebuildBoneOverlay(G1mData? model, bool showBones)
    {
        BoneRoot.Children.Clear();
        if (!showBones || model == null || model.Bones.Length == 0) return;

        // Bone connection lines
        var linePoints = new Point3DCollection();
        foreach (var bone in model.Bones)
        {
            if (bone.ParentIndex >= 0 && bone.ParentIndex < model.Bones.Length)
            {
                linePoints.Add(ToWpf(model.Bones[bone.ParentIndex].WorldPosition));
                linePoints.Add(ToWpf(bone.WorldPosition));
            }
        }
        if (linePoints.Count > 0)
            BoneRoot.Children.Add(new LinesVisual3D
            {
                Points    = linePoints,
                Color     = Color.FromArgb(220, 255, 210, 30),
                Thickness = 1.5,
            });

        // Joint position points
        var jointPoints = new Point3DCollection(model.Bones.Length);
        foreach (var bone in model.Bones)
            jointPoints.Add(ToWpf(bone.WorldPosition));
        if (jointPoints.Count > 0)
            BoneRoot.Children.Add(new PointsVisual3D
            {
                Points = jointPoints,
                Color  = Color.FromArgb(255, 255, 90, 90),
                Size   = 5,
            });
    }

    // ── Skybox ────────────────────────────────────────────────────────────────

    private void BuildSkybox(BitmapSource? tex)
    {
        const double R = 8000;
        const int TD = 48, PD = 24;
        var pts = new Point3DCollection((PD + 1) * (TD + 1));
        var uvs = new PointCollection((PD + 1) * (TD + 1));
        var idx = new Int32Collection(PD * TD * 6);
        for (int pi = 0; pi <= PD; pi++)
        {
            double phi = Math.PI * pi / PD;
            double sp = Math.Sin(phi), cp = Math.Cos(phi);
            for (int ti = 0; ti <= TD; ti++)
            {
                double theta = 2 * Math.PI * ti / TD;
                pts.Add(new Point3D(sp * Math.Cos(theta) * R, cp * R, sp * Math.Sin(theta) * R));
                uvs.Add(new System.Windows.Point((double)ti / TD, (double)pi / PD));
            }
        }
        for (int pi = 0; pi < PD; pi++)
        {
            for (int ti = 0; ti < TD; ti++)
            {
                int p0 = pi * (TD + 1) + ti, p1 = p0 + 1, p2 = p0 + TD + 1, p3 = p2 + 1;
                // Standard winding; BackMaterial below makes interior faces visible
                idx.Add(p0); idx.Add(p1); idx.Add(p2);
                idx.Add(p1); idx.Add(p3); idx.Add(p2);
            }
        }
        var mesh = new MeshGeometry3D { Positions = pts, TextureCoordinates = uvs, TriangleIndices = idx };
        Brush brush = tex != null
            ? (Brush)new ImageBrush(tex)
            : new SolidColorBrush(Color.FromRgb(14, 14, 22));
        var mat = new EmissiveMaterial(brush);
        // BackMaterial = rendered when camera is inside the sphere (back face from exterior)
        SkyboxRoot.Content = new GeometryModel3D(mesh, null) { BackMaterial = mat };
    }

    // Minimal Radiance RGBE (.hdr) loader with Reinhard tone-map + blur
    private static BitmapSource? LoadAndPrepareHdr(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            // Skip header until blank line
            while (ReadHdrLine(fs).Length > 0) { }
            // Resolution string: "-Y H +X W"
            var res = ReadHdrLine(fs).Split(' ');
            int srcH = int.Parse(res[1]), srcW = int.Parse(res[3]);

            // Decode all scanlines to float RGB
            var rgbeRow  = new byte[srcW * 4];
            var floatRgb = new float[srcW * srcH * 3];
            for (int y = 0; y < srcH; y++)
            {
                ReadRgbeScanline(fs, rgbeRow, srcW);
                for (int x = 0; x < srcW; x++)
                {
                    int qi = x * 4, pi = (y * srcW + x) * 3;
                    int e = rgbeRow[qi + 3];
                    if (e != 0)
                    {
                        float s = MathF.Pow(2f, e - 128) / 255f;
                        floatRgb[pi]     = rgbeRow[qi]     * s;
                        floatRgb[pi + 1] = rgbeRow[qi + 1] * s;
                        floatRgb[pi + 2] = rgbeRow[qi + 2] * s;
                    }
                }
            }

            // Box-filter downsample → Reinhard tone-map
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

            // Convert ImageSharp → WPF BitmapSource
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

    // ── World axis lines at origin ────────────────────────────────────────────

    private void BuildWorldAxes(double axisLen)
    {
        AxesRoot.Children.Clear();
        var axisColors = new (Color col, Vector3D dir)[]
        {
            (Color.FromRgb(220, 65,  65),  new Vector3D(1, 0, 0)),  // X red
            (Color.FromRgb( 90, 195, 75),  new Vector3D(0, 1, 0)),  // Y green
            (Color.FromRgb( 60, 130, 220), new Vector3D(0, 0, 1)),  // Z blue
        };
        foreach (var (col, dir) in axisColors)
        {
            AxesRoot.Children.Add(new LinesVisual3D
            {
                Color     = col,
                Thickness = 2.0,
                Points    = new Point3DCollection
                {
                    new Point3D(0, 0, 0),
                    new Point3D(dir.X * axisLen, dir.Y * axisLen, dir.Z * axisLen),
                },
            });
        }
    }

    // ── 3D Scene ──────────────────────────────────────────────────────────────

    // game X=lateral, game Y=up(height), game Z=forward(facing)
    // → WPF X=game X, WPF Z=game Y(up), WPF Y=-game Z(depth). det=+1 (orientation-preserving)
    private static Point3D  ToWpf(System.Numerics.Vector3 v) => new(v.X, -v.Z, v.Y);
    private static Vector3D ToWpfDir(System.Numerics.Vector3 v) => new(v.X, -v.Z, v.Y);

    private static readonly DiffuseMaterial s_fallbackMat =
        new(new SolidColorBrush(Color.FromRgb(180, 180, 190)));

    private static readonly SpecularMaterial s_specMat =
        new(new SolidColorBrush(Color.FromRgb(60, 60, 80)), 30);

    // Returns true if the Bgra32 BitmapSource contains any pixels with alpha < 250.
    // Samples every 16th pixel for speed; good enough for hair/cloth transparency detection.
    private static bool TextureHasTransparency(BitmapSource bmp)
    {
        if (bmp.Format != PixelFormats.Bgra32 && bmp.Format != PixelFormats.Pbgra32) return false;
        int stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 64) // alpha byte of every 16th BGRA pixel
            if (pixels[i] < 250) return true;
        return false;
    }

    private static Material MakeMeshMaterial(BitmapSource? tex)
    {
        var group = new MaterialGroup();
        if (tex != null)
        {
            // ViewportUnits=Absolute: UV (0,0)→(1,1) maps directly to the texture regardless
            // of each submesh's UV bounding box (the RelativeToBoundingBox default stretches
            // the texture to fit each submesh's UV extents, giving wrong texture regions).
            //
            // Always Opacity=1.0 (fully opaque from WPF's perspective): WPF's transparent pass
            // sorts by bounding sphere center, which is wrong for 50+ overlapping hair strands.
            // Per-pixel alpha from the ImageBrush still works, and the depth buffer handles
            // correct per-pixel ordering. Render order (opaque mat first, alpha-tex mat last)
            // ensures the hat/body renders before hair without needing the transparent pass.
            var ib = new ImageBrush(tex)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
            };
            var emit = new EmissiveMaterial(new ImageBrush(tex)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
            }) { Color = Color.FromRgb(200, 200, 200) };
            var diff = new DiffuseMaterial(ib);
            group.Children.Add(emit);
            group.Children.Add(diff);
        }
        else
        {
            group.Children.Add(s_fallbackMat);
        }
        return group;
    }

    private void ClearScene()
    {
        // Unsubscribe item visibility events
        if (_subscribedTab != null)
        {
            UnsubscribeItemVisibility(_subscribedTab);
        }

        _submeshGeos.Clear();
        _selectedSubmesh  = -1;
        _selectedMaterial = -1;

        _suppressSelection = true;
        if (SubmeshListBox  != null) SubmeshListBox.SelectedItem  = null;
        if (MaterialListBox != null) MaterialListBox.SelectedItem = null;
        _suppressSelection = false;

        SceneRoot.Content = null;
        BoneRoot.Children.Clear();
        _lastBounds = Rect3D.Empty;
        GizmoCanvas.Children.Clear();
        _matUvLayers.Clear();
        _matUvTiling.Clear();
        if (UvMapCanvas  != null) UvMapCanvas.Children.Clear();
        if (UvMapImage   != null) UvMapImage.Source = null;
    }

    private void Rebuild3DScene(G1mData model, IReadOnlyDictionary<int, BitmapSource> textures)
    {
        // Clear previous state
        if (_subscribedTab != null) UnsubscribeItemVisibility(_subscribedTab);
        _submeshGeos.Clear();
        _selectedSubmesh  = -1;
        _selectedMaterial = -1;

        _suppressSelection = true;
        if (SubmeshListBox  != null) SubmeshListBox.SelectedItem  = null;
        if (MaterialListBox != null) MaterialListBox.SelectedItem = null;
        _suppressSelection = false;

        var allMeshes = new Model3DGroup();

        // Detect which material textures have real alpha pixels (e.g. hair, cloth).
        // Transparent meshes must be added to allMeshes AFTER opaque ones so WPF's depth
        // sort renders them correctly and the body shows through transparent hair gaps.
        var alphaMatIds = new HashSet<int>();
        foreach (var (matIdx, bmp) in textures)
            if (TextureHasTransparency(bmp)) alphaMatIds.Add(matIdx);

        var pendingOpaque = new List<GeometryModel3D>();
        var pendingAlpha  = new List<GeometryModel3D>();

        // Build matIdx → uvLayer / tiling from MaterialTextures (TexType==1 = BaseColor)
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

        for (int smIdx = 0; smIdx < model.Submeshes.Length; smIdx++)
        {
            var sm = model.Submeshes[smIdx];
            if (sm.Positions.Length == 0 || sm.Indices.Length == 0)
            {
                _submeshGeos.Add((null, null, null, sm.MaterialIndex));
                continue;
            }
            var mesh = new MeshGeometry3D();

            var pts = new Point3DCollection(sm.Positions.Length);
            foreach (var p in sm.Positions) pts.Add(ToWpf(p));
            mesh.Positions = pts;

            var idxColl = new Int32Collection(sm.Indices.Length);
            foreach (var idx in sm.Indices) idxColl.Add(idx);
            mesh.TriangleIndices = idxColl;

            if (sm.Normals.Length == sm.Positions.Length)
            {
                var norms = new Vector3DCollection(sm.Normals.Length);
                // ToWpf has det=+1 (orientation-preserving) → no normal negation needed
                foreach (var n in sm.Normals) norms.Add(ToWpfDir(n));
                mesh.Normals = norms;
            }

            int uvCh = _matUvLayers.TryGetValue(sm.MaterialIndex, out var ch) ? ch : 0;
            var uvSrc = uvCh < sm.AllTexCoords.Length ? sm.AllTexCoords[uvCh] : sm.TexCoords;
            {
                string firstUv = uvSrc.Length > 0 ? $"({uvSrc[0].X:F3},{uvSrc[0].Y:F3})" : "none";
                AppLogger.Info($"[3D] SM[{smIdx}] cloth={sm.ClothId} mat={sm.MaterialIndex} uvCh={uvCh} allUvLen={sm.AllTexCoords.Length} uvSrcLen={uvSrc.Length} uv0={firstUv}");
            }
            if (uvSrc.Length > 0)
            {
                var uvs = new PointCollection(uvSrc.Length);
                foreach (var uv in uvSrc) uvs.Add(new System.Windows.Point(uv.X, uv.Y));
                mesh.TextureCoordinates = uvs;
            }

            textures.TryGetValue(sm.MaterialIndex, out var tex);
            AppLogger.Info($"[3D] SM[{smIdx}] tex={(tex != null ? $"{tex.PixelWidth}x{tex.PixelHeight}" : "NULL")}");

            bool hasAlpha = alphaMatIds.Contains(sm.MaterialIndex);
            AppLogger.Info($"[3D] SM[{smIdx}] mat={sm.MaterialIndex} cloth={sm.ClothId} hasAlpha={hasAlpha}");
            var frontMat = MakeMeshMaterial(tex);
            // Cloth (ClothId==1) is a single-layer mesh with no flipped duplicate → two-sided.
            // Rigid/physics meshes have a flipped-normal copy for the back face → cull.
            Material? backMat = sm.ClothId == 1 ? MakeMeshMaterial(tex) : null;
            var geo = new GeometryModel3D(mesh, frontMat) { BackMaterial = backMat };
            _submeshGeos.Add((geo, frontMat, backMat, sm.MaterialIndex));
            if (hasAlpha) pendingAlpha.Add(geo);
            else          pendingOpaque.Add(geo);
        }

        // Opaque first, then transparent — correct depth-sort order for WPF 3D
        foreach (var g in pendingOpaque) allMeshes.Children.Add(g);
        foreach (var g in pendingAlpha)  allMeshes.Children.Add(g);

        SceneRoot.Content = allMeshes.Children.Count > 0 ? allMeshes : null;

        // Re-subscribe item visibility events for current tab
        var tab = _vm.SelectedTab;
        if (tab != null)
        {
            _subscribedTab = tab;
            SubscribeItemVisibility(tab);
        }

        // Bone overlay
        RebuildBoneOverlay(model, tab?.ShowBones ?? false);

        var bounds = allMeshes.Bounds;
        _lastBounds = bounds;

        if (!bounds.IsEmpty)
        {
            double maxDim = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ);
            if (maxDim < 1e-6) maxDim = 1.0;  // guard: degenerate mesh (all verts at same point)
            var center    = new Point3D(
                bounds.X + bounds.SizeX / 2,
                bounds.Y + bounds.SizeY / 2,
                bounds.Z + bounds.SizeZ / 2);

            double dist = maxDim * 2.0;
            Viewport3D.Camera = new PerspectiveCamera
            {
                Position          = new Point3D(center.X, center.Y - dist, center.Z + maxDim * 0.5),
                LookDirection     = new Vector3D(0, dist, -maxDim * 0.5),
                UpDirection       = new Vector3D(0, 0, 1),
                FieldOfView       = 45,
                NearPlaneDistance = 0.1,
                FarPlaneDistance  = 20000,
            };
        }

        DrawGizmo();
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

        // Background texture
        int bgMat = matIdx >= 0 ? matIdx
            : smOnly >= 0 && smOnly < model.Submeshes.Length ? model.Submeshes[smOnly].MaterialIndex
            : -1;
        if (bgMat >= 0 && tab.ModelTextures.TryGetValue(bgMat, out var bmp))
            UvMapImage.Source = bmp;

        int ci = 0;
        for (int si = 0; si < model.Submeshes.Length; si++)
        {
            if (smOnly >= 0 && si != smOnly) continue;
            var sm = model.Submeshes[si];
            if (matIdx >= 0 && sm.MaterialIndex != matIdx) continue;

            // Prefer UV from the actual rendered MeshGeometry3D so the UV map
            // reflects exactly what WPF is drawing, not just parsed data.
            MeshGeometry3D? mesh3d = si < _submeshGeos.Count
                ? _submeshGeos[si].Geo?.Geometry as MeshGeometry3D
                : null;
            PointCollection?   texCoords  = mesh3d?.TextureCoordinates;
            Int32Collection?   triIndices = mesh3d?.TriangleIndices;
            bool useRendered = texCoords is { Count: > 0 } && triIndices is { Count: >= 3 };

            // Fallback to parsed UV when geometry is null (skipped submesh)
            System.Windows.Point[]? uvFallback  = null;
            int[]?                  idxFallback = null;
            if (!useRendered)
            {
                int uvCh = _matUvLayers.TryGetValue(sm.MaterialIndex, out var c) ? c : 0;
                var uvSrc = uvCh < sm.AllTexCoords.Length ? sm.AllTexCoords[uvCh] : sm.TexCoords;
                if (uvSrc.Length == 0 || sm.Indices.Length < 3) continue;
                var pts = new System.Windows.Point[uvSrc.Length];
                for (int k = 0; k < uvSrc.Length; k++) pts[k] = new System.Windows.Point(uvSrc[k].X, uvSrc[k].Y);
                uvFallback  = pts;
                idxFallback = sm.Indices;
            }

            var col = s_uvPalette[ci++ % s_uvPalette.Length];
            var brush = new SolidColorBrush(Color.FromArgb(204, col.R, col.G, col.B));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                if (useRendered)
                {
                    int n = texCoords!.Count;
                    for (int fi = 0; fi + 2 < triIndices!.Count; fi += 3)
                    {
                        int i0 = triIndices[fi], i1 = triIndices[fi + 1], i2 = triIndices[fi + 2];
                        if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;
                        ctx.BeginFigure(new System.Windows.Point(texCoords[i0].X * sz, texCoords[i0].Y * sz), false, true);
                        ctx.LineTo(new System.Windows.Point(texCoords[i1].X * sz, texCoords[i1].Y * sz), true, false);
                        ctx.LineTo(new System.Windows.Point(texCoords[i2].X * sz, texCoords[i2].Y * sz), true, false);
                    }
                }
                else
                {
                    int n = uvFallback!.Length;
                    for (int fi = 0; fi + 2 < idxFallback!.Length; fi += 3)
                    {
                        int i0 = idxFallback[fi], i1 = idxFallback[fi + 1], i2 = idxFallback[fi + 2];
                        if ((uint)i0 >= (uint)n || (uint)i1 >= (uint)n || (uint)i2 >= (uint)n) continue;
                        ctx.BeginFigure(new System.Windows.Point(uvFallback[i0].X * sz, uvFallback[i0].Y * sz), false, true);
                        ctx.LineTo(new System.Windows.Point(uvFallback[i1].X * sz, uvFallback[i1].Y * sz), true, false);
                        ctx.LineTo(new System.Windows.Point(uvFallback[i2].X * sz, uvFallback[i2].Y * sz), true, false);
                    }
                }
            }
            geo.Freeze();

            UvMapCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = geo,
                Stroke = brush,
                StrokeThickness = 0.5,
                Fill = null,
            });
        }
    }
}
