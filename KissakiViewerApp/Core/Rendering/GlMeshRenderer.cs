using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using KissakiViewer.Core.Formats;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace KissakiViewer.Core.Rendering;

/// <summary>
/// OpenGL 3.3 Core renderer for G1M models.
/// Must only call OpenGL methods from within GLWpfControl.Render callback (GL context thread).
/// CPU-side state changes (SetSubmeshVisible, LoadModel, etc.) are queued and applied
/// at the start of the next Render call.
/// </summary>
public sealed class GlMeshRenderer : IDisposable
{
    // ── Shader programs ────────────────────────────────────────────────────────
    private int _meshProg;
    private int _lineProg;
    private int _outlineProg;

    // Mesh uniforms
    private int _uMeshMvp, _uMeshModel, _uMeshTex, _uMeshHasTex, _uMeshLightDir, _uMeshAmbient;

    // Line uniforms
    private int _uLineMvp, _uLineColor;

    // Outline uniforms
    private int _uOutlineMvp, _uOutlineScale, _uOutlineColor;

    // ── Scene geometry ─────────────────────────────────────────────────────────
    private readonly List<SubmeshGpu> _submeshes = [];
    private readonly List<LineBatch>  _boneBatches = [];
    private readonly List<LineBatch>  _axisBatches = [];

    // ── Pending uploads (set on UI thread, consumed on first Render after) ────
    private G1mLoadData?  _pendingLoad;
    private bool          _pendingClear;
    private BoneLoadData? _pendingBones;

    // ── Camera (arcball) ──────────────────────────────────────────────────────
    private float   _azimuth   = 0f;
    private float   _elevation = 0.2f;
    private float   _distance  = 2f;
    private Vector3 _target    = Vector3.Zero;
    private float   _minDist   = 0.01f;
    private float   _maxDist   = 100_000f;

    // ── Mouse state ────────────────────────────────────────────────────────────
    private bool   _leftDown, _rightDown;
    private float  _lastMx, _lastMy;
    private float  _orbitSensitivity = 0.007f;
    private float  _panSensitivity   = 0.001f;

    // ── Highlight/LOD state ────────────────────────────────────────────────────
    private float        _shellScale = 0.01f;
    private bool         _bonesVisible;
    private int          _activeLodGroup = -1;  // -1 = no LOD filter

    private bool _glInitialized;
    private bool _disposed;

    // ══════════════════════════════════════════════════════════════════════════
    // Public API (UI thread, no GL context required)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Queue a G1M model for upload. Applied on the next Render call.</summary>
    public void LoadModel(G1mData model, IReadOnlyDictionary<int, BitmapSource> textures,
                          Dictionary<int, int> matUvLayers, float shellScale)
    {
        _pendingLoad  = new G1mLoadData(model, textures, matUvLayers, shellScale);
        _pendingClear = false;
    }

    public void Clear()
    {
        _pendingClear = true;
        _pendingLoad  = null;
        _pendingBones = null;
    }

    public void SetBoneOverlay(G1mData? model, bool visible)
    {
        _pendingBones = new BoneLoadData(model, visible);
    }

    public void SetSubmeshVisible(int idx, bool visible)
    {
        if ((uint)idx < (uint)_submeshes.Count)
        {
            _submeshes[idx].UserVisible = visible;
            UpdateSubmeshFinalVisible(idx);
        }
    }

    public void ApplyLodFilter(int lodGroup, bool hasLods)
    {
        _activeLodGroup = hasLods ? lodGroup : -1;
        for (int i = 0; i < _submeshes.Count; i++)
            UpdateSubmeshFinalVisible(i);
    }

    public void SetHighlight(IEnumerable<int>? indices)
    {
        foreach (var sm in _submeshes) sm.Highlighted = false;
        if (indices != null)
            foreach (int i in indices)
                if ((uint)i < (uint)_submeshes.Count) _submeshes[i].Highlighted = true;
    }

    // ── Camera control ─────────────────────────────────────────────────────────

    public void MouseDown(bool left, float x, float y)
    {
        if (left)  _leftDown  = true;
        else       _rightDown = true;
        _lastMx = x; _lastMy = y;
    }

