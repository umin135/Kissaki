using System.Numerics;

namespace KissakiViewer.Core.Formats;

// ── Data model ───────────────────────────────────────────────────────────────

public sealed class G1mBone
{
    public int        ParentIndex    { get; set; } = -1;
    public Vector3    LocalPosition  { get; set; }
    public Quaternion LocalRotation  { get; set; } = Quaternion.Identity;
    public Vector3    LocalScale     { get; set; } = Vector3.One;
    public Matrix4x4  WorldMatrix    { get; set; } = Matrix4x4.Identity;
}

public sealed class G1mSubmesh
{
    public Vector3[] Positions     { get; set; } = [];
    public Vector3[] Normals       { get; set; } = [];
    public Vector2[] TexCoords     { get; set; } = [];
    public int[]     Indices       { get; set; } = [];
    public int       MaterialIndex { get; set; } = -1;
    public uint      MatPalId      { get; set; }
}

public sealed class G1mData
{
    public G1mBone[]    Bones      { get; set; } = [];
    public G1mSubmesh[] Submeshes  { get; set; } = [];
    public Vector3      BoundsMin  { get; set; }
    public Vector3      BoundsMax  { get; set; }
    public List<(uint Sig, int Offset, int Size)> Chunks { get; } = [];
    public byte[]?      G1mmRaw    { get; set; }
    public byte[]?      G1mfRaw    { get; set; }
    // All G1MG section IDs found (including unknown ones)
    public List<(uint Id, int Offset, int Size)> G1mgSections { get; } = [];
    // Raw bytes of specific G1MG sections for analysis (keyed by section id)
    public Dictionary<uint, byte[]> G1mgSectionRaw { get; } = [];
    // material index → COLOR texture slot index within the G1T file (from sec 0x10002)
    public List<(int MatIdx, int G1tSlot)> MaterialTextures { get; } = [];
}

// ── Parser ───────────────────────────────────────────────────────────────────

public static class G1mReader
{
    // File/chunk signatures
    private const uint SIG_G1M  = 0x47314D5F; // "_G1M"
    private const uint SIG_G1MF = 0x47314D46; // "G1MF"
    private const uint SIG_G1MS = 0x47314D53; // "G1MS"
    private const uint SIG_G1MG = 0x47314D47; // "G1MG"
    private const uint SIG_G1MM = 0x47314D4D; // "G1MM"

    // G1MG section IDs
    private const uint SEC_VERTICES  = 0x10004;
    private const uint SEC_LAYOUTS   = 0x10005;
    private const uint SEC_INDICES   = 0x10007;
    private const uint SEC_SUBMESHES = 0x10008;

    // G1MGSemantic.semantic
    private const ushort SEM_POSITION = 0;
    private const ushort SEM_NORMAL   = 3;
    private const ushort SEM_TEXCOORD = 5;

    // G1MGSemantic.data_type
    private const ushort DT_FLOAT2 = 1;
    private const ushort DT_FLOAT3 = 2;
    private const ushort DT_FLOAT4 = 3;
    private const ushort DT_UBYTE4 = 5;
    private const ushort DT_HALF2  = 10;
    private const ushort DT_HALF4  = 11;

    public static G1mData? Read(byte[] data)
    {
        if (data.Length < 0x18) return null;
        if (ReadU32(data, 0) != SIG_G1M) return null;

        uint headerSize = ReadU32(data, 0x0C);
        uint numChunks  = ReadU32(data, 0x14);

        var result = new G1mData();
        int pos = (int)headerSize;

        for (int c = 0; c < numChunks && pos + 0xC <= data.Length; c++)
        {
            uint chunkSig  = ReadU32(data, pos);
            uint chunkSize = ReadU32(data, pos + 8);
            int  chunkEnd  = pos + (int)chunkSize;

            result.Chunks.Add((chunkSig, pos, (int)chunkSize));

            if (chunkSig == SIG_G1MF)
            {
                result.G1mfRaw = new byte[(int)chunkSize];
                Buffer.BlockCopy(data, pos, result.G1mfRaw, 0, Math.Min((int)chunkSize, data.Length - pos));
            }
            else if (chunkSig == SIG_G1MS) ReadG1MS(data, pos, result);
            else if (chunkSig == SIG_G1MG) ReadG1MG(data, pos, result);
            else if (chunkSig == SIG_G1MM)
            {
                result.G1mmRaw = new byte[(int)chunkSize];
                Buffer.BlockCopy(data, pos, result.G1mmRaw, 0, Math.Min((int)chunkSize, data.Length - pos));
            }

            pos = chunkEnd;
        }

        ComputeWorldMatrices(result.Bones);
        return result;
    }

