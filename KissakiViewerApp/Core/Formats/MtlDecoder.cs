using System.Text.Json;
using System.Text.Json.Serialization;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses .mtl (material group bind table) files.
/// Layout: 0x10-byte header → variable-length name groups → cloth pairs → ponytail pairs.
/// </summary>
public static class MtlDecoder
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public record NameGroup(
        [property: JsonPropertyName("hash")]   string Hash,
        [property: JsonPropertyName("matIds")] uint[] MatIds);

    public record PhysicsBind(
        [property: JsonPropertyName("srcId")] uint SrcId,
        [property: JsonPropertyName("dstId")] uint DstId);

    public record MtlData(
        [property: JsonPropertyName("numMat")]     uint        NumMat,
        [property: JsonPropertyName("names")]      NameGroup[] Names,
        [property: JsonPropertyName("cloths")]     PhysicsBind[] Cloths,
        [property: JsonPropertyName("ponytails")]  PhysicsBind[] Ponytails);

    public static MtlData Parse(byte[] data)
    {
        if (data.Length < 0x10)
            return new MtlData(0, [], [], []);

        uint numNames     = ReadU32(data, 0x00);
        uint numMat       = ReadU32(data, 0x04);
        uint numCloths    = ReadU32(data, 0x08);
        uint numPonytails = ReadU32(data, 0x0C);

        int pos = 0x10;

        var names = new NameGroup[numNames];
        for (int i = 0; i < numNames; i++)
        {
            if (pos + 8 > data.Length) break;
            uint hash  = ReadU32(data, pos); pos += 4;
            uint count = ReadU32(data, pos); pos += 4;
            var matIds = new uint[count];
            for (int j = 0; j < count && pos + 4 <= data.Length; j++)
            {
                matIds[j] = ReadU32(data, pos); pos += 4;
            }
            names[i] = new NameGroup($"0x{hash:x8}", matIds);
        }

        var cloths = new PhysicsBind[numCloths];
        for (int i = 0; i < numCloths && pos + 8 <= data.Length; i++)
        {
            cloths[i] = new PhysicsBind(ReadU32(data, pos), ReadU32(data, pos + 4));
            pos += 8;
        }

        var ponytails = new PhysicsBind[numPonytails];
        for (int i = 0; i < numPonytails && pos + 8 <= data.Length; i++)
        {
            ponytails[i] = new PhysicsBind(ReadU32(data, pos), ReadU32(data, pos + 4));
            pos += 8;
        }

        return new MtlData(numMat, names, cloths, ponytails);
    }

    public static string ToJson(string assetName, uint fileKtid, MtlData mtl)
    {
        var doc = new
        {
            assetName,
            assetNameHash = $"0x{fileKtid:x8}",
            numMat        = mtl.NumMat,
            numNames      = (uint)mtl.Names.Length,
            numCloths     = (uint)mtl.Cloths.Length,
            numPonytails  = (uint)mtl.Ponytails.Length,
            names         = mtl.Names,
            cloths        = mtl.Cloths.Length > 0 ? (object)mtl.Cloths    : Array.Empty<PhysicsBind>(),
            ponytails     = mtl.Ponytails.Length > 0 ? (object)mtl.Ponytails : Array.Empty<PhysicsBind>(),
        };
        return JsonSerializer.Serialize(doc, s_jsonOpts);
    }

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
}
