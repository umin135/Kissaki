using System.Text.Json;
using System.Text.Json.Serialization;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses .grp (parts group bind table) files.
/// Format: headerless array of 0x20-byte GRPEntry records.
/// </summary>
public static class GrpDecoder
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public record Entry(
        [property: JsonPropertyName("partsNameHash")] string PartsNameHash,
        [property: JsonPropertyName("partsId")]       uint   PartsId,
        [property: JsonPropertyName("countT")]        uint   CountT,
        [property: JsonPropertyName("countBigT")]     uint   CountBigT,
        [property: JsonPropertyName("countS")]        uint   CountS,
        [property: JsonPropertyName("setCountT")]     uint   SetCountT,
        [property: JsonPropertyName("setCountBigT")]  uint   SetCountBigT,
        [property: JsonPropertyName("setCountS")]     uint   SetCountS);

    public static Entry[] Parse(byte[] data)
    {
        int n = data.Length / 0x20;
        var result = new Entry[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 0x20;
            result[i] = new Entry(
                $"0x{ReadU32(data, o + 0x00):x8}",
                ReadU32(data, o + 0x04),
                ReadU32(data, o + 0x08),
                ReadU32(data, o + 0x0C),
                ReadU32(data, o + 0x10),
                ReadU32(data, o + 0x14),
                ReadU32(data, o + 0x18),
                ReadU32(data, o + 0x1C));
        }
        return result;
    }

    public static string ToJson(string assetName, uint fileKtid, Entry[] entries)
    {
        var doc = new
        {
            assetName,
            assetNameHash = $"0x{fileKtid:x8}",
            entryCount    = entries.Length,
            entries,
        };
        return JsonSerializer.Serialize(doc, s_jsonOpts);
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