    // ── G1MS ─────────────────────────────────────────────────────────────────

    // G1MSChunkHeader (0x1C):
    //   +00 sig +04 ver +08 size +0C bones_offset +10 unk
    //   +14 num_bones u16 +16 num_indices u16 +18 num_parents u16 +1A unk u16
    // G1MSBone (0x30):
    //   +00 scale[3] f32  +0C parent u16  +0E flags u16
    //   +10 rotation[4] f32 (xyzw)  +20 position[4] f32 (xyz,w=0)
    private static void ReadG1MS(byte[] data, int cs, G1mData r)
    {
        if (cs + 0x1C > data.Length) return;
        uint   bonesOffset = ReadU32(data, cs + 0x0C);
        ushort numBones    = ReadU16(data, cs + 0x14);
        if (numBones == 0) return;

        int bp = cs + (int)bonesOffset;
        var bones = new G1mBone[numBones];
        for (int i = 0; i < numBones; i++)
        {
            int o = bp + i * 0x30;
            if (o + 0x30 > data.Length) break;

            float sx = ReadF32(data, o + 0x00), sy = ReadF32(data, o + 0x04), sz = ReadF32(data, o + 0x08);
            ushort parent = ReadU16(data, o + 0x0C);
            float rx = ReadF32(data, o + 0x10), ry = ReadF32(data, o + 0x14),
                  rz = ReadF32(data, o + 0x18), rw = ReadF32(data, o + 0x1C);
            float px = ReadF32(data, o + 0x20), py = ReadF32(data, o + 0x24), pz = ReadF32(data, o + 0x28);

            bones[i] = new G1mBone
            {
                ParentIndex   = parent == 0xFFFF ? -1 : (int)parent,
                LocalPosition = new Vector3(px, py, pz),
                LocalRotation = new Quaternion(rx, ry, rz, rw),
                LocalScale    = new Vector3(sx, sy, sz),
            };
        }
        r.Bones = bones;
    }

    // ── G1MG ─────────────────────────────────────────────────────────────────

