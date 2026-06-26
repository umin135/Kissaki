using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace KissakiViewer.Core.Compression;

/// <summary>
/// Decompresses the KatanaEngine "zlibext" chunked stream.
/// Format per chunk: uint16 comp_size | uint8[8] padding | deflate payload
/// Each chunk decompresses to at most 16384 bytes.
/// </summary>
public static class ZlibExtHelper
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, long uncompressedSize)
    {
        using var output = new MemoryStream((int)uncompressedSize);
        int pos = 0;

        while (pos + 10 <= src.Length && output.Length < uncompressedSize)
        {
            ushort chunkSize = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]);
            pos += 10; // uint16 + 8 bytes padding

            if (chunkSize == 0 || pos + chunkSize > src.Length)
                break;

            var chunk = src.Slice(pos, chunkSize);
            pos += chunkSize;

            // Try zlib-wrapped deflate first, fall back to raw deflate
            if (!TryZlib(chunk, output))
                TryDeflate(chunk, output);
        }

        return output.ToArray();
    }

    private static bool TryZlib(ReadOnlySpan<byte> data, Stream output)
    {
        try
        {
            long before = output.Length;
            output.Position = before;
            using var ms = new MemoryStream(data.ToArray());
            using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
            zlib.CopyTo(output);
            return true;
        }
        catch
        {
            output.SetLength(output.Length); // no-op but clear intent
            return false;
        }
    }

    private static bool TryDeflate(ReadOnlySpan<byte> data, Stream output)
    {
        try
        {
            long before = output.Length;
            output.Position = before;
            using var ms = new MemoryStream(data.ToArray());
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            deflate.CopyTo(output);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
