using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using KissakiViewer.Core.Compression;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Reads and decompresses assets from KatanaEngine .fdata containers.
/// FDATA layout: [IDRK 56B] [param_count×12B + param_data_size B] [zlibext payload]
/// </summary>
public sealed class FdataExtractor
{
    // IDRK embedded header is exactly 56 bytes
    private const int IDRK_SIZE = 56;

    private readonly string _fdataDir;

    public string FdataDir => _fdataDir;

    public FdataExtractor(string fdataDir) => _fdataDir = fdataDir;

    public bool ContainerExists(string containerName) =>
        File.Exists(Path.Combine(_fdataDir, containerName));

    /// <summary>Decompress asset into memory. Returns empty array on failure.</summary>
    public byte[] ExtractToMemory(AssetRecord rec, string containerName)
    {
        if (rec.FileSize == 0)
        {
            AppLogger.Warn($"[FData] FileSize=0: 0x{rec.FileKtid:x8} (TypeKtid=0x{rec.TypeKtid:x8})");
            return [];
        }

        string fullPath = Path.Combine(_fdataDir, containerName);
        if (!File.Exists(fullPath))
        {
            AppLogger.Error($"[FData] container not found: {Path.GetFileName(fullPath)} (FK=0x{rec.FileKtid:x8}, Storage={rec.Storage})");
            return [];
        }

        byte[]? raw = ReadRaw(containerName, rec.FdataOffset, rec.SizeInContainer);
        if (raw is null)
        {
            AppLogger.Error($"[FData] ReadRaw null: FK=0x{rec.FileKtid:x8} offset={rec.FdataOffset} size={rec.SizeInContainer}");
            return [];
        }
        if (raw.Length < 4) return [];

        byte[] result = ParseAndDecompress(raw);
        if (result.Length > 0) return result;

        // Fallback: some assets are stored without IDRK wrapping (e.g. raw or encrypted).
        // Return the raw block directly — the RDB-recorded FileSize is authoritative.
        return TryRawFallback(raw, rec.FileSize, rec.TypeKtid);
    }

    /// <summary>
    /// Reads the dependency FileKtid at IDRK byte 0x74 (valid only for G1M files,
    /// which use 136-byte overhead). Returns 0 on failure or if the field is zero.
    /// </summary>
    public uint ReadG1mDependencyKtid(AssetRecord rec, string containerName)
    {
        uint toRead = Math.Min(rec.SizeInContainer, 0x88u);
        if (toRead < 0x78) return 0;
        byte[]? raw = ReadRaw(containerName, rec.FdataOffset, toRead);
        if (raw is null || raw.Length < 0x78) return 0;
        if (raw[0] != 'I' || raw[1] != 'D' || raw[2] != 'R' || raw[3] != 'K') return 0;
        return ReadI32U(raw, 0x74);
    }

