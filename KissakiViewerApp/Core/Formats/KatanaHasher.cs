using System.IO;
using System.Text;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// KatanaEngine forward name→FileKtid hash. Given a full asset name (e.g. "KAS_COS_006.g1m")
/// it reproduces the RDB FileKtid the engine assigns to that name.
///
/// Faithful port of DeathChaos25/fdata_dump RDB_NameHash.Hash — same int/unchecked arithmetic:
///   input = "R_&lt;EXT&gt;" + ［(EF BC BB) + &lt;name-without-ext&gt; + ］(EF BC BD)   (UTF-8)
///   iv = input[0]*0x1F ;  key = 0x1F
///   for each following byte b: iv += 0x1F * key * (sbyte)b ;  key *= 0x1F
///   result = (uint)iv
///
/// Verified: Hash("KAS_COS_006.g1m") == 0x0957FBEC (independent of the DOK chain that maps it),
/// and 96.5% round-trip over the full DOA6/DOA6LR name lists. Note: engine DB/system files
/// (.kidssingletondb / .kidsobjdb / .name …) use a different convention and are NOT covered here.
/// </summary>
public static class KatanaHasher
{
    private const int Key = 0x1F;
    private static readonly byte[] Prefix = [0xEF, 0xBC, 0xBB]; // ［
    private static readonly byte[] Suffix = [0xEF, 0xBC, 0xBD]; // ］

    public static uint Hash(string nameWithExt)
    {
        string fileName = Path.GetFileNameWithoutExtension(nameWithExt);
        string ext      = "R_" + Path.GetExtension(nameWithExt).ToUpperInvariant().Replace(".", "");

        byte[] extBytes  = Encoding.UTF8.GetBytes(ext);
        byte[] nameBytes = Encoding.UTF8.GetBytes(fileName);

        int len = extBytes.Length + Prefix.Length + nameBytes.Length + Suffix.Length;
        byte[] data = new byte[len];
        int p = 0;
        Array.Copy(extBytes,  0, data, p, extBytes.Length);  p += extBytes.Length;
        Array.Copy(Prefix,    0, data, p, Prefix.Length);    p += Prefix.Length;
        Array.Copy(nameBytes, 0, data, p, nameBytes.Length); p += nameBytes.Length;
        Array.Copy(Suffix,    0, data, p, Suffix.Length);

        unchecked
        {
            int iv  = data[0] * Key;
            int key = Key;
            for (int i = 1; i < data.Length; i++)
            {
                int state = key;
                key *= Key;
                iv += Key * state * (sbyte)data[i];
            }
            return (uint)iv;
        }
    }
}
