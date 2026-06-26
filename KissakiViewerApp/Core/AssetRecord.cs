namespace KissakiViewer.Core;

public enum StorageType : byte { Unknown, External, Internal }
public enum CompressionType : byte { None = 0, Zlib = 1, Lz4 = 2, Encrypted = 3, ZlibExt = 4 }

public sealed class AssetRecord
{
    public uint FileKtid { get; init; }
    public uint TypeKtid { get; init; }
    public ulong FileSize { get; init; }
    public StorageType Storage { get; init; }
    public CompressionType Compression { get; init; }
    public ulong FdataOffset { get; init; }
    public uint SizeInContainer { get; init; }
    public ushort FdataId { get; init; }
    public string TypeExt { get; init; } = string.Empty;
}
