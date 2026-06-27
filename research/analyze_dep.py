"""
analyze_dep.py -- Extract dep.bin and G1M IDRK header; examine raw bytes
and try multiple parse interpretations to find correct G1T references.

Target: G1M in 0x05d43035.fdata, dep KTID = 0xf312228a
"""
import struct, zlib, io, os, sys

GAME_DIR  = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round"
FDATA_DIR = os.path.join(GAME_DIR, "fdata_package")
RDB_PATH  = os.path.join(GAME_DIR, "fdata_package", "root.rdb")
RDX_PATH  = os.path.join(GAME_DIR, "fdata_package", "root.rdx")

# G1M we're analyzing:  in 0x05d43035.fdata
G1M_CONTAINER  = "0x05d43035.fdata"
DEP_KTID       = 0xf312228a   # at IDRK+0x74 of the G1M
DEP_CONTAINER  = "0x05d43035.fdata"

# ── zlibext decompressor ──────────────────────────────────────────────────────
def decompress_zlibext(payload: bytes, uncomp_size: int) -> bytes:
    out = io.BytesIO()
    pos = 0
    while pos + 10 <= len(payload) and out.tell() < uncomp_size:
        chunk_size = struct.unpack_from("<H", payload, pos)[0]
        pos += 10
        if chunk_size == 0 or pos + chunk_size > len(payload):
            break
        chunk = payload[pos:pos+chunk_size]
        pos += chunk_size
        try:
            out.write(zlib.decompress(chunk))
        except zlib.error:
            try:
                out.write(zlib.decompress(chunk, -15))
            except zlib.error:
                pass
    return out.getvalue()

def extract_idrk(fdata_path: str, offset: int, size: int) -> bytes:
    with open(fdata_path, "rb") as f:
        f.seek(offset)
        raw = f.read(size)
    assert raw[:4] == b"IDRK", f"bad magic: {raw[:4]!r}"
    comp  = struct.unpack_from("<q", raw, 0x10)[0]
    uncomp= struct.unpack_from("<q", raw, 0x18)[0]
    overhead = size - comp
    payload  = raw[overhead:]
    return decompress_zlibext(payload, uncomp)

# ── RDB loader (minimal) ──────────────────────────────────────────────────────
def load_rdb():
    """Returns dict: fileKtid -> (typeKtid, fdataId, fdataOffset, sizeInCont, fileSize)"""
    rdb = open(RDB_PATH,"rb").read()
    rdx_raw = open(RDX_PATH,"rb").read() if os.path.exists(RDX_PATH) else b""
    # parse rdx: u16 idx, u16 pad, u32 fileId  (8B each)
    rdx = {}
    for i in range(0, len(rdx_raw)-7, 8):
        idx    = struct.unpack_from("<H", rdx_raw, i)[0]
        fileId = struct.unpack_from("<I", rdx_raw, i+4)[0]
        rdx[idx] = fileId

    assert rdb[:4] == b"_DRK"
    hdr_size  = struct.unpack_from("<I", rdb, 0x08)[0]
    file_count= struct.unpack_from("<I", rdb, 0x10)[0]

    assets = {}  # fileKtid -> dict
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
        flags      = struct.unpack_from("<I", rdb, pos+0x2C)[0]

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
        assets[file_ktid] = dict(typeKtid=type_ktid, fdataId=fdata_id,
                                  fdataOff=fdata_off, sizeInCont=size_in_cont,
                                  fileSize=file_size, container=container)
        parsed += 1
        pos = int(((pos + entry_size) + 3) & ~3)

    print(f"RDB loaded: {len(assets)} assets")
    return assets, rdx

# ── G1T TypeKtids ─────────────────────────────────────────────────────────────
G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}
G1M_TYPE_KTID  = 0x563bdef1

