using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Decodes KatanaEngine GT1G texture files (version "0600" / "1600").
/// Uses BCnEncoder.NET for BCn block decompression (no native DLL required).
/// </summary>
public static class G1tDecoder
{
    // ── Format code table ────────────────────────────────────────────────────

    private enum FmtKind { Unknown, Uncompressed, BCn }

    private record FmtDesc(FmtKind Kind, BCnEncoder.Shared.CompressionFormat BCnFmt,
                           int BytesPerPixel, string Name);

    private static readonly Dictionary<byte, FmtDesc> s_fmtMap = new()
    {
        [0x01] = new(FmtKind.Uncompressed, default, 4, "RGBA8_UNORM"),
        [0x02] = new(FmtKind.Uncompressed, default, 4, "BGRA8_UNORM"),
        [0x03] = new(FmtKind.Uncompressed, default, 8, "RGBA16_UNORM"),
        [0x04] = new(FmtKind.Uncompressed, default, 1, "R8_UNORM"),
        [0x22] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x22"),
        [0x3C] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_SRGB"),
        [0x3D] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_SRGB"),
        [0x3E] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_SRGB"),
        [0x54] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x54"),
        [0x59] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_UNORM"),
        [0x5A] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_UNORM"),
        [0x5B] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_UNORM"),
        [0x5C] = new(FmtKind.BCn, CompressionFormat.Bc4,  0, "BC4_UNORM"),
        [0x5D] = new(FmtKind.BCn, CompressionFormat.Bc5,  0, "BC5_UNORM"),
        [0x5E] = new(FmtKind.BCn, CompressionFormat.Bc6U, 0, "BC6H_UF16"),
        [0x5F] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_UNORM"),
        [0x4C] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x4C"),
        [0xF9] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0xF9"),
        [0xFC] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0xFC"),
    };

    public static string GetFormatName(byte code) =>
        code == 0 ? "null" :
        s_fmtMap.TryGetValue(code, out var d) ? d.Name : $"?0x{code:x2}";

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Parse G1T header metadata without decoding pixels.</summary>
    public static G1TFileInfo Survey(byte[] data)
    {
        if (data.Length < 32 || data[0] != 'G' || data[1] != 'T' || data[2] != '1' || data[3] != 'G')
            return new G1TFileInfo(false, "", 0, []);

        string version   = Encoding.ASCII.GetString(data, 4, 4);
        uint   headerSz  = ReadU32(data, 0x0C);
        uint   texCount  = ReadU32(data, 0x14);

        int tableBase = (int)headerSz;
        if (tableBase + 4 > data.Length)
            return new G1TFileInfo(false, version, texCount, []);

        // "1600" format: the offset table has only ONE 4-byte entry (offset to tex0).
        // All subsequent textures are stored sequentially. "0600" uses a full N-entry table.
        bool sequential = version == "1600";

        var texInfos = new List<G1TTexInfo>();
        int nextEp = -1;

        for (uint i = 0; i < texCount; i++)
        {
            int ep;
            if (sequential)
            {
                ep = i == 0
                    ? tableBase + (int)ReadU32(data, tableBase)
                    : nextEp;
            }
            else
            {
                if (tableBase + (i + 1) * 4 > data.Length) break;
                ep = tableBase + (int)ReadU32(data, tableBase + (int)(i * 4));
            }

            if (ep < 0 || ep + 12 > data.Length) break;

            byte byte0   = data[ep];
            byte fmtCode = data[ep + 1];
            byte dim     = data[ep + 2];

            uint mipCount = (uint)((byte0 >> 4) & 0xF);
            if (mipCount == 0) mipCount = 1;
            uint w = 1u << ((dim >> 4) & 0xF);
            uint h = 1u << (dim & 0xF);

            // Format 0x4C encodes square textures as w = h = 1 << ((dim >> 4) - 1).
            // The standard formula produces 4096×2 from dim=0xC1; the actual size is 2048×2048.
            if (fmtCode == 0x4C && ((dim >> 4) & 0xF) > 0)
            {
                uint side = 1u << (((dim >> 4) & 0xF) - 1);
                w = h = side;
            }

            uint extSize = 0;
            if (fmtCode != 0)
            {
                extSize = ReadU32(data, ep + 8);
                if (extSize > 0x400) extSize = 0x0C;
            }

            int pixStart = ep + 12 + (int)extSize;

            bool fmtKnown = s_fmtMap.TryGetValue(fmtCode, out var fd);
            if (sequential && fmtCode == 0)
                nextEp = ep + 8; // null/placeholder slot — fixed 8-byte header, no pixel data
            else if (sequential && fmtKnown)
                nextEp = pixStart + ComputeMipChainSize(fd!, w, h, mipCount);

            AppLogger.Info($"  G1T[{i}] ep=0x{ep:x} fmt=0x{fmtCode:x2}({GetFormatName(fmtCode)}) {w}x{h} mips={mipCount} extSize={extSize} pixStart=0x{pixStart:x}" +
                           (sequential ? $" nextEp=0x{nextEp:x}" : ""));

            // Dump first 32 bytes at pixStart to help identify unknown formats
            if (fmtCode != 0 && !fmtKnown && pixStart + 32 <= data.Length)
            {
                var sb = new System.Text.StringBuilder($"  G1T[{i}] UNKNOWN fmt=0x{fmtCode:x2} pixData[0..31]:");
                for (int b = 0; b < 32; b++)
                    sb.Append($" {data[pixStart + b]:x2}");
                AppLogger.Warn(sb.ToString());
            }

            texInfos.Add(new G1TTexInfo((int)i, fmtCode, w, h, mipCount, extSize,
                                        GetFormatName(fmtCode), pixStart));

            // In sequential mode, if format is unknown we can't advance nextEp → stop
            if (sequential && fmtCode != 0 && !fmtKnown)
            {
                AppLogger.Warn($"  G1T sequential: unknown fmt=0x{fmtCode:x2} at slot {i}, cannot compute mip size → stopping");
                break;
            }
        }

        return new G1TFileInfo(true, version, texCount, [.. texInfos]);
    }

