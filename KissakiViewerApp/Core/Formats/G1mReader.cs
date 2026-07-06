using System.Numerics;
using KissakiViewer;

namespace KissakiViewer.Core.Formats;

// ── Data model ────────────────────────────────────────────────────────────────

// One NUNO cloth entry: parent bone index + control points.
// NUNO1 (DOA6): CPs are in local/bone space → transformed via parent bone (IsWorldSpace=false).
// NUNO4 (FF2):  CPs are already in world space → used directly (IsWorldSpace=true).
// NUNO5 (FF2/Yumia): CPs are in parent-bone local space; after bone transform they reach world
//   scale (~10-150 units). Vertex CP indices are LOCAL per-entry (same as NUNO1). However d=Cross(b,c)
//   must be normalized before depth displacement because of world-scale magnitudes. UseGlobalIndex=true
//   flags this normalization requirement; it does NOT change the CP indexing scheme.
public sealed class NunoEntry
{
    public int       ParentBoneId  { get; set; }
    public Vector4[] ControlPoints { get; set; } = [];
    // Per-CP parent index within this entry (P3 field of NunInfluence).
    // -1 (== 0xFFFFFFFF as signed) = root CP → parents the skeleton bone.
    // >= 0 = parents another CP at that index within this entry.
    public int[]     CpParents     { get; set; } = [];
    // NUNO4 (FF2): CPs are stored in world space; skip bone transform in ApplyNunoTransform.
    public bool      IsWorldSpace  { get; set; }
    // NUNO5 (FF2/Yumia): world-scale CPs (after bone transform) require d=Cross(b,c) normalization
    // before depth displacement. CP indexing is still LOCAL per-entry (same as NUNO1).
    public bool      UseGlobalIndex { get; set; }
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
    // 0=rigid, 1=NUNO cloth (overlaps rigid mesh with different UV), 2=physics cloth
    public int         ClothId       { get; set; }
    // LOD level from MeshGroups group header (pos+0 u32): 0=highest quality, 1=lower, …; -1=no group info
    public int         LodGroup      { get; set; } = -1;
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
    // TileX/TileY: texture repeat counts (e.g. 4 = tile 4× in U/V; apply before sampling)
    public List<(int MatIdx, int G1tSlot, int UvLayer, int TexType, int TileX, int TileY)> MaterialTextures { get; } = [];
    // G1MS BoneIDList: BoneIdList[globalID] → internal bone local index
    public ushort[]? BoneIdList { get; set; }
    // External skeleton support (models with both internal + external G1MS chunks)
    public bool HasExternalSkeleton { get; set; }
    public int InternalBoneCount { get; set; }
    public ushort[]? ExternalBoneIdList { get; set; }
    // G1MG JOINTPALETTES: per-submesh bone palette for LBS skinning
    public List<uint[]> BonePalettes { get; } = new();
    // G1MG JOINTPALETTES physics palette: per-palette physics bone indices (for clothId=2 submeshes)
    // Built from physIdx & 0xFFFF (middle field of each 12-byte entry).
    public List<uint[]> PhysicsPalettes { get; } = new();
    // Number of MeshGroups (Section 0x10009) groups; 0=no section, 1=single group (no real LOD), 2+=multiple LOD levels
    public int LodGroupCount { get; set; }
    // Index into Bones[] where NUNO control-point bones start (set by AppendNunoBones; 0 = not set).
    // Bones[0..NunoCpStartIndex-1] = skeleton, Bones[NunoCpStartIndex..] = NUNO CPs.
    public int NunoCpStartIndex { get; set; }
    // NunoEntries index where NUNO5 entries begin (= count of NUNO1+NUNO4 entries; 0 = no NUNO5 section).
    // Used by ApplyNunoTransform to remap extId >= 20000 → NunoEntries[PreNuno5Count + extId%20000].
    public int PreNuno5Count { get; set; }
    // Skeleton bone indices that serve as NUNO cloth entry parents (highlighted in overlay).
    public HashSet<int> NunoParentBoneIndices { get; } = [];
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

    // Inner section magic constants for NUNO sub-sections
    private const uint NUNO_SEC_NUNO1 = 0x00030001; // DOA6 / v0029 format
    private const uint NUNO_SEC_NUNO4 = 0x00030004; // FF2  / v0036 format
    private const uint NUNO_SEC_NUNO5 = 0x00030005; // FF2  / v0036 bone-chain cloth (432B hdr + bc×44B)

    // G1M outer version thresholds for NUNO dataOffset (from RDB Explorer ParseNuno1Entry).
    // The version read from NUNO_CHUNK+4 is the G1M file version as an ASCII u32 (e.g. "0039").
    // DOA6 version = 0x30303239 satisfies BOTH conditions → dataOffset = entryStart + 24 + 0x3C + 0x20
    private const uint NUVER_GT_0023 = 0x30303233; // version > this → +0x10
    private const uint NUVER_GE_0025 = 0x30303235; // version >= this → +0x10
    private const uint NUVER_GE_0030 = 0x30303330; // version >= "0030" → +0x20 (FF2 ver "0036")

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

        // G1MS selection: prefer internal skeleton (joints[0].parent != 0x80000000).
        // External skeleton is saved separately so its bones can be merged after internal is read.
        // ProjectG1M: builds globalToFinal map from all skeletons; we approximate by appending externals.
        bool      hasInternalG1MS    = false;
        G1mBone[]? externalBones     = null;
        ushort[]?  externalBoneIdList = null;
        uint[]?    externalRawParents = null;

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
            else if (chunkSig == SIG_G1MS && !hasInternalG1MS)
            {
                bool isInternal = IsInternalG1MS(data, pos);
                if (isInternal)
                {
                    ReadG1MS(data, pos, result);
                    result.InternalBoneCount = result.Bones.Length;
                    hasInternalG1MS = true;
                }
                else if (externalBones == null)
                {
                    // Save external skeleton; will be merged after internal is known.
                    (externalBones, externalBoneIdList, externalRawParents) = ReadG1MSRaw(data, pos);
                    if (externalBones.Length > 0)
                    {
                        result.HasExternalSkeleton = true;
                        result.ExternalBoneIdList  = externalBoneIdList;
                    }
                }
            }
            else if (chunkSig == SIG_G1MG) ReadG1MG(data, pos, result, pendingCloth, pendingRigid);
            else if (chunkSig == SIG_G1MM)
            {
                result.G1mmRaw = new byte[(int)chunkSize];
                Buffer.BlockCopy(data, pos, result.G1mmRaw, 0, Math.Min((int)chunkSize, data.Length - pos));
            }
            else if (chunkSig == SIG_NUNO) ParseNunoChunk(data, pos, result);

