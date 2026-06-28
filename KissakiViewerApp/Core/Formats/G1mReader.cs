using System.Numerics;
using KissakiViewer;

namespace KissakiViewer.Core.Formats;

// ── Data model ────────────────────────────────────────────────────────────────

// One NUNO1 cloth entry: parent bone index + local-space control points.
// Cloth vertices blend across these CPs (transformed to world space via the parent bone).
public sealed class NunoEntry
{
    public int       ParentBoneId  { get; set; }
    public Vector4[] ControlPoints { get; set; } = [];
}

public sealed class G1mBone
{
    public int        ParentIndex     { get; set; } = -1;
    public Vector3    LocalPosition   { get; set; }
    public Quaternion LocalRotation   { get; set; } = Quaternion.Identity;
    public Vector3    LocalScale      { get; set; } = Vector3.One;
    public Matrix4x4  WorldMatrix     { get; set; } = Matrix4x4.Identity;
    // Scale-free world transform — matches RDB Explorer ComputeBoneWorldTransforms.
    // Used for cloth CP transform to avoid scale contamination in the rotation part.
    public Vector3    WorldPosition   { get; set; }
    public Quaternion WorldQuaternion { get; set; } = Quaternion.Identity;
}

public sealed class G1mSubmesh
{
    public Vector3[]   Positions     { get; set; } = [];
    public Vector3[]   Normals       { get; set; } = [];
    public Vector2[]   TexCoords     { get; set; } = [];   // channel 0 (primary UV)
    public Vector2[][] AllTexCoords  { get; set; } = [];   // all channels: [0]=primary, [1]=secondary, …
    public int[]       Indices       { get; set; } = [];
    public int         MaterialIndex { get; set; } = -1;
    public uint        MatPalId      { get; set; }
}

public sealed class G1mData
{
    public G1mBone[]    Bones      { get; set; } = [];
    public G1mSubmesh[] Submeshes  { get; set; } = [];
    public NunoEntry[]  NunoEntries { get; set; } = [];
    public Vector3      BoundsMin  { get; set; }
    public Vector3      BoundsMax  { get; set; }
    public List<(uint Sig, int Offset, int Size)> Chunks { get; } = [];
    public byte[]?      G1mmRaw    { get; set; }
    public byte[]?      G1mfRaw    { get; set; }
    // All G1MG section IDs found (including unknown ones)
    public List<(uint Id, int Offset, int Size)> G1mgSections { get; } = [];
    // Raw bytes of specific G1MG sections for analysis (keyed by section id)
    public Dictionary<uint, byte[]> G1mgSectionRaw { get; } = [];
    // material index → texture slot bindings from sec 0x10002
    // TexType: 1=COLOR, 2=NORMAL, 3=SPEC, 4=ROUGHNESS, 5=DIRT, ...
    public List<(int MatIdx, int G1tSlot, int UvLayer, int TexType)> MaterialTextures { get; } = [];
    // G1MS BoneIDList: BoneIdList[nunoParentID] → bone array index
    public ushort[]? BoneIdList { get; set; }
    // G1MG JOINTPALETTES: per-submesh bone palette for LBS skinning
    public List<uint[]> BonePalettes { get; } = new();
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
    private const uint SIG_NUNO = 0x4E554E4F; // "NUNO"

    // Inner section magic for NUNO1 entries
    private const uint NUNO_SEC_NUNO1 = 0x00030001;

    // G1M outer version thresholds for NUNO dataOffset (from RDB Explorer ParseNuno1Entry).
    // The version read from NUNO_CHUNK+4 is the G1M file version as an ASCII u32 (e.g. "0039").
    // DOA6 version = 0x30303239 satisfies BOTH conditions → dataOffset = entryStart + 24 + 0x3C + 0x20
    private const uint NUVER_GT_0023 = 0x30303233; // version > this → +0x10
    private const uint NUVER_GE_0025 = 0x30303235; // version >= this → +0x10

    // G1MG section type values (low 16 bits of the combined type+version uint).
    // The full section header u32 = (version<<16)|type — we mask to compare only the type.
    private const uint SEC_SECTION1      = 1;
    private const uint SEC_MATERIALS     = 2;
    private const uint SEC_PROPSET       = 3;
    private const uint SEC_VERTICES      = 4;
    private const uint SEC_LAYOUTS       = 5;
    private const uint SEC_JOINTPALETTES = 6;
    private const uint SEC_INDICES       = 7;
    private const uint SEC_SUBMESHES     = 8;
    private const uint SEC_MESHGROUPS    = 9;

    // MeshGroups version thresholds (compare against G1MG version field)
    private const uint MGVER_GT_0300 = 0x30303330; // "0300"
    private const uint MGVER_GT_0400 = 0x30303430; // "0400"

    // G1MGSemantic.semantic (low byte of Kind field)
    private const byte SEM_POSITION     = 0;
    private const byte SEM_BLENDWEIGHT  = 1;
    private const byte SEM_BLENDINDICES = 2;
    private const byte SEM_NORMAL       = 3;
    private const byte SEM_PSIZE        = 4;
    private const byte SEM_TEXCOORD     = 5;
    private const byte SEM_TANGENT      = 6;
    private const byte SEM_BITANGENT    = 7;
    private const byte SEM_COLOR        = 10; // secondary combination weights (cpWeights2 row-combine)
    private const byte SEM_FOG          = 11; // 3rd CP index set for cloth deformation

    // G1MGSemantic.data_type (EG1MGVADatatype enum values)
    private const ushort DT_FLOAT1    = 0x00;
    private const ushort DT_FLOAT2    = 0x01;
    private const ushort DT_FLOAT3    = 0x02;
    private const ushort DT_FLOAT4    = 0x03;
    private const ushort DT_UBYTE4    = 0x05;
    private const ushort DT_USHORT4   = 0x07;
    private const ushort DT_HALF2     = 0x0A;
    private const ushort DT_HALF4     = 0x0B;
    private const ushort DT_NORMBYTE4 = 0x0D;

    public static G1mData? Read(byte[] data)
    {
        if (data.Length < 0x18) return null;
        if (ReadU32(data, 0) != SIG_G1M) return null;

        uint headerSize = ReadU32(data, 0x0C);
        uint numChunks  = ReadU32(data, 0x14);

        var result       = new G1mData();
        var pendingCloth = new List<PendingCloth>();
        var pendingRigid = new List<PendingRigidSkin>();
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
            else if (chunkSig == SIG_G1MG) ReadG1MG(data, pos, result, pendingCloth, pendingRigid);
            else if (chunkSig == SIG_G1MM)
            {
                result.G1mmRaw = new byte[(int)chunkSize];
                Buffer.BlockCopy(data, pos, result.G1mmRaw, 0, Math.Min((int)chunkSize, data.Length - pos));
            }
            else if (chunkSig == SIG_NUNO) ParseNunoChunk(data, pos, result);

            pos = chunkEnd;
        }