    // G1MGChunkHeader (0x30):
    //   +00 sig +04 ver +08 size +0C platform +10 unk
    //   +14 min_x/y/z f32  +20 max_x/y/z f32  +2C num_sections u32
    // Sections after header: [sectionId u32][sectionSize u32 (incl. 8B header)][...data...]
    private static void ReadG1MG(byte[] data, int cs, G1mData r)
    {
        if (cs + 0x30 > data.Length) return;

        r.BoundsMin = new Vector3(ReadF32(data, cs+0x14), ReadF32(data, cs+0x18), ReadF32(data, cs+0x1C));
        r.BoundsMax = new Vector3(ReadF32(data, cs+0x20), ReadF32(data, cs+0x24), ReadF32(data, cs+0x28));

        // numSections is at +0x2C in older G1MG versions; newer versions (with LLOC/ONUN chunks)
        // insert an extra 4-byte field, pushing numSections to +0x30.
        uint numSections = ReadU32(data, cs + 0x2C);
        int  hdrEnd      = cs + 0x30;
        if (numSections == 0 && cs + 0x34 <= data.Length)
        {
            uint alt = ReadU32(data, cs + 0x30);
            if (alt > 0 && alt < 64) { numSections = alt; hdrEnd = cs + 0x34; }
        }
        int pos = hdrEnd;

        var vbs      = new List<VertexBuffer>();
        var layouts  = new List<LayoutEntry>();
        var ibs      = new List<IndexBuffer>();
        var rawSubs  = new List<RawSubmesh>();

        for (int s = 0; s < numSections && pos + 8 <= data.Length; s++)
        {
            uint secId   = ReadU32(data, pos);
            uint secSize = ReadU32(data, pos + 4);
            int  secEnd  = pos + (int)secSize;
            int  secData = pos + 8;

            r.G1mgSections.Add((secId, pos, (int)secSize));

            // Capture raw bytes of specific sections for analysis / parsing
            const uint SEC_DUMP_0 = 0x10001;
            const uint SEC_DUMP_1 = 0x10002; // material/texture mapping
            const uint SEC_DUMP_2 = 0x10006;
            const uint SEC_DUMP_3 = 0x10009;
            if (secId == SEC_DUMP_0 || secId == SEC_DUMP_1 || secId == SEC_DUMP_2 || secId == SEC_DUMP_3)
            {
                var raw = new byte[(int)secSize];
                Buffer.BlockCopy(data, pos, raw, 0, Math.Min((int)secSize, data.Length - pos));
                r.G1mgSectionRaw[secId] = raw;
            }

            if (secId == SEC_DUMP_1) ReadMaterialSection(data, secData, secEnd, r);

            if (secId == SEC_VERTICES)  ReadVertexSection (data, secData, secEnd, vbs);
            else if (secId == SEC_LAYOUTS)   ReadLayoutSection (data, secData, secEnd, layouts);
            else if (secId == SEC_INDICES)   ReadIndexSection  (data, secData, secEnd, ibs);
            else if (secId == SEC_SUBMESHES) ReadSubmeshSection(data, secData, secEnd, rawSubs);

            pos = secEnd;
        }

        r.Submeshes = BuildSubmeshes(rawSubs, vbs, layouts, ibs);
    }

    // ── Section readers ───────────────────────────────────────────────────────

    // Section 0x10002: material → texture mapping
    // Layout (confirmed from DOA6 raw bytes):
    //   [8B sec header already skipped — secData starts here]
    //   uint32 mat_count
    //   per material:
    //     uint32 unk0
    //     uint32 tex_count
    //     uint32 unk1
    //     uint32 unk2
    //     tex_count × 12B entries:
    //       uint16 tex_index  ← index into G1T texture array
    //       uint16 layer      (usually 0)
    //       uint16 tex_type   (1=COLOR/albedo, 2=NORMAL, 3=SPEC, 5=DIRT, ...)
    //       uint16 unk_a
    //       uint16 tile_x
    //       uint16 tile_y
    private static void ReadMaterialSection(byte[] data, int start, int end, G1mData r)
    {
        if (start + 4 > end) return;
        uint matCount = ReadU32(data, start);
        if (matCount == 0 || matCount > 512) return;
        int pos = start + 4;

        for (int matIdx = 0; matIdx < matCount && pos + 16 <= end; matIdx++)
        {
            uint texCount = ReadU32(data, pos + 4);
            if (texCount > 64) return;
            pos += 16;

            for (uint t = 0; t < texCount && pos + 12 <= end; t++, pos += 12)
            {
                ushort texIndex = ReadU16(data, pos + 0);
                ushort texType  = ReadU16(data, pos + 4);
                if (texType == 1) // COLOR = albedo in DOA6
                    r.MaterialTextures.Add((matIdx, (int)texIndex));
            }
        }
    }

    private static void ReadVertexSection(byte[] data, int start, int end, List<VertexBuffer> vbs)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int pos = start + 4;

