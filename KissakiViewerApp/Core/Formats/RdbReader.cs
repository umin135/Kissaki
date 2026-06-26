using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace KissakiViewer.Core.Formats;

/// <summary>
/// Parses KatanaEngine _DRK asset database (.rdb + .rdx).
/// </summary>
public sealed class RdbReader
{
    // ── On-disk header (0x20 bytes) ──────────────────────────────────────────
    private const int RDB_HEADER_SIZE = 0x20;
    private const int RDX_ENTRY_SIZE  = 8;

    private string _rdbPath;
    private string _rdxPath;

    public string FolderPath { get; private set; } = string.Empty;
    public uint   SystemId   { get; private set; }
    public uint   DatabaseId { get; private set; }
    public uint   FileCount  { get; private set; }

    public IReadOnlyList<AssetRecord> Entries => _entries;
    private readonly List<AssetRecord> _entries = new();

    // fdata_id → "0x{hash:08x}.fdata" mapping
    private readonly List<(ushort index, uint fileId)> _rdx = new();

    public RdbReader(string rdbPath, string rdxPath)
    {
        _rdbPath = rdbPath;
        _rdxPath = rdxPath;
    }

    public bool Load()
    {
        if (!File.Exists(_rdbPath)) return false;

        byte[] rdb = File.ReadAllBytes(_rdbPath);
        if (rdb.Length < RDB_HEADER_SIZE) return false;

        // ── Header ──────────────────────────────────────────────────────────
        if (rdb[0] != '_' || rdb[1] != 'D' || rdb[2] != 'R' || rdb[3] != 'K')
            return false;

        uint headerSize = ReadU32(rdb, 0x08);
        SystemId   = ReadU32(rdb, 0x0C);
        FileCount  = ReadU32(rdb, 0x10);
        DatabaseId = ReadU32(rdb, 0x14);
        FolderPath = Encoding.ASCII.GetString(rdb, 0x18, 8).TrimEnd('\0');

        // ── .rdx (fdata index) ───────────────────────────────────────────────
        if (File.Exists(_rdxPath))
            LoadRdx(File.ReadAllBytes(_rdxPath));

        // ── Entries ──────────────────────────────────────────────────────────
        int pos = (int)headerSize;
        uint parsed = 0;
        while (parsed < FileCount && pos + 0x30 <= rdb.Length)
        {
            int entryStart = pos;

            // RdbEntryHeader (0x30 bytes)
            if (rdb[pos] != 'I' || rdb[pos+1] != 'D' || rdb[pos+2] != 'R' || rdb[pos+3] != 'K')
                break;

            ulong entrySize = ReadU64(rdb, pos + 0x08);
            ulong dataSize  = ReadU64(rdb, pos + 0x10);
            ulong fileSize  = ReadU64(rdb, pos + 0x18);
            uint  flags     = ReadU32(rdb, pos + 0x28);
            uint  fileKtid  = ReadU32(rdb, pos + 0x2C);
            uint  typeKtid  = ReadU32(rdb, pos + 0x24); // entry_type at +0x20, type_info_ktid at +0x24

            // Reread properly per struct layout:
            // +00 magic[4], +04 version[4], +08 entry_size u64, +10 data_size u64,
            // +18 file_size u64, +20 entry_type u32, +24 file_ktid u32,
            // +28 type_info_ktid u32, +2C flags u32
            fileKtid = ReadU32(rdb, pos + 0x24);
            typeKtid = ReadU32(rdb, pos + 0x28);
            flags    = ReadU32(rdb, pos + 0x2C);

            var storage     = GetStorage(flags);
            var compression = GetCompression(flags);
            string typeExt  = KtidExtension.Get(typeKtid);

            // Location metadata at end of entry, just before data_size bytes
            ulong locOffset = entrySize - dataSize;
            int locPos = entryStart + (int)locOffset;

            ulong fdataOff       = 0;
            uint  sizeInCont     = 0;
            ushort fdataId       = 0;

            if (dataSize == 0x0D && locPos + 0x0D <= rdb.Length)
            {
                // RdbLocation32
                fdataOff   = ReadU32(rdb, locPos + 0x02);
                sizeInCont = ReadU32(rdb, locPos + 0x06);
                fdataId    = ReadU16(rdb, locPos + 0x0A);
            }
            else if (dataSize == 0x11 && locPos + 0x11 <= rdb.Length)
            {
                // RdbLocation40 (>4 GB offset, high byte in [+02])
                byte offsetHigh = rdb[locPos + 0x02];
                uint offsetLow  = ReadU32(rdb, locPos + 0x06);
                fdataOff   = ((ulong)offsetHigh << 32) | offsetLow;
                sizeInCont = ReadU32(rdb, locPos + 0x0A);
                fdataId    = ReadU16(rdb, locPos + 0x0E);
            }

            _entries.Add(new AssetRecord
            {
                FileKtid        = fileKtid,
                TypeKtid        = typeKtid,
                FileSize        = fileSize,
                Storage         = storage,
                Compression     = compression,
                FdataOffset     = fdataOff,
                SizeInContainer = sizeInCont,
                FdataId         = fdataId,
                TypeExt         = typeExt,
            });

            parsed++;
            // Advance to next entry (4-byte aligned)
            long raw = entryStart + (long)entrySize;
            pos = (int)((raw + 3) & ~3L);
        }

        return _entries.Count > 0;
    }

    private void LoadRdx(byte[] rdx)
    {
        for (int i = 0; i + RDX_ENTRY_SIZE <= rdx.Length; i += RDX_ENTRY_SIZE)
        {
            ushort idx    = ReadU16(rdx, i);
            uint   fileId = ReadU32(rdx, i + 4);
            _rdx.Add((idx, fileId));
        }
    }

    public string ResolveFdata(ushort fdataId)
    {
        foreach (var (idx, fileId) in _rdx)
            if (idx == fdataId)
                return $"0x{fileId:x8}.fdata";
        return $"0x{fdataId:x4}.fdata";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static StorageType GetStorage(uint flags)
    {
        if ((flags & 0x00020000) != 0) return StorageType.Internal;
        if ((flags & 0x00010000) != 0) return StorageType.External;
        return StorageType.Unknown;
    }

    private static CompressionType GetCompression(uint flags) =>
        (CompressionType)((flags >> 20) & 0x3F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadU32(byte[] b, int o) =>
        (uint)(b[o] | b[o+1]<<8 | b[o+2]<<16 | b[o+3]<<24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(byte[] b, int o) =>
        (ulong)ReadU32(b, o) | ((ulong)ReadU32(b, o+4) << 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadU16(byte[] b, int o) =>
        (ushort)(b[o] | b[o+1]<<8);
}