        ComputeWorldMatrices(result.Bones);
        if (pendingRigid.Count > 0) ApplyRigidSkinning(result, pendingRigid);
        if (pendingCloth.Count > 0) ApplyNunoTransform(result, pendingCloth);
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
        ushort numIndices  = ReadU16(data, cs + 0x16);

        // BoneIDList at cs+0x1C: maps NUNO parentID → bone array index
        if (numIndices > 0)
        {
            var boneIdList = new ushort[numIndices];
            int blPos = cs + 0x1C;
            for (int i = 0; i < numIndices && blPos + 2 <= data.Length; i++, blPos += 2)
                boneIdList[i] = ReadU16(data, blPos);
            r.BoneIdList = boneIdList;
        }

        if (numBones == 0) return;

        int bp = cs + (int)bonesOffset;
        var bones = new G1mBone[numBones];
        for (int i = 0; i < numBones; i++)
        {
            int o = bp + i * 0x30;
            if (o + 0x30 > data.Length) break;

            float sx = ReadF32(data, o + 0x00), sy = ReadF32(data, o + 0x04), sz = ReadF32(data, o + 0x08);
            int parent = ReadI32(data, o + 0x0C);   // stored as int32; -1 (0xFFFFFFFF) = root
            float rx = ReadF32(data, o + 0x10), ry = ReadF32(data, o + 0x14),
                  rz = ReadF32(data, o + 0x18), rw = ReadF32(data, o + 0x1C);
            float px = ReadF32(data, o + 0x20), py = ReadF32(data, o + 0x24), pz = ReadF32(data, o + 0x28);

            bones[i] = new G1mBone
            {
                ParentIndex   = parent < 0 ? -1 : parent,
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
    private static void ReadG1MG(byte[] data, int cs, G1mData r, List<PendingCloth> pendingCloth, List<PendingRigidSkin> pendingRigid)
    {
        if (cs + 0x30 > data.Length) return;

        uint g1mgVersion = ReadU32(data, cs + 0x04);

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
        int mgSecData = -1, mgSecEnd = -1;

        for (int s = 0; s < numSections && pos + 8 <= data.Length; s++)
        {
            uint secId   = ReadU32(data, pos);
            uint secSize = ReadU32(data, pos + 4);
            int  secEnd  = pos + (int)secSize;
            int  secData = pos + 8;
            // Section header = [type: u16 | version: u16 | size: u32 | count: u32]
            // Mask to low 16 bits so version differences don't break dispatch.
            uint secType = secId & 0xFFFF;

            r.G1mgSections.Add((secId, pos, (int)secSize));

            // Capture raw bytes of specific sections for analysis / parsing
            if (secType == SEC_SECTION1 || secType == SEC_MATERIALS || secType == SEC_JOINTPALETTES || secType == SEC_MESHGROUPS)
            {
                var raw = new byte[(int)secSize];
                Buffer.BlockCopy(data, pos, raw, 0, Math.Min((int)secSize, data.Length - pos));
                r.G1mgSectionRaw[secType] = raw;
            }

            if (secType == SEC_MATERIALS) ReadMaterialSection(data, secData, secEnd, r);

            if      (secType == SEC_VERTICES)   ReadVertexSection (data, secData, secEnd, vbs, g1mgVersion);
            else if (secType == SEC_LAYOUTS)    ReadLayoutSection (data, secData, secEnd, layouts);
            else if (secType == SEC_INDICES)    ReadIndexSection  (data, secData, secEnd, ibs, g1mgVersion);
            else if (secType == SEC_SUBMESHES)  ReadSubmeshSection(data, secData, secEnd, rawSubs);
            else if (secType == SEC_JOINTPALETTES) ParseJointPalettes(data, secData, secEnd, r);
            else if (secType == SEC_MESHGROUPS) { mgSecData = secData; mgSecEnd = secEnd; }

            pos = secEnd;
        }

        var clothInfo = mgSecData >= 0
            ? ParseMeshGroupClothIds(data, mgSecData, mgSecEnd, g1mgVersion)
            : new Dictionary<int, (int clothId, int extId)>();

        r.Submeshes = BuildSubmeshes(rawSubs, vbs, layouts, ibs, clothInfo, pendingCloth, pendingRigid);
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
                ushort uvLayer  = ReadU16(data, pos + 2);
                ushort texType  = ReadU16(data, pos + 4);
                r.MaterialTextures.Add((matIdx, (int)texIndex, (int)uvLayer, (int)texType));
            }
        }
    }

