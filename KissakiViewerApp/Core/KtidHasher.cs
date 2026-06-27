using System.IO;
using System.Text;

namespace KissakiViewer.Core;

/// <summary>
/// KatanaEngine KTID 해시 함수.
/// 알고리즘: fdata_dump RDB_NameHash.Hash() 기반 (polynomial, key=0x1F).
/// 입력 형식: R_&lt;EXT&gt;［name］  (fullwidth square brackets U+FF3B/U+FF3D)
/// </summary>
public static class KtidHasher
{
    private const byte   Key    = 0x1F;
    // U+FF3B ［ / U+FF3D ］  (fullwidth square brackets — KatanaEngine 구분자)
    private static readonly byte[] BracketOpen  = { 0xEF, 0xBC, 0xBB };
    private static readonly byte[] BracketClose = { 0xEF, 0xBC, 0xBD };

    /// <summary>
    /// 파일 경로("path/to/file.g1t")로부터 FileKtid를 계산한다.
    /// R_G1T［path/to/file］ 형식으로 변환 후 해시.
    /// </summary>
    public static uint ComputeForFile(string filePath)
    {
        string name    = Path.GetFileNameWithoutExtension(filePath);
        string extCode = "R_" + Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        byte[] bytes   = Encoding.UTF8.GetBytes(extCode)
                         .Concat(BracketOpen)
                         .Concat(Encoding.UTF8.GetBytes(name))
                         .Concat(BracketClose)
                         .ToArray();
        return HashBytes(bytes);
    }

    /// <summary>임의 문자열을 직접 해시한다.</summary>
    public static uint Compute(string s) => HashBytes(Encoding.UTF8.GetBytes(s));

    private static uint HashBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        unchecked
        {
            int iv  = bytes[0] * Key;
            int key = Key;
            for (int i = 1; i < bytes.Length; i++)
            {
                int state = key;
                key *= Key;
                iv  += Key * state * (sbyte)bytes[i];
            }
            return (uint)iv;
        }
    }
}
