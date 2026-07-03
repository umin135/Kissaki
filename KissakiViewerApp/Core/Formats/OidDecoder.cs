using System.Text.Json;
using System.Text.Json.Nodes;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses .oid (OIDBindTableBinaryFile) files and serialises to JSON.
/// Format: 16B header + 12B entries × (bone_count - 1).
/// Each entry maps a global bone instance ID (global_oid) to a bone name KTID hash.
/// </summary>
public static class OidDecoder
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    // Frequently recurring bone_name_fk hashes in DOA6 character G1Ms
    private static readonly Dictionary<uint, string> s_boneHints = new()
    {
        [0x7AEE68CCu] = "common_bone_01",
        [0x8630DCF5u] = "common_bone_02",
        [0xF6BB7F79u] = "common_bone_03",
        [0x90F79E3Au] = "part_bone_01",
        [0x4FAE6099u] = "part_bone_02",
    };

    /// <summary>
    /// Parses a .oid OIDBindTableBinaryFile blob and returns indented JSON.
    /// </summary>
    public static string ToJson(byte[] data, string assetName, string ktidHex)
    {
        if (data.Length < 16)
            return """{ "error": "file too small for OIDBindTable header" }""";

        uint maxOidIndex = ReadU32(data, 0x00);
        // 0x04 = padding (0)
        uint constant    = ReadU32(data, 0x08);
        // 0x0C = padding (0)
        int  entryCount  = (data.Length - 16) / 12;

        // Derive paired G1M FileKtid: G1M_fk = OID_fk + 0x0E05C687 (confirmed across all DOA6 pairs)
        ReadOnlySpan<char> hexSpan = ktidHex.AsSpan().TrimStart("0xX ".ToCharArray());
        uint.TryParse(hexSpan, System.Globalization.NumberStyles.HexNumber, null, out uint oidFk);
        uint g1mFk  = oidFk + 0x0E05C687u;

        var entries = new JsonArray();
        for (int i = 0; i < entryCount; i++)
        {
            int  off        = 16 + i * 12;
            uint globalOid  = ReadU32(data, off + 0);
            uint boneNameFk = ReadU32(data, off + 4);
            // off + 8 = padding (0)

            entries.Add(new JsonObject
            {
                ["globalOid"]  = globalOid,
                ["boneNameFk"] = HashLabel(boneNameFk),
            });
        }

        var root = new JsonObject
        {
            ["format"]      = "OIDBindTable",
            ["ktidHash"]    = ktidHex,
            ["pairedG1mFk"] = $"0x{g1mFk:x8}",
            ["assetName"]   = assetName,
            ["maxOidIndex"] = maxOidIndex,
            ["constant"]    = $"0x{constant:X8}",
            ["entryCount"]  = entryCount,
            ["entries"]     = entries,
        };

        return root.ToJsonString(s_json);
    }

    private static string HashLabel(uint hash) =>
        s_boneHints.TryGetValue(hash, out string? hint)
            ? $"0x{hash:X8}|{hint}"
            : $"0x{hash:X8}";

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
