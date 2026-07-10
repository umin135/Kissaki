using System.Text.Json;
using System.Text.Json.Nodes;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses .kidsobjdb DOK files and serialises to JSON.
/// Handles IDOK (inline object) and RDOK (cross-file reference) records,
/// decoding all property values with correct type sizes from the Python tool analysis.
/// </summary>
public static class KidsObjDbDecoder
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private static readonly Dictionary<uint, string> s_typeNames = new()
    {
        [0x563BDEF1u] = "ModelData",
        [0xAFBEC60Cu] = "TexContext",
        [0xAD57EBBAu] = "TexContext",
        [0x8E39AA37u] = "KtidFile",
        [0x20A6A0BBu] = "ObjectDatabaseFile",
        [0x6FA91671u] = "G1AFile",
        [0x7BCD279Fu] = "G1SFile",
    };

    private static readonly Dictionary<uint, string> s_propNames = new()
    {
        [0xD2D2D5AFu] = "OIDResourceNameHash",
        [0xAD260326u] = "PropKtidLink",          // DM: DOK-local obj_id → KTID IDOK (value is NOT a FileKtid)
        [0x8AB68B3Fu] = "DmG1mFk",               // DM: G1M FileKtid
        [0x3BBFd9A5u] = "DmGrpFk",               // DM: GRP FileKtid
        [0x8DFD0584u] = "DmOidexFk",             // DM: OIDEX FileKtid
        [0x1B4FF321u] = "DmRigbinFk",            // DM: rigbin FileKtid
        [0xF92C5190u] = "DmCsvObjId",            // DM: TextureBindTableCSV local obj_id
        [0x07AAF542u] = "DmSidFk",               // DM: SID (physics sim) FileKtid
        [0x2B6EB4F6u] = "DmMudObjId",            // DM: MUD DOK local obj_id
        [0xBAF0DF79u] = "EnableModelGroupBuffer",
        [0xD3C00659u] = "ShadowCasterAlphaThreshold",
        [0xD69D6C64u] = "ZeroLevel",
        [0x0FDA9260u] = "CeilPower",
        [0x2841F996u] = "UC_AM_ChangeToLowRange",
        [0x8F04DB22u] = "UC_AM_ChangeToMidRange",
    };

    // index = type number; (displayName, bytes per element)
    private static readonly (string Name, int UnitSize)[] s_typeInfo =
    [
        ("SByte",   1),   // 0
        ("Byte",    1),   // 1
        ("Int16",   2),   // 2
        ("UInt16",  2),   // 3
        ("Int32",   4),   // 4
        ("UInt32",  4),   // 5
        ("",        0),   // 6 (unknown)
        ("",        0),   // 7 (unknown)
        ("Single",  4),   // 8
        ("",        0),   // 9 (unknown)
        ("Vector4", 16),  // 10
        ("",        0),   // 11 (unknown)
        ("Vector2", 8),   // 12
        ("Vector3", 12),  // 13
    ];

    /// <summary>
    /// Parses a DOK binary blob and returns the full JSON representation
    /// including all IDOK/RDOK records and their decoded property values.
    /// </summary>
    public static string ToJson(byte[] data)
    {
        if (data.Length < 0x1C ||
            data[0] != '_' || data[1] != 'D' || data[2] != 'O' || data[3] != 'K' ||
            data[4] != '0' || data[5] != '0' || data[6] != '0' || data[7] != '0')
            return """{ "error": "not a _DOK0000 file" }""";

        uint elementsCount = ReadU32(data, 0x10);
        uint rootName      = ReadU32(data, 0x14);
        uint declaredSize  = ReadU32(data, 0x18);

        var objects = new JsonArray();
        int pos = 0x1C;

        while (pos + 8 <= data.Length)
        {
            bool isIdok = data[pos]=='I' && data[pos+1]=='D' && data[pos+2]=='O' && data[pos+3]=='K';
            bool isRdok = !isIdok && data[pos]=='R' && data[pos+1]=='D' && data[pos+2]=='O' && data[pos+3]=='K';

            if (!isIdok && !isRdok)
            {
                // scan forward for next record header
                int found = -1;
                for (int i = pos + 1; i + 8 <= data.Length; i++)
                {
                    if ((data[i] == 'I' || data[i] == 'R') &&
                        data[i+1] == 'D' && data[i+2] == 'O' && data[i+3] == 'K')
                    { found = i; break; }
                }
                if (found < 0) break;
                pos = found;
                continue;
            }

            int recSize = (int)ReadU32(data, pos + 8);
            if (recSize < 8) recSize = 8;

            var node = isIdok ? ParseIdok(data, pos) : ParseRdok(data, pos);
            if (node != null) objects.Add(node);

            pos += recSize;
        }

        var root = new JsonObject
        {
            ["magic"]         = "_DOK0000",
            ["elementsCount"] = elementsCount,
            ["rootName"]      = FmtHex(rootName),
            ["declaredSize"]  = declaredSize,
            ["objectCount"]   = objects.Count,
            ["objects"]       = objects,
        };

        return root.ToJsonString(s_json);
    }

    private static JsonObject? ParseIdok(byte[] data, int pos)
    {
        if (pos + 0x18 > data.Length) return null;

        uint propName = ReadU32(data, pos + 0x0C);
        uint typeName = ReadU32(data, pos + 0x10);
        int  numProps = (int)ReadU32(data, pos + 0x14);
        if ((uint)numProps > 4096) return null;

        int metaStart = pos + 0x18;
        int valStart  = metaStart + numProps * 12;
        if (valStart > data.Length) return null;

        return new JsonObject
        {
            ["kind"]     = "IDOK",
            ["name"]     = FmtHex(propName),
            ["typeName"] = HashLabel(typeName, s_typeNames),
            ["props"]    = ReadProps(data, metaStart, numProps, valStart),
        };
    }

    private static JsonObject? ParseRdok(byte[] data, int pos)
    {
        if (pos + 0x1C > data.Length) return null;

        uint propName = ReadU32(data, pos + 0x0C);
        uint hash2    = ReadU32(data, pos + 0x10);
        uint hash3    = ReadU32(data, pos + 0x14);
        int  numProps = (int)ReadU32(data, pos + 0x18);
        if ((uint)numProps > 4096) return null;

        int metaStart = pos + 0x1C;
        int valStart  = metaStart + numProps * 12;
        if (valStart > data.Length) return null;

        return new JsonObject
        {
            ["kind"]  = "RDOK",
            ["name"]  = FmtHex(propName),
            ["hash2"] = FmtHex(hash2),
            ["hash3"] = FmtHex(hash3),
            ["props"] = ReadProps(data, metaStart, numProps, valStart),
        };
    }

    private static JsonArray ReadProps(byte[] data, int metaStart, int count, int valStart)
    {
        var arr    = new JsonArray();
        int valPos = valStart;

        for (int i = 0; i < count; i++)
        {
            int doff = metaStart + i * 12;
            if (doff + 12 > data.Length) break;

            uint typeNum   = ReadU32(data, doff + 0);
            int  arraySize = (int)ReadU32(data, doff + 4);
            uint propHash  = ReadU32(data, doff + 8);

            int    unitSize = typeNum < (uint)s_typeInfo.Length ? s_typeInfo[typeNum].UnitSize : 0;
            string typeName = typeNum < (uint)s_typeInfo.Length && s_typeInfo[typeNum].Name.Length > 0
                ? s_typeInfo[typeNum].Name
                : $"unknown({typeNum})";
            int totalB = arraySize * unitSize;

            string value;
            if (unitSize == 0 || arraySize == 0 || valPos + totalB > data.Length)
            {
                value  = "(empty)";
                totalB = 0;
            }
            else if (arraySize == 1)
            {
                value = FormatValue(data, valPos, typeNum);
            }
            else
            {
                var parts = new string[arraySize];
                for (int j = 0; j < arraySize; j++)
                    parts[j] = FormatValue(data, valPos + j * unitSize, typeNum);
                value = "[" + string.Join(", ", parts) + "]";
            }

            arr.Add(new JsonObject
            {
                ["name"]  = HashLabel(propHash, s_propNames),
                ["type"]  = typeName,
                ["value"] = value,
            });

            valPos += totalB;
        }

        return arr;
    }

    private static string FormatValue(byte[] data, int offset, uint typeNum)
    {
        return typeNum switch
        {
            0 when offset     <  data.Length => ((sbyte)data[offset]).ToString(),
            1 when offset     <  data.Length => data[offset].ToString(),
            2 when offset + 2 <= data.Length => ((short)(data[offset] | data[offset+1]<<8)).ToString(),
            3 when offset + 2 <= data.Length => ((ushort)(data[offset] | data[offset+1]<<8)).ToString(),
            4 when offset + 4 <= data.Length =>
                (data[offset] | data[offset+1]<<8 | data[offset+2]<<16 | data[offset+3]<<24).ToString(),
            5 when offset + 4 <= data.Length => FmtU32(ReadU32(data, offset)),
            8 when offset + 4 <= data.Length => BitConverter.ToSingle(data, offset).ToString("G"),
            10 when offset + 16 <= data.Length =>
                $"[{F32(data, offset)}, {F32(data, offset+4)}, {F32(data, offset+8)}, {F32(data, offset+12)}]",
            12 when offset + 8  <= data.Length =>
                $"[{F32(data, offset)}, {F32(data, offset+4)}]",
            13 when offset + 12 <= data.Length =>
                $"[{F32(data, offset)}, {F32(data, offset+4)}, {F32(data, offset+8)}]",
            _ => "?",
        };
    }

    private static string F32(byte[] data, int offset) =>
        BitConverter.ToSingle(data, offset).ToString("G");

    // UInt32: < 10 → decimal, >= 10 → hex (matches Python tool stringifyFloat behavior for uint)
    private static string FmtU32(uint v) => v < 10 ? v.ToString() : $"0x{v:X8}";

    private static string FmtHex(uint v) => $"0x{v:X8}";

    private static string HashLabel(uint hash, Dictionary<uint, string> lookup)
        => lookup.TryGetValue(hash, out string? name) ? $"0x{hash:X8}|{name}" : $"0x{hash:X8}";

    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);
}
