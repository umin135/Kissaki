namespace KissakiViewer.Core.Formats;

/// <summary>
/// KatanaEngine .mtl file reader.
/// The exact binary layout of .mtl is not fully documented; we use a KTID scan
/// approach: scan every 4-byte word in the decompressed data and collect any
/// value that matches a known asset FileKtid.  This reliably extracts the
/// G1T texture references embedded in the material file.
/// </summary>
public static class MtlReader
{
    /// <summary>
    /// Returns the 4-byte magic string of the decompressed .mtl payload.
    /// Useful for reverse-engineering the format header.
    /// </summary>
    public static string GetMagic(byte[] data) =>
        data.Length >= 4
            ? $"{(char)data[0]}{(char)data[1]}{(char)data[2]}{(char)data[3]}"
            : "(empty)";

    /// <summary>
    /// Scans the raw .mtl bytes for 4-byte values that appear in
    /// <paramref name="knownKtids"/>.  Returns them in discovery order
    /// (each KTID returned at most once).
    /// </summary>
    public static uint[] ScanForTextureKtids(byte[] data, IReadOnlySet<uint> knownKtids)
    {
        var found = new List<uint>();
        var seen  = new HashSet<uint>();

        for (int i = 0; i <= data.Length - 4; i += 4)
        {
            uint v = ReadU32(data, i);
            if (v != 0 && v != 0xFFFFFFFF && knownKtids.Contains(v) && seen.Add(v))
                found.Add(v);
        }
        return [.. found];
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
