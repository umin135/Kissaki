using Assimp;
using System.IO;

namespace KissakiViewer.Core.Formats;

public static class G1mFbxExporter
{
    private static readonly (string Id, string Ext)[] FormatCandidates =
    [
        ("fbx",  ".fbx"),
        ("fbxa", ".fbx"),
        ("obj",  ".obj"),
    ];

    /// <summary>
    /// Exports G1M geometry. matTexPaths: matIdx → relative PNG filename (written into MTL/FBX material).
    /// Returns the path of the file that was actually written.
    /// </summary>
    public static string Export(
        G1mData model,
        string exportDir,
        string stem,
        IReadOnlyDictionary<int, string>? matTexPaths = null)
    {
        Directory.CreateDirectory(exportDir);

        using var ctx = new AssimpContext();

        var supported = ctx.GetSupportedExportFormats();
        AppLogger.Info($"[FBX] Assimp formats: {string.Join(", ", supported.Select(f => f.FormatId))}");

        var scene = BuildScene(model, matTexPaths);
        AppLogger.Info($"[FBX] Scene: {scene.MeshCount} meshes, {scene.MaterialCount} mats");

        foreach (var (fmtId, ext) in FormatCandidates)
        {
            if (!supported.Any(f => f.FormatId == fmtId))
            {
                AppLogger.Warn($"[FBX] '{fmtId}' not available");
                continue;
            }

            string outPath = Path.Combine(exportDir, stem + ext);
            bool ok = ctx.ExportFile(scene, outPath, fmtId);
            AppLogger.Info($"[FBX] ExportFile('{fmtId}') → {ok}");

            if (ok) return outPath;
        }

        throw new IOException("모든 포맷(FBX/OBJ) 내보내기 실패 — 로그를 확인하세요.");
    }

    private static Scene BuildScene(G1mData model, IReadOnlyDictionary<int, string>? matTexPaths)
    {
        var scene      = new Scene();
        scene.RootNode = new Node("Root");

        int matCount = 0;
        foreach (var sm in model.Submeshes)
            if (sm.MaterialIndex >= 0)
                matCount = Math.Max(matCount, sm.MaterialIndex + 1);
        if (matCount == 0) matCount = 1;

        var matNames = new string[matCount];
        foreach (var (matIdx, g1tSlot, uvLayer, _) in model.MaterialTextures)
            if (matIdx < matCount)
                matNames[matIdx] = $"Mat{matIdx}_G1T{g1tSlot}_UV{uvLayer}";

        for (int i = 0; i < matCount; i++)
        {
            var mat = new Material();
            mat.Name         = matNames[i] ?? $"Material_{i}";
            mat.ColorDiffuse = new Color4D(0.8f, 0.8f, 0.8f, 1.0f);

            if (matTexPaths != null && matTexPaths.TryGetValue(i, out string? texFile))
            {
                mat.TextureDiffuse = new TextureSlot(
                    texFile,
                    TextureType.Diffuse,
                    0,
                    TextureMapping.FromUV,
                    0,
                    1.0f,
                    TextureOperation.Multiply,
                    TextureWrapMode.Wrap,
                    TextureWrapMode.Wrap,
                    0);
            }

            scene.Materials.Add(mat);
        }

        int meshAdded = 0;
        for (int si = 0; si < model.Submeshes.Length; si++)
        {
            var sm = model.Submeshes[si];
            if (sm.Positions.Length == 0 || sm.Indices.Length == 0) continue;

            var mesh = new Mesh(PrimitiveType.Triangle) { Name = $"Submesh_{si}" };

            foreach (var p in sm.Positions)
                mesh.Vertices.Add(new Vector3D(p.X, p.Y, p.Z));

            if (sm.Normals.Length == sm.Positions.Length)
                foreach (var n in sm.Normals)
                    mesh.Normals.Add(new Vector3D(n.X, n.Y, n.Z));

            int uvChCount = Math.Min(sm.AllTexCoords.Length, 8);
            for (int ch = 0; ch < uvChCount; ch++)
            {
                var chData = sm.AllTexCoords[ch];
                if (chData.Length == 0) continue;

                var uvList = new List<Vector3D>(chData.Length);
                foreach (var uv in chData)
                    uvList.Add(new Vector3D(uv.X, 1.0f - uv.Y, 0f)); // flip V: G1M=DirectX(V↑), OBJ/Blender=OpenGL(V↑ flipped)

                mesh.TextureCoordinateChannels[ch] = uvList;
                mesh.UVComponentCount[ch]          = 2;
            }

            int faceCount = 0;
            for (int fi = 0; fi + 2 < sm.Indices.Length; fi += 3, faceCount++)
                mesh.Faces.Add(new Face(new[] { sm.Indices[fi], sm.Indices[fi + 1], sm.Indices[fi + 2] }));

            if (faceCount == 0) continue;

            int matIdx = sm.MaterialIndex;
            if (matIdx < 0 || matIdx >= matCount) matIdx = 0;
            mesh.MaterialIndex = matIdx;

            int meshIdx = scene.MeshCount;
            scene.Meshes.Add(mesh);
            meshAdded++;

            var node = new Node($"Submesh_{si}");
            node.MeshIndices.Add(meshIdx);
            scene.RootNode.Children.Add(node);
        }

        AppLogger.Info($"[FBX] BuildScene: {meshAdded}/{model.Submeshes.Length} submeshes");
        return scene;
    }
}