    /// <summary>
    /// Decode all non-null textures in a G1T file.
    /// Returns (slot index, ImageSharp image) pairs.
    /// Caller owns the images and must dispose them.
    /// </summary>
    public static List<(int Slot, Image<Rgba32> Image)> DecodeAll(byte[] data)
    {
        var result = new List<(int, Image<Rgba32>)>();
        G1TFileInfo info = Survey(data);
        if (!info.Valid) return result;

        var decoder = new BcDecoder();

        for (int i = 0; i < info.Textures.Length; i++)
        {
            G1TTexInfo t = info.Textures[i];
            if (t.FmtCode == 0) continue;
            if (!s_fmtMap.TryGetValue(t.FmtCode, out var fd)) continue;

            int pixStart = t.PixelStart;
            int pixSize  = ComputeBlockSize(fd, t.Width, t.Height);
            if (pixSize == 0 || pixStart + pixSize > data.Length) continue;

            var pixData = new ReadOnlyMemory<byte>(data, pixStart, pixSize);

            Image<Rgba32>? img = fd.Kind switch
            {
                FmtKind.BCn          => DecodeBCn(decoder, pixData, t, fd),
                FmtKind.Uncompressed => DecodeUncompressed(data, pixStart, t, fd),
                _                    => null,
            };

            if (img is not null)
                result.Add((i, img));
        }

        return result;
    }

    /// <summary>Decode all textures and save as PNG. Returns number of files saved.</summary>
    public static int SaveAllAsPng(byte[] data, string outputDir, string stem)
    {
        Directory.CreateDirectory(outputDir);
        var textures = DecodeAll(data);
        int saved = 0;

        foreach (var (slot, img) in textures)
        {
            using (img)
            {
                string suffix = textures.Count > 1 ? $"_{slot}" : "";
                string path   = Path.Combine(outputDir, $"{stem}{suffix}.png");
                img.SaveAsPng(path);
                saved++;
            }
        }

        return saved;
    }

    // ── BCn decode with post-processing ──────────────────────────────────────

    private static Image<Rgba32>? DecodeBCn(BcDecoder decoder, ReadOnlyMemory<byte> raw,
                                             G1TTexInfo t, FmtDesc fd)
    {
        try
        {
            using var ms = new MemoryStream(raw.ToArray());

            // BC6H is HDR (half-float): use DecodeRawHdr → ColorRgbFloat → Reinhard LDR
            if (fd.BCnFmt is CompressionFormat.Bc6U or CompressionFormat.Bc6S)
            {
                Memory<BCnEncoder.Shared.ColorRgbFloat> hdr =
                    decoder.DecodeRawHdr(ms, (int)t.Width, (int)t.Height, fd.BCnFmt);
                return PostProcessHdr(hdr, (int)t.Width, (int)t.Height);
            }

            Memory<ColorRgba32> pixels = decoder.DecodeRaw(ms, (int)t.Width, (int)t.Height, fd.BCnFmt);
            return PostProcess(pixels, (int)t.Width, (int)t.Height, t.FmtCode);
        }
        catch (Exception ex)
        {
            AppLogger.Exception($"DecodeBCn fmt={fd.Name} {t.Width}×{t.Height}", ex);
            return null;
        }
    }