    /// <summary>Decompress asset and write to disk. Returns true on success.</summary>
    public bool Extract(AssetRecord rec, string containerName, string outPath)
    {
        byte[] data = ExtractToMemory(rec, containerName);
        if (data.Length == 0) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllBytes(outPath, data);
        return true;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private byte[] ParseAndDecompress(byte[] raw)
    {
        if (raw.Length < IDRK_SIZE) return [];

        // Verify IDRK magic
        if (raw[0] != 'I' || raw[1] != 'D' || raw[2] != 'R' || raw[3] != 'K')
        {
            string magic = BitConverter.ToString(raw, 0, Math.Min(16, raw.Length)).Replace("-", " ");
            AppLogger.Warn($"[FData] non-IDRK block: first16=[{magic}] totalLen={raw.Length}");
            return [];
        }

        long compressedSize   = ReadI64(raw, 0x10);
        long uncompressedSize = ReadI64(raw, 0x18);

        int overhead     = (int)(raw.Length - compressedSize);
        int payloadStart = overhead;
        int payloadSize  = (int)compressedSize;

        if (payloadStart < 0 || payloadStart + payloadSize > raw.Length)
        {
            AppLogger.Warn($"[FData] IDRK bad range: payloadStart={payloadStart} payloadSize={payloadSize} rawLen={raw.Length}");
            return [];
        }

        // compSz == uncompSz → stored uncompressed, skip decompressor
        if (compressedSize == uncompressedSize)
            return raw[payloadStart..(payloadStart + payloadSize)];

        try
        {
            return ZlibExtHelper.Decompress(
                new ReadOnlySpan<byte>(raw, payloadStart, payloadSize),
                uncompressedSize);
        }
        catch (Exception ex)
        {
            AppLogger.Exception("ZlibExt decompress failed", ex);
            return [];
        }
    }

    private static byte[] TryRawFallback(byte[] raw, ulong fileSize, uint typeKtid = 0)
    {
        if (raw.Length < 4) return [];

        int take = fileSize > 0 && (ulong)raw.Length >= fileSize
            ? (int)fileSize
            : raw.Length;
        string magic = System.Text.Encoding.ASCII.GetString(raw, 0, Math.Min(4, raw.Length));
        AppLogger.Info($"[FData] raw fallback: TK=0x{typeKtid:x8} magic=[{magic}] take={take} of {raw.Length}");
        return raw[..take];
    }

    // Large-file threshold: above this, use streaming export instead of full memory load.
    internal const long StreamingThreshold = 256 * 1024 * 1024; // 256 MB

    /// <summary>
    /// Extracts an asset directly to <paramref name="outPath"/> without buffering the entire
    /// content in memory. Returns the number of bytes written, or -1 on failure.
    /// Use this for large raw assets (e.g., 1 GB SRST) where ExtractToMemory would OOM.
    /// </summary>
    public long ExtractToFile(AssetRecord rec, string containerName, string outPath)
    {
        if (rec.FileSize == 0) return -1;

        string srcPath = Path.Combine(_fdataDir, containerName);
        if (!File.Exists(srcPath)) { AppLogger.Error($"fdata not found: {srcPath}"); return -1; }

        try
        {
            using var src  = new FileStream(srcPath,  FileMode.Open,   FileAccess.Read,  FileShare.Read);
            using var dst  = new FileStream(outPath,  FileMode.Create, FileAccess.Write, FileShare.None);

            src.Seek((long)rec.FdataOffset, SeekOrigin.Begin);

            // Peek at the IDRK header
            int headerLen = (int)Math.Min(IDRK_SIZE, (long)rec.SizeInContainer);
            byte[] hdr    = new byte[headerLen];
            int hdrRead   = src.ReadAtLeast(hdr, headerLen, throwOnEndOfStream: false);
            if (hdrRead < 4) return -1;

            if (hdr[0] == 'I' && hdr[1] == 'D' && hdr[2] == 'R' && hdr[3] == 'K')
            {
                // IDRK-wrapped: decompress in memory (these are never multi-hundred-MB in practice)
                src.Seek((long)rec.FdataOffset, SeekOrigin.Begin);
                byte[] buf = new byte[rec.SizeInContainer];
                src.ReadAtLeast(buf, (int)rec.SizeInContainer, throwOnEndOfStream: false);
                byte[] decompressed = ParseAndDecompress(buf);
                if (decompressed.Length == 0) return -1;
                dst.Write(decompressed);
                AppLogger.Info($"[FData] stream-decompressed: {decompressed.Length:N0} B → {Path.GetFileName(outPath)}");
                return decompressed.Length;
            }

            // Raw (no IDRK): stream directly — respecting FileSize as the logical end
            long limit   = (long)(rec.FileSize > 0 ? rec.FileSize : rec.SizeInContainer);
            byte[] chunk = new byte[64 * 1024];
            long written = 0;

            // Write the already-read header bytes first
            int take = (int)Math.Min(hdrRead, limit);
            dst.Write(hdr, 0, take);
            written += take;

            while (written < limit)
            {
                int toRead = (int)Math.Min(chunk.Length, limit - written);
                int got    = src.Read(chunk, 0, toRead);
                if (got == 0) break;
                dst.Write(chunk, 0, got);
                written += got;
            }

            AppLogger.Info($"[FData] stream-raw: {written:N0} B → {Path.GetFileName(outPath)}");
            return written;
        }
        catch (Exception ex)
        {
            AppLogger.Exception($"[FData] ExtractToFile ({containerName} @ {rec.FdataOffset})", ex);
            try { File.Delete(outPath); } catch { }
            return -1;
        }
    }

    private byte[]? ReadRaw(string containerName, ulong offset, uint size)
    {
        string path = Path.Combine(_fdataDir, containerName);
        if (!File.Exists(path)) return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek((long)offset, SeekOrigin.Begin);
            byte[] buf = new byte[size];
            int read = fs.ReadAtLeast(buf, (int)size, throwOnEndOfStream: false);
            return read == (int)size ? buf : null;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[FData] ReadRaw exception (offset={offset}, size={size}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ReadI64(byte[] b, int o) =>
        (long)((ulong)ReadI32U(b, o) | ((ulong)ReadI32U(b, o+4) << 32));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadI32(byte[] b, int o) =>
        b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadI32U(byte[] b, int o) =>
        (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);
}
