using System.Numerics;
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

    private static int ReadI32(byte[] b, int o) => (int)ReadU32(b, o);

    private static string ToHex(uint v)  => $"0x{v:x8}";

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
                ".g1t"       => ExtractG1t(raw, assetName, ktidHex),
                ".ktid"      => KtidDecoder.ToJson(assetName, ParseHexKtid(ktidHex), KtidDecoder.Parse(raw)),
                ".grp"       => GrpDecoder.ToJson(assetName, ParseHexKtid(ktidHex), GrpDecoder.Parse(raw)),
                ".mtl"       => ExtractMtlSummary(raw, assetName, ktidHex),
                ".srsa"      => ExtractSrsa(raw, "Sound Resource Archive"),
                ".srst"      => ExtractSrsa(raw, "Sound Resource Stream"),
                ".kidsobjdb" => ExtractKidsObjDb(raw),
                ".g1n"       => ExtractG1n(raw),
                ".rigbin"    => ExtractMagicAndSize(raw, "Rig Binary", ".rigbin"),
                ".g1co"      => ExtractMagicAndSize(raw, "G1 Collision", ".g1co"),
                ".g1a"       => ExtractMagicAndSize(raw, "G1A Animation", ".g1a"),
                _            => ExtractGeneric(raw, typeExt),
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
        // chunk sig uint → 4-char ASCII (little-endian on x86 = correct byte order)
        static string SigStr(uint sig)
        {
            var bytes = BitConverter.GetBytes(sig);
            var sb = new StringBuilder(4);
            foreach (byte b in bytes) sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            return sb.ToString();
        }

        // Collect distinct material indices and their texture assignments
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

        var doc = new
        {
            format    = "G1M Model",
            magic     = "G1M_",
            ktidHash  = ktidHex,
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

    // MTL: show only counts, not full name-group arrays (can be huge)
    private static string ExtractMtlSummary(byte[] raw, string assetName, string ktidHex)
    {
        if (raw.Length < 0x10)
            return ExtractGeneric(raw, ".mtl");

        uint numNames     = ReadU32(raw, 0x00);
        uint numMat       = ReadU32(raw, 0x04);
        uint numCloths    = ReadU32(raw, 0x08);
        uint numPonytails = ReadU32(raw, 0x0C);

        var doc = new
        {
            format       = "MTL Material Bind",
            ktidHash     = ktidHex,
            assetName,
            matCount     = (int)numMat,
            nameGroupCount = (int)numNames,
            clothCount   = (int)numCloths,
            ponytailCount = (int)numPonytails,
            note         = "Full name/cloth arrays available in export.",
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // SRSA / SRST: ASRS + KTSR header
    private static string ExtractSrsa(byte[] raw, string label)
    {
        bool outerOk = raw.Length >= 8 &&
            raw[0] == 'A' && raw[1] == 'S' && raw[2] == 'R' && raw[3] == 'S';
        bool innerOk = raw.Length >= 0x18 &&
            raw[0x10] == 'K' && raw[0x11] == 'T' && raw[0x12] == 'S' && raw[0x13] == 'R';

        string platform = raw.Length > 0x1B ? raw[0x1B] switch
        {
            0x01 => "PC",
            0x03 => "PS4/Vita",
            0x04 => "Switch",
            _    => $"0x{raw[0x1B]:X2}",
        } : "";

        var doc = new
        {
            format     = label,
            outerMagic = outerOk ? "ASRS" : BytesToHex(raw, 0, 4),
            innerMagic = innerOk ? "KTSR" : (raw.Length >= 0x14 ? BytesToHex(raw, 0x10, 4) : ""),
            typeHash   = innerOk ? ToHex(ReadU32(raw, 0x14)) : "",
            platform,
            audioId    = raw.Length >= 0x20 ? ToHex(ReadU32(raw, 0x1C)) : "",
            fileSize   = raw.Length,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // KidsObjDb: full IDOK/RDOK record decode via KidsObjDbDecoder
    private static string ExtractKidsObjDb(byte[] raw) =>
        KidsObjDbDecoder.ToJson(raw);

    // G1N bitmap font header
    private static string ExtractG1n(byte[] raw)
    {
        bool valid = raw.Length >= 0x20 &&
            raw[0] == '_' && raw[1] == 'N' && raw[2] == '1' && raw[3] == 'G';

        var doc = new
        {
            format       = "Bitmap Font (G1N)",
            magic        = valid ? "_N1G0000" : BytesToHex(raw, 0, Math.Min(8, raw.Length)),
            fileSize     = raw.Length,
            headerSize   = valid ? ReadI32(raw, 0x0C) : 0,
            atlasOffset  = valid ? ReadI32(raw, 0x14) : 0,
            paletteCount = valid ? ReadI32(raw, 0x18) : 0,
            tableCount   = valid ? ReadI32(raw, 0x1C) : 0,
        };
        return JsonSerializer.Serialize(doc, s_opts);
    }

    // Formats where we know the magic but not the internal structure
    private static string ExtractMagicAndSize(byte[] raw, string label, string ext)
    {
        int hLen = Math.Min(8, raw.Length);
        var doc = new
        {
            format    = label,
            magic     = BytesToAscii(raw, 0, hLen),
            magicHex  = BytesToHex(raw, 0, hLen),
            fileSize  = raw.Length,
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