# ── Main ──────────────────────────────────────────────────────────────────────
def main():
    print("Loading RDB...")
    assets, rdx = load_rdb()
    byKtid = assets  # alias

    # Build reverse lookup: typeKtid -> list of fileKtids
    byType = {}
    for fk, info in assets.items():
        tk = info['typeKtid']
        byType.setdefault(tk, []).append(fk)

    print(f"  G1T assets (0xafbec60c): {len(byType.get(0xafbec60c,[]))}")
    print(f"  G1T assets (0xAD57EBBA): {len(byType.get(0xAD57EBBA,[]))}")

    # ── Find the dep.bin in the RDB ───────────────────────────────────────────
    if DEP_KTID not in byKtid:
        print(f"\nERROR: dep KTID 0x{DEP_KTID:08x} not in RDB!")
        return
    dep_info = byKtid[DEP_KTID]
    print(f"\nDep file 0x{DEP_KTID:08x}:")
    print(f"  typeKtid=0x{dep_info['typeKtid']:08x} fdataOff=0x{dep_info['fdataOff']:x} sizeInCont={dep_info['sizeInCont']} container={dep_info['container']}")

    # Extract dep.bin raw decompressed bytes
    fdata_path = os.path.join(FDATA_DIR, dep_info['container'])
    dep_raw = extract_idrk(fdata_path, dep_info['fdataOff'], dep_info['sizeInCont'])
    print(f"  dep raw bytes: {len(dep_raw)}")

    # Full hex dump
    print("\n=== DEP.BIN HEX DUMP ===")
    for i in range(0, len(dep_raw), 16):
        row = dep_raw[i:i+16]
        hex_part = " ".join(f"{b:02x}" for b in row)
        asc_part = "".join(chr(b) if 32<=b<127 else "." for b in row)
        print(f"  {i:04x}: {hex_part:<48}  {asc_part}")

    # ── Try interpretation A: header(4B) + N×(u32 idx, u32 ktid, u32 pad) ────
    print("\n=== INTERPRETATION A: 4B header + N×12B (u32 idx, u32 ktid, u32 pad) ===")
    hdr_a = struct.unpack_from("<I", dep_raw, 0)[0]
    n_a = (len(dep_raw) - 4) // 12
    print(f"  header=0x{hdr_a:08x}  entries={n_a}")
    for i in range(n_a):
        off = 4 + i*12
        idx  = struct.unpack_from("<I", dep_raw, off)[0]
        ktid = struct.unpack_from("<I", dep_raw, off+4)[0]
        pad  = struct.unpack_from("<I", dep_raw, off+8)[0]
        match = byKtid.get(ktid)
        tkt = match['typeKtid'] if match else None
        ext = "G1T" if tkt in G1T_TYPE_KTIDS else (f"0x{tkt:08x}" if tkt else "??")
        print(f"  [{idx}] ktid=0x{ktid:08x} pad=0x{pad:08x}  → {ext}")

    # ── Try interpretation B: header(4B) + N×8B (u32 typeKtid, u32 fileKtid) ─
    print("\n=== INTERPRETATION B: 4B header + N×8B (u32 typeKtid, u32 fileKtid) ===")
    n_b = (len(dep_raw) - 4) // 8
    print(f"  entries={n_b}")
    for i in range(n_b):
        off = 4 + i*8
        tkt  = struct.unpack_from("<I", dep_raw, off)[0]
        fkt  = struct.unpack_from("<I", dep_raw, off+4)[0]
        match = byKtid.get(fkt)
        ext = ("G1T" if (match and match['typeKtid'] in G1T_TYPE_KTIDS)
               else (f"0x{match['typeKtid']:08x}" if match else "??"))
        print(f"  [{i}] typeKtid=0x{tkt:08x} fileKtid=0x{fkt:08x}  → {ext}")

    # ── Try interpretation C: all u32 values ──────────────────────────────────
    print("\n=== INTERPRETATION C: all u32 values → RDB lookup ===")
    all_u32 = [struct.unpack_from("<I", dep_raw, i)[0]
               for i in range(0, len(dep_raw)-3, 4)]
    for idx, v in enumerate(all_u32):
        match = byKtid.get(v)
        if match:
            ext = "G1T" if match['typeKtid'] in G1T_TYPE_KTIDS else f"0x{match['typeKtid']:08x}"
            print(f"  u32[{idx}] @0x{idx*4:x} = 0x{v:08x} → FOUND: {ext} in {match['container']}")

    # ── Search for G1T assets in same container as G1M ────────────────────────
    print(f"\n=== G1T assets in container {G1M_CONTAINER} ===")
    for fk, info in assets.items():
        if info['container'] == G1M_CONTAINER and info['typeKtid'] in G1T_TYPE_KTIDS:
            print(f"  0x{fk:08x} typeKtid=0x{info['typeKtid']:08x} fileSize={info['fileSize']} fdataOff=0x{info['fdataOff']:x}")

    # ── Search for G1M assets in same container ────────────────────────────────
    print(f"\n=== G1M assets in container {G1M_CONTAINER} ===")
    for fk, info in assets.items():
        if info['container'] == G1M_CONTAINER and info['typeKtid'] == G1M_TYPE_KTID:
            print(f"  G1M 0x{fk:08x} fileSize={info['fileSize']} fdataOff=0x{info['fdataOff']:x}")

    # ── All assets in container ────────────────────────────────────────────────
    print(f"\n=== ALL asset types in container {G1M_CONTAINER} ===")
    type_counts = {}
    for fk, info in assets.items():
        if info['container'] == G1M_CONTAINER:
            type_counts[info['typeKtid']] = type_counts.get(info['typeKtid'], 0) + 1
    for tk, cnt in sorted(type_counts.items(), key=lambda x:-x[1]):
        # find extension
        ext_name = "G1T" if tk in G1T_TYPE_KTIDS else ("G1M" if tk==G1M_TYPE_KTID else f"0x{tk:08x}")
        print(f"  typeKtid=0x{tk:08x} ({ext_name}): {cnt} assets")

    # ── Dep entry values as potential TypeKtids ────────────────────────────────
    print("\n=== Dep fktid values 0x0696ca22 etc — check as TypeKtids ===")
    dep_values = []
    n = (len(dep_raw) - 4) // 12
    for i in range(n):
        off = 4 + i*12
        dep_values.append(struct.unpack_from("<I", dep_raw, off+4)[0])

    for v in dep_values:
        assets_of_type = byType.get(v, [])
        print(f"  0x{v:08x} as TypeKtid → {len(assets_of_type)} assets")
        for fk in assets_of_type[:3]:
            print(f"    fileKtid=0x{fk:08x} container={byKtid[fk]['container']}")

    # ── G1M dep reference: check IDRK header of first G1M in container ────────
    print(f"\n=== IDRK header dump of dep file 0x{DEP_KTID:08x} ===")
    with open(fdata_path, "rb") as f:
        f.seek(dep_info['fdataOff'])
        raw_idrk = f.read(min(dep_info['sizeInCont'], 0x100))
    for i in range(0, min(len(raw_idrk), 0x80), 16):
        row = raw_idrk[i:i+16]
        print(f"  0x{i:02x}: " + " ".join(f"{b:02x}" for b in row))

    print("\nDone.")

if __name__ == "__main__":
    main()
