namespace KissakiViewer.Core.Formats;

/// <summary>
/// Maps type_info_ktid hashes to file extension strings.
/// Source: KatanaEngine rdb.cpp kKtidMap table + probe analysis.
/// </summary>
public static class KtidExtension
{
    private static readonly Dictionary<uint, string> s_map = new()
    {
        // ── Core asset types ──────────────────────────────────────────────────
        [0x563bdef1] = ".g1m",
        [0x6fa91671] = ".g1a",
        [0xafbec60c] = ".g1t",
        [0xAD57EBBA] = ".g1t",
        [0x8e39aa37] = ".ktid",
        [0xBE144B78] = ".ktid",
        [0x20a6a0bb] = ".kidsobjdb",
        [0x5153729b] = ".mtl",
        [0xb340861a] = ".mtl",
        [0x56efe45c] = ".grp",
        [0xbbf9b49d] = ".grp",
        [0x0d34474d] = ".srst",
        [0x27bc54b7] = ".rigbin",
        [0x1B4FF321] = ".rigbin",
        [0x54738c76] = ".g1co",
        [0xbbd39f2d] = ".srsa",
        [0xD7F47FB1] = ".efpl",
        [0xb0a14534] = ".sgcbin",
        [0x786dcd84] = ".g1n",
        [0x56D8DEDA] = ".sid",
        [0x5C3E543C] = ".swg",
        [0x7BCD279F] = ".g1s",
        [0x9CB3A4B6] = ".oidex",
        // ── Identified via probe (DOA6LR) ─────────────────────────────────────
        [0x17614af5] = ".m1gk",   // M1GK — medium-quality geometry (87 files)
        [0x79c724c2] = ".p1gk",   // P1GK — geometry variant          (87 files)
        [0xa8d88566] = ".c1gk",   // C1GK — geometry variant          (87 files)
        [0x4d0102ac] = ".me1g",   // ME1G — large mesh data           (40 files)
        [0x5599aa51] = ".lcsk",   // LCSK — culling/scene data        (189 files)
        [0xb097d41f] = ".xf1g",   // XF1G — shader/VFX data          (1121 files)
        [0xed410290] = ".gstk",   // GSTK — small config             (28 files)
        // ── RDBExplorer-derived (non-conflicting additions) ───────────────────
        [0xBEF563DD] = ".g1m",   // StreamingMeshletModelData
        [0x7461C7CA] = ".g1h",   // ShapeData
        [0xDB0AE0AA] = ".gii",   // G1IIFile
        [0x8D735C52] = ".oboro", // OBOROStaticResourceBinaryFile
        [0x1AB40AE8] = ".oid",   // OIDBindTableBinaryFile
        [0xDBCB74A9] = ".oid",   // OIDFile
        [0xE6A3C3BB] = ".oidex", // OIDBindTableBinaryFileEx
        [0x753AA042] = ".oidsq", // OIDSQTBindTableBinaryFile
        [0x4F16D0EF] = ".kts",   // KTSFile
        [0xA1BDB205] = ".g2n",   // G2NFile
        [0x96C74B4F] = ".g2n",   // G2NGlyphSetFile
        [0xA027E46B] = ".mov",   // VideoStreamset
        [0x5B2970FC] = ".ktf2",  // KTF2File
        [0x193D2E44] = ".grbf",  // RBFData
        [0x82945A44] = ".lsqtree", // LandscapeQuadtree
        [0x6DBD6EA6] = ".csv",   // CSVFile
        [0x1FDCAA40] = ".kidstask",   // TaskGraphFile
        [0xBF6B52C7] = ".name",       // NameDatabaseFile
        [0xB1630F51] = ".kidsrender", // RenderGraphFile
        [0xCBFD49B2] = ".mmdb",       // MotionMatchingDatabase
        [0x4638B72D] = ".rbg",        // River2BakedGeometry
        [0x60A5ABFF] = ".g1fr",       // G1FRAni
        [0x8725D306] = ".g1fpose",    // G1FPose
        // ── Identified via probe (DOA6LR — previously unknown) ────────────────
        [0xF20DE437] = ".effselect",  // eff_select table (249 files)
        [0x133D2C3B] = ".sid",         // SID — confirmed via name CSV (e.g. ARD_COS_001_0001.sid)
        [0x757347E0] = ".bpo",         // BPO — confirmed via name CSV (e.g. FE4_S0899LOS.bpo)
    };

    public static string Get(uint typeKtid) =>
        s_map.TryGetValue(typeKtid, out var ext) ? ext : ".bin";

    public static string GetCategory(uint typeKtid) => typeKtid switch
    {
        0xafbec60c or 0xAD57EBBA                        => "Textures (.g1t)",
        0x563bdef1                                      => "Models (.g1m)",
        0x6fa91671                                      => "Animations (.g1a)",
        0xbbd39f2d or 0x0d34474d                        => "Audio (.srsa)",
        0x20a6a0bb                                      => "Database (.kidsobjdb)",
        0xD7F47FB1                                      => "Effects (.efpl)",
        0x5153729b or 0xb340861a                        => "Materials (.mtl)",
        0x27bc54b7 or 0x1B4FF321                        => "Rigs (.rigbin)",
        0x8e39aa37 or 0xBE144B78                        => "KtidRefs (.ktid)",
        0x54738c76                                      => "Collision (.g1co)",
        0x56efe45c or 0xbbf9b49d                        => "Parts (.grp)",
        0x5C3E543C                                      => "Physics (.swg)",
        0xb097d41f                                      => "VFX (.xf1g)",
        0x17614af5 or 0x79c724c2 or 0xa8d88566          => "Mesh (.m1gk)",
        0xF20DE437                                      => "Effects (.effselect)",
        _                                               => "Misc",
    };
}
