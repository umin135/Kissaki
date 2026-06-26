"""
analyze_g1t.py  –  G1T texture format analyzer for KatanaEngine (DOA6)

Usage:
    python analyze_g1t.py [fdata_path] [offset_hex] [size_in_container]
"""

import struct, zlib, io, sys, os

# ── defaults from the log (G1T 0xf782f325 in 0x05d43035.fdata) ───────────────
FDATA_PATH   = r"G:\SteamLibrary\steamapps\common\Dead or Alive 6 Last Round\fdata_package\0x05d43035.fdata"
FDATA_OFFSET = 0x71405f0
SIZE_IN_CONT = 13_137_510

# ── zlibext (matches ZlibExtHelper.cs) ───────────────────────────────────────
def decompress_zlibext(payload: bytes, uncomp_size: int) -> bytes:
    out = io.BytesIO()
    pos = 0
    while pos + 10 <= len(payload) and out.tell() < uncomp_size:
        chunk_size = struct.unpack_from("<H", payload, pos)[0]
        pos += 10
        if chunk_size == 0 or pos + chunk_size > len(payload):
            break
        chunk = payload[pos:pos + chunk_size]
        pos += chunk_size
        try:
            out.write(zlib.decompress(chunk))
        except zlib.error:
            try:
                out.write(zlib.decompress(chunk, -15))
            except zlib.error:
                print(f"  [WARN] chunk decompress failed")
    return out.getvalue()

def extract_from_fdata(path, offset, size_in_cont):
    with open(path, "rb") as f:
        f.seek(offset)
        raw = f.read(size_in_cont)
    if raw[:4] != b"IDRK":
        raise ValueError(f"Bad IDRK magic: {raw[:4]!r}")
    comp_size   = struct.unpack_from("<q", raw, 0x10)[0]
    uncomp_size = struct.unpack_from("<q", raw, 0x18)[0]
    overhead    = size_in_cont - comp_size
    print(f"IDRK: overhead={overhead}  comp={comp_size}  uncomp={uncomp_size}")
    return decompress_zlibext(raw[overhead:], uncomp_size), uncomp_size

# ── helpers ───────────────────────────────────────────────────────────────────
def hexdump(data: bytes, label: str, cols: int = 16):
    lines = []
    for i in range(0, len(data), cols):
        row = data[i:i+cols]
        hex_part = " ".join(f"{b:02x}" for b in row)
        lines.append(f"    {i:04x}: {hex_part}")
    print(f"  {label}:\n" + "\n".join(lines))

FMT_NAMES = {
    0x01: ("RGBA8",    False, 4),
    0x02: ("BGRA8",    False, 4),
    0x03: ("RGBA16",   False, 8),
    0x04: ("R8",       False, 1),
    0x3C: ("BC1_SRGB", True,  8),
    0x3D: ("BC2_SRGB", True, 16),
    0x3E: ("BC3_SRGB", True, 16),
    0x59: ("BC1",      True,  8),
    0x5A: ("BC2",      True, 16),
    0x5B: ("BC3",      True, 16),
    0x5C: ("BC4",      True,  8),
    0x5D: ("BC5",      True, 16),
    0x5E: ("BC6H",     True, 16),
    0x5F: ("BC7",      True, 16),
}

