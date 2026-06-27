using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using KissakiViewer.Core.Formats;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Windows;

public partial class AssetViewerWindow : Window
{
    private readonly AssetViewerViewModel _vm;

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
    }

    public void OpenAsset(AssetItemViewModel vm)
    {
        _vm.OpenAsset(vm);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssetViewerViewModel.SelectedTab)) return;

        var tab = _vm.SelectedTab;
        if (tab == null) { ClearScene(); return; }

        tab.PropertyChanged += OnTabPropertyChanged;
        if (tab.G1mData != null)
            Rebuild3DScene(tab.G1mData, tab.ModelTextures);
        else if (!tab.IsLoading && tab.G1mData == null)
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
    }

    private void TabHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AssetTabItem tab)
            _vm.SelectedTab = tab;
    }

    // ── 3D Scene ──────────────────────────────────────────────────────────────

    private static Point3D  ToWpf(System.Numerics.Vector3 v) => new(v.Y, -v.Z, v.X);
    private static Vector3D ToWpfDir(System.Numerics.Vector3 v) => new(v.Y, -v.Z, v.X);

    private static readonly DiffuseMaterial s_fallbackMat =
        new(new SolidColorBrush(Color.FromRgb(180, 180, 190)));

    private static readonly SpecularMaterial s_specMat =
        new(new SolidColorBrush(Color.FromRgb(60, 60, 80)), 30);

    private static Material MakeMeshMaterial(BitmapSource? tex)
    {
        var group = new MaterialGroup();
        if (tex != null)
        {
            var ib = new ImageBrush(tex) { TileMode = TileMode.Tile, Stretch = Stretch.Fill };
            var emit = new EmissiveMaterial(new ImageBrush(tex) { TileMode = TileMode.Tile, Stretch = Stretch.Fill })
            { Color = Color.FromRgb(15, 15, 15) };
            var diff = new DiffuseMaterial(ib);
            group.Children.Add(emit);
            group.Children.Add(diff);
        }
        else
        {
            group.Children.Add(s_fallbackMat);
        }
        group.Children.Add(s_specMat);
        return group;
    }

    private void ClearScene() => SceneRoot.Content = null;

    private void Rebuild3DScene(G1mData model, IReadOnlyDictionary<int, BitmapSource> textures)
    {
        var allMeshes = new Model3DGroup();

        // Build per-material UV channel lookup (from section 0x10002 layer field)
        var matUvLayer = new Dictionary<int, int>();
        foreach (var (matIdx, _, uvLayer) in model.MaterialTextures)
            matUvLayer.TryAdd(matIdx, uvLayer);

        foreach (var sm in model.Submeshes)
        {
            if (sm.Positions.Length == 0 || sm.Indices.Length == 0) continue;

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
                // Negate normals: ToWpf has det=-1 (LH→RH flip), which reverses normal direction.
                // Without negation the diffuse lighting hits the inside of every face.
                foreach (var n in sm.Normals) norms.Add(ToWpfDir(-n));
                mesh.Normals = norms;
            }

            // Select the correct UV channel from material's 'layer' field
            int uvLayer = matUvLayer.TryGetValue(sm.MaterialIndex, out int lay) ? lay : 0;
            var rawUvs  = (uvLayer < sm.AllTexCoords.Length && sm.AllTexCoords[uvLayer].Length > 0)
                ? sm.AllTexCoords[uvLayer]
                : sm.TexCoords;

            if (rawUvs.Length > 0)
            {
                var uvs = new PointCollection(rawUvs.Length);
                // Flip V: G1M stores V with 0 at bottom (OpenGL-style) while WPF expects 0 at top.
                foreach (var uv in rawUvs) uvs.Add(new System.Windows.Point(uv.X, 1.0 - uv.Y));
                mesh.TextureCoordinates = uvs;
            }

            int matIdx = sm.MaterialIndex;
            textures.TryGetValue(matIdx, out var tex);
            var material = MakeMeshMaterial(tex);

            allMeshes.Children.Add(new GeometryModel3D(mesh, material)
            {
                BackMaterial = MakeMeshMaterial(tex),
            });
        }

        SceneRoot.Content = allMeshes.Children.Count > 0 ? allMeshes : null;

        var bounds = allMeshes.Bounds;
        if (!bounds.IsEmpty)
        {
            double maxDim = Math.Max(Math.Max(bounds.SizeX, bounds.SizeY), bounds.SizeZ);
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
                FarPlaneDistance  = dist * 10,
            };
        }
    }
}
