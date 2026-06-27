"""
check_system_rdb.py -- Load system.rdb and check if dep.bin KTID values
exist there. Also compare with root.rdb to understand both databases.
"""
import struct, os

GAME_DIR  = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round"
FDATA_DIR = os.path.join(GAME_DIR, "fdata_package")

DEP_KTIDS = {
    0x0696ca22, 0xa016e227, 0x32b852ba, 0x4660263b, 0x5a07f9bc,
    0x6dafcd3d, 0x8157a0be, 0x94ff743f, 0xa8a747c0, 0xbc4f1b41,
}
G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}

def load_rdb(rdb_path, rdx_path, label):
    rdb = open(rdb_path, "rb").read()
    rdx_raw = open(rdx_path, "rb").read() if os.path.exists(rdx_path) else b""

    rdx = {}
    for i in range(0, len(rdx_raw) - 7, 8):
        idx    = struct.unpack_from("<H", rdx_raw, i)[0]
        fileId = struct.unpack_from("<I", rdx_raw, i + 4)[0]
        rdx[idx] = fileId

    assert rdb[:4] == b"_DRK", f"bad magic in {label}"
    hdr_size   = struct.unpack_from("<I", rdb, 0x08)[0]
    file_count = struct.unpack_from("<I", rdb, 0x10)[0]
    db_id      = struct.unpack_from("<I", rdb, 0x14)[0]
    folder     = rdb[0x18:0x20].rstrip(b"\x00").decode("ascii", errors="replace")

    print(f"\n{label}: {file_count} files, dbId=0x{db_id:08x}, folder={folder!r}")

    assets = {}
    pos = hdr_size
    parsed = 0
    while parsed < file_count and pos + 0x30 <= len(rdb):
        if rdb[pos:pos+4] != b"IDRK":
            break
        entry_size = struct.unpack_from("<Q", rdb, pos + 0x08)[0]
        data_size  = struct.unpack_from("<Q", rdb, pos + 0x10)[0]
        file_ktid  = struct.unpack_from("<I", rdb, pos + 0x24)[0]
        type_ktid  = struct.unpack_from("<I", rdb, pos + 0x28)[0]
        file_size  = struct.unpack_from("<Q", rdb, pos + 0x18)[0]

        loc_off = int(entry_size - data_size)
        loc_pos = pos + loc_off
        fdata_off = 0; size_in_cont = 0; fdata_id = 0

        if data_size == 0x0D and loc_pos + 0x0D <= len(rdb):
            fdata_off    = struct.unpack_from("<I", rdb, loc_pos + 0x02)[0]
            size_in_cont = struct.unpack_from("<I", rdb, loc_pos + 0x06)[0]
            fdata_id     = struct.unpack_from("<H", rdb, loc_pos + 0x0A)[0]
        elif data_size == 0x11 and loc_pos + 0x11 <= len(rdb):
            hi           = rdb[loc_pos + 0x02]
            fdata_off    = (hi << 32) | struct.unpack_from("<I", rdb, loc_pos + 0x06)[0]
            size_in_cont = struct.unpack_from("<I", rdb, loc_pos + 0x0A)[0]
            fdata_id     = struct.unpack_from("<H", rdb, loc_pos + 0x0E)[0]

        container = f"0x{rdx.get(fdata_id, fdata_id):08x}.fdata"
        assets[file_ktid] = dict(typeKtid=type_ktid, fdataId=fdata_id,
                                  fdataOff=fdata_off, sizeInCont=size_in_cont,
                                  fileSize=file_size, container=container)
        parsed += 1
        pos = int(((pos + entry_size) + 3) & ~3)

    print(f"  Loaded {len(assets)} assets")
    return assets, rdx

def main():
    root_assets, root_rdx = load_rdb(
        os.path.join(FDATA_DIR, "root.rdb"),
        os.path.join(FDATA_DIR, "root.rdx"),
        "root.rdb"
    )
    sys_assets, sys_rdx = load_rdb(
        os.path.join(FDATA_DIR, "system.rdb"),
        os.path.join(FDATA_DIR, "system.rdx"),
        "system.rdb"
    )

    print("\n=== Dep KTID lookup in system.rdb ===")
    found_in_sys = False
    for ktid in sorted(DEP_KTIDS):
        if ktid in sys_assets:
            info = sys_assets[ktid]
            ext = "G1T" if info['typeKtid'] in G1T_TYPE_KTIDS else f"0x{info['typeKtid']:08x}"
            print(f"  FOUND 0x{ktid:08x} → {ext} in {info['container']} fileSize={info['fileSize']}")
            found_in_sys = True
        else:
            print(f"  MISSING 0x{ktid:08x} in system.rdb")

    print("\n=== Dep KTID lookup in root.rdb ===")
    for ktid in sorted(DEP_KTIDS):
        if ktid in root_assets:
            info = root_assets[ktid]
            ext = "G1T" if info['typeKtid'] in G1T_TYPE_KTIDS else f"0x{info['typeKtid']:08x}"
            print(f"  FOUND 0x{ktid:08x} → {ext} in {info['container']}")
        else:
            print(f"  MISSING 0x{ktid:08x} in root.rdb")

    # Type distribution in system.rdb
    print("\n=== system.rdb type distribution (top 15) ===")
    type_counts = {}
    for fk, info in sys_assets.items():
        tk = info['typeKtid']
        type_counts[tk] = type_counts.get(tk, 0) + 1
    for tk, cnt in sorted(type_counts.items(), key=lambda x: -x[1])[:15]:
        ext = "G1T" if tk in G1T_TYPE_KTIDS else f"0x{tk:08x}"
        print(f"  0x{tk:08x} ({ext}): {cnt}")

    # G1T assets in system.rdb
    print("\n=== G1T assets in system.rdb (first 10) ===")
    g1t_sys = [(fk, info) for fk, info in sys_assets.items()
               if info['typeKtid'] in G1T_TYPE_KTIDS]
    for fk, info in g1t_sys[:10]:
        print(f"  0x{fk:08x} typeKtid=0x{info['typeKtid']:08x} container={info['container']} fileSize={info['fileSize']}")

    # Check if the G1M's dep KTID 0xf312228a is in system.rdb
    print("\n=== Looking up dep.bin KTID 0xf312228a ===")
    for ktid in [0xf312228a]:
        if ktid in sys_assets:
            print(f"  system.rdb: 0x{ktid:08x} → typeKtid=0x{sys_assets[ktid]['typeKtid']:08x} container={sys_assets[ktid]['container']}")
        else:
            print(f"  system.rdb: 0x{ktid:08x} NOT FOUND")
        if ktid in root_assets:
            print(f"  root.rdb:   0x{ktid:08x} → typeKtid=0x{root_assets[ktid]['typeKtid']:08x} container={root_assets[ktid]['container']}")

    # Unique containers in system.rdb
    print("\n=== Unique containers in system.rdb ===")
    containers = set(info['container'] for info in sys_assets.values())
    for c in sorted(containers):
        print(f"  {c}")

    print("\nDone.")

if __name__ == "__main__":
    main()
