using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Decodes KatanaEngine GT1G texture files (version "0600" / "1600").
/// Uses BCnEncoder.NET for BCn block decompression (no native DLL required).
/// </summary>
public static class G1tDecoder
{
    // ── EX_SWIZZLE_TYPE constants ─────────────────────────────────────────────
    private const byte EX_SWIZZLE_NONE      = 0x00;
    private const byte EX_SWIZZLE_DX12_64KB = 0x01;
    private const byte EX_SWIZZLE_ZLIB      = 0x03;

    // ── Format code table ────────────────────────────────────────────────────

    private enum FmtKind { Unknown, Uncompressed, BCn }

    private record FmtDesc(FmtKind Kind, BCnEncoder.Shared.CompressionFormat BCnFmt,
                           int BytesPerPixel, string Name);

    private static readonly Dictionary<byte, FmtDesc> s_fmtMap = new()
    {
        // ── DOA6 probe-derived uncompressed ───────────────────────────────────
        [0x01] = new(FmtKind.Uncompressed, default, 4, "RGBA8_UNORM"),
        [0x02] = new(FmtKind.Uncompressed, default, 4, "BGRA8_UNORM"),
        [0x03] = new(FmtKind.Uncompressed, default, 8, "RGBA16_UNORM"),
        [0x04] = new(FmtKind.Uncompressed, default, 1, "R8_UNORM"),
        // ── RDBExplorer-derived uncompressed (non-conflicting) ────────────────
        [0x09] = new(FmtKind.Uncompressed, default, 4, "RGBA8_0x09"),
        [0x0A] = new(FmtKind.Uncompressed, default, 4, "BGRA8_0x0A"),
        [0x0C] = new(FmtKind.Uncompressed, default, 8, "RGBA16F_0x0C"),
        [0x67] = new(FmtKind.Uncompressed, default, 4, "RGBA8_0x67"),
        [0x74] = new(FmtKind.Uncompressed, default, 4, "RGBA8_0x74"),
        // ── BCn — DOA6 probe ──────────────────────────────────────────────────
        [0x22] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x22"),
        [0x3C] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_SRGB"),
        [0x3D] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_SRGB"),
        [0x3E] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_SRGB"),
        [0x4C] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x4C"),
        [0x54] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x54"),
        [0x59] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_UNORM"),
        [0x5A] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_UNORM"),
        [0x5B] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_UNORM"),
        [0x5C] = new(FmtKind.BCn, CompressionFormat.Bc4,  0, "BC4_UNORM"),
        [0x5D] = new(FmtKind.BCn, CompressionFormat.Bc5,  0, "BC5_UNORM"),
        [0x5E] = new(FmtKind.BCn, CompressionFormat.Bc6U, 0, "BC6H_UF16"),
        [0x5F] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_UNORM"),
        [0xF9] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0xF9"),
        [0xFC] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0xFC"),
        // ── BCn — older KatanaEngine range (0x06-0x12) ────────────────────────
        [0x06] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_0x06"),
        [0x07] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_0x07"),
        [0x08] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_0x08"),
        [0x10] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_0x10"),
        [0x11] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_0x11"),
        [0x12] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_0x12"),
        // ── BCn — 0x60-0x66 aliases (newer platform codes) ───────────────────
        [0x60] = new(FmtKind.BCn, CompressionFormat.Bc1,  0, "BC1_0x60"),
        [0x61] = new(FmtKind.BCn, CompressionFormat.Bc2,  0, "BC2_0x61"),
        [0x62] = new(FmtKind.BCn, CompressionFormat.Bc3,  0, "BC3_0x62"),
        [0x63] = new(FmtKind.BCn, CompressionFormat.Bc4,  0, "BC4_0x63"),
        [0x64] = new(FmtKind.BCn, CompressionFormat.Bc5,  0, "BC5_0x64"),
        [0x65] = new(FmtKind.BCn, CompressionFormat.Bc6U, 0, "BC6H_0x65"),
        [0x66] = new(FmtKind.BCn, CompressionFormat.Bc7,  0, "BC7_0x66"),
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
            // dim byte layout: (log2_height << 4) | log2_width  — upper nibble = height, lower = width
            uint w = 1u << (dim & 0xF);
            uint h = 1u << ((dim >> 4) & 0xF);

            // Format 0x4C encodes square textures: actual side = 1 << (upper_nibble - 1).
            // e.g. dim=0xC1 → standard gives 4096h×2w; actual is 2048×2048.
            if (fmtCode == 0x4C && ((dim >> 4) & 0xF) > 0)
            {
                uint side = 1u << (((dim >> 4) & 0xF) - 1);
                w = h = side;
            }

            uint extSize      = 0;
            byte exSwizzleType = EX_SWIZZLE_NONE;
            if (fmtCode != 0)
            {
                extSize = ReadU32(data, ep + 8);
                if (extSize > 0x400) extSize = 0x0C;

                // ExtraHeaderRaw layout (offsets relative to ep+8):
                //   [0..3]  = extSize itself
                //   [4..7]  = ZScale
                //   [8..9]  = exInfo (faces | array<<4)
                //   [10]    = EX_SwizzleType
                //   [12..15]= Width override  (if extSize >= 0x10)
                //   [16..19]= Height override (if extSize >= 0x14)
                if (extSize >= 0x0C && ep + 18 < data.Length)
                    exSwizzleType = data[ep + 18];

                if (extSize >= 0x10 && ep + 20 + 4 <= data.Length)
                {
                    uint ow = ReadU32(data, ep + 20);
                    if (ow > 0 && ow <= 65536) w = ow;  // reject garbage from non-standard extSize layouts
                }
                if (extSize >= 0x14 && ep + 24 + 4 <= data.Length)
                {
                    uint oh = ReadU32(data, ep + 24);
                    if (oh > 0 && oh <= 65536) h = oh;
                }
            }

            int pixStart = ep + 8 + (int)extSize;

            bool fmtKnown = s_fmtMap.TryGetValue(fmtCode, out var fd);
            if (sequential && fmtCode == 0)
                nextEp = ep + 8; // null/placeholder slot — fixed 8-byte header, no pixel data
            else if (sequential && fmtKnown && exSwizzleType != EX_SWIZZLE_ZLIB)
                nextEp = pixStart + ComputeMipChainSize(fd!, w, h, mipCount);

            AppLogger.Info($"  G1T[{i}] ep=0x{ep:x} fmt=0x{fmtCode:x2}({GetFormatName(fmtCode)}) {w}x{h} mips={mipCount} extSize={extSize} swizzle=0x{exSwizzleType:x2} pixStart=0x{pixStart:x}" +
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
                                        GetFormatName(fmtCode), pixStart, exSwizzleType));

