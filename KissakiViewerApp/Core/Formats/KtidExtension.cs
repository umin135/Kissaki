namespace KissakiViewer.Core.Formats;

/// <summary>
/// Maps type_info_ktid hashes to file extension strings.
/// Source: KatanaEngine rdb.cpp kKtidMap table.
/// </summary>
public static class KtidExtension
{
    private static readonly Dictionary<uint, string> s_map = new()
    {
        [0x563bdef1] = ".g1m",
        [0x6fa91671] = ".g1a",
        [0xafbec60c] = ".g1t",
        [0xAD57EBBA] = ".g1t",
        [0x8e39aa37] = ".ktid",
        [0x20a6a0bb] = ".kidsobjdb",
        [0x5153729b] = ".mtl",
        [0x56efe45c] = ".grp",
        [0x0d34474d] = ".srst",
        [0x27bc54b7] = ".rigbin",
        [0x54738c76] = ".g1co",
        [0xbbd39f2d] = ".srsa",
        [0xD7F47FB1] = ".efpl",
        [0xb0a14534] = ".sgcbin",
        [0x786dcd84] = ".g1n",
    };

    public static string Get(uint typeKtid) =>
        s_map.TryGetValue(typeKtid, out var ext) ? ext : ".bin";
}
