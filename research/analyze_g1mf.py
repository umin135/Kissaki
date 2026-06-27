"""
analyze_g1mf.py -- Extract and dump G1MF chunk from a G1M file to find
G1T file references. Also look in section 0x10001 of G1MG.

Target: G1M in 0x05d43035.fdata with dep KTID = 0xf312228a
"""
import struct, zlib, io, os

FDATA_DIR = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round\fdata_package"
RDB_PATH  = os.path.join(FDATA_DIR, "root.rdb")
RDX_PATH  = os.path.join(FDATA_DIR, "root.rdx")

CONTAINER = "0x05d43035.fdata"
TARGET_DEP_KTID = 0xf312228a

G1T_TYPE_KTIDS = {0xafbec60c, 0xAD57EBBA}
G1M_TYPE_KTID  = 0x563bdef1

# ── zlibext ──────────────────────────────────────────────────────────────────
def decompress_zlibext(payload, uncomp_size):
    out = io.BytesIO()
    pos = 0
    while pos + 10 <= len(payload) and out.tell() < uncomp_size:
        chunk_size = struct.unpack_from("<H", payload, pos)[0]
        pos += 10
        if chunk_size == 0 or pos + chunk_size > len(payload): break
        chunk = payload[pos:pos+chunk_size]
        pos += chunk_size
        try: out.write(zlib.decompress(chunk))
        except:
            try: out.write(zlib.decompress(chunk, -15))
            except: pass
    return out.getvalue()

def extract_idrk(fdata_path, offset, size):
    with open(fdata_path, "rb") as f:
        f.seek(offset)
        raw = f.read(size)
    assert raw[:4] == b"IDRK", f"bad magic"
    comp  = struct.unpack_from("<q", raw, 0x10)[0]
    uncomp= struct.unpack_from("<q", raw, 0x18)[0]
    overhead = size - comp
    return decompress_zlibext(raw[overhead:], uncomp)

# ── RDB ──────────────────────────────────────────────────────────────────────
def load_rdb():
    rdb = open(RDB_PATH, "rb").read()
    rdx_raw = open(RDX_PATH, "rb").read() if os.path.exists(RDX_PATH) else b""
    rdx = {}
    for i in range(0, len(rdx_raw) - 7, 8):
        rdx[struct.unpack_from("<H", rdx_raw, i)[0]] = struct.unpack_from("<I", rdx_raw, i+4)[0]

    assert rdb[:4] == b"_DRK"
    hdr_size   = struct.unpack_from("<I", rdb, 0x08)[0]
    file_count = struct.unpack_from("<I", rdb, 0x10)[0]

    by_ktid = {}
    pos = hdr_size
    parsed = 0
    while parsed < file_count and pos + 0x30 <= len(rdb):
        if rdb[pos:pos+4] != b"IDRK": break
        entry_size = struct.unpack_from("<Q", rdb, pos + 0x08)[0]
        data_size  = struct.unpack_from("<Q", rdb, pos + 0x10)[0]
        file_size  = struct.unpack_from("<Q", rdb, pos + 0x18)[0]
        file_ktid  = struct.unpack_from("<I", rdb, pos + 0x24)[0]
        type_ktid  = struct.unpack_from("<I", rdb, pos + 0x28)[0]
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
        by_ktid[file_ktid] = dict(typeKtid=type_ktid, fdataOff=fdata_off,
                                   sizeInCont=size_in_cont, fileSize=file_size,
                                   container=container)
        parsed += 1
        pos = int(((pos + entry_size) + 3) & ~3)
    return by_ktid