    public void MouseMove(float x, float y)
    {
        float dx = x - _lastMx, dy = y - _lastMy;
        _lastMx = x; _lastMy = y;
        if (dx == 0 && dy == 0) return;

        if (_leftDown)
        {
            _azimuth   -= dx * _orbitSensitivity;
            _elevation += dy * _orbitSensitivity;
            _elevation  = Math.Clamp(_elevation, -MathF.PI * 0.49f, MathF.PI * 0.49f);
        }
        else if (_rightDown)
        {
            // Pan: move target in camera-right and camera-up
            var right   = Vector3.Cross(LookDirection, Vector3.UnitY).Normalized();
            var camUp   = Vector3.Cross(right, LookDirection).Normalized();
            float scale = _distance * _panSensitivity;
            _target -= right * dx * scale;
            _target += camUp  * dy * scale;
        }
    }

    public void MouseUp(bool left)
    {
        if (left) _leftDown  = false;
        else      _rightDown = false;
    }

    public void MouseWheel(float delta)
    {
        _distance *= MathF.Pow(0.9f, delta / 120f);
        _distance  = Math.Clamp(_distance, _minDist, _maxDist);
    }

    // ── Camera queries (for gizmo) ─────────────────────────────────────────────

    public Vector3 LookDirection
    {
        get
        {
            float cosEl = MathF.Cos(_elevation);
            var dir = new Vector3(MathF.Sin(_azimuth) * cosEl, MathF.Sin(_elevation), MathF.Cos(_azimuth) * cosEl);
            return -dir;  // look = target - eye direction = negate offset
        }
    }

    public Vector3 UpDirection
    {
        get
        {
            // True camera-up: cross(right, look)
            var look  = LookDirection;
            var right = Vector3.Cross(look, Vector3.UnitY).Normalized();
            if (right.LengthSquared < 1e-6f) right = Vector3.UnitX;
            return Vector3.Cross(right, look).Normalized();
        }
    }

    public Vector3D LookWpf => ToWpfVec(LookDirection);
    public Vector3D UpWpf   => ToWpfVec(UpDirection);

    // ══════════════════════════════════════════════════════════════════════════
    // Render (must be called from GL thread / GLWpfControl.Render event)
    // ══════════════════════════════════════════════════════════════════════════

    public void Render(int fbWidth, int fbHeight)
    {
        if (!_glInitialized) InitGl();

        // Process pending mutations
        if (_pendingClear)   { DoClear();          _pendingClear = false; }
        if (_pendingLoad != null) { DoLoad(_pendingLoad); _pendingLoad  = null; }
        if (_pendingBones != null) { DoBones(_pendingBones); _pendingBones = null; }

        GL.Viewport(0, 0, fbWidth, fbHeight);
        GL.ClearColor(0.07f, 0.07f, 0.09f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_submeshes.Count == 0 && _axisBatches.Count == 0) return;

        var view = GetViewMatrix();
        float aspect = fbHeight > 0 ? (float)fbWidth / fbHeight : 1f;
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 200_000f);
        var vp   = view * proj;

        // World axes (behind model, rendered first)
        RenderLineBatches(_axisBatches, vp);