            // In sequential mode, stop if format unknown or ZLIB-compressed (can't compute next offset)
            if (sequential && fmtCode != 0 && (!fmtKnown || exSwizzleType == EX_SWIZZLE_ZLIB))
            {
                if (!fmtKnown)
                    AppLogger.Warn($"  G1T sequential: unknown fmt=0x{fmtCode:x2} at slot {i}, cannot compute mip size → stopping");
                else
                    AppLogger.Warn($"  G1T sequential: ZLIB-compressed at slot {i}, cannot compute sequential size → stopping");
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

            // Resolve the raw pixel bytes, handling ZLIB decompression
            byte[] srcData;
            int    srcOffset;

            if (t.ExSwizzleType == EX_SWIZZLE_ZLIB)
            {
                srcData   = DecompressZlibTexture(data, t.PixelStart);
                srcOffset = 0;
                if (srcData.Length == 0) continue;
            }
            else
            {
                srcData   = data;
                srcOffset = t.PixelStart;
            }

            int pixSize = ComputeBlockSize(fd, t.Width, t.Height);
            if (pixSize == 0 || srcOffset + pixSize > srcData.Length) continue;

            // DX12 64KB tile deswizzle (needed for newer platform assets)
            if (t.ExSwizzleType == EX_SWIZZLE_DX12_64KB && fd.Kind == FmtKind.BCn && pixSize > 65536)
            {
                byte[] tile = new byte[pixSize];
                Array.Copy(srcData, srcOffset, tile, 0, pixSize);
                srcData   = DeswizzleD3D12_64KB(tile, t.Width, t.Height, fd);
                srcOffset = 0;
            }

            var pixData = new ReadOnlyMemory<byte>(srcData, srcOffset, pixSize);

            Image<Rgba32>? img = fd.Kind switch
            {
                FmtKind.BCn          => DecodeBCn(decoder, pixData, t, fd),
                FmtKind.Uncompressed => DecodeUncompressed(srcData, srcOffset, t, fd),
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
                        0x5C or 0x63 => new Rgba32(c.r, c.r, c.r, 255), // BC4 variants → grayscale
                        0x5D or 0x64 => ReconstructNormal(c),            // BC5 variants → normal map
                        _            => new Rgba32(c.r, c.g, c.b, c.a),
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
                        0x01 or 0x09 or 0x67 or 0x74
                             => new Rgba32(data[o], data[o+1], data[o+2], data[o+3]),        // RGBA8
                        0x02 or 0x0A
                             => new Rgba32(data[o+2], data[o+1], data[o], data[o+3]),        // BGRA8→RGBA8
                        0x03 or 0x0C
                             => new Rgba32(data[o], data[o+2], data[o+4], data[o+6]),        // RGBA16/16F→8 (high byte)
                        0x04 => new Rgba32(data[o], data[o], data[o], 255),                  // R8→gray
                        _    => new Rgba32(0, 0, 0, 255),
                    };
                }
            }
        });

        return img;
    }

    // ── ZLIB texture decompression ───────────────────────────────────────────

    /// <summary>
    /// Decompresses a ZLIB_COMPRESSED texture block (EX_SWIZZLE_TYPE = 0x03).
    /// Format: custom chunk table header followed by individually-zlib-compressed windows.
    /// </summary>
    private static byte[] DecompressZlibTexture(byte[] data, int startPos)
    {
        try
        {
            using var ms = new MemoryStream(data, startPos, data.Length - startPos, writable: false);
            using var br = new BinaryReader(ms);

            br.ReadBytes(4);            // magic
            br.ReadInt32();             // tableSize
            br.ReadInt32();             // unk
            int windowSize     = br.ReadInt32();
            int meta1Count     = br.ReadInt32();
            int chunkCount     = br.ReadInt32();
            int meta2Count     = br.ReadInt32();
            int hasUncompChunk = br.ReadInt32();
            int uncompChunkSz  = br.ReadInt32();

            // skip meta tables (16 bytes per entry)
            br.ReadBytes(meta1Count * 16);
            br.ReadBytes(meta2Count * 16);

            var chunkTable = new (int offset, int size)[chunkCount];
            for (int j = 0; j < chunkCount; j++)
                chunkTable[j] = (br.ReadInt32(), br.ReadInt32());

            (int offset, int size) uncompInfo = (0, 0);
            if (hasUncompChunk > 0)
                uncompInfo = (br.ReadInt32(), br.ReadInt32());

            int totalSize = chunkCount * windowSize + (hasUncompChunk > 0 ? uncompChunkSz : 0);
            byte[] result = new byte[totalSize];

            for (int j = 0; j < chunkCount; j++)
            {
                ms.Position = chunkTable[j].offset - startPos;
                uint compSize     = br.ReadUInt32();
                byte[] compressed = br.ReadBytes((int)compSize);

                using var csMs = new MemoryStream(compressed);
                using var zs   = new ZLibStream(csMs, CompressionMode.Decompress);
                using var tmp  = new MemoryStream();
                zs.CopyTo(tmp);
                byte[] dec   = tmp.ToArray();
                int toCopy   = Math.Min(dec.Length, windowSize);
                Array.Copy(dec, 0, result, j * windowSize, toCopy);
            }

            if (hasUncompChunk > 0)
            {
                ms.Position = uncompInfo.offset - startPos;
                byte[] last = br.ReadBytes(uncompChunkSz);
                int finalOff = chunkCount * windowSize;
                int toCopy   = Math.Min(last.Length, result.Length - finalOff);
                Array.Copy(last, 0, result, finalOff, toCopy);
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Exception("G1T ZLIB decompress failed", ex);
            return [];
        }
    }

    // ── D3D12 64KB tile deswizzle ────────────────────────────────────────────

    /// <summary>Deswizzles D3D12 64KB-tiled BCn data into linear row-major layout.</summary>
    private static byte[] DeswizzleD3D12_64KB(byte[] src, uint width, uint height, FmtDesc fd)
    {
        int bytesPerBlock = fd.BCnFmt switch
        {
            CompressionFormat.Bc1 or CompressionFormat.Bc4 => 8,
            _ => 16,
        };
        if (bytesPerBlock == 0) return src;

        uint blockW = (width  + 3) / 4;
        uint blockH = (height + 3) / 4;

        byte[] dst = new byte[src.Length];

        const uint TileBytes    = 64 * 1024;
        const uint TileRowBytes = 1024;
        uint tileWidth  = TileRowBytes / (uint)bytesPerBlock;
        const uint TileHeight = 64;

        uint tilesX = (blockW + tileWidth  - 1) / tileWidth;
        uint tilesY = (blockH + TileHeight - 1) / TileHeight;

        for (uint ty = 0; ty < tilesY; ty++)
        {
            for (uint tx = 0; tx < tilesX; tx++)
            {
                uint tileBase = (ty * tilesX + tx) * TileBytes;
                for (uint row = 0; row < TileHeight; row++)
                {
                    uint y = ty * TileHeight + row;
                    if (y >= blockH) continue;

                    uint srcRow  = tileBase + row * TileRowBytes;
                    uint dstRow  = y * blockW * (uint)bytesPerBlock;
                    uint xOffset = tx * tileWidth;
                    if (xOffset >= blockW) continue;

                    uint copyBlocks = Math.Min(tileWidth, blockW - xOffset);
                    uint copyBytes  = copyBlocks * (uint)bytesPerBlock;

                    if (srcRow + copyBytes <= src.Length &&
                        dstRow + xOffset * bytesPerBlock + copyBytes <= dst.Length)
                    {
                        Array.Copy(src, (int)srcRow,
                                   dst, (int)(dstRow + xOffset * (uint)bytesPerBlock),
                                   (int)copyBytes);
                    }
                }
            }
        }
        return dst;
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
                         uint MipCount, uint ExtSize, string FmtName, int PixelStart,
                         byte ExSwizzleType = 0);

public record G1TFileInfo(bool Valid, string Version, uint TexCount, G1TTexInfo[] Textures);