        for (int i = 0; i < count && pos + 0x10 <= end; i++)
        {
            // G1MGVertexBufHeader (0x10): flags, vertex_size, num_vertex, unk_0C
            uint flags      = ReadU32(data, pos);
            uint vertexSize = ReadU32(data, pos + 4);
            uint numVerts   = ReadU32(data, pos + 8);
            pos += 0x10;

            // vertex_size == 1 → group marker (no data)
            if (vertexSize <= 1) { vbs.Add(new VertexBuffer { IsGroup = true }); continue; }

            int dataSize = (int)(vertexSize * numVerts);
            var vb = new VertexBuffer { VertexSize = (int)vertexSize, NumVertices = (int)numVerts, Flags = flags };
            vb.Data = new byte[dataSize];
            Buffer.BlockCopy(data, pos, vb.Data, 0, Math.Min(dataSize, end - pos));
            vbs.Add(vb);
            pos += dataSize;
        }
    }

    private static void ReadLayoutSection(byte[] data, int start, int end, List<LayoutEntry> layouts)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int pos = start + 4;

        for (int i = 0; i < count && pos < end; i++)
        {
            if (pos + 4 > end) break;
            uint numRefs = ReadU32(data, pos); pos += 4;

            var refs = new uint[numRefs];
            for (int r = 0; r < numRefs && pos + 4 <= end; r++, pos += 4)
                refs[r] = ReadU32(data, pos);

            if (pos + 4 > end) break;
            uint numSems = ReadU32(data, pos); pos += 4;

            var sems = new Semantic[numSems];
            for (int j = 0; j < numSems && pos + 8 <= end; j++, pos += 8)
            {
                sems[j] = new Semantic
                {
                    BufferIndex = ReadU16(data, pos),
                    Offset      = ReadU16(data, pos + 2),
                    DataType    = ReadU16(data, pos + 4),
                    Kind        = ReadU16(data, pos + 6),
                };
            }
            layouts.Add(new LayoutEntry { Refs = refs, Semantics = sems });
        }
    }

    private static void ReadIndexSection(byte[] data, int start, int end, List<IndexBuffer> ibs)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int pos = start + 4;

        for (int i = 0; i < count && pos + 0xC <= end; i++)
        {
            pos = Align4(pos);
            if (pos + 0xC > end) break;

            // G1MGIndexBufferHeader (0xC): num_indices, type, unk
            uint numIdx = ReadU32(data, pos);
            uint type   = ReadU32(data, pos + 4);
            pos += 0xC;

            int indexSize = type switch { 8 => 1, 0x10 => 2, 0x20 => 4, _ => 2 };
            int dataSize  = (int)(numIdx * indexSize);

            var ib = new IndexBuffer { NumIndices = (int)numIdx, IndexSize = indexSize };
            ib.Data = new byte[dataSize];
            Buffer.BlockCopy(data, pos, ib.Data, 0, Math.Min(dataSize, end - pos));
            ibs.Add(ib);
            pos = Align4(pos + dataSize);
        }
    }

    private static void ReadSubmeshSection(byte[] data, int start, int end, List<RawSubmesh> subs)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int pos = start + 4;

        // G1MGSubmesh (0x38 = 14 × 4B)
        for (int i = 0; i < count && pos + 0x38 <= end; i++, pos += 0x38)
        {
            subs.Add(new RawSubmesh
            {
                Flags          = ReadU32(data, pos + 0x00),
                VertexBufRef   = ReadI32(data, pos + 0x04),
                BoneMapIndex   = ReadI32(data, pos + 0x08),
                MatPalId       = ReadU32(data, pos + 0x0C),
                Unk10          = ReadU32(data, pos + 0x10),
                Attribute      = ReadI32(data, pos + 0x14),
                Material       = ReadI32(data, pos + 0x18),
                IndexBufRef    = ReadI32(data, pos + 0x1C),
                Unk20          = ReadU32(data, pos + 0x20),
                IndexBufFmt    = ReadU32(data, pos + 0x24),
                VertexBufStart = ReadU32(data, pos + 0x28),
                NumVertices    = ReadU32(data, pos + 0x2C),
                IndexBufStart  = ReadU32(data, pos + 0x30),
                NumIndices     = ReadU32(data, pos + 0x34),
            });
        }
    }

    // ── Build submesh geometry ────────────────────────────────────────────────

    private static G1mSubmesh[] BuildSubmeshes(
        List<RawSubmesh> rawSubs, List<VertexBuffer> vbs,
        List<LayoutEntry> layouts, List<IndexBuffer> ibs)
    {
        // Build ref[0] → layout map for fast lookup
        var refToLayout = new Dictionary<uint, LayoutEntry>();
        foreach (var lay in layouts)
            if (lay.Refs.Length > 0 && !refToLayout.ContainsKey(lay.Refs[0]))
                refToLayout[lay.Refs[0]] = lay;

        var result = new List<G1mSubmesh>();
        foreach (var rs in rawSubs)
        {
            if (rs.VertexBufRef < 0 || rs.VertexBufRef >= vbs.Count) continue;
            if (rs.IndexBufRef  < 0 || rs.IndexBufRef  >= ibs.Count) continue;

            var vb = vbs[rs.VertexBufRef];
            var ib = ibs[rs.IndexBufRef];
            if (vb.IsGroup || vb.Data == null || ib.Data == null) continue;

            // Find layout: first try refs[0] == vertex buffer index, then attribute field
            LayoutEntry? layout = null;
            refToLayout.TryGetValue((uint)rs.VertexBufRef, out layout);
            if (layout == null && rs.Attribute >= 0 && rs.Attribute < layouts.Count)
                layout = layouts[rs.Attribute];
            if (layout == null) continue;

            // Find POSITION / NORMAL / TEXCOORD semantics
            Semantic? posSem = null, normSem = null, uvSem = null;
            foreach (var sem in layout.Semantics)
            {
                switch (sem.Kind)
                {
                    case SEM_POSITION: posSem  ??= sem; break;
                    case SEM_NORMAL:   normSem ??= sem; break;
                    case SEM_TEXCOORD: uvSem   ??= sem; break;
                }
            }
            if (posSem == null) continue;

            int numVerts = (int)rs.NumVertices;
            int vStart   = (int)rs.VertexBufStart;
            int vs       = vb.VertexSize;

            // For multi-stream layouts, semantic.BufferIndex → refs[bufferIndex] → actual VB
            // We resolve each semantic's VB separately.
            var positions = new Vector3[numVerts];
            var normals   = new Vector3[numVerts];
            var uvs       = new Vector2[numVerts];

            for (int vi = 0; vi < numVerts; vi++)
            {
                if (posSem  != null) positions[vi] = ReadVec3(data: GetVertexData(vbs, layout, posSem,  rs), vi: vStart + vi, stride: GetStride(vbs, layout, posSem),  offset: posSem.Offset,  dt: posSem.DataType);
                if (normSem != null) normals  [vi] = ReadVec3(data: GetVertexData(vbs, layout, normSem, rs), vi: vStart + vi, stride: GetStride(vbs, layout, normSem), offset: normSem.Offset, dt: normSem.DataType);
                if (uvSem   != null) uvs      [vi] = ReadVec2(data: GetVertexData(vbs, layout, uvSem,   rs), vi: vStart + vi, stride: GetStride(vbs, layout, uvSem),   offset: uvSem.Offset,   dt: uvSem.DataType);
            }

            // Index buffer
            int numIdx   = (int)rs.NumIndices;
            int idxStart = (int)rs.IndexBufStart;
            var indices  = new int[numIdx];
            for (int ii = 0; ii < numIdx; ii++)
            {
                int iOff = (idxStart + ii) * ib.IndexSize;
                if (iOff + ib.IndexSize > ib.Data.Length) break;
                indices[ii] = ib.IndexSize switch
                {
                    1 => ib.Data[iOff],
                    2 => (int)ReadU16(ib.Data, iOff),
                    _ => (int)ReadU32(ib.Data, iOff),
                };
            }

            result.Add(new G1mSubmesh
            {
                Positions     = positions,
                Normals       = normals,
                TexCoords     = uvs,
                Indices       = indices,
                MaterialIndex = rs.Material,
                MatPalId      = rs.MatPalId,
            });
        }
        return [.. result];
    }

    // For multi-stream: semantic.BufferIndex → layout.Refs[bufferIndex] → vertex buffer
    private static byte[]? GetVertexData(List<VertexBuffer> vbs, LayoutEntry layout, Semantic sem, RawSubmesh rs)
    {
        int vbIdx = sem.BufferIndex < layout.Refs.Length
            ? (int)layout.Refs[sem.BufferIndex]
            : rs.VertexBufRef;
        return vbIdx >= 0 && vbIdx < vbs.Count ? vbs[vbIdx].Data : null;
    }

    private static int GetStride(List<VertexBuffer> vbs, LayoutEntry layout, Semantic sem)
    {
        int vbIdx = sem.BufferIndex < layout.Refs.Length
            ? (int)layout.Refs[sem.BufferIndex]
            : -1;
        return (vbIdx >= 0 && vbIdx < vbs.Count) ? vbs[vbIdx].VertexSize : 0;
    }

    // ── Vector reading ────────────────────────────────────────────────────────

    private static Vector3 ReadVec3(byte[]? data, int vi, int stride, int offset, ushort dt)
    {
        if (data == null || stride <= 0) return Vector3.Zero;
        int o = vi * stride + offset;
        if (o < 0 || o + 4 > data.Length) return Vector3.Zero;

        return dt switch
        {
            DT_FLOAT3 or DT_FLOAT4 => new Vector3(ReadF32(data, o), ReadF32(data, o+4), ReadF32(data, o+8)),
            DT_HALF2               => new Vector3(ReadF16(data, o), ReadF16(data, o+2), 0),
            DT_HALF4               => new Vector3(ReadF16(data, o), ReadF16(data, o+2), ReadF16(data, o+4)),
            DT_UBYTE4              => new Vector3(
                data[o]   / 127.5f - 1f,
                data[o+1] / 127.5f - 1f,
                data[o+2] / 127.5f - 1f),
            _ => Vector3.Zero,
        };
    }

    private static Vector2 ReadVec2(byte[]? data, int vi, int stride, int offset, ushort dt)
    {
        if (data == null || stride <= 0) return Vector2.Zero;
        int o = vi * stride + offset;
        if (o < 0 || o + 4 > data.Length) return Vector2.Zero;

        return dt switch
        {
            DT_HALF2  => new Vector2(ReadF16(data, o), ReadF16(data, o+2)),
            DT_FLOAT2 => new Vector2(ReadF32(data, o), ReadF32(data, o+4)),
            _ => Vector2.Zero,
        };
    }

    // ── World matrix ──────────────────────────────────────────────────────────

    private static void ComputeWorldMatrices(G1mBone[] bones)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            var b = bones[i];
            var local = Matrix4x4.CreateScale(b.LocalScale)
                      * Matrix4x4.CreateFromQuaternion(b.LocalRotation)
                      * Matrix4x4.CreateTranslation(b.LocalPosition);

            b.WorldMatrix = (b.ParentIndex >= 0 && b.ParentIndex < i)
                ? local * bones[b.ParentIndex].WorldMatrix
                : local;
        }
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    private static uint   ReadU32(byte[] b, int o) => (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);
    private static int    ReadI32(byte[] b, int o) => b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24;
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | b[o+1]<<8);
    private static float  ReadF32(byte[] b, int o) => BitConverter.ToSingle(b, o);
    private static float  ReadF16(byte[] b, int o) => (float)BitConverter.UInt16BitsToHalf(ReadU16(b, o));
    private static int    Align4(int v) => (v + 3) & ~3;

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class VertexBuffer
    {
        public bool    IsGroup     { get; set; }
        public int     VertexSize  { get; set; }
        public int     NumVertices { get; set; }
        public uint    Flags       { get; set; }
        public byte[]? Data        { get; set; }
    }

    private sealed class IndexBuffer
    {
        public int     NumIndices { get; set; }
        public int     IndexSize  { get; set; }
        public byte[]? Data       { get; set; }
    }

    private sealed class Semantic
    {
        public ushort BufferIndex { get; set; }
        public ushort Offset      { get; set; }
        public ushort DataType    { get; set; }
        public ushort Kind        { get; set; }
    }

    private sealed class LayoutEntry
    {
        public uint[]     Refs      { get; set; } = [];
        public Semantic[] Semantics { get; set; } = [];
    }

    private sealed class RawSubmesh
    {
        public uint Flags, MatPalId, Unk10, Unk20, IndexBufFmt;
        public int  VertexBufRef, BoneMapIndex, Attribute, Material, IndexBufRef;
        public uint VertexBufStart, NumVertices, IndexBufStart, NumIndices;
    }
}