        // ── Mesh render: opaque pass ──────────────────────────────────────────
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);

        GL.UseProgram(_meshProg);
        SetMeshLighting();

        var identity = Matrix4.Identity;
        GL.UniformMatrix4(_uMeshModel, false, ref identity);

        foreach (var sm in _submeshes)
        {
            if (!sm.FinalVisible || sm.HasAlpha) continue;
            DrawSubmesh(sm, vp);
        }

        // ── Mesh render: alpha (discard) pass ─────────────────────────────────
        // Two-sided for hair / cloth — discard in frag shader handles per-pixel cutout.
        GL.Disable(EnableCap.CullFace);
        foreach (var sm in _submeshes)
        {
            if (!sm.FinalVisible || !sm.HasAlpha) continue;
            DrawSubmesh(sm, vp);
        }
        GL.Enable(EnableCap.CullFace);

        // ── Outline pass (selected submeshes) ─────────────────────────────────
        GL.UseProgram(_outlineProg);
        GL.CullFace(TriangleFace.Front);  // show only expanded back faces
        GL.Uniform1(_uOutlineScale, _shellScale);
        GL.Uniform4(_uOutlineColor, new Vector4(1.0f, 0.83f, 0.0f, 1.0f)); // yellow
        foreach (var sm in _submeshes)
        {
            if (!sm.FinalVisible || !sm.Highlighted) continue;
            GL.UniformMatrix4(_uOutlineMvp, false, ref vp);
            GL.BindVertexArray(sm.Vao);
            GL.DrawElements(PrimitiveType.Triangles, sm.IndexCount, DrawElementsType.UnsignedInt, 0);
        }
        GL.CullFace(TriangleFace.Back);

        // ── Bone overlay (X-ray: clear depth so bones always on top) ──────────
        if (_bonesVisible && _boneBatches.Count > 0)
        {
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Disable(EnableCap.CullFace);
            RenderLineBatches(_boneBatches, vp);
            GL.Enable(EnableCap.CullFace);
        }

        GL.Flush();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Disposal
    // ══════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_glInitialized) return;

        foreach (var sm in _submeshes) sm.Delete();
        _submeshes.Clear();
        DeleteLineBatches(_boneBatches);
        DeleteLineBatches(_axisBatches);
        if (_meshProg    != 0) GL.DeleteProgram(_meshProg);
        if (_lineProg    != 0) GL.DeleteProgram(_lineProg);
        if (_outlineProg != 0) GL.DeleteProgram(_outlineProg);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private — GL initialization
    // ══════════════════════════════════════════════════════════════════════════

    private void InitGl()
    {
        _glInitialized = true;

        string shaderDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "res", "shaders");
        _meshProg    = CompileProgram(Path.Combine(shaderDir, "mesh.vert"),    Path.Combine(shaderDir, "mesh.frag"));
        _lineProg    = CompileProgram(Path.Combine(shaderDir, "line.vert"),    Path.Combine(shaderDir, "line.frag"));
        _outlineProg = CompileProgram(Path.Combine(shaderDir, "outline.vert"), Path.Combine(shaderDir, "outline.frag"));

        // Cache uniform locations
        _uMeshMvp      = GL.GetUniformLocation(_meshProg, "uMVP");
        _uMeshModel    = GL.GetUniformLocation(_meshProg, "uModel");
        _uMeshTex      = GL.GetUniformLocation(_meshProg, "uTex");
        _uMeshHasTex   = GL.GetUniformLocation(_meshProg, "uHasTex");
        _uMeshLightDir = GL.GetUniformLocation(_meshProg, "uLightDir");
        _uMeshAmbient  = GL.GetUniformLocation(_meshProg, "uAmbient");

        _uLineMvp   = GL.GetUniformLocation(_lineProg, "uMVP");
        _uLineColor = GL.GetUniformLocation(_lineProg, "uColor");

        _uOutlineMvp   = GL.GetUniformLocation(_outlineProg, "uMVP");
        _uOutlineScale = GL.GetUniformLocation(_outlineProg, "uScale");
        _uOutlineColor = GL.GetUniformLocation(_outlineProg, "uOutlineColor");

        BuildAxisLines();
    }

    private static int CompileProgram(string vertPath, string fragPath)
    {
        int vs = CompileShader(ShaderType.VertexShader,   File.ReadAllText(vertPath));
        int fs = CompileShader(ShaderType.FragmentShader, File.ReadAllText(fragPath));
        int prog = GL.CreateProgram();
        GL.AttachShader(prog, vs);
        GL.AttachShader(prog, fs);
        GL.LinkProgram(prog);
        GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            AppLogger.Error($"[GL] Link error ({Path.GetFileName(vertPath)}): {GL.GetProgramInfoLog(prog)}");
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        return prog;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int id = GL.CreateShader(type);
        GL.ShaderSource(id, source);
        GL.CompileShader(id);
        GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            AppLogger.Error($"[GL] Shader compile error ({type}): {GL.GetShaderInfoLog(id)}");
        return id;
    }

    // ── World axes (built once at init) ───────────────────────────────────────

    private void BuildAxisLines()
    {
        DeleteLineBatches(_axisBatches);
        _axisBatches.Clear();

        (float[] pts, Vector4 col)[] axes =
        [
            ([0, 0, 0,  50, 0, 0],   new Vector4(0.86f, 0.25f, 0.25f, 1f)), // X red
            ([0, 0, 0,  0, 50, 0],   new Vector4(0.35f, 0.76f, 0.29f, 1f)), // Y green
            ([0, 0, 0,  0, 0, 50],   new Vector4(0.24f, 0.51f, 0.86f, 1f)), // Z blue
        ];
        foreach (var (pts, col) in axes)
            _axisBatches.Add(UploadLineBatch(pts, PrimitiveType.Lines, col, 2f));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private — scene upload
    // ══════════════════════════════════════════════════════════════════════════

    private void DoClear()
    {
        foreach (var sm in _submeshes) sm.Delete();
        _submeshes.Clear();
        DeleteLineBatches(_boneBatches);
        _boneBatches.Clear();
        _bonesVisible = false;
    }

    private void DoLoad(G1mLoadData data)
    {
        DoClear();
        _shellScale = data.ShellScale;
        var model = data.Model;

        // Build submesh GPU data
        for (int si = 0; si < model.Submeshes.Length; si++)
        {
            var sm = model.Submeshes[si];
            if (sm.Positions.Length == 0 || sm.Indices.Length == 0)
            {
                _submeshes.Add(SubmeshGpu.Empty(sm.MaterialIndex, sm.LodGroup));
                continue;
            }

            // Interleaved vertex: [px, py, pz, nx, ny, nz, u, v]
            int vCount = sm.Positions.Length;
            var verts  = new float[vCount * 8];

            int uvCh  = data.MatUvLayers.TryGetValue(sm.MaterialIndex, out var ch) ? ch : 0;
            var uvSrc = uvCh < sm.AllTexCoords.Length ? sm.AllTexCoords[uvCh] : sm.TexCoords;

            for (int i = 0; i < vCount; i++)
            {
                int o = i * 8;
                var p = sm.Positions[i];
                verts[o]   = p.X; verts[o+1] = p.Y; verts[o+2] = p.Z;

                var n = i < sm.Normals.Length ? sm.Normals[i] : System.Numerics.Vector3.UnitY;
                verts[o+3] = n.X; verts[o+4] = n.Y; verts[o+5] = n.Z;

                var uv = i < uvSrc.Length ? uvSrc[i] : System.Numerics.Vector2.Zero;
                verts[o+6] = uv.X; verts[o+7] = uv.Y;
            }

            // Upload VAO
            GL.GenVertexArrays(1, out int vao);
            GL.BindVertexArray(vao);

            GL.GenBuffers(1, out int vbo);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);

            GL.GenBuffers(1, out int ibo);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ibo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, sm.Indices.Length * sizeof(int), sm.Indices, BufferUsageHint.StaticDraw);

            const int stride = 8 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(2);

            GL.BindVertexArray(0);

            // Texture
            int texId = 0;
            bool hasAlpha = false;
            if (data.Textures.TryGetValue(sm.MaterialIndex, out var bmp) && bmp != null)
            {
                texId    = UploadTexture(bmp, out hasAlpha);
            }

            var gpu = new SubmeshGpu
            {
                Vao        = vao,
                Vbo        = vbo,
                Ibo        = ibo,
                IndexCount = sm.Indices.Length,
                TexId      = texId,
                HasAlpha   = hasAlpha,
                IsCloth    = sm.ClothId == 1,
                MatIdx     = sm.MaterialIndex,
                LodGroup   = sm.LodGroup,
                UserVisible = true,
                FinalVisible = true,
            };
            _submeshes.Add(gpu);
        }

        // Compute model center from first non-empty submesh or all positions
        float mnX = float.MaxValue, mnY = float.MaxValue, mnZ = float.MaxValue;
        float mxX = float.MinValue, mxY = float.MinValue, mxZ = float.MinValue;
        foreach (var sm in model.Submeshes)
        {
            foreach (var p in sm.Positions)
            {
                if (p.X < mnX) mnX = p.X; if (p.X > mxX) mxX = p.X;
                if (p.Y < mnY) mnY = p.Y; if (p.Y > mxY) mxY = p.Y;
                if (p.Z < mnZ) mnZ = p.Z; if (p.Z > mxZ) mxZ = p.Z;
            }
        }

        if (mnX < mxX)
        {
            float maxDim = Math.Max(Math.Max(mxX - mnX, mxY - mnY), mxZ - mnZ);
            if (maxDim < 1e-4f) maxDim = 1f;
            _target    = new Vector3((mnX+mxX)*0.5f, (mnY+mxY)*0.5f, (mnZ+mxZ)*0.5f);
            _distance  = maxDim * 2.0f;
            _minDist   = maxDim * 0.01f;
            _maxDist   = maxDim * 100f;
            _azimuth   = 0f;
            _elevation = 0.15f;
            _panSensitivity = maxDim * 0.0005f;
        }
    }

    private void DoBones(BoneLoadData data)
    {
        DeleteLineBatches(_boneBatches);
        _boneBatches.Clear();
        _bonesVisible = data.Visible;

        var model = data.Model;
        if (!data.Visible || model == null || model.Bones.Length == 0) return;

        int skelCount = model.NunoCpStartIndex > 0 ? model.NunoCpStartIndex
                      : model.InternalBoneCount  > 0 ? model.InternalBoneCount
                      : model.Bones.Length;

        const float kMaxPos = 2000f;
        static bool IsValid(System.Numerics.Vector3 p)
            => MathF.Abs(p.X) < kMaxPos && MathF.Abs(p.Y) < kMaxPos && MathF.Abs(p.Z) < kMaxPos;

        var nunoParents = model.NunoParentBoneIndices;

        // Skeleton lines (white)
        var skelLinesPts = new List<float>();
        for (int i = 0; i < skelCount; i++)
        {
            var bone = model.Bones[i];
            int pi   = bone.ParentIndex;
            if (pi >= 0 && pi < skelCount
                && IsValid(model.Bones[pi].WorldPosition) && IsValid(bone.WorldPosition))
            {
                var pp = model.Bones[pi].WorldPosition;
                var bp = bone.WorldPosition;
                skelLinesPts.AddRange([pp.X, pp.Y, pp.Z, bp.X, bp.Y, bp.Z]);
            }
        }
        if (skelLinesPts.Count > 0)
            _boneBatches.Add(UploadLineBatch([.. skelLinesPts], PrimitiveType.Lines,
                new Vector4(1f, 1f, 1f, 1f), 1f));

        // Skeleton joint dots (white / orange for NUNO parents)
        var dotPts        = new List<float>();
        var nunoParentPts = new List<float>();
        for (int i = 0; i < skelCount; i++)
        {
            var p = model.Bones[i].WorldPosition;
            if (!IsValid(p)) continue;
            if (nunoParents.Contains(i)) nunoParentPts.AddRange([p.X, p.Y, p.Z]);
            else                         dotPts.AddRange([p.X, p.Y, p.Z]);
        }
        if (dotPts.Count > 0)
            _boneBatches.Add(UploadLineBatch([.. dotPts], PrimitiveType.Points,
                new Vector4(1f, 1f, 1f, 1f), 4f));
        if (nunoParentPts.Count > 0)
            _boneBatches.Add(UploadLineBatch([.. nunoParentPts], PrimitiveType.Points,
                new Vector4(1f, 0.6f, 0f, 1f), 7f));

        // CP bone chains (cyan)
        if (skelCount < model.Bones.Length)
        {
            var cpLinePts = new List<float>();
            for (int i = skelCount; i < model.Bones.Length; i++)
            {
                var bone = model.Bones[i];
                int pi   = bone.ParentIndex;
                if (pi >= skelCount && pi < model.Bones.Length
                    && IsValid(model.Bones[pi].WorldPosition) && IsValid(bone.WorldPosition))
                {
                    var pp = model.Bones[pi].WorldPosition;
                    var bp = bone.WorldPosition;
                    cpLinePts.AddRange([pp.X, pp.Y, pp.Z, bp.X, bp.Y, bp.Z]);
                }
            }
            if (cpLinePts.Count > 0)
                _boneBatches.Add(UploadLineBatch([.. cpLinePts], PrimitiveType.Lines,
                    new Vector4(0f, 1f, 1f, 1f), 1f));

            var cpDotPts = new List<float>();
            for (int i = skelCount; i < model.Bones.Length; i++)
            {
                var p = model.Bones[i].WorldPosition;
                if (IsValid(p)) cpDotPts.AddRange([p.X, p.Y, p.Z]);
            }
            if (cpDotPts.Count > 0)
                _boneBatches.Add(UploadLineBatch([.. cpDotPts], PrimitiveType.Points,
                    new Vector4(0f, 1f, 1f, 1f), 4f));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private — per-frame drawing
    // ══════════════════════════════════════════════════════════════════════════

    private void DrawSubmesh(SubmeshGpu sm, Matrix4 vp)
    {
        GL.UniformMatrix4(_uMeshMvp, false, ref vp);

        if (sm.TexId != 0)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, sm.TexId);
            GL.Uniform1(_uMeshTex, 0);
            GL.Uniform1(_uMeshHasTex, 1);
        }
        else
        {
            GL.Uniform1(_uMeshHasTex, 0);
        }

        GL.BindVertexArray(sm.Vao);
        GL.DrawElements(PrimitiveType.Triangles, sm.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    private void SetMeshLighting()
    {
        var lightDir = new Vector3(-0.5f, -1f, -0.3f);
        lightDir.Normalize();
        GL.Uniform3(_uMeshLightDir, lightDir);
        GL.Uniform1(_uMeshAmbient, 0.35f);
    }

    private void RenderLineBatches(List<LineBatch> batches, Matrix4 vp)
    {
        if (batches.Count == 0) return;
        GL.UseProgram(_lineProg);
        GL.UniformMatrix4(_uLineMvp, false, ref vp);
        foreach (var b in batches)
        {
            GL.Uniform4(_uLineColor, b.Color);
            GL.LineWidth(b.Width);
            GL.PointSize(b.Width);
            GL.BindVertexArray(b.Vao);
            GL.DrawArrays(b.Type, 0, b.VertexCount);
            GL.BindVertexArray(0);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Private — helpers
    // ══════════════════════════════════════════════════════════════════════════

    private Matrix4 GetViewMatrix()
    {
        float cosEl = MathF.Cos(_elevation);
        float offset_x = MathF.Sin(_azimuth) * cosEl * _distance;
        float offset_y = MathF.Sin(_elevation) * _distance;
        float offset_z = MathF.Cos(_azimuth) * cosEl * _distance;
        var eye = _target + new Vector3(offset_x, offset_y, offset_z);
        return Matrix4.LookAt(eye, _target, Vector3.UnitY);
    }

    private void UpdateSubmeshFinalVisible(int idx)
    {
        var sm = _submeshes[idx];
        bool inLod = _activeLodGroup < 0 || sm.LodGroup < 0 || sm.LodGroup == _activeLodGroup;
        sm.FinalVisible = sm.UserVisible && inLod;
    }

    private static int UploadTexture(BitmapSource bmp, out bool hasAlpha)
    {
        hasAlpha = false;

        // Ensure Bgra32 format
        BitmapSource src = bmp;
        if (src.Format != PixelFormats.Bgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        src.CopyPixels(pixels, stride, 0);

        // Detect transparency (sample every 16th pixel's alpha byte)
        for (int i = 3; i < pixels.Length && !hasAlpha; i += 64)
            if (pixels[i] < 250) hasAlpha = true;

        GL.GenTextures(1, out int texId);
        GL.BindTexture(TextureTarget.Texture2D, texId);
        // GL_BGRA matches WPF Bgra32 — no byte swap needed
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texId;
    }

    private static LineBatch UploadLineBatch(float[] pts, PrimitiveType type, Vector4 color, float width)
    {
        GL.GenVertexArrays(1, out int vao);
        GL.BindVertexArray(vao);
        GL.GenBuffers(1, out int vbo);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        return new LineBatch { Vao = vao, Vbo = vbo, VertexCount = pts.Length / 3, Type = type, Color = color, Width = width };
    }

    private static void DeleteLineBatches(List<LineBatch> batches)
    {
        foreach (var b in batches)
        {
            GL.DeleteVertexArray(b.Vao);
            GL.DeleteBuffer(b.Vbo);
        }
    }

    private static Vector3D ToWpfVec(Vector3 v) => new(v.X, v.Y, v.Z);

    // ══════════════════════════════════════════════════════════════════════════
    // Inner types
    // ══════════════════════════════════════════════════════════════════════════

    private sealed class SubmeshGpu
    {
        public int  Vao, Vbo, Ibo;
        public int  IndexCount;
        public int  TexId;
        public bool HasAlpha;
        public bool IsCloth;
        public int  MatIdx;
        public int  LodGroup;
        public bool UserVisible  = true;
        public bool FinalVisible = true;
        public bool Highlighted;

        public void Delete()
        {
            if (Vao != 0) GL.DeleteVertexArray(Vao);
            if (Vbo != 0) GL.DeleteBuffer(Vbo);
            if (Ibo != 0) GL.DeleteBuffer(Ibo);
            if (TexId != 0) GL.DeleteTexture(TexId);
            Vao = Vbo = Ibo = TexId = 0;
        }

        public static SubmeshGpu Empty(int matIdx, int lodGroup) => new()
        {
            MatIdx = matIdx, LodGroup = lodGroup,
            UserVisible = true, FinalVisible = true,
        };
    }

    private struct LineBatch
    {
        public int Vao, Vbo, VertexCount;
        public PrimitiveType Type;
        public Vector4 Color;
        public float Width;
    }

    private sealed record G1mLoadData(
        G1mData Model,
        IReadOnlyDictionary<int, BitmapSource> Textures,
        Dictionary<int, int> MatUvLayers,
        float ShellScale);

    private sealed record BoneLoadData(G1mData? Model, bool Visible);
}