            pos = chunkEnd;
        }

        // Merge external skeleton bones (appended after internal).
        // bit 31 SET on raw parent = internal local index; bit 31 CLEAR = external local index.
        if (result.HasExternalSkeleton && externalBones != null && externalBones.Length > 0 && result.Bones.Length > 0)
        {
            int N = result.InternalBoneCount;
            for (int i = 0; i < externalBones.Length; i++)
            {
                uint rawP = externalRawParents != null && i < externalRawParents.Length
                            ? externalRawParents[i] : 0xFFFFFFFFu;
                externalBones[i].ParentIndex =
                    rawP == 0xFFFFFFFFu          ? -1 :
                    (rawP & 0x80000000u) != 0    ? (int)(rawP ^ 0x80000000u) :  // → internal[0..N-1]
                    N + (int)rawP;                                               // → external[N..N+M-1]
            }
            result.Bones = [.. result.Bones, .. externalBones];
            AppLogger.Info($"[G1MS] Merged external: internal={N} external={externalBones.Length} total={result.Bones.Length}");
        }

        AppLogger.Info($"[G1M] bones={result.Bones.Length} nunoEntries={result.NunoEntries.Length} pendingCloth={pendingCloth.Count} pendingRigid={pendingRigid.Count}");
        ComputeWorldMatrices(result.Bones);
        if (result.Bones.Length > 0)
        {
            var b0 = result.Bones[0];
            AppLogger.Info($"[G1M] bone[0] worldPos=({b0.WorldPosition.X:F3},{b0.WorldPosition.Y:F3},{b0.WorldPosition.Z:F3}) worldQuat=({b0.WorldQuaternion.X:F4},{b0.WorldQuaternion.Y:F4},{b0.WorldQuaternion.Z:F4},{b0.WorldQuaternion.W:F4})");
        }
        if (pendingRigid.Count > 0) ApplyRigidSkinning(result, pendingRigid);
        if (result.NunoEntries.Length > 0) AppendNunoBones(result);
        if (pendingCloth.Count > 0) ApplyNunoTransform(result, pendingCloth);
        // Log first position of first non-empty submesh after transforms
        for (int _si = 0; _si < result.Submeshes.Length; _si++)
        {
            var _sm = result.Submeshes[_si];
            if (_sm.Positions.Length > 0 && _sm.Positions[0] != System.Numerics.Vector3.Zero)
            {
                var _p = _sm.Positions[0];
                AppLogger.Info($"[G1M] SM[{_si}] cloth={_sm.ClothId} pos[0]=({_p.X:F3},{_p.Y:F3},{_p.Z:F3})");
                break;
            }
        }
        return result;
    }

    // Returns true if the G1MS chunk at cs has an internal skeleton.
    // External skeletons have joints[0].parent == 0x80000000 (attaches to outer body skeleton).
    private static bool IsInternalG1MS(byte[] data, int cs)
    {
        if (cs + 0x10 > data.Length) return true;
        uint version = ReadU32(data, cs + 4);
        if (version < 0x30303332) return true;           // old format: no external concept
        uint bonesOffset = ReadU32(data, cs + 0x0C);     // jointInfoOffset
        int parentPos = cs + (int)bonesOffset + 12;      // joints[0].parent (after 3×f32 scale)
        if (parentPos + 4 > data.Length) return true;
        return ReadU32(data, parentPos) != 0x80000000u;
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
            int rawParent = ReadI32(data, o + 0x0C);  // global bone ID; -1/0xFFFFFFFF = root
            float rx = ReadF32(data, o + 0x10), ry = ReadF32(data, o + 0x14),
                  rz = ReadF32(data, o + 0x18), rw = ReadF32(data, o + 0x1C);
            float px = ReadF32(data, o + 0x20), py = ReadF32(data, o + 0x24), pz = ReadF32(data, o + 0x28);

            // rawParent is already a LOCAL bone array index (not a global ID).
            // BoneIdList is used only for NUNO parentBoneId resolution, not for skeleton parent chains.
            int parent = rawParent < 0 ? -1 : rawParent;

            bones[i] = new G1mBone
            {
                ParentIndex   = parent,
                LocalPosition = new Vector3(px, py, pz),
                LocalRotation = new Quaternion(rx, ry, rz, rw),
                LocalScale    = new Vector3(sx, sy, sz),
            };
        }
        r.Bones = bones;

    }

    // Reads G1MS bone data without modifying r. Returns raw parent u32 values for post-merge fix-up.
    private static (G1mBone[] bones, ushort[] boneIdList, uint[] rawParents) ReadG1MSRaw(byte[] data, int cs)
    {
        if (cs + 0x1C > data.Length) return ([], [], []);
        uint   bonesOffset = ReadU32(data, cs + 0x0C);
        ushort numBones    = ReadU16(data, cs + 0x14);
        ushort numIndices  = ReadU16(data, cs + 0x16);

        var boneIdList = new ushort[numIndices];
        int blPos = cs + 0x1C;
        for (int i = 0; i < numIndices && blPos + 2 <= data.Length; i++, blPos += 2)
            boneIdList[i] = ReadU16(data, blPos);

        if (numBones == 0) return ([], boneIdList, []);

        int bp = cs + (int)bonesOffset;
        var bones      = new G1mBone[numBones];
        var rawParents = new uint[numBones];
        for (int i = 0; i < numBones; i++)
        {
            int o = bp + i * 0x30;
            if (o + 0x30 > data.Length) break;

            float sx = ReadF32(data, o + 0x00), sy = ReadF32(data, o + 0x04), sz = ReadF32(data, o + 0x08);
            uint  rawP = ReadU32(data, o + 0x0C);
            float rx = ReadF32(data, o + 0x10), ry = ReadF32(data, o + 0x14),
                  rz = ReadF32(data, o + 0x18), rw = ReadF32(data, o + 0x1C);
            float px = ReadF32(data, o + 0x20), py = ReadF32(data, o + 0x24), pz = ReadF32(data, o + 0x28);

            rawParents[i] = rawP;
            bones[i] = new G1mBone
            {
                ParentIndex   = -1,  // resolved during merge step
                LocalPosition = new Vector3(px, py, pz),
                LocalRotation = new Quaternion(rx, ry, rz, rw),
                LocalScale    = new Vector3(sx, sy, sz),
            };
        }
        return (bones, boneIdList, rawParents);
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

        var (clothInfo, lodGroupCount) = mgSecData >= 0
            ? ParseMeshGroupClothIds(data, mgSecData, mgSecEnd, g1mgVersion, rawSubs)
            : (new Dictionary<int, (int clothId, int extId, int lodGroup)>(), 0);
        r.LodGroupCount = lodGroupCount;

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
                ushort tileX    = ReadU16(data, pos + 8);
                ushort tileY    = ReadU16(data, pos + 10);
                r.MaterialTextures.Add((matIdx, (int)texIndex, (int)uvLayer, (int)texType, (int)tileX, (int)tileY));
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

    // ── MeshGroups cloth-ID / LOD parser ─────────────────────────────────────
    //
    // Reads Section 9 (MeshGroups) and returns:
    //   map: submeshIndex → (clothId, extId, lodGroup)
    //   lodGroup count (for LOD dropdown)
    //
    // clothId values: 0=rigid, 1=NUNO cloth, 2=physics cloth.
    //
    // Two LOD strategies (chosen after a full collection pass):
    //   Strategy 1 — Group lod field (MUA3 style): any group header has lodLevel > 0.
    //                lodGroup = group header's lod u32.
    //   Strategy 2 — Name-based (DOA6 style): all lod=0 but mesh names repeat.
    //                lodGroup = occurrence index of that name (0=first/highest, 1=second, …).
    private static (Dictionary<int, (int clothId, int extId, int lodGroup)>, int groupCount) ParseMeshGroupClothIds(
        byte[] data, int start, int end, uint version, List<RawSubmesh> rawSubs)
    {
        var result = new Dictionary<int, (int, int, int)>();
        if (start + 4 > end) return (result, 0);

        int groupCount = (int)ReadU32(data, start);
        int pos = start + 4;
        int maxLodLevel = -1;

        // Per-smIdx entries for result population and Strategy 1.
        var rawEntries = new List<(int smIdx, int clothId, int extId, int lodLevel,
                                   uint n0, uint n1, uint n2, uint n3, bool isMulti)>();
        // Per-mesh-entry records for Strategy 2 LOD detection.
        // One record per mesh entry in Section 9, holding ALL smIdxs for that entry.
        var rawMeshEntries = new List<(int clothId, int extId, uint n0, uint n1, uint n2, uint n3, List<int> smIdxs)>();

        for (int g = 0; g < groupCount; g++)
        {
            int sm1, sm2, lodLevel;

            if (version > MGVER_GT_0400)        // DOA6: 36-byte group header
            {
                if (pos + 20 > end) break;
                lodLevel = (int)ReadU32(data, pos + 0);
                sm1 = (int)ReadU32(data, pos + 12);
                sm2 = (int)ReadU32(data, pos + 16);
                pos += 36;
            }
            else if (version > MGVER_GT_0300)   // 20-byte group header
            {
                if (pos + 20 > end) break;
                lodLevel = (int)ReadU32(data, pos + 0);
                sm1 = (int)ReadU32(data, pos + 12);
                sm2 = (int)ReadU32(data, pos + 16);
                pos += 20;
            }
            else                                // 12-byte group header
            {
                if (pos + 12 > end) break;
                lodLevel = (int)ReadU32(data, pos + 0);
                sm1 = (int)ReadU32(data, pos + 4);
                sm2 = (int)ReadU32(data, pos + 8);
                pos += 12;
            }

            if (lodLevel >= 0 && lodLevel < 64) maxLodLevel = Math.Max(maxLodLevel, lodLevel);

            int meshCount = sm1 + sm2;
            for (int m = 0; m < meshCount; m++)
            {
                if (pos + 28 > end) break;
                // Mesh entry: name[16] + clothId(u16) + unk(u16) + externalId(u32) + idxCount(u32)
                uint n0      = ReadU32(data, pos +  0);
                uint n1      = ReadU32(data, pos +  4);
                uint n2      = ReadU32(data, pos +  8);
                uint n3      = ReadU32(data, pos + 12);
                int clothId  = ReadU16(data, pos + 16);
                int extId    = (int)ReadU32(data, pos + 20);
                int idxCount = (int)ReadU32(data, pos + 24);
                pos += 28;

                bool isMulti = idxCount > 1;
                var entrySmIdxs = new List<int>(Math.Max(idxCount, 1));
                if (idxCount > 0)
                {
                    for (int i = 0; i < idxCount && pos + 4 <= end; i++, pos += 4)
                    {
                        int smIdx = (int)ReadU32(data, pos);
                        rawEntries.Add((smIdx, clothId, extId, lodLevel, n0, n1, n2, n3, isMulti));
                        entrySmIdxs.Add(smIdx);
                    }
                }
                else
                {
                    if (pos + 4 <= end) pos += 4;  // skip dummy u32
                }
                rawMeshEntries.Add((clothId, extId, n0, n1, n2, n3, entrySmIdxs));
            }
        }

        // Strategy 1: group lod field is used (MUA3 style)
        if (maxLodLevel > 0)
        {
            foreach (var (smIdx, clothId, extId, lodLevel, _, _, _, _, _) in rawEntries)
                result.TryAdd(smIdx, (clothId, extId, lodLevel));
            return (result, maxLodLevel + 1);
        }

        // Strategy 2: strict LOD detection (DOA6 style).
        //
        // Groups by name at MESH-ENTRY level (not per-smIdx) so that multi-submesh entries
        // (idxCount>1 where all smIdxs share the same bmi) are treated as a single LOD slot.
        //
        // A "LOD chain" exists when a mesh name appears N≥2 times (N entries) and:
        //   1. No entry has mixed bmis within its smIdxs (a single uniform bmi per entry)
        //   2. All entries have clothId=0 (not cloth/physics)
        //   3. All N entries in the group have DISTINCT bmi values (one palette per LOD level)
        //   4. ALL name groups share the same count N (consistent LOD depth)
        //   5. ALL name groups share the SAME SET of bmi values
        //
        // LOD rank 0 = entry with the most total vertices (highest quality).
        // Singleton names with bmi outside the LOD set → billboard group (rank N).

        // Build smIdx → boneMapIdx from Section 8
        var smBmi = new Dictionary<int, int>(rawSubs.Count);
        for (int i = 0; i < rawSubs.Count; i++)
            smBmi[i] = rawSubs[i].BoneMapIndex;

        // Group rawMeshEntries by name, computing per-entry representative bmi.
        // isMixed = true when smIdxs in one entry have different bmi values.
        var nameGroups = new Dictionary<(uint, uint, uint, uint),
            List<(int entryBmi, int clothId, bool isMixed, List<int> smIdxs)>>();
        foreach (var (clothId, extId, n0, n1, n2, n3, smIdxs) in rawMeshEntries)
        {
            if (smIdxs.Count == 0) continue;
            int firstBmi = smBmi.GetValueOrDefault(smIdxs[0], -1);
            bool isMixed = smIdxs.Skip(1).Any(idx => smBmi.GetValueOrDefault(idx, -1) != firstBmi);
            int entryBmi = isMixed ? -1 : firstBmi;
            var key = (n0, n1, n2, n3);
            if (!nameGroups.TryGetValue(key, out var list))
                nameGroups[key] = list = [];
            list.Add((entryBmi, clothId, isMixed, smIdxs));
        }

        // Identify duplicate-name groups (≥2 mesh entries with the same name)
        var dupGroups = nameGroups.Where(kv => kv.Value.Count >= 2).ToList();

        bool isLodChain = false;
        HashSet<int>? lodBmiSet = null;

        if (dupGroups.Count > 0)
        {
            // Check 1 & 2: no mixed-bmi entries, all cloth=0
            bool clean = dupGroups.All(kv =>
                kv.Value.All(e => !e.isMixed && e.clothId == 0));

            if (clean)
            {
                // Check 3: within each group, all entry bmi values must be distinct
                bool distinctBmi = dupGroups.All(kv =>
                    kv.Value.Select(e => e.entryBmi).Distinct().Count() == kv.Value.Count);

                // Check 4: all groups have the same count N
                int firstCount = dupGroups[0].Value.Count;
                bool sameCount = dupGroups.All(kv => kv.Value.Count == firstCount);

                if (distinctBmi && sameCount)
                {
                    // Check 5: all groups share the same bmi set
                    var firstBmis = dupGroups[0].Value.Select(e => e.entryBmi).ToHashSet();
                    bool sameBmis = dupGroups.All(kv =>
                        kv.Value.Select(e => e.entryBmi).ToHashSet().SetEquals(firstBmis));

                    if (sameBmis)
                    {
                        isLodChain = true;
                        lodBmiSet  = firstBmis;
                    }
                }
            }
        }

        if (!isLodChain)
        {
            // No LOD structure — single group, no dropdown
            foreach (var (smIdx, clothId, extId, _, _, _, _, _, _) in rawEntries)
                result.TryAdd(smIdx, (clothId, extId, 0));
            return (result, groupCount > 0 ? 1 : 0);
        }

        // Assign LOD ranks: order by TOTAL VERTEX COUNT descending so rank 0 = highest quality.
        var bmiVertexSum = new Dictionary<int, long>();
        foreach (int bmi in lodBmiSet!)
            bmiVertexSum[bmi] = 0;
        for (int i = 0; i < rawSubs.Count; i++)
        {
            int bmi = smBmi.GetValueOrDefault(i, -1);
            if (bmiVertexSum.ContainsKey(bmi))
                bmiVertexSum[bmi] += rawSubs[i].NumVertices;
        }

        var bmiToRank = new Dictionary<int, int>();
        int lodRank = 0;
        foreach (int bmi in lodBmiSet!.OrderByDescending(b => bmiVertexSum.GetValueOrDefault(b, 0L)))
            bmiToRank[bmi] = lodRank++;
        int billboardRank = lodRank; // singletons with bmi outside the LOD set

        foreach (var (smIdx, clothId, extId, _, _, _, _, _, _) in rawEntries)
        {
            int bmi = smBmi.GetValueOrDefault(smIdx, -1);
            int lod = bmiToRank.TryGetValue(bmi, out int r) ? r : billboardRank;
            result.TryAdd(smIdx, (clothId, extId, lod));
        }

        // LodGroupCount = N + 1 if any billboard exists, else N
        bool hasBillboard = rawEntries.Any(e =>
        {
            int bmi = smBmi.GetValueOrDefault(e.smIdx, -1);
            return !bmiToRank.ContainsKey(bmi);
        });
        return (result, hasBillboard ? billboardRank + 1 : billboardRank);
    }

    // ── Build submesh geometry ────────────────────────────────────────────────

    private static G1mSubmesh[] BuildSubmeshes(
        List<RawSubmesh> rawSubs, List<VertexBuffer> vbs,
        List<LayoutEntry> layouts, List<IndexBuffer> ibs,
        Dictionary<int, (int clothId, int extId, int lodGroup)> clothInfo,
        List<PendingCloth> pendingCloth,
        List<PendingRigidSkin> pendingRigid)
    {
        var result = new List<G1mSubmesh>();
        for (int smIdx = 0; smIdx < rawSubs.Count; smIdx++)
        {
            var rs = rawSubs[smIdx];

            bool inGroup = clothInfo.TryGetValue(smIdx, out var ci);
            int clothId  = ci.clothId;
            int extId    = ci.extId;
            int lodGroup = inGroup ? ci.lodGroup : -1;

            // clothId=2 (physics cloth) has the same vertex layout as clothId=0: falls through to rigid path.

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
                // NUNO cloth deformation channel mapping (EG1MGVASemantic enum, confirmed from ProjectG1M):
                //   G1MG semantic 0 (Position)    = POSITION  → cpW1 (U-basis blend weights, FLOAT4, sum~1)
                //   G1MG semantic 1 (JointWeight) = NORMAL    → cW1  (row combination weights, FLOAT4)
                //   G1MG semantic 2 (JointIndex)  = BLENDWEIGHT → idx1 (row 0 CP indices, UBYTE4)
                //   G1MG semantic 3 (Normal)      = BLENDINDICES → normalDepth (xyz=norm, w=depth, FLOAT4)
                //   G1MG semantic 4 (PSize)       = PSIZE     → idx2 (row 1 CP indices, UBYTE4)
                //   G1MG semantic 5 (UV) L5 UByte = TEXCOORD5 → idx4 (row 3 CP indices, UBYTE4)
                //   G1MG semantic 7 (Binormal)    = BITANGENT → cpW2 (V-basis tangent weights, FLOAT4)
                //   G1MG semantic 10 (Color) L≠0  = COLOR     → cW2  (secondary row weights, FLOAT4)
                //   G1MG semantic 11 (Fog)        = FOG       → idx3 (row 2 CP indices, UBYTE4)
                // For each vertex: u_k = GetPoint(idx_k, cpW1); v_k = GetPoint(idx_k, cpW2)
                //   a = Σ(u_k × cW1_k); c = Σ(v_k × cW1_k)
                //   b = Σ(u_k × cW2_k) if COLOR_1 else a
                //   d = normalize(cross(normalize(b), normalize(c)))
                //   finalPos = a + d × depth

                Semantic? cpW1Sem = layout.Semantics.FirstOrDefault(s => s.Type == SEM_POSITION);
                Semantic? cpW2Sem = layout.Semantics.FirstOrDefault(s => s.Type == SEM_BITANGENT);
                Semantic? cW1Sem  = layout.Semantics.FirstOrDefault(s => s.Type == SEM_BLENDWEIGHT);   // type=1: row combination weights (FLOAT4)
                Semantic? cW2Sem  = layout.Semantics.FirstOrDefault(s => s.Type == SEM_COLOR && s.Layer == 1);
                Semantic? i1Sem   = layout.Semantics.FirstOrDefault(s => s.Type == SEM_BLENDINDICES); // type=2: CP row-0 indices (UBYTE4)
                Semantic? ndSem   = layout.Semantics.FirstOrDefault(s => s.Type == SEM_NORMAL);       // type=3: normal xyz + depth w (FLOAT4)
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
                byte[]? i1D   = VD(vbs, layout, i1Sem);   int i1S   = ST(vbs, layout, i1Sem);
                byte[]? i2D   = VD(vbs, layout, i2Sem);   int i2S   = ST(vbs, layout, i2Sem);
                byte[]? i3D   = VD(vbs, layout, i3Sem);   int i3S   = ST(vbs, layout, i3Sem);
                byte[]? i4D   = VD(vbs, layout, i4Sem);   int i4S   = ST(vbs, layout, i4Sem);
                byte[]? ndD   = VD(vbs, layout, ndSem);   int ndS   = ST(vbs, layout, ndSem);

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
                ushort i1Dt   = i1Sem?.DataType    ?? DT_UBYTE4;
                ushort i2Dt   = i2Sem?.DataType   ?? DT_UBYTE4;
                ushort i3Dt   = i3Sem?.DataType   ?? DT_UBYTE4;
                ushort i4Dt   = i4Sem?.DataType   ?? DT_UBYTE4;
                ushort ndDt   = ndSem?.DataType    ?? DT_FLOAT4;
                int cpW1Of = cpW1Sem?.Offset ?? 0;
                int cpW2Of = cpW2Sem?.Offset ?? 0;
                int cW1Of  = cW1Sem?.Offset  ?? 0;
                int cW2Of  = cW2Sem?.Offset  ?? 0;
                int i1Of   = i1Sem?.Offset    ?? 0;
                int i2Of   = i2Sem?.Offset   ?? 0;
                int i3Of   = i3Sem?.Offset   ?? 0;
                int i4Of   = i4Sem?.Offset   ?? 0;
                int ndOf   = ndSem?.Offset    ?? 0;



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
                    ClothId       = 1,
                    LodGroup      = lodGroup,
                });

                // Store raw extId — remapped to nunoIdx in ApplyNunoTransform once NUNO section is parsed.
                // extId < 10000 → NUNO1 entry index; 10000–19999 → NUNO4 local index;
                // extId >= 20000 → NUNO5 entry (PreNuno5Count + extId%20000).
                pendingCloth.Add(new PendingCloth
                {
                    NunoEntryIndex = extId,
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
                    ClothId       = clothId,
                    LodGroup      = lodGroup,
                });
                pendingRigid.Add(new PendingRigidSkin
                {
                    SubmeshIndex = subIdx,
                    BoneMapIndex = rs.BoneMapIndex,
                    ClothId      = clothId,
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
                    ClothId       = clothId,
                    LodGroup      = lodGroup,
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
                // ── DOA6 / v0029 NUNO entry format ───────────────────────────
                for (int j = 0; j < entryCount; j++)
                {
                    if (ep < 0 || ep + 28 > secEnd || ep + 28 > data.Length) break;
                    int  entryStart = ep;
                    int  parentId   = ReadI32(data, ep);
                    uint cpCount    = ReadU32(data, ep + 4);
                    uint unkSecs    = ReadU32(data, ep + 8);
                    uint skip1      = ReadU32(data, ep + 12);
                    uint skip2      = ReadU32(data, ep + 16);
                    uint skip3      = ReadU32(data, ep + 20);

                    int dataOff = entryStart + 24 + 0x3C;
                    if (outerVersion > NUVER_GT_0023) dataOff += 0x10;
                    if (outerVersion >= NUVER_GE_0025) dataOff += 0x10;
                    int advance = (dataOff - entryStart) + (int)(cpCount * 16 + cpCount * 24 + 48 * unkSecs + 4 * (skip1 + skip2 + skip3));

                    if (cpCount == 0 || cpCount > 1024 || advance <= 0 || dataOff + (int)cpCount * 16 > data.Length)
                    { ep = secEnd; break; }

                    var cps = new Vector4[cpCount];
                    for (int k = 0; k < cpCount; k++)
                    {
                        int o = dataOff + k * 16;
                        cps[k] = new Vector4(ReadF32(data, o), ReadF32(data, o+4), ReadF32(data, o+8), ReadF32(data, o+12));
                    }

                    // NunInfluence = 24 bytes per CP; parent index at byte +8.
                    int inflBase = dataOff + (int)cpCount * 16;
                    var cpParents = new int[cpCount];
                    for (int k = 0; k < cpCount; k++)
                    {
                        int io = inflBase + k * 24 + 8;
                        cpParents[k] = (io + 4 <= data.Length) ? ReadI32(data, io) : -1;
                    }

                    entries.Add(new NunoEntry { ParentBoneId = parentId, ControlPoints = cps, CpParents = cpParents });
                    ep = entryStart + advance;
                }
            }
            else if (secMagic == NUNO_SEC_NUNO4)
            {
                if (outerVersion >= NUVER_GE_0030)
                {
                    // ── FF2 / v0036 NUNO4: alternating type-A / type-B entries ──
                    //   type-A (f6≠0): header=208, cpWorldOff = entryStart + 196 + unkSecs*40
                    //   type-B (f6=0): header=184, cpWorldOff = entryStart + 184 + unkSecs*40
                    //   advance = header + cp*36 + unk*64 + (typeA?skip1:parentId)*4
                    //   Stride = 28B per CP: float4 (x,y,z,w=1.0) + 12B extra
                    for (int j = 0; j < entryCount; j++)
                    {
                        if (ep < 0 || ep + 28 > secEnd || ep + 28 > data.Length) break;
                        int  entryStart = ep;
                        int  parentId   = ReadI32(data, ep);
                        uint cpCount    = ReadU32(data, ep + 4);
                        uint unkSecs    = ReadU32(data, ep + 8);
                        uint skip1      = ReadU32(data, ep + 12);
                        uint f6         = ReadU32(data, ep + 24);

                        bool typeA   = (f6 != 0);
                        int  advance = typeA
                            ? 208 + (int)cpCount * 36 + (int)unkSecs * 64 + (int)skip1 * 4
                            : 184 + (int)cpCount * 36 + (int)unkSecs * 64 + parentId  * 4;
                        int cpWorldOff = entryStart + (typeA ? 196 : 184) + (int)unkSecs * 40;

                        if (cpCount == 0 || cpCount > 1024 || unkSecs > 255 || advance <= 0
                            || cpWorldOff + (int)cpCount * 28 > entryStart + advance
                            || cpWorldOff + (int)cpCount * 28 > data.Length)
                        { ep = secEnd; break; }

                        var cps       = new Vector4[cpCount];
                        var cpParents = new int[cpCount];
                        for (int k = 0; k < cpCount; k++)
                        {
                            int o = cpWorldOff + k * 28;
                            cps[k]       = new Vector4(ReadF32(data, o), ReadF32(data, o+4), ReadF32(data, o+8), ReadF32(data, o+12));
                            cpParents[k] = (o + 28 <= data.Length) ? ReadI32(data, o + 24) : -1;
                        }

                        entries.Add(new NunoEntry { ParentBoneId = parentId, ControlPoints = cps, CpParents = cpParents, IsWorldSpace = true });
                        ep = entryStart + advance;
                    }
                }
                else
                {
                    // ── DOA6LR / v0029 NUNO4: compact format ──
                    //   Fixed header = 64B, unkSec = 60B each, CP stride = 28B, tail = cpCount*8B
                    //   advance = 64 + unkSecs*60 + cpCount*36
                    //   cpWorldOff = entryStart + 64 + unkSecs*60
                    //   Positions are in WORLD SPACE (no bone transform needed).
                    for (int j = 0; j < entryCount; j++)
                    {
                        if (ep < 0 || ep + 28 > secEnd || ep + 28 > data.Length) break;
                        int  entryStart = ep;
                        int  parentId   = ReadI32(data, ep);
                        uint cpCount    = ReadU32(data, ep + 4);
                        uint unkSecs    = ReadU32(data, ep + 8);

                        int advance    = 64 + (int)unkSecs * 60 + (int)cpCount * 36;
                        int cpWorldOff = entryStart + 64 + (int)unkSecs * 60;

                        if (cpCount == 0 || cpCount > 1024 || unkSecs > 255 || advance <= 0
                            || cpWorldOff + (int)cpCount * 28 > entryStart + advance
                            || cpWorldOff + (int)cpCount * 28 > data.Length)
                        { ep = secEnd; break; }

                        var cps       = new Vector4[cpCount];
                        var cpParents = new int[cpCount];
                        for (int k = 0; k < cpCount; k++)
                        {
                            int o = cpWorldOff + k * 28;
                            cps[k]       = new Vector4(ReadF32(data, o), ReadF32(data, o+4), ReadF32(data, o+8), ReadF32(data, o+12));
                            cpParents[k] = parentId;
                        }

                        entries.Add(new NunoEntry { ParentBoneId = parentId, ControlPoints = cps, CpParents = cpParents, IsWorldSpace = true });
                        ep = entryStart + advance;
                    }
                }
            }
            else if (secMagic == NUNO_SEC_NUNO5)
            {
                // ── FF2 / v0036 NUNO5 bone-chain cloth format ────────────────────────────
                // Each entry: 432-byte (0x1B0) fixed header + boneCount×44 bone records + variable constraint data.
                // Header key fields:
                //   +0x04: parent global bone ID  +0x28: bone count
                //   +0x0C: always 2               +0x58: always 344  ← used as scan anchor
                // Bone record (44 bytes): float3 xyz (world pos) + float + float2 + int parentInChain + int seq + int(-1) + int + int(1)
                // Constraint data size formula is complex and version-dependent → use scan-based advance.

                if (r.PreNuno5Count == 0) r.PreNuno5Count = entries.Count;
                int ep5 = pos + 12;  // first entry starts immediately after 12-byte section header

                for (int found = 0; found < entryCount && ep5 >= 0 && ep5 + 0x1B0 + 44 <= secEnd; )
                {
                    // Validate this position as a NUNO5 entry header via constant-field checks.
                    // +0x0C == 2, +0x2C == 3, +0x58 == 344: three independent constants → very low false-positive rate.
                    if (ep5 + 0x5C > secEnd ||
                        ReadU32(data, ep5 + 0x0C) != 2 ||
                        ReadU32(data, ep5 + 0x2C) != 3 ||
                        ReadU32(data, ep5 + 0x58) != 344)
                    {
                        ep5 = ScanForNuno5Header(data, ep5 + 4, secEnd);
                        continue;
                    }

                    uint n5Parent    = ReadU32(data, ep5 + 0x04);
                    uint n5BoneCount = ReadU32(data, ep5 + 0x28);

                    if (n5BoneCount == 0 || n5BoneCount > 500 || n5Parent >= 4096)
                    {
                        ep5 = ScanForNuno5Header(data, ep5 + 4, secEnd);
                        continue;
                    }

                    int boneStart5 = ep5 + 0x1B0;
                    if (boneStart5 + (int)n5BoneCount * 44 > secEnd) break;

                    var n5Cps       = new Vector4[n5BoneCount];
                    var n5CpParents = new int[n5BoneCount];
                    for (int k = 0; k < n5BoneCount; k++)
                    {
                        int o = boneStart5 + k * 44;
                        n5Cps[k]       = new Vector4(ReadF32(data, o), ReadF32(data, o + 4), ReadF32(data, o + 8), 1f);
                        n5CpParents[k] = ReadI32(data, o + 24);  // parent_in_chain; -1 = chain root
                    }

                    entries.Add(new NunoEntry
                    {
                        ParentBoneId   = (int)n5Parent,
                        ControlPoints  = n5Cps,
                        CpParents      = n5CpParents,
                        IsWorldSpace   = false,   // NUNO5 CPs are in parent skeleton bone LOCAL space
                        UseGlobalIndex = true,    // but vertex CP indices are GLOBAL across all NUNO5 entries
                    });
                    found++;

                    // Advance: scan forward from after bone data for the next valid NUNO5 header
                    ep5 = ScanForNuno5Header(data, boneStart5 + (int)n5BoneCount * 44, secEnd);
                }
            }

            pos = secEnd;
        }

        r.NunoEntries = [.. entries];
    }

    // Scans forward at 4-byte alignment for the next NUNO5 entry header.
    // Returns -1 if not found within [searchStart, secEnd).
    private static int ScanForNuno5Header(byte[] data, int searchStart, int secEnd)
    {
        // Three independent constants must match simultaneously → very low false-positive rate.
        //   +0x0C == 2   (always 2 in observed entries)
        //   +0x2C == 3   (always 3 in observed entries)
        //   +0x58 == 344 (always 344 in observed entries)
        for (int p = searchStart; p + 0x5C <= secEnd; p += 4)
        {
            if (ReadU32(data, p + 0x0C) == 2 &&
                ReadU32(data, p + 0x2C) == 3 &&
                ReadU32(data, p + 0x58) == 344)
            {
                uint bc = ReadU32(data, p + 0x28);
                uint pg = ReadU32(data, p + 0x04);
                if (bc > 0 && bc <= 500 && pg < 4096)
                    return p;
            }
        }
        return -1;
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
    // maxLocal: maximum valid local index (exclusive) within this entry; guards against OOR indices
    private static Vector3 GetClothPoint(BlendIdx4 idx, Vector4 weights, Vector3[] wCPs, int offset = 0, int maxLocal = int.MaxValue)
    {
        var res = Vector3.Zero;
        int i0 = offset + idx.I0, i1 = offset + idx.I1, i2 = offset + idx.I2, i3 = offset + idx.I3;
        int lim = (long)offset + maxLocal > wCPs.Length ? wCPs.Length : offset + maxLocal;
        if (weights.X != 0 && i0 >= offset && i0 < lim) res += wCPs[i0] * weights.X;
        if (weights.Y != 0 && i1 >= offset && i1 < lim) res += wCPs[i1] * weights.Y;
        if (weights.Z != 0 && i2 >= offset && i2 < lim) res += wCPs[i2] * weights.Z;
        if (weights.W != 0 && i3 >= offset && i3 < lim) res += wCPs[i3] * weights.W;
        return res;
    }

    private static void ApplyNunoTransform(G1mData r, List<PendingCloth> pending)
    {
        // NUNO4 (FF2, IsWorldSpace=true): CP indices in vertex data are GLOBAL — they address the
        //   concatenated CP array (globalWCPs) directly. cpOffset=0; do NOT add per-entry offset.
        // NUNO5 (FF2/Yumia, UseGlobalIndex=true): same global indexing, but CPs are bone-local
        //   and need bone transform applied; vertex indices still address globalWCPs from index 0.
        // NUNO1 (DOA6, IsWorldSpace=false, UseGlobalIndex=false): indices are per-entry local; wCPs built per submesh.
        bool isNuno4 = r.NunoEntries.Length > 0 && r.NunoEntries[0].IsWorldSpace;
        bool hasNuno5 = r.NunoEntries.Any(e => e.UseGlobalIndex);
        Vector3[]? globalWCPs = null;
        int[]? globalEntryStarts = null;
        if (isNuno4 || hasNuno5)
        {
            int total = 0;
            foreach (var e in r.NunoEntries) total += e.ControlPoints.Length;
            globalWCPs = new Vector3[total];
            globalEntryStarts = new int[r.NunoEntries.Length];
            int gi = 0;
            for (int ei = 0; ei < r.NunoEntries.Length; ei++)
            {
                var e = r.NunoEntries[ei];
                globalEntryStarts[ei] = gi;

                // NUNO5 entries are stored in parent-bone local space (IsWorldSpace=false).
                // Resolve parent bone once per entry so they land in world space in globalWCPs.
                Vector3    eBonePos  = Vector3.Zero;
                Quaternion eBoneQuat = Quaternion.Identity;
                if (!e.IsWorldSpace)
                {
                    int pid = ResolveNunoParentBoneId(r, e.ParentBoneId);
                    if (pid >= 0) { eBonePos = r.Bones[pid].WorldPosition; eBoneQuat = r.Bones[pid].WorldQuaternion; }
                }

                foreach (var cp in e.ControlPoints)
                {
                    var wcp = e.IsWorldSpace
                        ? new Vector3(cp.X, cp.Y, cp.Z)
                        : eBonePos + Vector3.Transform(new Vector3(cp.X, cp.Y, cp.Z), eBoneQuat);
                    globalWCPs[gi++] = wcp;
                }
            }
        }

        foreach (var pc in pending)
        {
            int rawExtId = pc.NunoEntryIndex;
            int nunoIdx = rawExtId >= 20000 ? r.PreNuno5Count + (rawExtId % 20000)
                        : rawExtId >= 10000 ? rawExtId % 10000
                        : rawExtId;
            if (nunoIdx < 0 || nunoIdx >= r.NunoEntries.Length) continue;
            var entry = r.NunoEntries[nunoIdx];
            bool isGlobalCloth = isNuno4 || entry.UseGlobalIndex; // world-space CPs for this entry

            int boneId = ResolveNunoParentBoneId(r, entry.ParentBoneId);
            if (boneId < 0) continue;
            var bone     = r.Bones[boneId];
            var bonePos  = bone.WorldPosition;
            var boneQuat = bone.WorldQuaternion;

            var sub = r.Submeshes[pc.SubmeshIndex];
            int n   = Math.Min(pc.CpWeights1.Length, sub.Positions.Length);

            // ── Cloth vertex transform ───────────────────────────────────────────
            // Per-vertex RIVET detection (matches RDB Explorer exactly):
            //   BITANGENT channel = zero  →  bone-attached (RIVET): bonePos + boneRot * localPos
            //   BITANGENT channel ≠ zero  →  cloth simulation: full basis-deformation formula
            Vector3[] wCPs;
            int cpOffset;
            int cpMaxLocal; // exclusive upper bound of local CP indices valid for this entry
            if (globalWCPs != null)
            {
                wCPs = globalWCPs;
                if (entry.IsWorldSpace)
                {
                    // NUNO4 (isWS=true): CP indices in vertex data are GLOBAL — address globalWCPs directly from 0.
                    cpOffset   = 0;
                    cpMaxLocal = globalWCPs.Length;
                }
                else
                {
                    // NUNO5 (isWS=false, UseGlobalIndex=true): CP indices are LOCAL to this entry.
                    // Add entry's global start offset so localIdx→globalWCPs[entryStart+localIdx].
                    cpOffset   = globalEntryStarts![nunoIdx];
                    cpMaxLocal = entry.ControlPoints.Length;
                }
            }
            else
            {
                // NUNO1: per-entry bone-local → world transform
                wCPs = new Vector3[entry.ControlPoints.Length];
                for (int k = 0; k < wCPs.Length; k++)
                {
                    var cp = entry.ControlPoints[k];
                    wCPs[k] = bonePos + Vector3.Transform(new Vector3(cp.X, cp.Y, cp.Z), boneQuat);
                }
                cpOffset   = 0;
                cpMaxLocal = wCPs.Length;
            }

            bool hasI2    = pc.Indices2.Length == n;
            bool hasI3    = pc.Indices3.Length == n;
            bool hasI4    = pc.Indices4.Length == n;
            bool hasCW2   = pc.ComWeights2.Length == n;
            bool hasComW1 = pc.ComWeights1.Length == n;
            bool hasNd    = pc.NormalDepth.Length == n;

            // Per-vertex rivet vs. cloth detection (matches RDB Explorer):
            //   cpW2 (BITANGENT) == zero-vector  →  RIVET: bone-attached, cpW1.XYZ = local pos
            //   cpW2 (BITANGENT) != zero-vector  →  CLOTH: bilinear blend over NUNO CPs
            for (int vi = 0; vi < n; vi++)
            {
                var cpW1 = pc.CpWeights1[vi];
                var cpW2 = pc.CpWeights2.Length > vi ? pc.CpWeights2[vi] : Vector4.Zero;
                var nd   = hasNd ? pc.NormalDepth[vi] : Vector4.Zero;
                bool isCloth = cpW2.X != 0f || cpW2.Y != 0f || cpW2.Z != 0f || cpW2.W != 0f;

                if (!isCloth)
                {
                    // Rivet: cpW1.XYZ is local position relative to NUNO parent bone
                    sub.Positions[vi] = bonePos + Vector3.Transform(new Vector3(cpW1.X, cpW1.Y, cpW1.Z), boneQuat);
                    var wn = Vector3.Transform(new Vector3(nd.X, nd.Y, nd.Z), boneQuat);
                    sub.Normals[vi] = wn.LengthSquared() > 1e-8f ? Vector3.Normalize(wn) : Vector3.UnitY;
                }
                else
                {
                    // Cloth: bilinear-patch basis deformation (ProjectG1M formula)
                    var idx1  = pc.Indices1[vi];
                    var idx2  = hasI2 ? pc.Indices2[vi] : default;
                    var idx3  = hasI3 ? pc.Indices3[vi] : default;
                    var idx4  = hasI4 ? pc.Indices4[vi] : default;
                    var comW1 = hasComW1 ? pc.ComWeights1[vi] : Vector4.Zero;
                    var comW2 = hasCW2   ? pc.ComWeights2[vi] : Vector4.Zero;

                    // U-direction: blend CPs using cpW1 weights for each row
                    var u1 = GetClothPoint(idx1, cpW1, wCPs, cpOffset, cpMaxLocal);
                    var u2 = hasI2 ? GetClothPoint(idx2, cpW1, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;
                    var u3 = hasI3 ? GetClothPoint(idx3, cpW1, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;
                    var u4 = hasI4 ? GetClothPoint(idx4, cpW1, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;

                    // V-direction: blend CPs using cpW2 weights for each row
                    var v1 = GetClothPoint(idx1, cpW2, wCPs, cpOffset, cpMaxLocal);
                    var v2 = hasI2 ? GetClothPoint(idx2, cpW2, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;
                    var v3 = hasI3 ? GetClothPoint(idx3, cpW2, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;
                    var v4 = hasI4 ? GetClothPoint(idx4, cpW2, wCPs, cpOffset, cpMaxLocal) : Vector3.Zero;

                    // Surface position: weighted sum of U-direction row positions
                    var a = u1 * comW1.X + u2 * comW1.Y + u3 * comW1.Z + u4 * comW1.W;

                    // Tangent frame for normal/depth reconstruction (ProjectG1M: NO normalization of b or c)
                    Vector3 b, c;
                    Vector3 d;
                    if (comW2.LengthSquared() > 1e-8f)
                    {
                        // comW2 (COLOR_1) present: b is a tangent vector along the row direction
                        b = u1 * comW2.X + u2 * comW2.Y + u3 * comW2.Z + u4 * comW2.W;
                        c = v1 * comW1.X + v2 * comW1.Y + v3 * comW1.Z + v4 * comW1.W;
                        d = Vector3.Cross(b, c);
                    }
                    else if (!isGlobalCloth)
                    {
                        // NUNO1 fallback: b = a (safe because bone-local positions are small)
                        b = a;
                        c = v1 * comW1.X + v2 * comW1.Y + v3 * comW1.Z + v4 * comW1.W;
                        d = Vector3.Cross(b, c);
                    }
                    else
                    {
                        // NUNO4/NUNO5 world-space without COLOR_1: skip depth displacement.
                        b = a;
                        c = v1 * comW1.X + v2 * comW1.Y + v3 * comW1.Z + v4 * comW1.W;
                        d = Vector3.Zero;
                    }

                    // World-space CPs (NUNO4/NUNO5 global): normalize d before applying depth.
                    // World-space CPs have large magnitude (~10-150 units), making Cross(b,c) huge.
                    // depth is in cloth-surface-normal units, so d must be a unit vector.
                    if (isGlobalCloth)
                    {
                        float dSq = d.LengthSquared();
                        d = dSq > 1e-12f ? d / MathF.Sqrt(dSq) : Vector3.Zero;
                    }

                    var finalPosTmp = a + d * nd.W;
                    sub.Positions[vi] = finalPosTmp;

                    // Build normalized basis for normal transform
                    float bLen = b.Length(), cLen = c.Length(), dLen = d.Length();
                    var bN = bLen > 1e-8f ? b / bLen : Vector3.UnitX;
                    var cN = cLen > 1e-8f ? c / cLen : Vector3.UnitZ;
                    var dN = dLen > 1e-8f ? d / dLen : Vector3.UnitY;
                    var localNorm = new Vector3(nd.X, nd.Y, nd.Z);
                    var worldNorm = bN * localNorm.Y + cN * localNorm.X + dN * localNorm.Z;
                    float wnLen = worldNorm.Length();
                    sub.Normals[vi] = wnLen > 1e-8f ? worldNorm / wnLen : Vector3.UnitY;
                }
            }

        }
    }

    // ── JOINTPALETTES section parser ──────────────────────────────────────────

    // Section 6 format: [count u32] then count palettes, each:
    //   [pCount u32] [pCount × 12B entries: G1MM_idx(skip) physIdx jointIdx]
    // jointIdx may have 0x80000000 flag (physics override); strip it to get actual bone index.
    // physIdx & 0xFFFF → PhysicsPalettes entry used by clothId=2 submeshes.
    private static void ParseJointPalettes(byte[] data, int start, int end, G1mData r)
    {
        if (start + 4 > end) return;
        uint count = ReadU32(data, start);
        int  pos   = start + 4;
        for (int i = 0; i < count && pos + 4 <= end; i++)
        {
            uint pCount = ReadU32(data, pos); pos += 4;
            var palette     = new uint[pCount];
            var physPalette = new uint[pCount];
            for (int j = 0; j < pCount && pos + 12 <= end; j++, pos += 12)
            {
                // [+0] G1MM index (skip), [+4] physics idx, [+8] joint idx
                // ProjectG1M G1MGBonePalette.h: with external skel, bit31 SET → internal local idx,
                // bit31 CLEAR → external local idx. Without external skel, strip bit31 and use directly.
                uint physIdx  = ReadU32(data, pos + 4);
                uint jointIdx = ReadU32(data, pos + 8);
                physPalette[j] = physIdx & 0xFFFF;
                if (r.HasExternalSkeleton)
                {
                    palette[j] = (jointIdx & 0x80000000u) != 0
                        ? jointIdx ^ 0x80000000u                          // internal local index
                        : (uint)(r.InternalBoneCount + (int)jointIdx);    // external local → N + local
                }
                else
                {
                    if ((jointIdx & 0x80000000u) != 0) jointIdx ^= 0x80000000u;
                    palette[j] = jointIdx;
                }
            }
            r.BonePalettes.Add(palette);
            r.PhysicsPalettes.Add(physPalette);
        }
    }

    // ── Rigid mesh LBS transform ──────────────────────────────────────────────

    // DOA6 rigid-mesh vertices are stored in model/world space (T-pose bind-pose coordinates).
    // Standard LBS: v_world = Σ w[i] * (current_bone_world[i] * bind_bone_inverse[i]) * v_model.
    // In T-pose (current == bind), the skinning matrix collapses to Identity, so v_world == v_model.
    // We copy raw positions directly, preserving bind-pose world coordinates.
    //
    // Exception — clothId==2 (physics cloth): vertices are stored in cloth simulation local space.
    // Each vertex has a blend index encoding which physics bone it attaches to (index / 3).
    // The physics bone global index comes from PhysicsPalettes (built from physIdx & 0xFFFF
    // in the JOINTPALETTE middle field). Transform: worldPos + Rotate(v_local, worldRot).
    // Reference: RDB Explorer G1MImporter.cs clothId==2 branch.
    private static void ApplyRigidSkinning(G1mData r, List<PendingRigidSkin> pending)
    {
        foreach (var p in pending)
        {
            var sub = r.Submeshes[p.SubmeshIndex];

            if (p.ClothId == 2 && p.BoneMapIndex >= 0 && p.BoneMapIndex < r.PhysicsPalettes.Count)
            {
                uint[] physPal = r.PhysicsPalettes[p.BoneMapIndex];
                for (int vi = 0; vi < p.RawPositions.Length; vi++)
                {
                    int localIdx = (int)Math.Round(p.BlendIndices[vi].I0 / 3.0);
                    if ((uint)localIdx < (uint)physPal.Length)
                    {
                        uint gbIdx = physPal[localIdx];
                        if (gbIdx < (uint)r.Bones.Length)
                        {
                            var bone    = r.Bones[(int)gbIdx];
                            sub.Positions[vi] = bone.WorldPosition + Vector3.Transform(p.RawPositions[vi], bone.WorldQuaternion);
                            var rawNorm = p.RawNormals[vi];
                            if (rawNorm != Vector3.Zero)
                            {
                                var rn = Vector3.Transform(rawNorm, bone.WorldQuaternion);
                                sub.Normals[vi] = rn.LengthSquared() > 1e-6f ? Vector3.Normalize(rn) : rawNorm;
                            }
                            continue;
                        }
                    }
                    // Fallback: no valid physics bone — copy raw
                    sub.Positions[vi] = p.RawPositions[vi];
                    sub.Normals[vi]   = p.RawNormals[vi];
                }
                continue;
            }

            // Standard rigid: T-pose bind-pose world coordinates — copy raw
            for (int vi = 0; vi < p.RawPositions.Length; vi++)
            {
                sub.Positions[vi] = p.RawPositions[vi];
                sub.Normals[vi]   = p.RawNormals[vi];
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
                ? new BlendIdx4(
                    (int)(ReadU16(data,o)   == 0xFFFF ? 0 : ReadU16(data,o)),
                    (int)(ReadU16(data,o+2) == 0xFFFF ? 0 : ReadU16(data,o+2)),
                    (int)(ReadU16(data,o+4) == 0xFFFF ? 0 : ReadU16(data,o+4)),
                    (int)(ReadU16(data,o+6) == 0xFFFF ? 0 : ReadU16(data,o+6)))
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

    // ── NUNO parent bone lookup ───────────────────────────────────────────────

    // Resolves a NUNO entry's raw parentBoneId to a valid index in r.Bones.
    // Matches ProjectG1M Source.cpp:
    //   if (nun1.parentID >> 31) nunParentJointID = globalToFinal[nun1.parentID ^ 0x80000000];
    //   else                     nunParentJointID = globalToFinal[nun1.parentID];
    // Returns -1 if the bone cannot be resolved.
    private static int ResolveNunoParentBoneId(G1mData r, int rawParentBoneId)
    {
        uint uId     = (uint)rawParentBoneId & 0x7FFFFFFFu;  // strip bit 31 flag
        int  listIdx = (int)uId;

        // Try internal BoneIdList (globalID → internal local index)
        if (r.BoneIdList != null && (uint)listIdx < (uint)r.BoneIdList.Length)
        {
            int boneId = r.BoneIdList[listIdx];
            if (boneId != 0xFFFF)
                return ((uint)boneId < (uint)r.Bones.Length) ? boneId : -1;
        }

        // Fallback: try external BoneIdList (globalID → external local index → N + local)
        if (r.HasExternalSkeleton && r.ExternalBoneIdList != null && (uint)listIdx < (uint)r.ExternalBoneIdList.Length)
        {
            int extLocal = r.ExternalBoneIdList[listIdx];
            if (extLocal != 0xFFFF)
            {
                int externalIdx = r.InternalBoneCount + extLocal;
                return (externalIdx < r.Bones.Length) ? externalIdx : -1;
            }
        }

        // Single-skeleton fallback: use stripped globalID as direct bone index
        if (!r.HasExternalSkeleton)
        {
            return ((uint)listIdx < (uint)r.Bones.Length) ? listIdx : -1;
        }

        return -1;
    }

    // ── World matrix ──────────────────────────────────────────────────────────

    // Appends NUNO cloth control points as additional bones after the skeleton bones.
    // Matches ProjectG1M: jointCount += nun1.controlPoints.size()
    private static void AppendNunoBones(G1mData r)
    {
        if (r.NunoEntries.Length == 0 || r.Bones.Length == 0) return;

        int skelCount = r.Bones.Length;
        r.NunoCpStartIndex = skelCount; // mark where skeleton ends and CP bones begin
        var newBones  = new List<G1mBone>(r.Bones);

        foreach (var entry in r.NunoEntries)
        {
            if (entry.ControlPoints.Length == 0) continue;

            int boneId = ResolveNunoParentBoneId(r, entry.ParentBoneId);
            if (boneId < 0) continue;

            r.NunoParentBoneIndices.Add(boneId); // track for bone overlay highlighting

            var parentBone = r.Bones[boneId];
            var bonePos    = parentBone.WorldPosition;
            var boneQuat   = parentBone.WorldQuaternion;
            int cpStartIdx = newBones.Count;  // index of first CP bone in this entry

            bool isWorldSpace = entry.IsWorldSpace;
            for (int k = 0; k < entry.ControlPoints.Length; k++)
            {
                var cp       = entry.ControlPoints[k];
                var localPos = new Vector3(cp.X, cp.Y, cp.Z);
                // NUNO4 CPs are already world-space; NUNO1 CPs are bone-local.
                var worldPos = isWorldSpace
                    ? localPos
                    : bonePos + Vector3.Transform(localPos, boneQuat);

                // P3: per-CP parent within entry. -1 (0xFFFFFFFF) = root → parents skeleton bone.
                int p3           = k < entry.CpParents.Length ? entry.CpParents[k] : -1;
                int cpParentIdx  = (p3 >= 0 && cpStartIdx + p3 < newBones.Count)
                                 ? cpStartIdx + p3
                                 : boneId;

                newBones.Add(new G1mBone
                {
                    ParentIndex     = cpParentIdx,
                    LocalPosition   = localPos,
                    LocalRotation   = Quaternion.Identity,
                    LocalScale      = Vector3.One,
                    WorldPosition   = worldPos,
                    WorldQuaternion = boneQuat,
                    WorldMatrix     = Matrix4x4.CreateTranslation(worldPos)
                });
            }
        }

        if (newBones.Count > skelCount)
        {
            r.Bones = newBones.ToArray();
        }
    }

    private static void ComputeWorldMatrices(G1mBone[] bones)
    {
        // Use DFS from each root to handle any parent ordering (parents may have index > child).
        var state = new byte[bones.Length]; // 0=unvisited, 1=in-progress, 2=done

        for (int i = 0; i < bones.Length; i++)
        {
            int pi = bones[i].ParentIndex;
            if (pi < 0 || pi >= bones.Length || pi == i)
                ComputeWorldMatrix_DFS(bones, i, state);
        }
        // DFS may not visit all bones if orphaned — finish unvisited
        for (int i = 0; i < bones.Length; i++)
        {
            if (state[i] == 0) ComputeWorldMatrix_DFS(bones, i, state);
        }

    }

    private static void ComputeWorldMatrix_DFS(G1mBone[] bones, int i, byte[] state)
    {
        if (state[i] == 2) return;
        if (state[i] == 1) { state[i] = 2; return; } // cycle guard
        state[i] = 1;

        var b = bones[i];
        int pi = b.ParentIndex;
        if (pi >= 0 && pi < bones.Length && pi != i)
        {
            if (state[pi] != 2) ComputeWorldMatrix_DFS(bones, pi, state);
            var p = bones[pi];
            var local = Matrix4x4.CreateScale(b.LocalScale)
                      * Matrix4x4.CreateFromQuaternion(b.LocalRotation)
                      * Matrix4x4.CreateTranslation(b.LocalPosition);
            b.WorldMatrix     = local * p.WorldMatrix;
            b.WorldPosition   = p.WorldPosition + Vector3.Transform(b.LocalPosition, p.WorldQuaternion);
            b.WorldQuaternion = Quaternion.Normalize(p.WorldQuaternion * b.LocalRotation);
        }
        else
        {
            var local = Matrix4x4.CreateScale(b.LocalScale)
                      * Matrix4x4.CreateFromQuaternion(b.LocalRotation)
                      * Matrix4x4.CreateTranslation(b.LocalPosition);
            b.WorldMatrix     = local;
            b.WorldPosition   = b.LocalPosition;
            b.WorldQuaternion = b.LocalRotation;
        }
        state[i] = 2;
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
        public int         ClothId      { get; set; }
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