    private static Image<Rgba32> PostProcessHdr(Memory<BCnEncoder.Shared.ColorRgbFloat> pixMem,
                                                 int w, int h)
    {
        BCnEncoder.Shared.ColorRgbFloat[] px = pixMem.ToArray();
        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(accessor =>
        {
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++, i++)
                {
                    BCnEncoder.Shared.ColorRgbFloat c = px[i];
                    // Reinhard tone mapping: t = x / (1 + x) maps HDR [0,∞) → [0,1)
                    row[x] = new Rgba32(
                        (byte)(c.r / (1f + c.r) * 255f + 0.5f),
                        (byte)(c.g / (1f + c.g) * 255f + 0.5f),
                        (byte)(c.b / (1f + c.b) * 255f + 0.5f),
                        255);
                }
            }
        });
        return img;
    }

    private static Image<Rgba32> PostProcess(Memory<ColorRgba32> pixMem, int w, int h, byte code)
    {
        // Copy to array so it can be safely captured in the ProcessPixelRows lambda
        // (Span<T> is a ref struct and cannot be captured by closures)
        ColorRgba32[] px = pixMem.ToArray();

        var img = new Image<Rgba32>(w, h);
        img.ProcessPixelRows(accessor =>
        {
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++, i++)
                {
                    ColorRgba32 c = px[i];
                    row[x] = code switch
                    {
                        0x5C => new Rgba32(c.r, c.r, c.r, 255),
                        0x5D => ReconstructNormal(c),
                        _    => new Rgba32(c.r, c.g, c.b, c.a),
                    };
                }
            }
        });
        return img;
    }

    private static Rgba32 ReconstructNormal(ColorRgba32 c)
    {
        float rx  = c.r / 127.5f - 1.0f;
        float ry  = c.g / 127.5f - 1.0f;
        float rz2 = 1.0f - rx * rx - ry * ry;
        float rz  = rz2 > 0.0f ? MathF.Sqrt(rz2) : 0.0f;
        return new Rgba32(
            (byte)((rx * 0.5f + 0.5f) * 255f),
            (byte)((ry * 0.5f + 0.5f) * 255f),
            (byte)((rz * 0.5f + 0.5f) * 255f),
            255);
    }

    // ── Uncompressed decode ───────────────────────────────────────────────────

    private static Image<Rgba32>? DecodeUncompressed(byte[] data, int pixStart,
                                                      G1TTexInfo t, FmtDesc fd)
    {
        int w = (int)t.Width, h = (int)t.Height;
        var img = new Image<Rgba32>(w, h);

        img.ProcessPixelRows(accessor =>
        {
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++, i++)
                {
                    int o = pixStart + i * fd.BytesPerPixel;
                    row[x] = t.FmtCode switch
                    {
                        0x01 => new Rgba32(data[o], data[o+1], data[o+2], data[o+3]),        // RGBA8
                        0x02 => new Rgba32(data[o+2], data[o+1], data[o], data[o+3]),        // BGRA8→RGBA8
                        0x03 => new Rgba32(data[o], data[o+2], data[o+4], data[o+6]),        // RGBA16→8 (high byte)
                        0x04 => new Rgba32(data[o], data[o], data[o], 255),                  // R8→gray
                        _    => new Rgba32(0, 0, 0, 255),
                    };
                }
            }
        });

        return img;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ComputeMipChainSize(FmtDesc fd, uint w, uint h, uint mipCount)
    {
        int total = 0;
        for (uint m = 0; m < mipCount; m++)
        {
            uint mw = Math.Max(w >> (int)m, 1u);
            uint mh = Math.Max(h >> (int)m, 1u);
            total += ComputeBlockSize(fd, mw, mh);
        }
        return total;
    }

    private static int ComputeBlockSize(FmtDesc fd, uint w, uint h)
    {
        if (fd.Kind == FmtKind.BCn)
        {
            int bpb = fd.BCnFmt switch
            {
                CompressionFormat.Bc1  => 8,
                CompressionFormat.Bc4  => 8,
                CompressionFormat.Bc2  => 16,
                CompressionFormat.Bc3  => 16,
                CompressionFormat.Bc5  => 16,
                CompressionFormat.Bc6U => 16,
                CompressionFormat.Bc7  => 16,
                _                      => 0,
            };
            return (int)(((w + 3) / 4) * ((h + 3) / 4) * (uint)bpb);
        }
        return (int)(w * h * (uint)fd.BytesPerPixel);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);
}

// ── Data records ─────────────────────────────────────────────────────────────

public record G1TTexInfo(int Slot, byte FmtCode, uint Width, uint Height,
                         uint MipCount, uint ExtSize, string FmtName, int PixelStart);

public record G1TFileInfo(bool Valid, string Version, uint TexCount, G1TTexInfo[] Textures);
