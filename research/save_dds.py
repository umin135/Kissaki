"""
save_dds.py  --  Export format-0x4c pixel data as DDS to verify the format.

Saves the pixel data at PIX_START_1 as:
  - BC7  2048x2048  (try as albedo/second-diffuse)
  - BC5  2048x2048  (try as normal map)
Also exports a small patch at 0x55D49C (PIX_START_1 + BC5_4096x2_7mip chain)
to check if THAT is the real tex2 ep.
"""

import struct, os

G1T_PATH    = r"G:\_GitProjectNEW\KissakiViewer\research\extracted\f782f325.g1t"
OUT_DIR     = r"G:\_GitProjectNEW\KissakiViewer\research\extracted"
PIX_START_1 = 0x55559C
CHAIN_BC7   = 5_592_400   # BC7 2048x2048 10-mip
CHAIN_BC5_4096x2_7mip = 32_512

with open(G1T_PATH, "rb") as f:
    data = f.read()
N = len(data)
print(f"Loaded {N:,}B")

# ── DDS header builder ───────────────────────────────────────────────────────

DDSD_CAPS       = 0x1
DDSD_HEIGHT     = 0x2
DDSD_WIDTH      = 0x4
DDSD_PITCH      = 0x8
DDSD_PIXELFORMAT= 0x1000
DDSD_MIPMAPCOUNT= 0x20000
DDSD_LINEARSIZE = 0x80000

DDSCAPS_COMPLEX = 0x8
DDSCAPS_MIPMAP  = 0x400000
DDSCAPS_TEXTURE = 0x1000

DXGI_FORMAT_BC5_UNORM    = 83
DXGI_FORMAT_BC7_UNORM    = 98
DXGI_FORMAT_BC5_SNORM    = 84
D3D10_RESOURCE_DIMENSION_TEXTURE2D = 3

