using System.Text.Json;
using System.Text.Json.Serialization;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses .ktid files: ordered list of (slot → object_id) pairs used to
/// map G1M material texture slots to G1T FileKtid references via KidsObjDb.
/// </summary>
public static class KtidDecoder
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented         = true,
        PropertyNamingPolicy  = JsonNamingPolicy.CamelCase,
    };

    public record Entry(
        [property: JsonPropertyName("slot")]     uint Slot,
        [property: JsonPropertyName("objectId")] string ObjectId);

    /// <summary>Parses raw .ktid bytes into slot→objectId pairs.</summary>
    public static Entry[] Parse(byte[] data)
    {
        var list = new List<Entry>(data.Length / 8);
        for (int i = 0; i + 7 < data.Length; i += 8)
        {
            uint slot  = ReadU32(data, i);
            uint objId = ReadU32(data, i + 4);
            list.Add(new Entry(slot, $"0x{objId:x8}"));
        }
        return [.. list];
    }

    /// <summary>
    /// Serialises parsed entries to indented JSON.
    /// <paramref name="assetName"/> = recovered name or hex hash when unavailable.
    /// </summary>
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