def main():
    print("Loading RDB...")
    by_ktid = load_rdb()
    print(f"  {len(by_ktid)} assets")

    # G1T FileKtids in the same container for fast lookup
    g1t_ktids = {fk for fk, info in by_ktid.items()
                 if info['container'] == CONTAINER and info['typeKtid'] in G1T_TYPE_KTIDS}
    print(f"  G1T assets in {CONTAINER}: {len(g1t_ktids)}")

    fdata_path = os.path.join(FDATA_DIR, CONTAINER)

    # Find G1Ms with the target dep KTID
    g1m_assets = [(fk, info) for fk, info in by_ktid.items()
                  if info['container'] == CONTAINER and info['typeKtid'] == G1M_TYPE_KTID]
    print(f"\nScanning {len(g1m_assets)} G1Ms for dep KTID = 0x{TARGET_DEP_KTID:08x}...")

    target_g1ms = []
    for fk, info in g1m_assets:
        try:
            with open(fdata_path, "rb") as f:
                f.seek(info['fdataOff'])
                hdr = f.read(min(info['sizeInCont'], 0x80))
            if len(hdr) >= 0x78 and hdr[:4] == b"IDRK":
                dep = struct.unpack_from("<I", hdr, 0x74)[0]
                if dep == TARGET_DEP_KTID:
                    target_g1ms.append((fk, info))
        except:
            pass

    print(f"Found {len(target_g1ms)} G1Ms with dep=0x{TARGET_DEP_KTID:08x}")

    if not target_g1ms:
        print("No G1Ms found!")
        return

    # Analyze the first large G1M found
    # Sort by fileSize descending to get a meaningful model
    target_g1ms.sort(key=lambda x: -x[1]['fileSize'])
    g1m_fk, g1m_info = target_g1ms[0]
    print(f"\nAnalyzing G1M 0x{g1m_fk:08x} (fileSize={g1m_info['fileSize']:,})")

    g1m_data = extract_idrk(fdata_path, g1m_info['fdataOff'], g1m_info['sizeInCont'])
    print(f"Decompressed: {len(g1m_data):,}B  first8={g1m_data[:8].hex()}")

    # G1M magic in little-endian byte order: "_G1M" as LE u32=0x47314D5F → bytes 5F 4D 31 47 = b"_M1G"
    G1M_MAGIC = b"\x5F\x4D\x31\x47"  # LE bytes of 0x47314D5F
    G1MF_SIG  = b"\x46\x4D\x31\x47"  # LE bytes of 0x47314D46 ("G1MF")
    G1MG_SIG  = b"\x47\x4D\x31\x47"  # LE bytes of 0x47314D47 ("G1MG")

    # Parse G1M chunks
    if g1m_data[:4] != G1M_MAGIC:
        print(f"Not a G1M file! Magic: {g1m_data[:4].hex()}  (expected {G1M_MAGIC.hex()})")
        return

    header_size = struct.unpack_from("<I", g1m_data, 0x0C)[0]
    num_chunks  = struct.unpack_from("<I", g1m_data, 0x14)[0]
    print(f"G1M: headerSize=0x{header_size:x} numChunks={num_chunks}")

    pos = header_size
    chunks = []
    for c in range(num_chunks):
        if pos + 12 > len(g1m_data): break
        sig_bytes = g1m_data[pos:pos+4]
        ver  = struct.unpack_from("<I", g1m_data, pos+4)[0]
        size = struct.unpack_from("<I", g1m_data, pos+8)[0]
        chunks.append((sig_bytes, pos, size))
        pos += size

    for s,p,sz in chunks:
        print(f"  Chunk: {s.hex()} @0x{p:x} size={sz}")

    # ── Analyze G1MF chunk ──────────────────────────────────────────────────
    g1mf_chunk = next(((s, p, sz) for s, p, sz in chunks if s == G1MF_SIG), None)
    if g1mf_chunk:
        sig, p, sz = g1mf_chunk
        g1mf = g1m_data[p:p+sz]
        print(f"\n=== G1MF chunk ({sz}B) ===")

        # Hex dump first 256 bytes
        for i in range(0, min(256, sz), 16):
            row = g1mf[i:i+16]
            print(f"  {i:04x}: {' '.join(f'{b:02x}' for b in row)}")

        # Search G1MF for G1T KTIDs
        print(f"\nG1T KTIDs found in G1MF:")
        g1t_refs_in_g1mf = []
        for i in range(0, len(g1mf) - 3, 4):
            v = struct.unpack_from("<I", g1mf, i)[0]
            if v in g1t_ktids:
                print(f"  0x{i:04x}: 0x{v:08x} (G1T)")
                g1t_refs_in_g1mf.append((i, v))

        if not g1t_refs_in_g1mf:
            print("  (none found)")

        # Search G1MF for dep.bin KTID values
        DEP_KTIDS = {0x0696ca22, 0xa016e227, 0x32b852ba, 0x4660263b, 0x5a07f9bc,
                     0x6dafcd3d, 0x8157a0be, 0x94ff743f, 0xa8a747c0, 0xbc4f1b41}
        print(f"\nDep.bin KTIDs found in G1MF:")
        dep_found = False
        for i in range(0, len(g1mf) - 3, 4):
            v = struct.unpack_from("<I", g1mf, i)[0]
            if v in DEP_KTIDS:
                print(f"  0x{i:04x}: 0x{v:08x}")
                dep_found = True
        if not dep_found:
            print("  (none found)")

    # ── Search ENTIRE G1M for G1T KTIDs ────────────────────────────────────
    print(f"\n=== All G1T KTIDs in full G1M data ===")
    all_g1t_refs = []
    for i in range(0, len(g1m_data) - 3, 4):
        v = struct.unpack_from("<I", g1m_data, i)[0]
        if v in g1t_ktids:
            all_g1t_refs.append((i, v))

    print(f"  Found {len(all_g1t_refs)} matches:")
    for off, v in all_g1t_refs[:20]:
        print(f"  0x{off:06x}: 0x{v:08x}")

    # ── Section 0x10001 analysis ────────────────────────────────────────────
    g1mg_chunk = next(((s, p, sz) for s, p, sz in chunks if s == G1MG_SIG), None)
    if g1mg_chunk:
        sig, p, sz = g1mg_chunk
        g1mg = g1m_data[p:p+sz]
        print(f"\n=== G1MG section 0x10001 (first 512B) ===")
        # Parse G1MG sections
        hdr_size_g1mg = struct.unpack_from("<I", g1mg, 0x0C)[0]
        num_secs = struct.unpack_from("<I", g1mg, 0x2C)[0]
        if num_secs == 0:
            alt = struct.unpack_from("<I", g1mg, 0x30)[0]
            if 0 < alt < 64:
                num_secs = alt
                hdr_size_g1mg += 4
        sp = hdr_size_g1mg
        for s_i in range(num_secs):
            if sp + 8 > len(g1mg): break
            sec_id   = struct.unpack_from("<I", g1mg, sp)[0]
            sec_size = struct.unpack_from("<I", g1mg, sp + 4)[0]
            if sec_id == 0x10001:
                sec_data = g1mg[sp+8:sp+sec_size]
                print(f"  Section 0x10001: {len(sec_data)}B")
                for row_i in range(0, min(512, len(sec_data)), 16):
                    row = sec_data[row_i:row_i+16]
                    print(f"  {row_i:04x}: {' '.join(f'{b:02x}' for b in row)}")

                # Search for G1T KTIDs in section 0x10001
                print(f"  G1T KTIDs in sec 0x10001:")
                for i in range(0, len(sec_data) - 3, 4):
                    v = struct.unpack_from("<I", sec_data, i)[0]
                    if v in g1t_ktids:
                        print(f"    0x{i:04x}: 0x{v:08x}")
                break
            sp += sec_size

    print("\nDone.")

if __name__ == "__main__":
    main()