def dds_header_dx10(w, h, mip_count, dxgi_fmt):
    """Build DDS + DX10 extended header (148 bytes total)."""
    flags = (DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT |
             DDSD_MIPMAPCOUNT | DDSD_LINEARSIZE)
    # pitch = first-mip linear size for BCn
    blk_w = max((w + 3) // 4, 1)
    blk_h = max((h + 3) // 4, 1)
    bpb   = 16 if dxgi_fmt in (DXGI_FORMAT_BC7_UNORM,) else 16  # BC5 also 16B/block
    pitch = blk_w * blk_h * bpb

    hdr = bytearray()
    hdr += b'DDS '
    hdr += struct.pack('<I', 124)       # dwSize
    hdr += struct.pack('<I', flags)     # dwFlags
    hdr += struct.pack('<I', h)         # dwHeight
    hdr += struct.pack('<I', w)         # dwWidth
    hdr += struct.pack('<I', pitch)     # dwPitchOrLinearSize
    hdr += struct.pack('<I', 0)         # dwDepth
    hdr += struct.pack('<I', mip_count) # dwMipMapCount
    hdr += b'\x00' * 44                 # dwReserved1[11]
    # DDS_PIXELFORMAT (32 bytes)
    hdr += struct.pack('<I', 32)        # pfSize
    hdr += struct.pack('<I', 0x4)       # pfFlags: DDPF_FOURCC
    hdr += b'DX10'                      # pfFourCC
    hdr += b'\x00' * 20                 # pfRGBBitCount, masks
    # dwCaps
    hdr += struct.pack('<I', DDSCAPS_COMPLEX | DDSCAPS_MIPMAP | DDSCAPS_TEXTURE)
    hdr += struct.pack('<I', 0)         # dwCaps2
    hdr += struct.pack('<I', 0)         # dwCaps3
    hdr += struct.pack('<I', 0)         # dwCaps4
    hdr += struct.pack('<I', 0)         # dwReserved2
    # DDS_HEADER_DXT10 (20 bytes)
    hdr += struct.pack('<I', dxgi_fmt)  # dxgiFormat
    hdr += struct.pack('<I', D3D10_RESOURCE_DIMENSION_TEXTURE2D)
    hdr += struct.pack('<I', 0)         # miscFlag
    hdr += struct.pack('<I', 1)         # arraySize
    hdr += struct.pack('<I', 0)         # miscFlags2
    assert len(hdr) == 148
    return bytes(hdr)

def mip_chain_size(bpb, w, h, mips):
    total = 0
    for m in range(mips):
        mw, mh = max(w >> m, 1), max(h >> m, 1)
        total += max((mw+3)//4, 1) * max((mh+3)//4, 1) * bpb
    return total

def save_dds(filename, dxgi_fmt, w, h, mips, pix_offset, bpb):
    chain = mip_chain_size(bpb, w, h, mips)
    if pix_offset + chain > N:
        print(f"  SKIP {filename}: need {chain}B but only {N-pix_offset}B available")
        return
    hdr  = dds_header_dx10(w, h, mips, dxgi_fmt)
    path = os.path.join(OUT_DIR, filename)
    with open(path, 'wb') as f:
        f.write(hdr)
        f.write(data[pix_offset:pix_offset+chain])
    print(f"  Saved {filename}  ({chain:,}B pixel data)")

# ── Exports ──────────────────────────────────────────────────────────────────
print("\n=== Saving format-0x4c pixel data as DDS ===")
print(f"  pixStart_1 = 0x{PIX_START_1:x}")

# Interpret as BC7 2048x2048 10mips
save_dds("fmt4c_as_BC7_2048x2048.dds", DXGI_FORMAT_BC7_UNORM, 2048, 2048, 10, PIX_START_1, 16)

# Interpret as BC5_UNORM 2048x2048 10mips (normal map)
save_dds("fmt4c_as_BC5_2048x2048.dds", DXGI_FORMAT_BC5_UNORM, 2048, 2048, 10, PIX_START_1, 16)

# Interpret as BC5_SNORM 2048x2048 10mips
save_dds("fmt4c_as_BC5snorm_2048x2048.dds", DXGI_FORMAT_BC5_SNORM, 2048, 2048, 10, PIX_START_1, 16)

# Also: try 1024x1024 (smaller textures common in DOA6)
save_dds("fmt4c_as_BC7_1024x1024.dds", DXGI_FORMAT_BC7_UNORM, 1024, 1024, 10, PIX_START_1, 16)
save_dds("fmt4c_as_BC5_1024x1024.dds", DXGI_FORMAT_BC5_UNORM, 1024, 1024, 10, PIX_START_1, 16)

# ── Probe position 0x55D49C (PIX_START_1 + 32512 = after 4096x2 BC5 7mip chain) ──
pos_after_small = PIX_START_1 + CHAIN_BC5_4096x2_7mip
print(f"\n=== 20 bytes at 0x{pos_after_small:x} (PIX_START_1 + 32512) ===")
b = data[pos_after_small:pos_after_small+20]
print("  " + " ".join(f"{b[i]:02x}" for i in range(20)))
fc = b[1] if len(b) > 1 else 0
b0 = b[0]; dm = b[2]
mc = (b0 >> 4) & 0xF or 1
ww = 1 << ((dm >> 4) & 0xF); hh = 1 << (dm & 0xF)
print(f"  b0=0x{b0:02x} fc=0x{fc:02x} dm=0x{dm:02x} b3=0x{b[3]:02x}  -> {ww}x{hh} mips={mc}")

# ── Check consecutive zeros at 0xaaaaec and scan forward for non-zero ────────
zero_start = PIX_START_1 + CHAIN_BC7
print(f"\n=== How far do zeros extend from 0x{zero_start:x}? ===")
pos = zero_start
zero_run = 0
while pos < N and data[pos] == 0:
    pos += 1
    zero_run += 1
print(f"  Zero run: {zero_run:,} bytes  (ends at 0x{pos:x}, byte=0x{data[pos]:02x})")

# Skip forward to find first significant non-zero region (>= 32 non-zero bytes)
print(f"\n=== First significant non-zero run after 0x{zero_start:x} ===")
pos = zero_start + zero_run
while pos < N - 32:
    if data[pos] != 0:
        # Check if at least 16 of next 32 bytes are non-zero
        nonzero = sum(1 for b in data[pos:pos+32] if b != 0)
        if nonzero >= 16:
            print(f"  0x{pos:x} (+0x{pos-zero_start:x} from zero-start): {' '.join(f'{data[pos+i]:02x}' for i in range(32))}")
            break
    pos += 1

print("\nDone.")
