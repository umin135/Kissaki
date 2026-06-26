"""probe_g1t.py — targeted analysis of format 0x4c in f782f325.g1t"""
import struct, os

G1T_PATH = r"G:\_GitProjectNEW\KissakiViewer\research\extracted\f782f325.g1t"
EP_1       = 0x555588
PIX_START_1 = 0x55559C  # ep_1 + 20

FMTS = {
    0x01: ("RGBA8",  False, 4), 0x02: ("BGRA8", False, 4),
    0x03: ("RGBA16", False, 8), 0x04: ("R8",    False, 1),
    0x3C: ("BC1s",   True,  8), 0x3D: ("BC2s",  True, 16),
    0x3E: ("BC3s",   True, 16), 0x59: ("BC1",   True,  8),
    0x5A: ("BC2",    True, 16), 0x5B: ("BC3",   True, 16),
    0x5C: ("BC4",    True,  8), 0x5D: ("BC5",   True, 16),
    0x5E: ("BC6H",   True, 16), 0x5F: ("BC7",   True, 16),
}

def chain(bcn, bpb, w, h, m):
    t = 0
    for i in range(m):
        mw, mh = max(w >> i, 1), max(h >> i, 1)
        t += (((mw+3)//4)*((mh+3)//4)*bpb) if bcn else (mw*mh*bpb)
    return t

def hdr_ok(d, off):
    if off + 12 > len(d): return False
    fc = d[off + 1]
    b0, dm = d[off], d[off + 2]
    mc = (b0 >> 4) & 0xF or 1
    ww = 1 << ((dm >> 4) & 0xF)
    hh = 1 << (dm & 0xF)
    return fc in FMTS and 1 <= mc <= 14 and 4 <= ww <= 4096 and 4 <= hh <= 4096

def fmt_str(d, off):
    if off + 8 > len(d): return "OOB"
    b0, fc, dm, b3 = d[off], d[off+1], d[off+2], d[off+3]
    mc = (b0 >> 4) & 0xF or 1
    ww, hh = 1 << ((dm >> 4) & 0xF), 1 << (dm & 0xF)
    fi = FMTS.get(fc)
    fn = fi[0] if fi else f"UNK0x{fc:02x}"
    return f"fmt=0x{fc:02x}({fn}) {ww}x{hh} mips={mc} b3=0x{b3:02x}"

with open(G1T_PATH, "rb") as f:
    data = f.read()
N = len(data)
remaining = N - PIX_START_1
print(f"Loaded {N:,}B  ep_1=0x{EP_1:x}  pixStart_1=0x{PIX_START_1:x}  remaining={remaining:,}")

# ── 1. GT1G magic search ─────────────────────────────────────────────────────
print("\n=== GT1G / IDRK / other magic in remaining data ===")
for magic in [b'GT1G', b'IDRK', b'K300', b'LFMO', b'G1M\x00']:
    p = data.find(magic, PIX_START_1)
    print(f"  {magic!r}: {'0x'+hex(p)[2:] if p >= 0 else 'not found'}")

# ── 2. Hex context around ep_1 ───────────────────────────────────────────────
print("\n=== 48 bytes at ep_1 ===")
b = data[EP_1:EP_1+48]
for row in range(0, 48, 16):
    print("  " + " ".join(f"{x:02x}" for x in b[row:row+16]))

# ── 3. Probe next-ep for each known-format assumption at pixStart_1 ──────────
print("\n=== next_ep validity if tex1 pixel data starts at pixStart_1 ===")
cases = [
    ("BC7", True,16,2048,2048,10), ("BC5",True,16,2048,2048,10),
    ("BC5", True,16,1024,1024,10), ("BC7",True,16,1024,1024,10),
    ("BC5", True,16,2048,2048, 7), ("BC1",True, 8,2048,2048,10),
    ("BC5", True,16, 512, 512, 9), ("BC1",True, 8,2048,2048, 7),
    ("BC5", True,16, 512, 512,10), ("BC1",True, 8,1024,1024,10),
]
for name,bcn,bpb,w,h,m in cases:
    sz = chain(bcn,bpb,w,h,m)
    nep = PIX_START_1 + sz
    ok  = hdr_ok(data, nep)
    tag = "*** VALID HEADER ***" if ok else ("OOB" if nep>=N else "bad")
    print(f"  {name} {w:4d}x{h:<4d} {m}mip  chain={sz:>8,}  next_ep=0x{nep:x}: {tag}")
    if ok:
        print(f"    >> {fmt_str(data, nep)}")

# ── 4. Same probe but assume pixStart = ep_1 + 4 / 8 / 12 ──────────────────
print("\n=== next_ep with alternative pixStart offsets (no ext block) ===")
for hdr_sz in [4, 8, 12]:
    pix = EP_1 + hdr_sz
    print(f"\n  -- pixStart = ep_1+{hdr_sz} = 0x{pix:x} --")
    for name,bcn,bpb,w,h,m in cases[:6]:
        sz  = chain(bcn,bpb,w,h,m)
        nep = pix + sz
        ok  = hdr_ok(data, nep)
        if ok:
            print(f"  *** {name} {w}x{h} {m}mip chain={sz:,} next_ep=0x{nep:x}: {fmt_str(data, nep)}")

# ── 5. Chained scan: find TWO consecutive valid texture headers ───────────────
print("\n=== Chained scan (first 8MB from pixStart_1) ===")
SCAN = min(remaining, 8_000_000)
hits = []
for off in range(0, SCAN, 4):
    ep = PIX_START_1 + off
    if not hdr_ok(data, ep): continue
    fc = data[ep+1]; b0 = data[ep]; dm = data[ep+2]
    mc = (b0>>4)&0xF or 1
    ww = 1<<((dm>>4)&0xF); hh = 1<<(dm&0xF)
    fi = FMTS.get(fc)
    if not fi: continue
    sz = chain(fi[1],fi[2],ww,hh,mc)
    # Try both: with ext block (ep+20) and without (ep+8)
    for pix_off in [20, 8]:
        pix = ep + pix_off
        nep = pix + sz
        if hdr_ok(data, nep):
            hits.append((off, ep, pix_off, sz, nep))
            break
    if len(hits) >= 10: break

if hits:
    for off, ep, pix_off, sz, nep in hits:
        print(f"  +0x{off:x}: ep=0x{ep:x} {fmt_str(data,ep)} chain={sz:,} pix_off={pix_off}")
        print(f"     → nep=0x{nep:x}: {fmt_str(data,nep)}")
else:
    print("  No chained pairs found")

# ── 6. Backward scan from EOF: find last texture header ─────────────────────
print("\n=== Backward scan from EOF for last texture header ===")
# Smallest plausible tail: BC1 4x4 1mip = 8 bytes, BC7 4x4 1mip = 16 bytes
# Most likely the last mip chain is a few hundred bytes for a small mip level.
# Scan last 2MB for valid headers
BACK_RANGE = min(2_000_000, N - EP_1)
last_hits = []
for off in range(N-12, N-BACK_RANGE, -4):
    if not hdr_ok(data, off): continue
    fc = data[off+1]; b0 = data[off]; dm = data[off+2]
    mc = (b0>>4)&0xF or 1
    ww = 1<<((dm>>4)&0xF); hh = 1<<(dm&0xF)
    fi = FMTS.get(fc)
    if not fi: continue
    sz = chain(fi[1],fi[2],ww,hh,mc)
    for pix_off in [20, 8]:
        pix_end = off + pix_off + sz
        if abs(pix_end - N) <= 64:  # ends near EOF
            last_hits.append((off, pix_off, sz, pix_end))
            break
    if len(last_hits) >= 5: break

if last_hits:
    for off, pix_off, sz, pix_end in last_hits:
        diff = N - pix_end
        print(f"  0x{off:x}: {fmt_str(data,off)}  chain={sz:,}  pix_end=0x{pix_end:x} (EOF-{diff})")
else:
    print("  None found near EOF")

print("\nDone.")
