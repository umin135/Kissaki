"""
analyze_g1t_idrk.py -- Extract and analyze the full IDRK overhead of
G1T 0xf782f325 in 0x05d43035.fdata.

The IDRK overhead is 4184 bytes (sizeInCont - compressedSize).
This might contain dep.bin KTID values or G1M back-references.
"""
import struct, os

FDATA_DIR = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round\fdata_package"
RDB_PATH  = os.path.join(FDATA_DIR, "root.rdb")
RDX_PATH  = os.path.join(FDATA_DIR, "root.rdx")

G1T_CONTAINER  = "0x05d43035.fdata"
G1T_FILE_KTID  = 0xf782f325
G1T_FDATA_OFF  = 0x71405f0
G1T_SIZE_IN_CONT = 13_137_510

DEP_KTIDS = {
    0x0696ca22, 0xa016e227, 0x32b852ba, 0x4660263b, 0x5a07f9bc,
    0x6dafcd3d, 0x8157a0be, 0x94ff743f, 0xa8a747c0, 0xbc4f1b41,
}
G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}
G1M_TYPE_KTID  = 0x563bdef1

def load_rdb_minimal():
    """Load just enough to check KTIDs."""
    rdb = open(RDB_PATH, "rb").read()
    rdx_raw = open(RDX_PATH, "rb").read() if os.path.exists(RDX_PATH) else b""
    rdx = {}
    for i in range(0, len(rdx_raw) - 7, 8):
        rdx[struct.unpack_from("<H", rdx_raw, i)[0]] = struct.unpack_from("<I", rdx_raw, i+4)[0]

    assert rdb[:4] == b"_DRK"
    hdr_size   = struct.unpack_from("<I", rdb, 0x08)[0]
    file_count = struct.unpack_from("<I", rdb, 0x10)[0]

    all_ktids = {}  # fileKtid → typeKtid
    pos = hdr_size
    parsed = 0
    while parsed < file_count and pos + 0x30 <= len(rdb):
        if rdb[pos:pos+4] != b"IDRK":
            break
        entry_size = struct.unpack_from("<Q", rdb, pos + 0x08)[0]
        file_ktid  = struct.unpack_from("<I", rdb, pos + 0x24)[0]
        type_ktid  = struct.unpack_from("<I", rdb, pos + 0x28)[0]
        all_ktids[file_ktid] = type_ktid
        parsed += 1
        pos = int(((pos + entry_size) + 3) & ~3)
    return all_ktids

def main():
    print("Loading RDB KTIDs...")
    all_ktids = load_rdb_minimal()
    print(f"  {len(all_ktids)} KTIDs loaded")

    fdata_path = os.path.join(FDATA_DIR, G1T_CONTAINER)
    comp_size = struct.unpack_from(
        "<q", open(fdata_path, "rb").read()[G1T_FDATA_OFF + 0x10:G1T_FDATA_OFF + 0x18])[0]
    overhead = G1T_SIZE_IN_CONT - comp_size
    print(f"\nG1T 0x{G1T_FILE_KTID:08x}:")
    print(f"  sizeInCont={G1T_SIZE_IN_CONT:,} compSize={comp_size:,} overhead={overhead:,}")

    with open(fdata_path, "rb") as f:
        f.seek(G1T_FDATA_OFF)
        idrk_raw = f.read(overhead)

    print(f"\nFull IDRK overhead ({overhead}B):")

    # Hex dump first 256 bytes
    print("First 256B:")
    for i in range(0, min(256, overhead), 16):
        row = idrk_raw[i:i+16]
        print(f"  {i:04x}: {' '.join(f'{b:02x}' for b in row)}")

    # Search for dep.bin KTIDs in the entire overhead
    print(f"\n=== Searching overhead for dep.bin KTIDs ===")
    dep_found = False
    for k in sorted(DEP_KTIDS):
        pat = struct.pack("<I", k)
        pos = 0
        while True:
            idx = idrk_raw.find(pat, pos)
            if idx < 0: break
            ctx = idrk_raw[max(0,idx-8):idx+12]
            print(f"  FOUND 0x{k:08x} @ overhead+0x{idx:x}: {ctx.hex()}")
            dep_found = True
            pos = idx + 1
    if not dep_found:
        print("  None found")

    # Scan all u32 values in the overhead for known RDB KTIDs
    print(f"\n=== Known RDB KTIDs in overhead ===")
    known_found = []
    for i in range(0, overhead - 3, 4):
        v = struct.unpack_from("<I", idrk_raw, i)[0]
        if v in all_ktids:
            tk = all_ktids[v]
            ext = "G1T" if tk in G1T_TYPE_KTIDS else ("G1M" if tk == G1M_TYPE_KTID else f"0x{tk:08x}")
            known_found.append((i, v, ext))

    print(f"  Found {len(known_found)} RDB KTID matches in overhead:")
    for (off, v, ext) in known_found[:50]:
        print(f"  0x{off:04x}: 0x{v:08x} → {ext}")

    # Try to parse the overhead as a structured list
    print(f"\n=== Structured parsing attempt: (typeKtid, count, [ktid×count]) ===")
    pos = 0x30  # skip fixed header
    prev_pos = pos
    while pos + 4 < overhead:
        v = struct.unpack_from("<I", idrk_raw, pos)[0]
        if v in all_ktids:
            tk = all_ktids[v]
            ext = "G1T" if tk in G1T_TYPE_KTIDS else ("G1M" if tk == G1M_TYPE_KTID else f"0x{tk:08x}")
            if pos + 8 < overhead:
                cnt = struct.unpack_from("<I", idrk_raw, pos+4)[0]
                print(f"  0x{pos:04x}: type=0x{v:08x}({ext}) count={cnt}")
                # Read cnt KTIDs
                for j in range(min(cnt, 20)):
                    if pos + 8 + j*4 + 4 > overhead: break
                    kv = struct.unpack_from("<I", idrk_raw, pos + 8 + j*4)[0]
                    kt = all_ktids.get(kv)
                    ke = "G1T" if (kt and kt in G1T_TYPE_KTIDS) else ("G1M" if kt==G1M_TYPE_KTID else ("??" if not kt else f"0x{kt:08x}"))
                    print(f"    [{j}] 0x{kv:08x} → {ke}")
                pos += 8 + cnt * 4
            else:
                pos += 4
        else:
            pos += 4

    # Last 256 bytes of overhead
    print(f"\nLast 128B of overhead:")
    for i in range(overhead-128, overhead, 16):
        row = idrk_raw[i:i+16]
        print(f"  {i:04x}: {' '.join(f'{b:02x}' for b in row)}")

    # Also check a SMALLER G1T in the same container
    print(f"\n=== Analyzing smaller G1T 0xeec77d0f ===")
    G1T2_OFF = 0x7dc7c60
    G1T2_SZ  = 144
    with open(fdata_path, "rb") as f:
        f.seek(G1T2_OFF + 0x10)
        cs2 = struct.unpack_from("<q", f.read(8))[0]
    oh2 = G1T2_SZ - cs2
    print(f"  overhead={oh2}")
    with open(fdata_path, "rb") as f:
        f.seek(G1T2_OFF)
        raw2 = f.read(oh2)
    for i in range(0, oh2, 16):
        row = raw2[i:i+16]
        print(f"  {i:04x}: {' '.join(f'{b:02x}' for b in row)}")

    print("\nDone.")

if __name__ == "__main__":
    main()
