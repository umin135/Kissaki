"""
parse_k300.py -- Parse K300 archive (.lnk) and look for dep.bin KTID values
in the embedded IDRK asset headers.

K300 structure (hypothesis from header analysis):
  0x00: "K300\0\0\0\0" magic
  0x08: u64 entry_count
  0x10: u64 file_size
  0x18: u64 header_size (= 0x1000?)
  0x20: u64 data_start  (= 0xA000?)
  0x28: entry_table[count] × 32B each:
           u64 data_offset
           u64 comp_size
           u64 uncomp_size
           u64 flags
  [data_start]: raw asset data, each entry begins with IDRK header

If each entry has an IDRK header with FileKtid at +0x24, we can match them
to dep.bin KTID values.
"""
import struct, os

LNK_PATH = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round\archive\archive_00.lnk"
LNK1_PATH= r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round\archive\archive_01.lnk"

DEP_KTIDS = {
    0x0696ca22, 0xa016e227, 0x32b852ba, 0x4660263b, 0x5a07f9bc,
    0x6dafcd3d, 0x8157a0be, 0x94ff743f, 0xa8a747c0, 0xbc4f1b41,
}
G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}

def parse_k300(path, label):
    print(f"\n=== {label} ===")
    with open(path, "rb") as f:
        data = f.read()

    N = len(data)
    print(f"Size: {N:,}B")

    magic = data[:8]
    print(f"Magic: {magic!r}")
    if magic[:4] != b"K300":
        print("Not a K300 file!")
        return

    entry_count = struct.unpack_from("<Q", data, 0x08)[0]
    file_size   = struct.unpack_from("<Q", data, 0x10)[0]
    field_18    = struct.unpack_from("<Q", data, 0x18)[0]
    field_20    = struct.unpack_from("<Q", data, 0x20)[0]
    print(f"count={entry_count} file_size=0x{file_size:x} field18=0x{field_18:x} field20=0x{field_20:x}")

    # Dump first few entries from entry table at 0x28
    print(f"\nEntry table (32B/entry, starting at 0x28):")
    for i in range(min(entry_count, 5)):
        off = 0x28 + i * 32
        if off + 32 > N: break
        data_off   = struct.unpack_from("<Q", data, off + 0)[0]
        comp_size  = struct.unpack_from("<Q", data, off + 8)[0]
        uncomp_size= struct.unpack_from("<Q", data, off + 16)[0]
        flags      = struct.unpack_from("<Q", data, off + 24)[0]
        print(f"  [{i}] dataOff=0x{data_off:x} compSz=0x{comp_size:x} uncompSz=0x{uncomp_size:x} flags=0x{flags:x}")

        if data_off + 16 <= N:
            hdr = data[data_off:data_off+32]
            print(f"       @ 0x{data_off:x}: {' '.join(f'{b:02x}' for b in hdr[:32])}")

    # Check what's at first entry's data offset
    first_data = struct.unpack_from("<Q", data, 0x28)[0]
    print(f"\nData at first entry offset 0x{first_data:x}:")
    if first_data + 64 <= N:
        hdr = data[first_data:first_data+64]
        for row in range(0, 64, 16):
            print(f"  {first_data + row:06x}: {' '.join(f'{b:02x}' for b in hdr[row:row+16])}")

    # ── Scan ALL entries for dep.bin KTID matches ──────────────────────────────
    print(f"\nScanning {entry_count} entries for dep.bin KTIDs...")
    entry_infos = []  # (data_off, comp_size, uncomp_size, flags)
    for i in range(entry_count):
        off = 0x28 + i * 32
        if off + 32 > N: break
        d_off  = struct.unpack_from("<Q", data, off + 0)[0]
        c_sz   = struct.unpack_from("<Q", data, off + 8)[0]
        u_sz   = struct.unpack_from("<Q", data, off + 16)[0]
        flags  = struct.unpack_from("<Q", data, off + 24)[0]
        entry_infos.append((d_off, c_sz, u_sz, flags))

    # Scan data at each entry offset for IDRK headers with matching KTIDs
    matches = []
    type_freq = {}  # typeKtid → count
    for i, (d_off, c_sz, u_sz, flags) in enumerate(entry_infos):
        if d_off + 32 > N: continue
        hdr = data[d_off:d_off+32]
        if hdr[:4] == b"IDRK":
            file_ktid = struct.unpack_from("<I", hdr, 0x24)[0]
            type_ktid = struct.unpack_from("<I", hdr, 0x28)[0]
            type_freq[type_ktid] = type_freq.get(type_ktid, 0) + 1
            if file_ktid in DEP_KTIDS:
                matches.append((i, d_off, file_ktid, type_ktid, c_sz))
                tk = "G1T" if type_ktid in G1T_TYPE_KTIDS else f"0x{type_ktid:08x}"
                print(f"  MATCH! entry[{i}] fileKtid=0x{file_ktid:08x} typeKtid={tk} dataOff=0x{d_off:x} sz={c_sz}")
        elif i < 5:
            print(f"  entry[{i}] @ 0x{d_off:x}: not IDRK: {hdr[:8].hex()}")

    print(f"\nTotal IDRK matches for dep KTIDs: {len(matches)}")

    print(f"\nType distribution in {label}:")
    for tk, cnt in sorted(type_freq.items(), key=lambda x: -x[1])[:10]:
        ext = "G1T" if tk in G1T_TYPE_KTIDS else f"0x{tk:08x}"
        print(f"  {ext}: {cnt}")

    # Also: search raw bytes for dep KTID patterns
    print(f"\nRaw byte search for dep KTIDs:")
    for k in sorted(DEP_KTIDS):
        pat = struct.pack("<I", k)
        pos = data.find(pat)
        if pos >= 0:
            # Find which entry this falls in
            ctx = data[max(0, pos-8):pos+12]
            print(f"  FOUND 0x{k:08x} at raw offset 0x{pos:x}: {ctx.hex()}")
        else:
            print(f"  NOT FOUND 0x{k:08x}")

if __name__ == "__main__":
    parse_k300(LNK_PATH, "archive_00.lnk")
    if os.path.exists(LNK1_PATH):
        parse_k300(LNK1_PATH, "archive_01.lnk")
