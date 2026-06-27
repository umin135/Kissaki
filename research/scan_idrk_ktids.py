"""
scan_idrk_ktids.py -- Check if dep.bin KTID values appear inside the IDRK
headers of G1T files in the same container.

Hypothesis: the dep.bin contains "IDRK-embedded KTIDs" (stored in the IDRK
overhead of each G1T file), NOT the outer RDB FileKtids.
"""
import struct, os, sys

GAME_DIR  = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round"
FDATA_DIR = os.path.join(GAME_DIR, "fdata_package")
RDB_PATH  = os.path.join(GAME_DIR, "fdata_package", "root.rdb")
RDX_PATH  = os.path.join(GAME_DIR, "fdata_package", "root.rdx")

CONTAINER = "0x05d43035.fdata"

# dep.bin KTIDs from the log
DEP_KTIDS = {
    0x0696ca22, 0xa016e227, 0x32b852ba, 0x4660263b, 0x5a07f9bc,
    0x6dafcd3d, 0x8157a0be, 0x94ff743f, 0xa8a747c0, 0xbc4f1b41,
}

G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}
G1M_TYPE_KTID  = 0x563bdef1

def load_rdb():
    rdb = open(RDB_PATH,"rb").read()
    rdx_raw = open(RDX_PATH,"rb").read() if os.path.exists(RDX_PATH) else b""
    rdx = {}
    for i in range(0, len(rdx_raw)-7, 8):
        idx    = struct.unpack_from("<H", rdx_raw, i)[0]
        fileId = struct.unpack_from("<I", rdx_raw, i+4)[0]
        rdx[idx] = fileId

    assert rdb[:4] == b"_DRK"
    hdr_size   = struct.unpack_from("<I", rdb, 0x08)[0]
    file_count = struct.unpack_from("<I", rdb, 0x10)[0]

    assets = []
    pos = hdr_size
    parsed = 0
    while parsed < file_count and pos + 0x30 <= len(rdb):
        if rdb[pos:pos+4] != b"IDRK":
            break
        entry_size = struct.unpack_from("<Q", rdb, pos+0x08)[0]
        data_size  = struct.unpack_from("<Q", rdb, pos+0x10)[0]
        file_size  = struct.unpack_from("<Q", rdb, pos+0x18)[0]
        file_ktid  = struct.unpack_from("<I", rdb, pos+0x24)[0]
        type_ktid  = struct.unpack_from("<I", rdb, pos+0x28)[0]

        loc_off = int(entry_size - data_size)
        loc_pos = pos + loc_off

        fdata_off = 0; size_in_cont = 0; fdata_id = 0
        if data_size == 0x0D and loc_pos+0x0D <= len(rdb):
            fdata_off    = struct.unpack_from("<I", rdb, loc_pos+0x02)[0]
            size_in_cont = struct.unpack_from("<I", rdb, loc_pos+0x06)[0]
            fdata_id     = struct.unpack_from("<H", rdb, loc_pos+0x0A)[0]
        elif data_size == 0x11 and loc_pos+0x11 <= len(rdb):
            hi           = rdb[loc_pos+0x02]
            fdata_off    = (hi << 32) | struct.unpack_from("<I", rdb, loc_pos+0x06)[0]
            size_in_cont = struct.unpack_from("<I", rdb, loc_pos+0x0A)[0]
            fdata_id     = struct.unpack_from("<H", rdb, loc_pos+0x0E)[0]

        container = f"0x{rdx.get(fdata_id, fdata_id):08x}.fdata"
        assets.append(dict(fileKtid=file_ktid, typeKtid=type_ktid,
                           fdataOff=fdata_off, sizeInCont=size_in_cont,
                           fileSize=file_size, container=container))
        parsed += 1
        pos = int(((pos + entry_size) + 3) & ~3)

    return assets

def read_idrk_header(fdata_path, offset, size, max_bytes=0x100):
    with open(fdata_path, "rb") as f:
        f.seek(offset)
        return f.read(min(size, max_bytes))

