using System.Text;
using System.Text.Json;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Extracts key header/summary metadata from any KatanaEngine asset format as indented JSON.
/// Large arrays (vertices, keyframes, control points) are intentionally excluded.
/// </summary>
public static class MetadataExtractor
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };

    // ── Helper readers ────────────────────────────────────────────────────────

    private static uint ReadU32(byte[] b, int o) =>
        o + 3 < b.Length ? (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24) : 0;

    private static ushort ReadU16(byte[] b, int o) =>
        o + 1 < b.Length ? (ushort)(b[o] | b[o+1]<<8) : (ushort)0;

    private static int ReadI32(byte[] b, int o) => (int)ReadU32(b, o);

    private static float ReadF32(byte[] b, int o) =>
        o + 3 < b.Length ? BitConverter.ToSingle(b, o) : 0f;

    private static string ToHex(uint v)  => $"0x{v:x8}";

    private static bool Magic4(byte[] b, int o, char c0, char c1, char c2, char c3) =>
        b.Length >= o + 4 &&
        b[o] == c0 && b[o+1] == c1 && b[o+2] == c2 && b[o+3] == c3;

    private static string BytesToHex(byte[] b, int offset, int count)
    {
        count = Math.Min(count, b.Length - offset);
        if (count <= 0) return string.Empty;
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(b[offset + i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string BytesToAscii(byte[] b, int offset, int count)
    {
        count = Math.Min(count, b.Length - offset);
        if (count <= 0) return string.Empty;
        var sb = new StringBuilder(count);
        for (int i = 0; i < count; i++)
        {
            byte c = b[offset + i];
            sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
        }
        return sb.ToString();
    }

    // Read null-terminated ASCII string up to maxLen bytes
    private static string ReadNullTermAscii(byte[] b, int offset, int maxLen)
    {
        int end = offset;
        while (end < b.Length && end - offset < maxLen && b[end] != 0)
            end++;
        return Encoding.ASCII.GetString(b, offset, end - offset);
    }

    private static uint ParseHexKtid(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan().TrimStart("0xX ".ToCharArray());
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract metadata from raw asset bytes. Used for all formats except .g1t and .g1m
    /// (which call the typed overloads below after their visual parse completes).
    /// </summary>
    public static string ExtractJson(byte[] raw, string typeExt, string ktidHex, string assetName)
    {
        try
        {
            return typeExt switch
            {
                ".g1t"        => ExtractG1t(raw, assetName, ktidHex),
                ".ktid"       => KtidDecoder.ToJson(assetName, ParseHexKtid(ktidHex), KtidDecoder.Parse(raw)),
                ".grp"        => GrpDecoder.ToJson(assetName, ParseHexKtid(ktidHex), GrpDecoder.Parse(raw)),
                ".mtl"        => ExtractMtlSummary(raw, assetName, ktidHex),
                ".srsa"       => ExtractSrsa(raw, "Sound Resource Archive (.srsa)"),
                ".srst"       => ExtractSrst(raw),
                ".kidsobjdb"  => ExtractKidsObjDb(raw),
                ".oid"        => OidDecoder.ToJson(raw, assetName, ktidHex),
                ".oidex"      => ExtractOidEx(raw, "OIDBindTableEx (.oidex)"),
                ".oidsq"      => ExtractOidEx(raw, "OIDSQTBindTable (.oidsq)"),
                ".g1n"        => ExtractG1n(raw),
                ".g1a"        => ExtractG1a(raw),
                ".rigbin"     => ExtractRigBin(raw),
                ".g1co"       => ExtractG1co(raw),
                ".g1s"        => ExtractG1s(raw),
                ".swg"        => ExtractSwg(raw),
                ".lcsk"       => ExtractLcsk(raw),
                ".gstk"       => ExtractGstk(raw),
                ".me1g"       => ExtractMe1g(raw),
                ".ktf2"       => ExtractKtf2(raw),
                ".sgcbin"     => ExtractSgcBin(raw),
                ".kidsrender" => ExtractKidsRender(raw),
                ".m1gk"       => ExtractGeomContainer(raw, "M1GK", "G1M"),
                ".p1gk"       => ExtractGeomContainer(raw, "P1GK", "G1P"),
                ".c1gk"       => ExtractGeomContainer(raw, "C1GK", "G1COX"),
                ".oboro"      => ExtractOboro(raw),
                ".efpl"       => ExtractEfpl(raw),
                ".xf1g"       => ExtractXf1g(raw),
                ".effselect"  => ExtractEffSelect(raw),
                ".name"       => ExtractNameDb(raw),
                ".unk_133d"   => ExtractUnk133d(raw),
                _             => ExtractGeneric(raw, typeExt),
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { format = typeExt, error = ex.Message }, s_opts);
        }
    }

    /// <summary>
    /// Build metadata JSON from an already-parsed G1mData.
    /// Called after LoadModelAsync completes so we don't re-parse the file.
    /// </summary>
    public static string ExtractG1mJson(G1mData data, string assetName, string ktidHex)
    {
        static string SigStr(uint sig)
        {
            var bytes = BitConverter.GetBytes(sig);
            var sb = new StringBuilder(4);
            foreach (byte b in bytes) sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            return sb.ToString();
        }

        var matGroups = data.MaterialTextures
            .GroupBy(t => t.MatIdx)
            .OrderBy(g => g.Key)
            .Take(32)
            .Select(g => new
            {
                matIndex = g.Key,
                textures = g.Select(t => new
                {
                    g1tSlot = t.G1tSlot,
                    type    = TexTypeName(t.TexType),
                }).ToArray(),
            })
            .ToArray();

        uint g1mFk  = ParseHexKtid(ktidHex);
        uint oidFk  = g1mFk - 0x0E05C687u;

        var doc = new
        {
            format    = "G1M Model",
            magic     = "G1M_",
            ktidHash  = ktidHex,
            pairedOidFk = $"0x{oidFk:x8}",
            assetName,
            chunkCount = data.Chunks.Count,
            chunks    = data.Chunks
                .Select(c => new { sig = SigStr(c.Sig), size = c.Size })
                .ToArray(),
            skeleton  = new
            {
                boneCount           = data.Bones.Length,
                hasExternalSkeleton = data.HasExternalSkeleton,
            },
            geometry  = new
            {
                submeshCount   = data.Submeshes.Length,
                lodGroupCount  = data.LodGroupCount,
                materialCount  = data.Submeshes.Select(s => s.MaterialIndex).Distinct().Count(),
                nunoEntryCount = data.NunoEntries.Length,
                boundsMin      = new float[] { Round3(data.BoundsMin.X), Round3(data.BoundsMin.Y), Round3(data.BoundsMin.Z) },
                boundsMax      = new float[] { Round3(data.BoundsMax.X), Round3(data.BoundsMax.Y), Round3(data.BoundsMax.Z) },
            },
            materials = matGroups,
        };

        return JsonSerializer.Serialize(doc, s_opts);
    }

    /// <summary>
    /// Build metadata JSON from an already-parsed G1TFileInfo.
    /// Called after LoadTextureAsync completes.
    /// </summary>
    public static string ExtractG1tJson(G1TFileInfo info, int fileSize, string assetName, string ktidHex)
    {
        var doc = new
        {
            format       = "G1T Texture Pack",
            magic        = "GT1G",
            ktidHash     = ktidHex,
            assetName,
            version      = info.Version,
            fileSize,
            textureCount = (int)info.TexCount,
            textures     = info.Textures.Select(t => new
            {
                slot      = t.Slot,
                format    = t.FmtName,
                width     = (int)t.Width,
                height    = (int)t.Height,
                mipLevels = (int)t.MipCount,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // ── Format-specific parsers ───────────────────────────────────────────────

    private static string ExtractG1t(byte[] raw, string assetName, string ktidHex)
    {
        var info = G1tDecoder.Survey(raw);
        return info.Valid
            ? ExtractG1tJson(info, raw.Length, assetName, ktidHex)
            : ExtractGeneric(raw, ".g1t");
    }

    private static string ExtractMtlSummary(byte[] raw, string assetName, string ktidHex)
    {
        if (raw.Length < 0x10)
            return ExtractGeneric(raw, ".mtl");

        var doc = new
        {
            format        = "MTL Material Bind",
            ktidHash      = ktidHex,
            assetName,
            matCount      = (int)ReadU32(raw, 0x04),
            nameGroupCount = (int)ReadU32(raw, 0x00),
            clothCount    = (int)ReadU32(raw, 0x08),
            ponytailCount = (int)ReadU32(raw, 0x0C),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // SRSA — "ASRS" outer + "KTSR" inner record
    private static string ExtractSrsa(byte[] raw, string label)
    {
        bool outerOk = raw.Length >= 4 && Magic4(raw, 0, 'A', 'S', 'R', 'S');
        bool innerOk = raw.Length >= 0x14 && Magic4(raw, 0x10, 'K', 'T', 'S', 'R');

        string platform = outerOk && raw.Length > 0x1B ? raw[0x1B] switch
        {
            0x01 => "PC",
            0x03 => "PS4/Vita",
            0x04 => "Switch",
            _    => $"0x{raw[0x1B]:X2}",
        } : "";

        var doc = new
        {
            format      = label,
            outerMagic  = outerOk ? "ASRS" : BytesToHex(raw, 0, 4),
            innerMagic  = innerOk ? "KTSR" : (raw.Length >= 0x14 ? BytesToHex(raw, 0x10, 4) : ""),
            typeHash    = innerOk ? ToHex(ReadU32(raw, 0x14)) : "",
            platform,
            audioId     = outerOk && raw.Length >= 0x20 ? ToHex(ReadU32(raw, 0x1C)) : "",
            fileSize    = raw.Length,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // SRST — raw encrypted/compressed audio stream (no readable header)
    private static string ExtractSrst(byte[] raw)
    {
        bool hasAsrs = raw.Length >= 4 && Magic4(raw, 0, 'A', 'S', 'R', 'S');
        if (hasAsrs)
            return ExtractSrsa(raw, "Sound Resource Stream (.srst)");

        var doc = new
        {
            format   = "Sound Resource Stream (.srst)",
            fileSize = raw.Length,
            note     = "Encrypted/compressed audio stream — no readable header",
            magicHex = BytesToHex(raw, 0, 8),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    private static string ExtractKidsObjDb(byte[] raw) =>
        KidsObjDbDecoder.ToJson(raw);

    // G1N bitmap font header: "_N1G0000"
    private static string ExtractG1n(byte[] raw)
    {
        bool valid = raw.Length >= 0x20 && Magic4(raw, 0, '_', 'N', '1', 'G');
        var doc = new
        {
            format       = "Bitmap Font (G1N)",
            magic        = valid ? "_N1G0000" : BytesToHex(raw, 0, 8),
            fileSize     = raw.Length,
            atlasCount   = valid ? ReadI32(raw, 0x10) : 0,
            atlasTotalSize = valid ? ReadI32(raw, 0x0C) : 0,
            paletteCount = valid ? ReadI32(raw, 0x18) : 0,
            tableCount   = valid ? ReadI32(raw, 0x1C) : 0,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // G1A Animation: "_A2G" + 4-char version
    private static string ExtractG1a(byte[] raw)
    {
        bool valid = raw.Length >= 0x1C && Magic4(raw, 0, '_', 'A', '2', 'G');
        if (!valid) return ExtractGeneric(raw, ".g1a");

        string version   = BytesToAscii(raw, 4, 4);
        uint   dataSize  = ReadU32(raw, 0x08);
        float  fps       = ReadF32(raw, 0x0C);
        ushort boneCount = ReadU16(raw, 0x10);
        ushort revision  = ReadU16(raw, 0x12);

        var doc = new
        {
            format     = "G1A Animation",
            version    = "_A2G" + version,
            fileSize   = raw.Length,
            dataSize   = (int)dataSize,
            fps        = MathF.Round(fps, 1),
            boneCount  = (int)boneCount,
            revision   = (int)revision,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // BGIR Rig Binary
    private static string ExtractRigBin(byte[] raw)
    {
        bool valid = raw.Length >= 0x10 && Magic4(raw, 0, 'B', 'G', 'I', 'R');
        if (!valid) return ExtractGeneric(raw, ".rigbin");

        var doc = new
        {
            format        = "Rig Binary (BGIR)",
            magic         = "BGIR",
            version       = (int)ReadU32(raw, 0x04),
            declaredSize  = (int)ReadU32(raw, 0x08),
            rigConfigHash = ToHex(ReadU32(raw, 0x0C)),
            fileSize      = raw.Length,
            note          = "Fixed-size blob containing physics/ragdoll float parameters",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // G1CO Collision: "OC1G" + version
    private static string ExtractG1co(byte[] raw)
    {
        bool valid = raw.Length >= 8 && Magic4(raw, 0, 'O', 'C', '1', 'G');
        if (!valid) return ExtractGeneric(raw, ".g1co");

        string version     = BytesToAscii(raw, 4, 4);
        uint   declaredSz  = ReadU32(raw, 0x08);
        uint   sectionCount = raw.Length >= 0x1C ? ReadU32(raw, 0x18) : 0;

        var doc = new
        {
            format       = "G1CO Collision",
            version      = "OC1G" + version,
            fileSize     = raw.Length,
            declaredSize = (int)declaredSz,
            sectionCount = (int)sectionCount,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // G1S Skeleton: "_S2G" + version
    private static string ExtractG1s(byte[] raw)
    {
        bool valid = raw.Length >= 0x20 && Magic4(raw, 0, '_', 'S', '2', 'G');
        if (!valid) return ExtractGeneric(raw, ".g1s");

        string version    = BytesToAscii(raw, 4, 4);
        uint   totalSize  = ReadU32(raw, 0x08);
        string name       = ReadNullTermAscii(raw, 0x10, 32);

        var doc = new
        {
            format       = "G1S Skeleton",
            version      = "_S2G" + version,
            fileSize     = raw.Length,
            declaredSize = (int)totalSize,
            name,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // SWGQ Physics/Swing
    private static string ExtractSwg(byte[] raw)
    {
        bool valid = raw.Length >= 0x10 && Magic4(raw, 0, 'S', 'W', 'G', 'Q');
        if (!valid) return ExtractGeneric(raw, ".swg");

        uint totalSize  = ReadU32(raw, 0x08);
        uint chainCount = ReadU32(raw, 0x0C);
        string firstName = raw.Length >= 0x20 ? ReadNullTermAscii(raw, 0x10, 32) : "";

        var doc = new
        {
            format       = "SWG Physics/Chain (SWGQ)",
            magic        = "SWGQ",
            fileSize     = raw.Length,
            declaredSize = (int)totalSize,
            chainCount   = (int)chainCount,
            firstName,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // LCSK Culling Scene: "LCSK" + version
    private static string ExtractLcsk(byte[] raw)
    {
        bool valid = raw.Length >= 0x10 && Magic4(raw, 0, 'L', 'C', 'S', 'K');
        if (!valid) return ExtractGeneric(raw, ".lcsk");

        string version   = BytesToAscii(raw, 4, 4);
        uint   totalSize = ReadU32(raw, 0x08);
        uint   itemCount = raw.Length >= 0x10 ? ReadU32(raw, 0x0C) : 0;

        var doc = new
        {
            format       = "LCSK Culling Scene",
            version      = "LCSK" + version,
            fileSize     = raw.Length,
            declaredSize = (int)totalSize,
            itemCount    = (int)itemCount,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // GSTK Config: "GSTK" header with entry count at byte 0x08
    private static string ExtractGstk(byte[] raw)
    {
        bool valid = raw.Length >= 0x10 && Magic4(raw, 0, 'G', 'S', 'T', 'K');
        if (!valid) return ExtractGeneric(raw, ".gstk");

        // [0x08] lo-byte = entry count, next byte = 0, [0x0A] = 0x01 (version?)
        int entryCount = raw.Length >= 0x09 ? ReadU16(raw, 0x08) : 0;

        var doc = new
        {
            format     = "GSTK Gesture/State Config",
            magic      = "GSTK",
            entryCount,
            fileSize   = raw.Length,
            note       = "12B entries: {stateIdx u16, val0 u16, a u16, b u16, 0x0004 u16, 0x0004 u16}",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // ME1G Large Mesh: "ME1G" + version
    private static string ExtractMe1g(byte[] raw)
    {
        bool valid = raw.Length >= 8 && Magic4(raw, 0, 'M', 'E', '1', 'G');
        if (!valid) return ExtractGeneric(raw, ".me1g");

        string version   = BytesToAscii(raw, 4, 4);
        // From sample analysis: [0x08] varies (may be mesh group flags)
        ushort meshFlags = ReadU16(raw, 0x08);
        ushort subType   = ReadU16(raw, 0x0A);

        var doc = new
        {
            format   = "ME1G Large Mesh",
            version  = "ME1G" + version,
            fileSize = raw.Length,
            meshFlags = $"0x{meshFlags:X4}",
            subType   = (int)subType,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // KTF2 Font: "2FTK" magic (KTF2 reversed in LE)
    private static string ExtractKtf2(byte[] raw)
    {
        bool valid = raw.Length >= 0x20 && raw[0]=='2' && raw[1]=='F' && raw[2]=='T' && raw[3]=='K';
        if (!valid) return ExtractGeneric(raw, ".ktf2");

        uint glyphGroupCount = ReadU32(raw, 0x08);
        uint declaredSize    = ReadU32(raw, 0x0C);
        uint atlasOffset     = ReadU32(raw, 0x14);

        var doc = new
        {
            format           = "KTF2 Font (2FTK0000)",
            magic            = "2FTK0000",
            glyphGroupCount  = (int)glyphGroupCount,
            declaredSize     = (int)declaredSize,
            atlasOffset      = (int)atlasOffset,
            fileSize         = raw.Length,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // CGRS Scene Graph Config: "CGRS"
    private static string ExtractSgcBin(byte[] raw)
    {
        bool valid   = raw.Length >= 0x10 && Magic4(raw, 0, 'C', 'G', 'R', 'S');
        if (!valid) return ExtractGeneric(raw, ".sgcbin");

        uint totalSize = ReadU32(raw, 0x08);
        bool hasKtsr   = raw.Length >= 0x14 && Magic4(raw, 0x10, 'K', 'T', 'S', 'R');

        var doc = new
        {
            format       = "Scene Graph Config (CGRS)",
            magic        = "CGRS",
            fileSize     = raw.Length,
            declaredSize = (int)totalSize,
            containsKtsr = hasKtsr,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // _RGK Render Graph: "_RGK0000"
    private static string ExtractKidsRender(byte[] raw)
    {
        bool valid = raw.Length >= 0x18 && Magic4(raw, 0, '_', 'R', 'G', 'K');
        if (!valid) return ExtractGeneric(raw, ".kidsrender");

        var doc = new
        {
            format       = "Render Graph (_RGK0000)",
            magic        = "_RGK0000",
            fileSize     = raw.Length,
            headerSize   = (int)ReadU32(raw, 0x08),
            nodeCount    = (int)ReadU32(raw, 0x0C),
            declaredSize = (int)ReadU32(raw, 0x10),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // M1GK / P1GK / C1GK geometry containers
    // Header: 4B magic + {03 03 03 03} + u32 headerSize + u32 pathLen + pathString + embedded data
    private static string ExtractGeomContainer(byte[] raw, string containerMagic, string innerType)
    {
        bool valid = raw.Length >= 0x10 &&
            raw[0] == containerMagic[0] && raw[1] == containerMagic[1] &&
            raw[2] == containerMagic[2] && raw[3] == containerMagic[3];
        if (!valid) return ExtractGeneric(raw, "." + containerMagic.ToLower());

        uint headerSize = ReadU32(raw, 0x08);
        uint pathLen    = ReadU32(raw, 0x0C);
        string srcPath  = raw.Length >= 0x10 + (int)pathLen
            ? ReadNullTermAscii(raw, 0x10, (int)pathLen)
            : "";

        // Find embedded magic after path/header
        string innerMagic = "";
        int innerOffset = (int)headerSize;
        if (innerOffset + 8 <= raw.Length)
            innerMagic = BytesToAscii(raw, innerOffset, 8);

        var doc = new
        {
            format      = containerMagic + " Geometry Container",
            magic       = containerMagic,
            innerType,
            sourcePath  = srcPath,
            innerMagic,
            fileSize    = raw.Length,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // OIDEx / OIDSq: count u32 + 8B padding + 0x00010003 marker + 12B entries
    private static string ExtractOidEx(byte[] raw, string label)
    {
        uint maxOidIndex   = raw.Length >= 4  ? ReadU32(raw, 0x00) : 0;
        uint versionMarker = raw.Length >= 0x10 ? ReadU32(raw, 0x0C) : 0;
        int  entryCount    = raw.Length >= 16 ? (raw.Length - 16) / 12 : 0;

        var doc = new
        {
            format         = label,
            maxOidIndex    = (int)maxOidIndex,
            versionMarker  = ToHex(versionMarker),
            entryCount,
            fileSize       = raw.Length,
            note           = "Entries: {oid_a u32, oid_b u32, flags u32} × 12B",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // OBORO static resource: count u32 + (x,y,z f32)×count
    private static string ExtractOboro(byte[] raw)
    {
        if (raw.Length < 4) return ExtractGeneric(raw, ".oboro");

        uint vertexCount   = ReadU32(raw, 0);
        int  expectedSize  = 4 + (int)vertexCount * 12;

        var doc = new
        {
            format       = "OBORO Static Resource",
            vertexCount  = (int)vertexCount,
            expectedSize,
            fileSize     = raw.Length,
            note         = "Vertex list: count u32 + (x f32, y f32, z f32) × count",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // EFPL Effect Playlist — files in DOA6LR are all 0xFF (placeholder)
    private static string ExtractEfpl(byte[] raw)
    {
        bool allFF = raw.Length > 0;
        if (allFF) { foreach (byte b in raw) { if (b != 0xFF) { allFF = false; break; } } }

        var doc = new
        {
            format  = "EFPL Effect Playlist",
            fileSize = raw.Length,
            isEmpty  = allFF,
            note     = allFF ? "Placeholder (all 0xFF bytes — no effects defined)" : "Effect playlist data present",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // XF1G VFX/Shader data: "XF1G" + version
    private static string ExtractXf1g(byte[] raw)
    {
        bool valid = raw.Length >= 8 && Magic4(raw, 0, 'X', 'F', '1', 'G');
        if (!valid) return ExtractGeneric(raw, ".xf1g");

        string version   = BytesToAscii(raw, 4, 4);
        uint   totalSize = ReadU32(raw, 0x08);
        uint   matCount  = raw.Length >= 0x18 ? ReadU32(raw, 0x14) : 0;

        var doc = new
        {
            format       = "XF1G VFX/Shader",
            version      = "XF1G" + version,
            fileSize     = raw.Length,
            declaredSize = (int)totalSize,
            matrixCount  = (int)matCount,
            note         = "Contains 4×4 float matrices for visual effect transforms",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // Effect Select table (TypeKtid 0xF20DE437) — ".effselect"
    // Structure: count u32 + count × 104B entries {name[64] + u32 id + u32 + 6×f32}
    private static string ExtractEffSelect(byte[] raw)
    {
        if (raw.Length < 4) return ExtractGeneric(raw, ".effselect");

        uint count = ReadU32(raw, 0);
        const int ENTRY_SIZE = 104;
        const int NAME_SIZE  = 64;

        var names = new List<string>();
        for (int i = 0; i < (int)count && 4 + i * ENTRY_SIZE + NAME_SIZE <= raw.Length; i++)
        {
            int nameOffset = 4 + i * ENTRY_SIZE;
            string name = ReadNullTermAscii(raw, nameOffset, NAME_SIZE);
            names.Add(name);
        }

        var doc = new
        {
            format      = "Effect Select Table",
            effectCount = (int)count,
            effectNames = names.ToArray(),
            fileSize    = raw.Length,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // Name Database (TypeKtid 0xBF6B52C7) — ".name"
    private static string ExtractNameDb(byte[] raw)
    {
        // Probe: magic unknown, show generic header for now
        var doc = new
        {
            format     = "Name Database (.name)",
            fileSize   = raw.Length,
            magicHex   = BytesToHex(raw, 0, Math.Min(8, raw.Length)),
            magicAscii = BytesToAscii(raw, 0, Math.Min(8, raw.Length)),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // Unknown 0x133D2C3B — KTID hash list used in G1M bundles
    // Structure: 00×4 + u32 count + 12B hash entries
    private static string ExtractUnk133d(byte[] raw)
    {
        if (raw.Length < 8) return ExtractGeneric(raw, ".unk_133d");

        uint count     = ReadU32(raw, 0x04);
        int  entryCount = raw.Length >= 8 ? (raw.Length - 8) / 12 : 0;

        var doc = new
        {
            format     = "Unknown KTID Hash List (0x133D2C3B)",
            fileSize   = raw.Length,
            count      = (int)count,
            entryCount,
            note       = "Packed KTID hash table, possibly G1M dependency list",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // Generic fallback — shows first 16 bytes
    private static string ExtractGeneric(byte[] raw, string ext)
    {
        int hLen = Math.Min(16, raw.Length);
        var doc = new
        {
            format     = $"Binary ({ext})",
            fileSize   = raw.Length,
            magicHex   = BytesToHex(raw, 0, hLen),
            magicAscii = BytesToAscii(raw, 0, hLen),
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static float Round3(float v) => MathF.Round(v, 3);

    private static string TexTypeName(int t) => t switch
    {
        1  => "COLOR",
        2  => "NORMAL",
        3  => "SPECULAR",
        5  => "DIRT",
        37 => "ROUGHNESS",
        41 => "METALLIC",
        47 => "EMISSIVE",
        55 => "SSS",
        62 => "AO",
        _  => $"TYPE_{t}",
    };
}
