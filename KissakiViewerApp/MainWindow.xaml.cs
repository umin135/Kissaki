using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using KissakiViewer.Core.Formats;
using KissakiViewer.ViewModels;
using System.Windows.Media.Imaging;

namespace KissakiViewer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentG1mData))
            Rebuild3DScene(((MainViewModel)DataContext!).CurrentG1mData);
    }

    // KatanaEngine: X=forward, Y=right, Z=down (left-hand DirectX).
    // WPF: Y-up, +Z=toward viewer (right-hand).
    // Mapping: game.Y→wpf.X, -game.Z→wpf.Y, game.X→wpf.Z
    private static Point3D  ToWpf(System.Numerics.Vector3 v) => new(v.Y, -v.Z, v.X);
    private static Vector3D ToWpfDir(System.Numerics.Vector3 v) => new(v.Y, -v.Z, v.X);

    private static readonly DiffuseMaterial s_fallbackMat =
        new(new SolidColorBrush(Color.FromRgb(180, 180, 190)));
    private static readonly DiffuseMaterial s_backMat =
        new(new SolidColorBrush(Color.FromRgb(120, 100, 100)));
    private static readonly SpecularMaterial s_specMat =
        new(new SolidColorBrush(Color.FromRgb(60, 60, 80)), 30);

    private static Material MakeMeshMaterial(BitmapSource? tex)
    {
        var diffuse = tex is not null
            ? new DiffuseMaterial(new ImageBrush(tex) { Stretch = Stretch.Fill, TileMode = TileMode.None })
            : s_fallbackMat;
        return new MaterialGroup { Children = { diffuse, s_specMat } };
    }

    private void Rebuild3DScene(G1mData? model)
    {
        // Keep only DefaultLights (first child added in XAML)
        while (Viewport3D.Children.Count > 1)
            Viewport3D.Children.RemoveAt(1);

        if (model is null) return;

        var textures = (DataContext as MainViewModel)?.ModelTextures
                       ?? new Dictionary<int, System.Windows.Media.Imaging.BitmapSource>();

        // ── Mesh ────────────────────────────────────────────────────────────
        foreach (var sub in model.Submeshes)
        {
            if (sub.Positions.Length == 0 || sub.Indices.Length < 3) continue;

            textures.TryGetValue(sub.MaterialIndex, out var tex);
            var mesh = new MeshGeometry3D();
            foreach (var p in sub.Positions)
                mesh.Positions.Add(ToWpf(p));
            foreach (var idx in sub.Indices)
                mesh.TriangleIndices.Add(idx);

            if (sub.Normals.Length == sub.Positions.Length)
            {
                foreach (var n in sub.Normals)
                    mesh.Normals.Add(ToWpfDir(n));
            }

            if (tex is not null && sub.TexCoords.Length == sub.Positions.Length)
            {
                foreach (var uv in sub.TexCoords)
                    mesh.TextureCoordinates.Add(new System.Windows.Point(uv.X, uv.Y));
            }

            var geo = new GeometryModel3D(mesh, MakeMeshMaterial(tex))
            {
                BackMaterial = s_backMat,
            };
            Viewport3D.Children.Add(new ModelVisual3D { Content = geo });
        }

        // ── Skeleton ─────────────────────────────────────────────────────────
        if (model.Bones.Length > 0)
        {
            var boneLines = new LinesVisual3D
            {
                Color     = Color.FromArgb(220, 255, 220, 50),
                Thickness = 1.5,
            };
            var jointPoints = new PointsVisual3D
            {
                Color = Color.FromArgb(255, 255, 100, 50),
                Size  = 4,
            };

            foreach (var bone in model.Bones)
            {
                var wt = bone.WorldMatrix.Translation;
                jointPoints.Points.Add(ToWpf(wt));

                if (bone.ParentIndex < 0 || bone.ParentIndex >= model.Bones.Length) continue;
                var pt = model.Bones[bone.ParentIndex].WorldMatrix.Translation;
                boneLines.Points.Add(ToWpf(pt));
                boneLines.Points.Add(ToWpf(wt));
            }

            Viewport3D.Children.Add(boneLines);
            Viewport3D.Children.Add(jointPoints);
        }

        Viewport3D.ZoomExtents(animationTime: 0);
    }
}