def main():
    print("Loading RDB...")
    assets = load_rdb()
    print(f"  {len(assets)} assets total")

    fdata_path = os.path.join(FDATA_DIR, CONTAINER)

    # Find G1T assets in the container
    g1t_assets = [a for a in assets
                  if a['container'] == CONTAINER and a['typeKtid'] in G1T_TYPE_KTIDS]
    g1m_assets = [a for a in assets
                  if a['container'] == CONTAINER and a['typeKtid'] == G1M_TYPE_KTID]

    print(f"\nContainer {CONTAINER}: {len(g1t_assets)} G1T, {len(g1m_assets)} G1M assets")

    # ── Scan ALL G1T IDRK headers for dep.bin KTID values ────────────────────
    print(f"\n=== Scanning G1T IDRK headers for dep KTIDs ===")
    found_matches = {}  # idrk_offset → (fileKtid, dep_ktid, pos_in_header)

    for i, a in enumerate(g1t_assets):
        hdr = read_idrk_header(fdata_path, a['fdataOff'], a['sizeInCont'])
        if len(hdr) < 8: continue
        if hdr[:4] != b"IDRK": continue

        # Scan all u32 values in the header
        for off in range(0, len(hdr)-3, 4):
            v = struct.unpack_from("<I", hdr, off)[0]
            if v in DEP_KTIDS:
                print(f"  MATCH! G1T fileKtid=0x{a['fileKtid']:08x} "
                      f"idrk_hdr_offset=0x{off:x} value=0x{v:08x}")
                found_matches[a['fileKtid']] = (v, off)

        if (i % 500) == 0:
            print(f"  [{i}/{len(g1t_assets)}] scanned...")

    print(f"\nTotal G1T matches: {len(found_matches)}")

    # ── Also scan G1M IDRK headers ─────────────────────────────────────────
    print(f"\n=== First few G1M IDRK header dumps ===")
    for a in g1m_assets[:3]:
        hdr = read_idrk_header(fdata_path, a['fdataOff'], a['sizeInCont'], 0x90)
        if len(hdr) < 8 or hdr[:4] != b"IDRK": continue
        print(f"\nG1M 0x{a['fileKtid']:08x} fdataOff=0x{a['fdataOff']:x}")
        for off in range(0, min(len(hdr), 0x90), 16):
            row = hdr[off:off+16]
            print(f"  0x{off:02x}: " + " ".join(f"{b:02x}" for b in row))

        # Extract u32s at key offsets
        print(f"  Key u32s:")
        for o in [0x00, 0x08, 0x10, 0x18, 0x20, 0x24, 0x28, 0x2c,
                  0x30, 0x38, 0x40, 0x50, 0x60, 0x70, 0x74, 0x78, 0x80]:
            if o + 4 <= len(hdr):
                v = struct.unpack_from("<I", hdr, o)[0]
                flag = " *** DEP KTID ***" if v in DEP_KTIDS else ""
                print(f"    +0x{o:02x}: 0x{v:08x}{flag}")

    # ── Check IDRK self-KTID for G1T files ────────────────────────────────────
    print(f"\n=== G1T IDRK header u32 dumps (first 5) ===")
    for a in sorted(g1t_assets, key=lambda x: x['fdataOff'])[:5]:
        hdr = read_idrk_header(fdata_path, a['fdataOff'], a['sizeInCont'], 0x80)
        if len(hdr) < 8 or hdr[:4] != b"IDRK": continue
        print(f"\nG1T 0x{a['fileKtid']:08x} fdataOff=0x{a['fdataOff']:x} sizeInCont={a['sizeInCont']}")
        for off in range(0, min(len(hdr), 0x80), 16):
            row = hdr[off:off+16]
            print(f"  0x{off:02x}: " + " ".join(f"{b:02x}" for b in row))

    # ── Find G1M with dep KTID = 0xf312228a ─────────────────────────────────
    print(f"\n=== G1M files in {CONTAINER} with dep KTID scan ===")
    G1M_DEP_KTID = 0xf312228a
    target_g1ms = []
    for a in g1m_assets:
        hdr = read_idrk_header(fdata_path, a['fdataOff'], a['sizeInCont'], 0x80)
        if len(hdr) < 0x78 or hdr[:4] != b"IDRK": continue
        dep_v = struct.unpack_from("<I", hdr, 0x74)[0]
        if dep_v == G1M_DEP_KTID:
            target_g1ms.append(a)
            print(f"  G1M 0x{a['fileKtid']:08x} fileSize={a['fileSize']} dep=0x{dep_v:08x}")

    print(f"\nFound {len(target_g1ms)} G1M(s) with dep=0x{G1M_DEP_KTID:08x}")

    print("\nDone.")

if __name__ == "__main__":
    main()
