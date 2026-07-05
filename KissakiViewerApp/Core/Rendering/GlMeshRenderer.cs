using System.Diagnostics;
using System.IO;
using System.Windows.Input;
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
    private int _gridProg;

    // Mesh uniforms
    private int _uMeshMvp, _uMeshModel, _uMeshTex, _uMeshHasTex, _uMeshLightDir, _uMeshAmbient;

    // Line uniforms
    private int _uLineMvp, _uLineColor;

    // Outline uniforms
    private int _uOutlineMvp, _uOutlineScale, _uOutlineColor;

    // Grid uniforms
    private int _uGridMvp, _uGridCamPos, _uGridFadeEnd;

    // ── Scene geometry ─────────────────────────────────────────────────────────
    private readonly List<SubmeshGpu> _submeshes   = [];
    private readonly List<LineBatch>  _boneBatches = [];
    private readonly List<LineBatch>  _axisBatches = [];

    // Grid VAO (separate from LineBatches — uses grid shader)
    private int   _gridVao, _gridVbo, _gridVertexCount;
    private float _gridFadeEnd = 100f;

    // ── Pending uploads (set on UI thread, consumed on first Render after) ────
    private G1mLoadData?  _pendingLoad;
    private bool          _pendingClear;
    private BoneLoadData? _pendingBones;

    // ── FPS Camera ────────────────────────────────────────────────────────────
    private Vector3 _camPos   = new(0f, 0.5f, 3f);
    private float   _camYaw   = MathF.PI;   // default: looking in −Z direction
    private float   _camPitch = -0.05f;
    private float   _moveSpeed = 1f;

    // ── Mouse state ────────────────────────────────────────────────────────────
    private bool  _leftDown;
    private float _lastMx, _lastMy;
    private const float LookSensitivity = 0.004f;

    // ── WASD keys ──────────────────────────────────────────────────────────────
    private readonly HashSet<Key> _keysDown = [];

    // ── Frame timing ───────────────────────────────────────────────────────────
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();

    // ── Highlight/LOD state ────────────────────────────────────────────────────
    private float _shellScale     = 0.01f;
    private bool  _bonesVisible;
    private int   _activeLodGroup = -1;  // -1 = no LOD filter

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
        if (left) _leftDown = true;
        _lastMx = x; _lastMy = y;
    }

    public void MouseMove(float x, float y)
    {
        float dx = x - _lastMx, dy = y - _lastMy;
        _lastMx = x; _lastMy = y;
        if (!_leftDown || (dx == 0 && dy == 0)) return;

        _camYaw   += dx * LookSensitivity;
        _camPitch -= dy * LookSensitivity;   // screen-Y down = pitch down
        _camPitch  = Math.Clamp(_camPitch, -MathF.PI * 0.49f, MathF.PI * 0.49f);
    }

    public void MouseUp(bool left)
    {
        if (left) _leftDown = false;
    }

    public void MouseWheel(float delta)
    {
        // Scroll to move forward/backward
        _camPos += GetLookDir() * (delta / 120f) * _moveSpeed;
    }

    public void HandleKey(Key key, bool down)
    {
        if (down) _keysDown.Add(key);
        else      _keysDown.Remove(key);
    }

    // ── Camera queries (for gizmo) ─────────────────────────────────────────────

    public Vector3 LookDirection => GetLookDir();

    public Vector3 UpDirection
    {
        get
        {
            var look  = GetLookDir();
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

        // ── Frame delta + WASD movement ───────────────────────────────────────
        float dt = (float)_frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        dt = MathF.Min(dt, 0.1f);  // cap to avoid jump after pause

        if (_keysDown.Count > 0)
        {
            var look  = GetLookDir();
            var right = Vector3.Cross(look, Vector3.UnitY).Normalized();
            float spd = _moveSpeed * dt;
            if (_keysDown.Contains(Key.W)) _camPos += look  * spd;
            if (_keysDown.Contains(Key.S)) _camPos -= look  * spd;
            if (_keysDown.Contains(Key.A)) _camPos -= right * spd;
            if (_keysDown.Contains(Key.D)) _camPos += right * spd;
        }

        // Process pending mutations
        if (_pendingClear)    { DoClear();          _pendingClear = false; }
        if (_pendingLoad  != null) { DoLoad(_pendingLoad);  _pendingLoad  = null; }
        if (_pendingBones != null) { DoBones(_pendingBones); _pendingBones = null; }

        GL.Viewport(0, 0, fbWidth, fbHeight);
        GL.ClearColor(0.07f, 0.07f, 0.09f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var view   = GetViewMatrix();
        float aspect = fbHeight > 0 ? (float)fbWidth / fbHeight : 1f;
        var proj   = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.001f, 200_000f);
        var vp     = view * proj;

        // ── Ground grid (semi-transparent, before model so model renders on top)
        if (_gridVao != 0)
        {
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            GL.UseProgram(_gridProg);
            GL.UniformMatrix4(_uGridMvp,    false, ref vp);
            GL.Uniform3(_uGridCamPos,  _camPos);
            GL.Uniform1(_uGridFadeEnd, _gridFadeEnd);
            GL.BindVertexArray(_gridVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount);
            GL.BindVertexArray(0);

            GL.Disable(EnableCap.Blend);
        }

        // ── World axes ────────────────────────────────────────────────────────
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
        GL.Disable(EnableCap.CullFace);
        foreach (var sm in _submeshes)
        {
            if (!sm.FinalVisible || !sm.HasAlpha) continue;
            DrawSubmesh(sm, vp);
        }
        GL.Enable(EnableCap.CullFace);

        // ── Outline pass (selected submeshes) ─────────────────────────────────
        GL.UseProgram(_outlineProg);
        GL.CullFace(TriangleFace.Front);
        GL.Uniform1(_uOutlineScale, _shellScale);
        GL.Uniform4(_uOutlineColor, new Vector4(1.0f, 0.83f, 0.0f, 1.0f));
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
        DeleteGrid();
        if (_meshProg    != 0) GL.DeleteProgram(_meshProg);
        if (_lineProg    != 0) GL.DeleteProgram(_lineProg);
        if (_outlineProg != 0) GL.DeleteProgram(_outlineProg);
        if (_gridProg    != 0) GL.DeleteProgram(_gridProg);
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
        _gridProg    = CompileProgram(Path.Combine(shaderDir, "grid.vert"),    Path.Combine(shaderDir, "grid.frag"));

        // Mesh uniforms
        _uMeshMvp      = GL.GetUniformLocation(_meshProg,    "uMVP");
        _uMeshModel    = GL.GetUniformLocation(_meshProg,    "uModel");
        _uMeshTex      = GL.GetUniformLocation(_meshProg,    "uTex");
        _uMeshHasTex   = GL.GetUniformLocation(_meshProg,    "uHasTex");
        _uMeshLightDir = GL.GetUniformLocation(_meshProg,    "uLightDir");
        _uMeshAmbient  = GL.GetUniformLocation(_meshProg,    "uAmbient");

        // Line uniforms
        _uLineMvp   = GL.GetUniformLocation(_lineProg, "uMVP");
        _uLineColor = GL.GetUniformLocation(_lineProg, "uColor");

        // Outline uniforms
        _uOutlineMvp   = GL.GetUniformLocation(_outlineProg, "uMVP");
        _uOutlineScale = GL.GetUniformLocation(_outlineProg, "uScale");
        _uOutlineColor = GL.GetUniformLocation(_outlineProg, "uOutlineColor");

        // Grid uniforms
        _uGridMvp     = GL.GetUniformLocation(_gridProg, "uMVP");
        _uGridCamPos  = GL.GetUniformLocation(_gridProg, "uCamPos");
        _uGridFadeEnd = GL.GetUniformLocation(_gridProg, "uFadeEnd");

        BuildAxisLines();
        BuildGridLines(1.0f);   // default scale; rebuilt when model loads
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

    // ── World axes: X (red), Z (blue) — very long for near-infinite look ──────

    private void BuildAxisLines()
    {
        DeleteLineBatches(_axisBatches);
        _axisBatches.Clear();

        const float Len = 50_000f;
        (float[] pts, Vector4 col)[] axes =
        [
            ([-Len, 0f, 0f,   Len, 0f, 0f],   new Vector4(0.86f, 0.25f, 0.25f, 1f)), // X red  (좌우)
            ([0f, 0f, -Len,   0f, 0f,  Len],   new Vector4(0.24f, 0.51f, 0.86f, 1f)), // Z blue (앞뒤)
        ];
        foreach (var (pts, col) in axes)
            _axisBatches.Add(UploadLineBatch(pts, PrimitiveType.Lines, col, 1.5f));
    }

    // ── Ground grid: XZ plane, distance-fade to look near-infinite ───────────

    private void BuildGridLines(float modelSize)
    {
        DeleteGrid();

        float step   = MathF.Max(modelSize / 5f, 0.001f);
        float extent = step * 200f;   // 200 steps each way
        _gridFadeEnd = extent * 0.8f;

        var pts = new List<float>();

        // Lines parallel to X (vary Z)
        for (float z = -extent; z <= extent + step * 0.5f; z += step)
            pts.AddRange([-extent, 0f, z,   extent, 0f, z]);

        // Lines parallel to Z (vary X)
        for (float x = -extent; x <= extent + step * 0.5f; x += step)
            pts.AddRange([x, 0f, -extent,   x, 0f, extent]);

        float[] arr = [.. pts];

        GL.GenVertexArrays(1, out _gridVao);
        GL.BindVertexArray(_gridVao);
        GL.GenBuffers(1, out _gridVbo);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        _gridVertexCount = arr.Length / 3;
    }

    private void DeleteGrid()
    {
        if (_gridVao != 0) { GL.DeleteVertexArray(_gridVao); _gridVao = 0; }
        if (_gridVbo != 0) { GL.DeleteBuffer(_gridVbo);      _gridVbo = 0; }
        _gridVertexCount = 0;
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

            int texId = 0;
            bool hasAlpha = false;
            if (data.Textures.TryGetValue(sm.MaterialIndex, out var bmp) && bmp != null)
                texId = UploadTexture(bmp, out hasAlpha);

            _submeshes.Add(new SubmeshGpu
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
                UserVisible  = true,
                FinalVisible = true,
            });
        }

        // Bounding box → camera placement + grid scale
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
            var center = new Vector3((mnX+mxX)*0.5f, (mnY+mxY)*0.5f, (mnZ+mxZ)*0.5f);

            // Position camera in front (+Z side), slightly above model center
            float dist = maxDim * 2.0f;
            _camPos    = center + new Vector3(0f, maxDim * 0.1f, dist);
            _camYaw    = MathF.PI;   // look toward −Z (front of character)
            _camPitch  = -0.05f;
            _moveSpeed = maxDim * 0.5f;

            BuildGridLines(maxDim);
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

        // Skeleton joint dots
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

    private Vector3 GetLookDir()
    {
        float cosPitch = MathF.Cos(_camPitch);
        return new Vector3(
            cosPitch * MathF.Sin(_camYaw),
            MathF.Sin(_camPitch),
            cosPitch * MathF.Cos(_camYaw));
    }

    private Matrix4 GetViewMatrix()
    {
        var look = GetLookDir();
        return Matrix4.LookAt(_camPos, _camPos + look, Vector3.UnitY);
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

        BitmapSource src = bmp;
        if (src.Format != PixelFormats.Bgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[h * stride];
        src.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length && !hasAlpha; i += 64)
            if (pixels[i] < 250) hasAlpha = true;

        GL.GenTextures(1, out int texId);
        GL.BindTexture(TextureTarget.Texture2D, texId);
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
            if (Vao   != 0) GL.DeleteVertexArray(Vao);
            if (Vbo   != 0) GL.DeleteBuffer(Vbo);
            if (Ibo   != 0) GL.DeleteBuffer(Ibo);
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