    // version > 0x30303430 ("0400") → VB header has extra 4-byte field → 16 bytes total; else 12 bytes
    private static void ReadVertexSection(byte[] data, int start, int end, List<VertexBuffer> vbs, uint version)
    {
        if (start + 4 > end) return;
        int count = (int)ReadU32(data, start);
        int pos   = start + 4;
        int hdr   = version > 0x30303430 ? 0x10 : 0x0C;
        int total = 0;

        while (total < count && pos + hdr <= end)
        {
            // G1MGVertexBufHeader: unknown1(4) + stride(4) + vCount(4) [+ unk_0C(4) if version>"0400"]
            uint flags      = ReadU32(data, pos);
            uint vertexSize = ReadU32(data, pos + 4);
            uint numVerts   = ReadU32(data, pos + 8);
            pos += hdr;
            total++;

            if (vertexSize <= 1)
            {
                // stride=1: raw repository buffer — sub-buffers with flag 0x80000000 slice from this data
                int repoSize = (int)numVerts;
                byte[] repoData = new byte[Math.Min(repoSize, Math.Max(0, end - pos))];
                if (repoData.Length > 0)
                    Buffer.BlockCopy(data, pos, repoData, 0, repoData.Length);
                vbs.Add(new VertexBuffer { IsGroup = true });
                pos += repoSize;

                int accOffset = 0;
                while (total < count && pos + 4 <= end)
                {
                    if (ReadU32(data, pos) != 0x80000000) break;
                    if (pos + hdr > end) break;

                    int subStride = (int)ReadU32(data, pos + 4);
                    int subCount  = (int)ReadU32(data, pos + 8);
                    pos += hdr;
                    total++;

                    int subBytes = subStride * subCount;
                    byte[] subData = new byte[subBytes];
                    int avail = repoData.Length - accOffset;
                    if (avail > 0)
                        Buffer.BlockCopy(repoData, accOffset, subData, 0, Math.Min(subBytes, avail));
                    accOffset += subBytes;

                    vbs.Add(new VertexBuffer { VertexSize = subStride, NumVertices = subCount, Data = subData });
                }
                continue;
            }

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

    // version > 0x30303430 ("0400") → IB header has extra 4-byte field → 12 bytes total; else 8 bytes
    private static void ReadIndexSection(byte[] data, int start, int end, List<IndexBuffer> ibs, uint version)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int  pos   = start + 4;
        int  hdr   = version > 0x30303430 ? 0x0C : 0x08;

        for (int i = 0; i < count && pos + hdr <= end; i++)
        {
            pos = Align4(pos);
            if (pos + hdr > end) break;

            // G1MGIndexBufferHeader: numIndices(4) + bitWidth(4) [+ unk(4) if version>"0400"]
            uint numIdx = ReadU32(data, pos);
            uint type   = ReadU32(data, pos + 4);  // bit-width: 16=2B, 32=4B
            pos += hdr;

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
                PrimType       = ReadU32(data, pos + 0x24),
                VertexBufStart = ReadU32(data, pos + 0x28),
                NumVertices    = ReadU32(data, pos + 0x2C),
                IndexBufStart  = ReadU32(data, pos + 0x30),
                NumIndices     = ReadU32(data, pos + 0x34),
            });
        }
    }

    // ── MeshGroups cloth-ID parser ────────────────────────────────────────────
    //
    // Reads Section 9 (MeshGroups) and returns a map: submeshIndex → (clothId, extId).
    // clothId values: 0=rigid, 1=NUNO cloth, 2=physics cloth.
    // extId for NUNO cloth: typically 20000+n where n is the NUNO entry index.
    private static Dictionary<int, (int clothId, int extId)> ParseMeshGroupClothIds(
        byte[] data, int start, int end, uint version)
    {
        var result = new Dictionary<int, (int, int)>();
        if (start + 4 > end) return result;

        int groupCount = (int)ReadU32(data, start);
        int pos = start + 4;

        for (int g = 0; g < groupCount; g++)
        {
            int sm1, sm2;

            if (version > MGVER_GT_0400)        // DOA6: 36-byte group header
            {
                if (pos + 20 > end) break;
                sm1 = (int)ReadU32(data, pos + 12);
                sm2 = (int)ReadU32(data, pos + 16);
                pos += 36;
            }
            else if (version > MGVER_GT_0300)   // 20-byte group header
            {
                if (pos + 20 > end) break;
                sm1 = (int)ReadU32(data, pos + 12);
                sm2 = (int)ReadU32(data, pos + 16);
                pos += 20;
            }
            else                                // 12-byte group header
            {
                if (pos + 12 > end) break;
                sm1 = (int)ReadU32(data, pos + 4);
                sm2 = (int)ReadU32(data, pos + 8);
                pos += 12;
            }

            int meshCount = sm1 + sm2;
            for (int m = 0; m < meshCount; m++)
            {
                if (pos + 28 > end) break;
                // Mesh entry: name[16] + clothId(u16) + unk(u16) + externalId(u32) + idxCount(u32)
                int clothId  = ReadU16(data, pos + 16);
                int extId    = (int)ReadU32(data, pos + 20);
                int idxCount = (int)ReadU32(data, pos + 24);
                pos += 28;

                if (idxCount > 0)
                {
                    for (int i = 0; i < idxCount && pos + 4 <= end; i++, pos += 4)
                    {
                        int smIdx = (int)ReadU32(data, pos);
                        result.TryAdd(smIdx, (clothId, extId));
                    }
                }
                else
                {
                    if (pos + 4 <= end) pos += 4;  // skip dummy u32
                }
            }
        }

        return result;
    }

    // ── Build submesh geometry ────────────────────────────────────────────────

    private static G1mSubmesh[] BuildSubmeshes(
        List<RawSubmesh> rawSubs, List<VertexBuffer> vbs,
        List<LayoutEntry> layouts, List<IndexBuffer> ibs,
        Dictionary<int, (int clothId, int extId)> clothInfo,
        List<PendingCloth> pendingCloth,
        List<PendingRigidSkin> pendingRigid)
    {
        var result = new List<G1mSubmesh>();
        for (int smIdx = 0; smIdx < rawSubs.Count; smIdx++)
        {
            var rs = rawSubs[smIdx];

            clothInfo.TryGetValue(smIdx, out var ci);
            int clothId = ci.clothId;
            int extId   = ci.extId;

            if (clothId == 2) continue;  // physics cloth: skip (no NUNO transform defined)

            if (rs.VertexBufRef < 0 || rs.VertexBufRef >= layouts.Count) continue;
            if (rs.IndexBufRef  < 0 || rs.IndexBufRef  >= ibs.Count) continue;

            LayoutEntry layout = layouts[rs.VertexBufRef];
            var ib = ibs[rs.IndexBufRef];
            if (ib.Data == null) continue;

            int numVerts = (int)rs.NumVertices;
            int vStart   = (int)rs.VertexBufStart;

            // Collect semantics
            Semantic? posSem = null, normSem = null, biIdxSem = null, bwSem = null;
            var uvSems = new SortedList<int, Semantic>();
            foreach (var sem in layout.Semantics)
            {
                switch (sem.Type)
                {
                    case SEM_POSITION:     posSem   ??= sem; break;
                    case SEM_NORMAL:       normSem  ??= sem; break;
                    case SEM_BLENDINDICES: biIdxSem ??= sem; break;
                    case SEM_BLENDWEIGHT:  bwSem    ??= sem; break;
                    case SEM_TEXCOORD:
                        // UB4 texcoords are CP index sets (cloth idx4), NOT UV channels
                        if (sem.DataType != DT_UBYTE4 && !uvSems.ContainsKey(sem.Layer))
                            uvSems[sem.Layer] = sem;
                        break;
                }
            }
            if (posSem == null) continue;

            // Build shared data: index array and UV channels
            int[]       indices       = BuildIndexArray(rs, ib, vStart);
            Vector2[][] allUvChannels = BuildUvChannels(vbs, layout, uvSems, numVerts, vStart);

            if (clothId == 1)
            {
                // NUNO cloth deformation (RDB Explorer formula):
                //   cpWeights1 = POSITION (U-basis blend weights, sum~1)
                //   cpWeights2 = BITANGENT (V-basis tangent weights, sum~0)
                //   comWeights1 = BLENDWEIGHT (row combination weights)
                //   comWeights2 = COLOR layer1 (secondary row combination, optional)
                //   idx1..4     = BLENDINDICES / PSIZE / FOG / TEXCOORD-layer5 (CP index sets)
                //   normalDepth = NORMAL (xyz=localNorm, w=depth)
                // For each vertex: u_k = GetPoint(idx_k, cpW1); v_k = GetPoint(idx_k, cpW2)
                //   a = Σ(u_k × cW1_k); c = Σ(v_k × cW1_k)
                //   b = Σ(u_k × cW2_k) if COLOR_1 else a
                //   d = normalize(cross(normalize(b), normalize(c)))
                //   finalPos = a + d × depth

                Semantic? cpW1Sem = layout.Semantics.FirstOrDefault(s => s.Type == SEM_POSITION);
                Semantic? cpW2Sem = layout.Semantics.FirstOrDefault(s => s.Type == SEM_BITANGENT);
                Semantic? cW1Sem  = layout.Semantics.FirstOrDefault(s => s.Type == SEM_BLENDWEIGHT);
                Semantic? cW2Sem  = layout.Semantics.FirstOrDefault(s => s.Type == SEM_COLOR && s.Layer == 1);
                // biIdxSem = idx1 (already found above)
                Semantic? i2Sem   = layout.Semantics.FirstOrDefault(s => s.Type == SEM_PSIZE);
                Semantic? i3Sem   = layout.Semantics.FirstOrDefault(s => s.Type == SEM_FOG);
                Semantic? i4Sem   = layout.Semantics.FirstOrDefault(s => s.Type == SEM_TEXCOORD && s.Layer == 5 && s.DataType == DT_UBYTE4);

                static byte[]? VD(List<VertexBuffer> vb, LayoutEntry ly, Semantic? s) =>
                    s != null ? GetVertexData(vb, ly, s) : null;
                static int ST(List<VertexBuffer> vb, LayoutEntry ly, Semantic? s) =>
                    s != null ? GetStride(vb, ly, s) : 0;

                byte[]? cpW1D = VD(vbs, layout, cpW1Sem); int cpW1S = ST(vbs, layout, cpW1Sem);
                byte[]? cpW2D = VD(vbs, layout, cpW2Sem); int cpW2S = ST(vbs, layout, cpW2Sem);
                byte[]? cW1D  = VD(vbs, layout, cW1Sem);  int cW1St = ST(vbs, layout, cW1Sem);
                byte[]? cW2D  = VD(vbs, layout, cW2Sem);  int cW2St = ST(vbs, layout, cW2Sem);
                byte[]? i1D   = VD(vbs, layout, biIdxSem);int i1S   = ST(vbs, layout, biIdxSem);
                byte[]? i2D   = VD(vbs, layout, i2Sem);   int i2S   = ST(vbs, layout, i2Sem);
                byte[]? i3D   = VD(vbs, layout, i3Sem);   int i3S   = ST(vbs, layout, i3Sem);
                byte[]? i4D   = VD(vbs, layout, i4Sem);   int i4S   = ST(vbs, layout, i4Sem);
                byte[]? ndD   = VD(vbs, layout, normSem);  int ndS   = ST(vbs, layout, normSem);

                var cpW1Arr = new Vector4[numVerts];
                var cpW2Arr = new Vector4[numVerts];
                var cW1Arr  = new Vector4[numVerts];
                var cW2Arr  = cW2Sem != null ? new Vector4[numVerts] : [];
                var i1Arr   = new BlendIdx4[numVerts];
                var i2Arr   = i2Sem  != null ? new BlendIdx4[numVerts] : [];
                var i3Arr   = i3Sem  != null ? new BlendIdx4[numVerts] : [];
                var i4Arr   = i4Sem  != null ? new BlendIdx4[numVerts] : [];
                var ndArr   = new Vector4[numVerts];

                ushort cpW1Dt = cpW1Sem?.DataType ?? DT_FLOAT4;
                ushort cpW2Dt = cpW2Sem?.DataType ?? DT_FLOAT4;
                ushort cW1Dt  = cW1Sem?.DataType  ?? DT_FLOAT4;
                ushort cW2Dt  = cW2Sem?.DataType  ?? DT_FLOAT4;
                ushort i1Dt   = biIdxSem?.DataType ?? DT_UBYTE4;
                ushort i2Dt   = i2Sem?.DataType   ?? DT_UBYTE4;
                ushort i3Dt   = i3Sem?.DataType   ?? DT_UBYTE4;
                ushort i4Dt   = i4Sem?.DataType   ?? DT_UBYTE4;
                ushort ndDt   = normSem?.DataType  ?? DT_FLOAT4;
                int cpW1Of = cpW1Sem?.Offset ?? 0;
                int cpW2Of = cpW2Sem?.Offset ?? 0;
                int cW1Of  = cW1Sem?.Offset  ?? 0;
                int cW2Of  = cW2Sem?.Offset  ?? 0;
                int i1Of   = biIdxSem?.Offset ?? 0;
                int i2Of   = i2Sem?.Offset   ?? 0;
                int i3Of   = i3Sem?.Offset   ?? 0;
                int i4Of   = i4Sem?.Offset   ?? 0;
                int ndOf   = normSem?.Offset  ?? 0;

                for (int vi = 0; vi < numVerts; vi++)
                {
                    cpW1Arr[vi] = ReadVec4(cpW1D, vStart+vi, cpW1S, cpW1Of, cpW1Dt);
                    cpW2Arr[vi] = ReadVec4(cpW2D, vStart+vi, cpW2S, cpW2Of, cpW2Dt);
                    cW1Arr[vi]  = ReadVec4(cW1D,  vStart+vi, cW1St, cW1Of,  cW1Dt);
                    if (cW2Sem != null) cW2Arr[vi] = ReadVec4(cW2D, vStart+vi, cW2St, cW2Of, cW2Dt);
                    i1Arr[vi]   = ReadBlendIdx4(i1D, vStart+vi, i1S, i1Of, i1Dt);
                    if (i2Sem  != null) i2Arr[vi] = ReadBlendIdx4(i2D, vStart+vi, i2S, i2Of, i2Dt);
                    if (i3Sem  != null) i3Arr[vi] = ReadBlendIdx4(i3D, vStart+vi, i3S, i3Of, i3Dt);
                    if (i4Sem  != null) i4Arr[vi] = ReadBlendIdx4(i4D, vStart+vi, i4S, i4Of, i4Dt);
                    ndArr[vi]   = ReadVec4(ndD,   vStart+vi, ndS,  ndOf,  ndDt);
                }

                int submeshIdx = result.Count;
                result.Add(new G1mSubmesh
                {
                    Positions     = new Vector3[numVerts],  // filled in ApplyNunoTransform
                    Normals       = new Vector3[numVerts],
                    TexCoords     = allUvChannels.Length > 0 ? allUvChannels[0] : [],
                    AllTexCoords  = allUvChannels,
                    Indices       = indices,
                    MaterialIndex = rs.Material,
                    MatPalId      = rs.MatPalId,
                });

                int nunoIdx = extId >= 20000 ? extId % 20000 : extId;
                pendingCloth.Add(new PendingCloth
                {
                    NunoEntryIndex = nunoIdx,
                    SubmeshIndex   = submeshIdx,
                    CpWeights1     = cpW1Arr,
                    CpWeights2     = cpW2Arr,
                    ComWeights1    = cW1Arr,
                    ComWeights2    = cW2Arr,
                    Indices1       = i1Arr,
                    Indices2       = i2Arr,
                    Indices3       = i3Arr,
                    Indices4       = i4Arr,
                    NormalDepth    = ndArr,
                });
                continue;
            }

            // ── Rigid mesh path ───────────────────────────────────────────────
            var rawPos  = new Vector3[numVerts];
            var rawNorm = new Vector3[numVerts];
            for (int vi = 0; vi < numVerts; vi++)
            {
                rawPos[vi] = ReadVec3(GetVertexData(vbs, layout, posSem), vStart+vi, GetStride(vbs, layout, posSem), posSem.Offset, posSem.DataType);
                if (normSem != null)
                    rawNorm[vi] = ReadVec3(GetVertexData(vbs, layout, normSem), vStart+vi, GetStride(vbs, layout, normSem), normSem.Offset, normSem.DataType);
            }

            if (bwSem != null && biIdxSem != null)
            {
                // Vertices in bone-local space → defer LBS to ApplyRigidSkinning
                var bwD = GetVertexData(vbs, layout, bwSem);  int bwS = GetStride(vbs, layout, bwSem);
                var biD = GetVertexData(vbs, layout, biIdxSem); int biS = GetStride(vbs, layout, biIdxSem);
                var bws = new Vector4[numVerts];
                var bis = new BlendIdx4[numVerts];
                for (int vi = 0; vi < numVerts; vi++)
                {
                    bws[vi] = ReadVec4(bwD, vStart+vi, bwS, bwSem.Offset, bwSem.DataType);
                    bis[vi] = ReadBlendIdx4(biD, vStart+vi, biS, biIdxSem.Offset, biIdxSem.DataType);
                }
                int subIdx = result.Count;
                result.Add(new G1mSubmesh
                {
                    Positions     = new Vector3[numVerts],  // filled in ApplyRigidSkinning
                    Normals       = new Vector3[numVerts],
                    TexCoords     = allUvChannels.Length > 0 ? allUvChannels[0] : [],
                    AllTexCoords  = allUvChannels,
                    Indices       = indices,
                    MaterialIndex = rs.Material,
                    MatPalId      = rs.MatPalId,
                });
                pendingRigid.Add(new PendingRigidSkin
                {
                    SubmeshIndex = subIdx,
                    BoneMapIndex = rs.BoneMapIndex,
                    RawPositions = rawPos,
                    RawNormals   = rawNorm,
                    BlendWeights = bws,
                    BlendIndices = bis,
                });
            }
            else
            {
                result.Add(new G1mSubmesh
                {
                    Positions     = rawPos,
                    Normals       = rawNorm,
                    TexCoords     = allUvChannels.Length > 0 ? allUvChannels[0] : [],
                    AllTexCoords  = allUvChannels,
                    Indices       = indices,
                    MaterialIndex = rs.Material,
                    MatPalId      = rs.MatPalId,
                });
            }
        }
        return [.. result];
    }

    // ── NUNO chunk parser ─────────────────────────────────────────────────────

    private static void ParseNunoChunk(byte[] data, int cs, G1mData r)
    {
        // NUNO chunk layout (same as generic G1M outer chunk format):
        //   cs+0:  sig "NUNO" (4)
        //   cs+4:  G1M outer version as ASCII u32, e.g. 0x30303239="0039" (4)
        //   cs+8:  chunk size (4) — not needed
        //   cs+12: section count (4)
        //   cs+16: sections start
        if (cs + 16 > data.Length) return;

        uint outerVersion  = ReadU32(data, cs + 4);
        uint sectionCount  = ReadU32(data, cs + 12);

        var entries = new List<NunoEntry>();
        int pos = cs + 16;

        for (int s = 0; s < sectionCount && pos + 12 <= data.Length; s++)
        {
            uint secMagic   = ReadU32(data, pos);
            uint secSize    = ReadU32(data, pos + 4);
            uint entryCount = ReadU32(data, pos + 8);
            int  secEnd     = pos + (int)secSize;
            int  ep         = pos + 12;

            if (secMagic == NUNO_SEC_NUNO1)
            {
                for (int j = 0; j < entryCount; j++)
                {
                    if (ep + 24 > secEnd || ep + 24 > data.Length) break;
                    int  entryStart = ep;
                    uint parentId   = ReadU32(data, ep);
                    uint cpCount    = ReadU32(data, ep + 4);
                    uint unkSecs    = ReadU32(data, ep + 8);
                    uint skip1      = ReadU32(data, ep + 12);
                    uint skip2      = ReadU32(data, ep + 16);
                    uint skip3      = ReadU32(data, ep + 20);

                    // dataOffset per RDB Explorer ParseNuno1Entry (version from NUNO outer chunk)
                    int dataOff = entryStart + 24 + 0x3C;
                    if (outerVersion > NUVER_GT_0023) dataOff += 0x10;
                    if (outerVersion >= NUVER_GE_0025) dataOff += 0x10;

                    if (cpCount == 0 || cpCount > 1024 || dataOff + (int)cpCount * 16 > data.Length)
                    {
                        ep = secEnd; break;  // bad entry — skip remainder of section
                    }

                    var cps = new Vector4[cpCount];
                    for (int k = 0; k < cpCount; k++)
                    {
                        int o = dataOff + k * 16;
                        cps[k] = new Vector4(ReadF32(data, o), ReadF32(data, o+4), ReadF32(data, o+8), ReadF32(data, o+12));
                    }
                    entries.Add(new NunoEntry { ParentBoneId = (int)parentId, ControlPoints = cps });

                    // Advance: re-derive dataOff so ep calculation is self-contained
                    int nextBase = entryStart + 24 + 0x3C;
                    if (outerVersion > NUVER_GT_0023) nextBase += 0x10;
                    if (outerVersion >= NUVER_GE_0025) nextBase += 0x10;
                    ep = nextBase + (int)(cpCount * 16 + cpCount * 24 + 48 * unkSecs + 4 * (skip1 + skip2 + skip3));
                }
            }

            pos = secEnd;
        }

        r.NunoEntries = [.. entries];
        AppLogger.Info($"[NUNO] Parsed {entries.Count} NUNO1 entries");
        for (int i = 0; i < entries.Count; i++)
            AppLogger.Info($"[NUNO]   [{i}] parentId={entries[i].ParentBoneId} cpCount={entries[i].ControlPoints.Length}");
    }

    // ── NUNO cloth vertex transform ───────────────────────────────────────────

    // NUNO cloth vertex deformation (RDB Explorer reference formula):
    //   For each vertex, 4 sets of CP indices define a bilinear patch on the cloth surface.
    //   cpWeights1 (POSITION) blends within each row → position vectors u1..u4
    //   cpWeights2 (BITANGENT) blends within each row → tangent vectors v1..v4
    //   comWeights1 (BLENDWEIGHT) combines rows → a = surface position, c = V-tangent
    //   comWeights2 (COLOR_1) optional → b = U-tangent (fallback: b = a)
    //   d = normalize(cross(normalize(b), normalize(c))) = cloth surface normal
    //   finalPos = a + d * depth
    private static Vector3 GetClothPoint(BlendIdx4 idx, Vector4 weights, Vector3[] wCPs)
    {
        var res = Vector3.Zero;
        if (weights.X != 0 && idx.I0 < wCPs.Length) res += wCPs[idx.I0] * weights.X;
        if (weights.Y != 0 && idx.I1 < wCPs.Length) res += wCPs[idx.I1] * weights.Y;
        if (weights.Z != 0 && idx.I2 < wCPs.Length) res += wCPs[idx.I2] * weights.Z;
        if (weights.W != 0 && idx.I3 < wCPs.Length) res += wCPs[idx.I3] * weights.W;
        return res;
    }

    private static void ApplyNunoTransform(G1mData r, List<PendingCloth> pending)
    {
        foreach (var pc in pending)
        {
            int nunoIdx = pc.NunoEntryIndex;
            if (nunoIdx < 0 || nunoIdx >= r.NunoEntries.Length) continue;
            var entry = r.NunoEntries[nunoIdx];

            int listIdx = entry.ParentBoneId;
            int boneId;
            if (r.BoneIdList != null && (uint)listIdx < (uint)r.BoneIdList.Length)
            {
                boneId = r.BoneIdList[listIdx];
                if (boneId == 0xFFFF) boneId = 0;
            }
            else
                boneId = listIdx;
            if ((uint)boneId >= (uint)r.Bones.Length) continue;
            var bone     = r.Bones[boneId];
            var bonePos  = bone.WorldPosition;
            var boneQuat = bone.WorldQuaternion;

            var sub = r.Submeshes[pc.SubmeshIndex];
            int n   = Math.Min(pc.CpWeights1.Length, sub.Positions.Length);

            // ── Cloth vertex transform ───────────────────────────────────────────
            // Per-vertex RIVET detection (matches RDB Explorer exactly):
            //   BITANGENT channel = zero  →  bone-attached (RIVET): bonePos + boneRot * localPos
            //   BITANGENT channel ≠ zero  →  cloth simulation: full basis-deformation formula
            // Using submesh-level detection was wrong; a single submesh can mix both types.
            var wCPs = new Vector3[entry.ControlPoints.Length];
            for (int k = 0; k < wCPs.Length; k++)
            {
                var cp = entry.ControlPoints[k];
                wCPs[k] = bonePos + Vector3.Transform(new Vector3(cp.X, cp.Y, cp.Z), boneQuat);
            }

            bool hasI2  = pc.Indices2.Length == n;
            bool hasI3  = pc.Indices3.Length == n;
            bool hasI4  = pc.Indices4.Length == n;
            bool hasCW2 = pc.ComWeights2.Length == n;

            bool logCloth = pc.SubmeshIndex == 7 || pc.SubmeshIndex == 1;
            for (int vi = 0; vi < n; vi++)
            {
                var cpW1 = pc.CpWeights1[vi];
                var cpW2 = pc.CpWeights2.Length > vi ? pc.CpWeights2[vi] : Vector4.Zero;
                var cW1  = pc.ComWeights1[vi];
                var cW2  = hasCW2 ? pc.ComWeights2[vi] : Vector4.Zero;
                var i1   = pc.Indices1.Length > vi ? pc.Indices1[vi] : default;
                var i2   = hasI2 ? pc.Indices2[vi] : default;
                var i3   = hasI3 ? pc.Indices3[vi] : default;
                var i4   = hasI4 ? pc.Indices4[vi] : default;
                var nd   = pc.NormalDepth.Length > vi ? pc.NormalDepth[vi] : Vector4.Zero;

                // Per-vertex RIVET: BITANGENT = zero → bone-attached vertex (RDB Explorer criterion)
                if (cpW2.LengthSquared() < 1e-6f)
                {
                    sub.Positions[vi] = bonePos + Vector3.Transform(new Vector3(cpW1.X, cpW1.Y, cpW1.Z), boneQuat);
                    var wn2 = Vector3.Transform(new Vector3(nd.X, nd.Y, nd.Z), boneQuat);
                    sub.Normals[vi] = wn2.LengthSquared() > 1e-8f ? Vector3.Normalize(wn2) : Vector3.UnitY;
                    continue;
                }

                var u1 = GetClothPoint(i1, cpW1, wCPs);
                var u2 = GetClothPoint(i2, cpW1, wCPs);
                var u3 = GetClothPoint(i3, cpW1, wCPs);
                var u4 = GetClothPoint(i4, cpW1, wCPs);

                var v1 = GetClothPoint(i1, cpW2, wCPs);
                var v2 = GetClothPoint(i2, cpW2, wCPs);
                var v3 = GetClothPoint(i3, cpW2, wCPs);
                var v4 = GetClothPoint(i4, cpW2, wCPs);

                var a = u1*cW1.X + u2*cW1.Y + u3*cW1.Z + u4*cW1.W;
                var c = v1*cW1.X + v2*cW1.Y + v3*cW1.Z + v4*cW1.W;

                Vector3 b = (hasCW2 && cW2.LengthSquared() > 0f)
                    ? (u1*cW2.X + u2*cW2.Y + u3*cW2.Z + u4*cW2.W)
                    : a;

                float bLen = b.Length(), cLen = c.Length();
                if (bLen < 1e-6f || cLen < 1e-6f)
                {
                    sub.Positions[vi] = a;
                    sub.Normals[vi]   = Vector3.UnitY;
                    continue;
                }
                var bN = b / bLen;
                var cN = c / cLen;

                var crossVec = Vector3.Cross(bN, cN);
                float crossLen = crossVec.Length();
                if (crossLen < 1e-6f)
                {
                    sub.Positions[vi] = a;
                    sub.Normals[vi]   = bN;
                    continue;
                }
                var d = crossVec / crossLen;

                sub.Positions[vi] = a + d * nd.W;
                if (logCloth && vi < 5) AppLogger.Info($"[CLOTH] sm={pc.SubmeshIndex} v{vi} i1=({i1.I0},{i1.I1},{i1.I2},{i1.I3}) cpW1=({cpW1.X:F3},{cpW1.Y:F3},{cpW1.Z:F3},{cpW1.W:F3}) cW1=({cW1.X:F3},{cW1.Y:F3}) a=({a.X:F3},{a.Y:F3},{a.Z:F3}) depth={nd.W:F4} pos=({sub.Positions[vi].X:F3},{sub.Positions[vi].Y:F3},{sub.Positions[vi].Z:F3})");

                var localNorm = new Vector3(nd.X, nd.Y, nd.Z);
                var wNorm = bN * localNorm.Y + cN * localNorm.X + d * localNorm.Z;
                sub.Normals[vi] = wNorm.LengthSquared() > 1e-6f ? Vector3.Normalize(wNorm) : d;
            }
        }
    }

    // ── JOINTPALETTES section parser ──────────────────────────────────────────

    // Section 6 format: [count u32] then count palettes, each:
    //   [pCount u32] [pCount × 12B entries: G1MM_idx(skip) physIdx(skip) jointIdx]
    // jointIdx may have 0x80000000 flag (physics override); strip it to get actual bone index.
    private static void ParseJointPalettes(byte[] data, int start, int end, G1mData r)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int  pos   = start + 4;
        for (int i = 0; i < count && pos + 4 <= end; i++)
        {
            uint pCount = ReadU32(data, pos); pos += 4;
            var palette = new uint[pCount];
            for (int j = 0; j < pCount && pos + 12 <= end; j++, pos += 12)
            {
                // [+0] G1MM index (skip), [+4] physics idx (skip), [+8] joint idx
                uint jointIdx = ReadU32(data, pos + 8);
                if ((jointIdx & 0x80000000) != 0) jointIdx ^= 0x80000000;
                palette[j] = jointIdx;
            }
            r.BonePalettes.Add(palette);
        }
    }

    // ── Rigid mesh LBS transform ──────────────────────────────────────────────

    // Applies bone world matrices to rigid-mesh vertices that were deferred from BuildSubmeshes.
    // DOA6 stores vertex positions in bone-local space; RDB Explorer's vertex shader applies
    // pos_out = Σ weight[i] * bone_world[palette[blendIdx/3]] * pos_local directly (not skinning matrices).
    private static void ApplyRigidSkinning(G1mData r, List<PendingRigidSkin> pending)
    {
        foreach (var p in pending)
        {
            var sub  = r.Submeshes[p.SubmeshIndex];
            uint[]? pal = p.BoneMapIndex >= 0 && p.BoneMapIndex < r.BonePalettes.Count
                          ? r.BonePalettes[p.BoneMapIndex] : null;

            for (int vi = 0; vi < p.RawPositions.Length; vi++)
            {
                var rawP = p.RawPositions[vi];
                var rawN = p.RawNormals[vi];
                var bw   = p.BlendWeights[vi];
                var bi   = p.BlendIndices[vi];

                var pos  = Vector3.Zero;
                var nrm  = Vector3.Zero;
                float tw = 0f;

                for (int k = 0; k < 4; k++)
                {
                    float w = k switch { 0 => bw.X, 1 => bw.Y, 2 => bw.Z, _ => bw.W };
                    if (w <= 0f) continue;
                    int rawIdx  = k switch { 0 => bi.I0, 1 => bi.I1, 2 => bi.I2, _ => bi.I3 };
                    int palIdx  = rawIdx / 3;
                    int boneIdx = pal != null && (uint)palIdx < (uint)pal.Length ? (int)pal[palIdx] : palIdx;
                    if ((uint)boneIdx >= (uint)r.Bones.Length) continue;

                    var bone = r.Bones[boneIdx];
                    pos += Vector3.Transform(rawP, bone.WorldMatrix) * w;
                    nrm += Vector3.TransformNormal(rawN, bone.WorldMatrix) * w;
                    tw  += w;
                }

                if (tw > 1e-4f)
                {
                    sub.Positions[vi] = tw < 0.999f ? pos / tw : pos;
                    float nLen = nrm.Length();
                    sub.Normals[vi] = nLen > 1e-6f ? nrm / nLen : Vector3.UnitY;
                }
                else
                {
                    sub.Positions[vi] = rawP;
                    sub.Normals[vi]   = rawN;
                }
            }
        }
    }

    private static bool IsZeroIdx(BlendIdx4 idx) =>
        idx.I0 == 0 && idx.I1 == 0 && idx.I2 == 0 && idx.I3 == 0;

    // semantic.BufferIndex → layout.Refs[bufferIndex] → actual vertex buffer
    private static byte[]? GetVertexData(List<VertexBuffer> vbs, LayoutEntry layout, Semantic sem)
    {
        int vbIdx = sem.BufferIndex < layout.Refs.Length
            ? (int)layout.Refs[sem.BufferIndex]
            : layout.Refs.Length > 0 ? (int)layout.Refs[0] : -1;
        return vbIdx >= 0 && vbIdx < vbs.Count ? vbs[vbIdx].Data : null;
    }

    private static int GetStride(List<VertexBuffer> vbs, LayoutEntry layout, Semantic sem)
    {
        int vbIdx = sem.BufferIndex < layout.Refs.Length
            ? (int)layout.Refs[sem.BufferIndex]
            : layout.Refs.Length > 0 ? (int)layout.Refs[0] : -1;
        return vbIdx >= 0 && vbIdx < vbs.Count ? vbs[vbIdx].VertexSize : 0;
    }

    // ── Shared geometry builders ──────────────────────────────────────────────

    private static int[] BuildIndexArray(RawSubmesh rs, IndexBuffer ib, int vStart)
    {
        int numIdx   = (int)rs.NumIndices;
        int idxStart = (int)rs.IndexBufStart;
        var raw = new uint[numIdx];
        for (int ii = 0; ii < numIdx; ii++)
        {
            int iOff = (idxStart + ii) * ib.IndexSize;
            if (iOff + ib.IndexSize > ib.Data!.Length) break;
            raw[ii] = ib.IndexSize switch
            {
                1 => ib.Data[iOff],
                2 => ReadU16(ib.Data, iOff),
                _ => ReadU32(ib.Data, iOff),
            };
        }

        uint restartIdx = ib.IndexSize == 4 ? 0xFFFFFFFFu : 0xFFFFu;
        if (rs.PrimType == 4)  // triangle strip → triangle list
        {
            var list    = new List<int>(numIdx * 2);
            int winding = 0;
            for (int ii = 0; ii < numIdx - 2; ii++)
            {
                uint i0 = raw[ii], i1 = raw[ii+1], i2 = raw[ii+2];
                if (i0 == restartIdx || i1 == restartIdx || i2 == restartIdx) { winding = 0; continue; }
                if (i0 != i1 && i1 != i2 && i0 != i2)
                {
                    int r0 = (int)(i0 >= (uint)vStart ? i0 - (uint)vStart : i0);
                    int r1 = (int)(i1 >= (uint)vStart ? i1 - (uint)vStart : i1);
                    int r2 = (int)(i2 >= (uint)vStart ? i2 - (uint)vStart : i2);
                    if (winding % 2 == 0) { list.Add(r0); list.Add(r1); list.Add(r2); }
                    else                  { list.Add(r0); list.Add(r2); list.Add(r1); }
                }
                winding++;
            }
            return [.. list];
        }
        else  // triangle list
        {
            var list = new List<int>(numIdx);
            for (int ii = 0; ii + 2 < numIdx; ii += 3)
            {
                uint i0 = raw[ii], i1 = raw[ii+1], i2 = raw[ii+2];
                list.Add((int)(i0 >= (uint)vStart ? i0 - (uint)vStart : i0));
                list.Add((int)(i1 >= (uint)vStart ? i1 - (uint)vStart : i1));
                list.Add((int)(i2 >= (uint)vStart ? i2 - (uint)vStart : i2));
            }
            return [.. list];
        }
    }

    private static Vector2[][] BuildUvChannels(
        List<VertexBuffer> vbs, LayoutEntry layout, SortedList<int, Semantic> uvSems,
        int numVerts, int vStart)
    {
        var channels = new Vector2[uvSems.Count][];
        int ch = 0;
        foreach (var uvSem in uvSems.Values)
        {
            var chUvs    = new Vector2[numVerts];
            byte[]? uvData = GetVertexData(vbs, layout, uvSem);
            int uvStride   = GetStride(vbs, layout, uvSem);
            for (int vi = 0; vi < numVerts; vi++)
                chUvs[vi] = ReadVec2(uvData, vStart + vi, uvStride, uvSem.Offset, uvSem.DataType);
            channels[ch++] = chUvs;
        }
        return channels;
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
            DT_UBYTE4              => new Vector3(   // signed-normalized: 128=0, 0=-1, 255=+1
                data[o]   / 127.5f - 1f,
                data[o+1] / 127.5f - 1f,
                data[o+2] / 127.5f - 1f),
            DT_NORMBYTE4           => new Vector3(   // unsigned-normalized [0,1]
                data[o] / 255f,
                data[o+1] / 255f,
                data[o+2] / 255f),
            DT_USHORT4             => new Vector3(   // quantized u16 normalized [0,1]
                ReadU16(data, o)   / 65535f,
                ReadU16(data, o+2) / 65535f,
                ReadU16(data, o+4) / 65535f),
            _ => Vector3.Zero,
        };
    }

    private static Vector4 ReadVec4(byte[]? data, int vi, int stride, int offset, ushort dt)
    {
        if (data == null || stride <= 0) return Vector4.Zero;
        int o = vi * stride + offset;
        if (o < 0 || o + 4 > data.Length) return Vector4.Zero;

        return dt switch
        {
            DT_FLOAT4    => new Vector4(ReadF32(data,o), ReadF32(data,o+4), ReadF32(data,o+8), ReadF32(data,o+12)),
            DT_FLOAT3    => new Vector4(ReadF32(data,o), ReadF32(data,o+4), ReadF32(data,o+8), 0f),
            // NORMBYTE4: 4 unsigned bytes, each divided by 255 → [0,1] range (blend weights stored compactly)
            DT_NORMBYTE4 => new Vector4(data[o]/255f, data[o+1]/255f, data[o+2]/255f, data[o+3]/255f),
            // UBYTE4: raw unsigned bytes (used as-is, e.g. for cloth CP index channels)
            DT_UBYTE4    => new Vector4(data[o], data[o+1], data[o+2], data[o+3]),
            // HALF4: 4 packed 16-bit floats
            DT_HALF4     => o+8 <= data.Length
                                ? new Vector4(ReadF16(data,o), ReadF16(data,o+2), ReadF16(data,o+4), ReadF16(data,o+6))
                                : Vector4.Zero,
            _ => Vector4.Zero,
        };
    }

    private static BlendIdx4 ReadBlendIdx4(byte[]? data, int vi, int stride, int offset, ushort dt)
    {
        if (data == null || stride <= 0) return default;
        int o = vi * stride + offset;
        if (o < 0 || o + 4 > data.Length) return default;

        return dt switch
        {
            DT_UBYTE4  => new BlendIdx4(data[o], data[o+1], data[o+2], data[o+3]),
            DT_USHORT4 => o + 8 <= data.Length
                ? new BlendIdx4(ReadU16(data,o), ReadU16(data,o+2), ReadU16(data,o+4), ReadU16(data,o+6))
                : default,
            _ => default,
        };
    }

    private static Vector2 ReadVec2(byte[]? data, int vi, int stride, int offset, ushort dt)
    {
        if (data == null || stride <= 0) return Vector2.Zero;
        int o = vi * stride + offset;
        if (o < 0 || o + 4 > data.Length) return Vector2.Zero;

        return dt switch
        {
            DT_HALF2     => new Vector2(ReadF16(data, o), ReadF16(data, o+2)),
            DT_HALF4     => new Vector2(ReadF16(data, o), ReadF16(data, o+2)),
            DT_FLOAT2    => new Vector2(ReadF32(data, o), ReadF32(data, o+4)),
            DT_FLOAT4    => new Vector2(ReadF32(data, o), ReadF32(data, o+4)),
            DT_USHORT4   => new Vector2(ReadU16(data, o) / 65535f, ReadU16(data, o+2) / 65535f),
            DT_NORMBYTE4 => new Vector2(data[o] / 255f, data[o+1] / 255f),
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

            if (b.ParentIndex >= 0 && b.ParentIndex < i)
            {
                var p = bones[b.ParentIndex];
                b.WorldMatrix = local * p.WorldMatrix;
                // Scale-free world transform (RDB Explorer ComputeBoneWorldTransforms):
                //   worldPos[i] = worldPos[parent] + Transform(localPos, worldRot[parent])
                //   worldRot[i] = Normalize(worldRot[parent] * localRot)
                b.WorldPosition   = p.WorldPosition + Vector3.Transform(b.LocalPosition, p.WorldQuaternion);
                b.WorldQuaternion = Quaternion.Normalize(p.WorldQuaternion * b.LocalRotation);
            }
            else
            {
                b.WorldMatrix     = local;
                b.WorldPosition   = b.LocalPosition;
                b.WorldQuaternion = b.LocalRotation;
            }
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
        // Kind = (layer << 8) | semanticType
        public ushort Kind        { get; set; }
        public byte   Type        => (byte)(Kind & 0xFF);
        public byte   Layer       => (byte)(Kind >> 8);
    }

    private sealed class LayoutEntry
    {
        public uint[]     Refs      { get; set; } = [];
        public Semantic[] Semantics { get; set; } = [];
    }

    private sealed class RawSubmesh
    {
        public uint Flags, MatPalId, Unk10, Unk20, PrimType;
        public int  VertexBufRef, BoneMapIndex, Attribute, Material, IndexBufRef;
        public uint VertexBufStart, NumVertices, IndexBufStart, NumIndices;
    }

    // Stores raw cloth vertex data accumulated in BuildSubmeshes;
    // positions are filled in ApplyNunoTransform once NUNO entries and bone matrices are ready.
    private sealed class PendingCloth
    {
        public int         NunoEntryIndex { get; set; }
        public int         SubmeshIndex   { get; set; }
        public Vector4[]   CpWeights1  { get; set; } = [];  // POSITION: U-basis blend weights
        public Vector4[]   CpWeights2  { get; set; } = [];  // BITANGENT: V-basis tangent weights
        public Vector4[]   ComWeights1 { get; set; } = [];  // BLENDWEIGHT: row combination weights
        public Vector4[]   ComWeights2 { get; set; } = [];  // COLOR layer1: secondary row weights (optional)
        public BlendIdx4[] Indices1    { get; set; } = [];  // BLENDINDICES: row 0 CP indices
        public BlendIdx4[] Indices2    { get; set; } = [];  // PSIZE: row 1 CP indices
        public BlendIdx4[] Indices3    { get; set; } = [];  // FOG: row 2 CP indices
        public BlendIdx4[] Indices4    { get; set; } = [];  // TEXCOORD layer5: row 3 CP indices
        public Vector4[]   NormalDepth { get; set; } = [];  // NORMAL: xyz=localNorm, w=depth
    }

    // Stores raw rigid-mesh vertex data; positions are filled in ApplyRigidSkinning
    // once bone world matrices are ready (after ComputeWorldMatrices).
    private sealed class PendingRigidSkin
    {
        public int         SubmeshIndex { get; set; }
        public int         BoneMapIndex { get; set; }
        public Vector3[]   RawPositions { get; set; } = [];
        public Vector3[]   RawNormals   { get; set; } = [];
        public Vector4[]   BlendWeights { get; set; } = [];
        public BlendIdx4[] BlendIndices { get; set; } = [];
    }

    private readonly struct BlendIdx4(int i0, int i1, int i2, int i3)
    {
        public readonly int I0 = i0, I1 = i1, I2 = i2, I3 = i3;
    }
}
