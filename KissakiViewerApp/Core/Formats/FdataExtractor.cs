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
        if (rec.FileSize == 0) return [];

        string fullPath = Path.Combine(_fdataDir, containerName);
        if (!File.Exists(fullPath))
        {
            AppLogger.Error($"fdata not found: {fullPath}");
            return [];
        }

        byte[]? raw = ReadRaw(containerName, rec.FdataOffset, rec.SizeInContainer);
        if (raw is null)
        {
            AppLogger.Error($"  → ReadRaw returned null (offset or size out of range)");
            return [];
        }
        if (raw.Length < IDRK_SIZE) return [];

        return ParseAndDecompress(raw);
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
        // Verify IDRK magic
        if (raw[0] != 'I' || raw[1] != 'D' || raw[2] != 'R' || raw[3] != 'K') return [];

        long compressedSize   = ReadI64(raw, 0x10);
        long uncompressedSize = ReadI64(raw, 0x18);

        int overhead     = (int)(raw.Length - compressedSize);
        int payloadStart = overhead;
        int payloadSize  = (int)compressedSize;

        if (payloadStart < 0 || payloadStart + payloadSize > raw.Length) return [];

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
        catch { return null; }
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
