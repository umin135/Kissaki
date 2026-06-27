using System.IO;
using System.Text;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// KatanaEngine NameDatabaseFile (.name, TypeKtid=0xBF6B52C7) IRNK 파서.
/// 포맷 출처: fdata_dump (DeathChaos25) — Program.cs ProcessDotNameFiles()
/// </summary>
public static class NameDbParser
{
    // U+FF3B ［ / U+FF3D ］ (KatanaEngine 구분자, fullwidth square brackets)
    private static readonly string BracketOpen  = Encoding.UTF8.GetString(new byte[] { 0xEF, 0xBC, 0xBB });
    private static readonly string BracketClose = Encoding.UTF8.GetString(new byte[] { 0xEF, 0xBC, 0xBD });

    public readonly record struct NameEntry(
        string RawString,   // 브라켓 포함 원본 (예: R_G1T［path/name］)
        string InnerName,   // 브라켓 사이 내용  (예: path/name)
        string TypeInfo     // 두 번째 문자열   (예: TypeInfo::Object::Render::Texture::Static)
    );

    /// <summary>
    /// 디코딩된 .name 파일 바이트로부터 IRNK 엔트리 목록을 반환한다.
    /// data는 IDRK 컨테이너를 zlibext 해제한 결과 바이트다.
    /// </summary>
    public static List<NameEntry> Parse(byte[] data)
    {
        var results = new List<NameEntry>();
        if (data.Length < 0x18) return results;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);

        // 0x00–0x17: 24-byte 파일 헤더 — 스킵
        br.BaseStream.Position = 0x18;

        while (br.BaseStream.Position + 16 < data.Length)
        {
            long entryStart = br.BaseStream.Position;

            ulong magic      = br.ReadUInt64();   // +0x00 (8 bytes)
            uint  entrySize  = br.ReadUInt32();   // +0x08
            uint  field04    = br.ReadUInt32();   // +0x0C
            uint  ptrCount   = br.ReadUInt32();   // +0x10

            if (entrySize == 0 || ptrCount < 2) break;
            if (entryStart + entrySize > data.Length) break;

            var ptrs = new uint[ptrCount];
            for (int i = 0; i < ptrCount; i++)
                ptrs[i] = br.ReadUInt32();

            // 문자열 0: 원본 경로 (R_G1T［name］ 형식)
            br.BaseStream.Position = entryStart + ptrs[0];
            string rawString = ReadNullTermString(br);
            string innerName = StripBrackets(rawString);

            // 문자열 1: TypeInfo 계층 문자열
            br.BaseStream.Position = entryStart + ptrs[1];
            string typeInfo = ReadNullTermString(br);

            if (!string.IsNullOrEmpty(innerName))
                results.Add(new NameEntry(rawString, innerName, typeInfo));

            // 다음 엔트리 (4-byte 정렬)
            long next = entryStart + entrySize;
            next = (next + 3) & ~3L;
            br.BaseStream.Position = next;
        }

        return results;
    }

    /// <summary>
    /// TypeInfo 문자열로부터 관련 파일 확장자 목록을 반환한다.
    /// fdata_dump GetNameHashesFromTypeInfo() 기반.
    /// </summary>
    public static IReadOnlyList<string> ExtensionsForTypeInfo(string typeInfo) =>
        typeInfo switch
        {
            "TypeInfo::Object::3D::Displayset::Model"           => [".g1m", ".ktid", ".mtl", ".grp", ".oid", ".oidex", ".rigbin"],
            "TypeInfo::Object::DopeSheet::Sound"                => [".srsa", ".srst"],
            "TypeInfo::Object::Animation::Data::Model::G1A"     => [".g1a"],
            "TypeInfo::Object::Render::Texture::Static"         => [".g1t"],
            "TypeInfo::Object::Render::Texture::Dynamic"        => [".g1t"],
            _ => []
        };

    // ── 내부 헬퍼 ────────────────────────────────────────────────────────────

    private static string ReadNullTermString(BinaryReader br)
    {
        using var buf = new MemoryStream();
        byte b;
        while (br.BaseStream.Position < br.BaseStream.Length && (b = br.ReadByte()) != 0)
            buf.WriteByte(b);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    private static string StripBrackets(string s)
    {
        int open  = s.IndexOf(BracketOpen,  StringComparison.Ordinal);
        int close = s.IndexOf(BracketClose, StringComparison.Ordinal);
        if (open < 0 || close < 0 || close <= open) return s;
        return s.Substring(open + BracketOpen.Length, close - open - BracketOpen.Length);
    }
}