def mip_chain_size(is_bcn, bpb_or_bpp, w, h, mip_count):
    total = 0
    for m in range(mip_count):
        mw = max(w >> m, 1)
        mh = max(h >> m, 1)
        if is_bcn:
            total += ((mw + 3) // 4) * ((mh + 3) // 4) * bpb_or_bpp
        else:
            total += mw * mh * bpb_or_bpp
    return total

# ── G1T parser ────────────────────────────────────────────────────────────────
def parse_g1t(data: bytes, save_path: str = ""):
    if data[:4] != b"GT1G":
        raise ValueError(f"Bad G1T magic: {data[:4]!r}")

    version    = data[4:8].decode()
    header_sz  = struct.unpack_from("<I", data, 0x0C)[0]
    tex_count  = struct.unpack_from("<I", data, 0x14)[0]
    table_base = header_sz
    file_size  = len(data)

    print(f"\n{'='*60}")
    print(f"G1T  version={version}  texCount={tex_count}  tableBase=0x{table_base:x}  fileSize=0x{file_size:x} ({file_size:,}B)")
    sequential = (version == "1600")
    print(f"mode: {'SEQUENTIAL (1600)' if sequential else 'OFFSET-TABLE (0600)'}")

    if save_path:
        with open(save_path, "wb") as f:
            f.write(data)
        print(f"→ saved G1T to: {save_path}")

    # Dump header and offset table
    print(f"\nG1T header (0x00..0x{min(table_base+16*4, 0x60)-1:02x}):")
    hexdump(data[:min(table_base + 16*4, 0x80)], f"bytes 0x00..0x{min(table_base+16*4,0x80)-1:x}")

    next_ep = -1
    ep_list = []

    for i in range(tex_count):
        if sequential:
            ep = (table_base + struct.unpack_from("<I", data, table_base)[0]) if i == 0 else next_ep
        else:
            ep = table_base + struct.unpack_from("<I", data, table_base + i * 4)[0]

        if ep < 0 or ep + 12 > file_size:
            print(f"\n  [{i}] ep=0x{ep:x} → OUT OF RANGE, stopping")
            break

        byte0    = data[ep]
        fmt_code = data[ep + 1]
        dim      = data[ep + 2]
        byte3    = data[ep + 3]
        u32_4    = struct.unpack_from("<I", data, ep + 4)[0]
        ext_size = struct.unpack_from("<I", data, ep + 8)[0] if fmt_code != 0 else 0
        if ext_size > 0x400:
            ext_size = 0x0C
        pix_start = ep + 8 + ext_size

        mip_cnt = (byte0 >> 4) & 0xF or 1
        flags   = byte0 & 0xF
        w = 1 << ((dim >> 4) & 0xF)
        h = 1 << (dim & 0xF)

        fmt_info  = FMT_NAMES.get(fmt_code)
        fmt_name  = fmt_info[0] if fmt_info else f"UNKNOWN(0x{fmt_code:02x})"
        chain_sz  = 0
        if fmt_info:
            chain_sz = mip_chain_size(fmt_info[1], fmt_info[2], w, h, mip_cnt)
            next_ep  = pix_start + chain_sz

        ep_list.append((i, ep, fmt_code, w, h, mip_cnt, pix_start, chain_sz))

        print(f"\n{'─'*60}")
        print(f"  [tex {i}]  ep=0x{ep:x}  fmt=0x{fmt_code:02x}({fmt_name})")
        print(f"           byte0=0x{byte0:02x} (mips={mip_cnt} flags=0x{flags:x})  dim=0x{dim:02x} ({w}x{h})  byte3=0x{byte3:02x}")
        print(f"           u32@ep+4=0x{u32_4:08x}  extSize={ext_size}  pixStart=0x{pix_start:x}")
        if chain_sz:
            print(f"           chainSz={chain_sz:,} (0x{chain_sz:x})  nextEp=0x{next_ep:x}")

        # Full header bytes (ep..pix_start)
        hdr_len = pix_start - ep
        hexdump(data[ep:ep + hdr_len], f"header ({hdr_len}B)")

        # First 64 bytes of pixel data
        pix_sample = min(64, file_size - pix_start)
        hexdump(data[pix_start:pix_start + pix_sample], f"pixData[0..{pix_sample-1}]")

        if sequential and fmt_code != 0 and not fmt_info:
            remaining = file_size - pix_start
            n_left    = tex_count - i
            print(f"\n  *** UNKNOWN FORMAT 0x{fmt_code:02x} ***")
            print(f"  remaining from pixStart: {remaining:,} B")
            print(f"  remaining textures (incl this): {n_left}")
            if n_left > 0:
                print(f"  bytes/tex if evenly split: {remaining / n_left:.1f}")

            # Probe: try all known formats and see which gives a clean division
            print(f"\n  FORMAT PROBE -- {w}x{h} mips={mip_cnt}:")
            print(f"  {'format':<22} {'chainSz':>12} {'total/tex':>12} {'×9fits':>8} {'leftover':>12}")
            probes = [
                ("BC1  8b/blk",   True,  8),
                ("BC2 16b/blk",   True, 16),
                ("BC3 16b/blk",   True, 16),
                ("BC4  8b/blk",   True,  8),
                ("BC5 16b/blk",   True, 16),
                ("BC7 16b/blk",   True, 16),
                ("RGBA8  4B/px",  False, 4),
                ("RG8   2B/px",   False, 2),
                ("R8G8   2B/px",  False, 2),
                ("RGBA16  8B/px", False, 8),
                ("RG16F  4B/px",  False, 4),
                ("RGBA16F 8B/px", False, 8),
                ("R32G32  8B/px", False, 8),
            ]
            for pname, is_bcn, bpb in probes:
                sz    = mip_chain_size(is_bcn, bpb, w, h, mip_cnt)
                total = sz + 8 + ext_size   # header + pixels
                # does remaining / total give integer with 0 leftover?
                if total > 0:
                    n_fit    = remaining // total
                    leftover = remaining - n_fit * total
                    mark = " ◄ EXACT!" if leftover == 0 else ""
                    print(f"  {pname:<22} {sz:>12,} {total:>12,} {n_fit:>8} {leftover:>12,}{mark}")

            # Heuristic: try to find the ACTUAL size by scanning for the next valid G1T header pattern
            print(f"\n  SCANNING for next valid texture header...")
            scan_limit = min(remaining, 4_000_000)  # scan up to 4MB
            found_eps = []
            for off in range(0, scan_limit, 4):
                cand = pix_start + off
                if cand + 12 > file_size:
                    break
                b0 = data[cand]
                fc = data[cand + 1]
                dm = data[cand + 2]
                mc = (b0 >> 4) & 0xF
                ww = 1 << ((dm >> 4) & 0xF)
                hh = 1 << (dm & 0xF)
                # A "valid" header: known format, reasonable mip count, reasonable dimensions
                if fc in FMT_NAMES and 1 <= mc <= 14 and 1 <= ww <= 8192 and 1 <= hh <= 8192:
                    found_eps.append((off, cand, b0, fc, dm, mc, ww, hh))
                    if len(found_eps) >= 5:
                        break

            if found_eps:
                print(f"  Candidate next-ep offsets from pixStart:")
                for off, cand, b0, fc, dm, mc, ww, hh in found_eps:
                    fmt_n = FMT_NAMES[fc][0]
                    print(f"    +0x{off:x} ({off:,}B)  ep=0x{cand:x}  fmt=0x{fc:02x}({fmt_n})  {ww}x{hh}  mips={mc}")
            break

    # Final coverage
    total_accounted = 0
    print(f"\n{'='*60}")
    print(f"TEX SUMMARY:")
    for i, ep, fc, w, h, mc, pix_start, chain_sz in ep_list:
        fn = FMT_NAMES.get(fc, ("?",))[0]
        print(f"  [{i}] ep=0x{ep:x}  fmt=0x{fc:02x}({fn})  {w}x{h}  mips={mc}  pixStart=0x{pix_start:x}  chain={chain_sz:,}")
        if chain_sz:
            total_accounted += (pix_start - ep) + chain_sz
    print(f"\n  file_size:       {file_size:>12,}")
    print(f"  accounted for:   {total_accounted:>12,}")
    print(f"  unaccounted:     {file_size - total_accounted:>12,}")

# ── main ─────────────────────────────────────────────────────────────────────
def main():
    fdata_path   = sys.argv[1] if len(sys.argv) > 1 else FDATA_PATH
    fdata_offset = int(sys.argv[2], 16) if len(sys.argv) > 2 else FDATA_OFFSET
    size_in_cont = int(sys.argv[3]) if len(sys.argv) > 3 else SIZE_IN_CONT

    print(f"fdata: {fdata_path}")
    print(f"offset: 0x{fdata_offset:x}  sizeInCont: {size_in_cont}")

    g1t_data, uncomp = extract_from_fdata(fdata_path, fdata_offset, size_in_cont)
    print(f"decompressed: {len(g1t_data):,} bytes (expected {uncomp:,})")

    save_to = os.path.join(os.path.dirname(__file__), "extracted", "f782f325.g1t")
    os.makedirs(os.path.dirname(save_to), exist_ok=True)

    parse_g1t(g1t_data, save_path=save_to)

if __name__ == "__main__":
    main()
