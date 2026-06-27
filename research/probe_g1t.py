"""probe_g1t.py — targeted analysis of format 0x4c in f782f325.g1t"""
import struct, os

G1T_PATH = r"G:\_GitProjectNEW\KissakiViewer\research\extracted\f782f325.g1t"
EP_1        = 0x555588
PIX_START_1 = 0x55559C  # ep_1 + 20 (extSize capped to 12)
B0_1 = 0x70  # mips=7, flags=0
FC_1 = 0x4C  # unknown format code
DM_1 = 0xC1  # dim byte at ep_1

with open(G1T_PATH, "rb") as f:
    data = f.read()
N = len(data)
remaining = N - PIX_START_1
print(f"Loaded {N:,}B  ep_1=0x{EP_1:x}  remaining={remaining:,}")

# ── 1. Find all occurrences of format 0x4c with same b0/dim as ep_1 ──────────
print(f"\n=== Search for exact header pattern (b0={B0_1:02x} fc={FC_1:02x} dm={DM_1:02x}) ===")
hits = []
pos = EP_1
while True:
    idx = data.find(bytes([FC_1]), pos + 1, N - 10)
    if idx < 0: break
    if data[idx - 1] == B0_1 and data[idx + 1] == DM_1:
        ep = idx - 1
        hits.append(ep)
        if len(hits) >= 20: break
    pos = idx

if len(hits) > 1:
    print(f"  Found {len(hits)} candidates (first is ep_1 = 0x{hits[0]:x})")
    for i, h in enumerate(hits):
        delta = h - (hits[i-1] if i > 0 else EP_1)
        print(f"  [{i}] ep=0x{h:x}  delta=+0x{delta:x} ({delta:,})")
    # Check if deltas are consistent
    if len(hits) >= 3:
        deltas = [hits[i] - hits[i-1] for i in range(1, len(hits))]
        if len(set(deltas)) == 1:
            print(f"\n  *** UNIFORM spacing: {deltas[0]:,} bytes ***")
            chain = deltas[0] - 20  # subtract header
            print(f"      chain_size = {deltas[0]:,} - 20 = {chain:,} bytes")
else:
    print(f"  Only found ep_1 itself — no repeated pattern")

# ── 2. Search for ANY format 0x4c header in the remaining data ───────────────
print(f"\n=== All format-0x4c byte-pairs in remaining data (up to 20) ===")
hits2 = []
pos = PIX_START_1
while len(hits2) < 20:
    idx = data.find(bytes([FC_1]), pos, N - 10)
    if idx < 0: break
    ep = idx - 1
    if ep >= PIX_START_1:
        b0 = data[ep]; dm = data[ep+2]; b3 = data[ep+3]
        mc = (b0 >> 4) & 0xF or 1
        ww = 1 << ((dm >> 4) & 0xF); hh = 1 << (dm & 0xF)
        delta_from_pix = ep - PIX_START_1
        hits2.append((ep, b0, dm, b3, mc, ww, hh, delta_from_pix))
    pos = idx + 1

for ep, b0, dm, b3, mc, ww, hh, delta in hits2:
    print(f"  0x{ep:x} (+0x{delta:x}): b0=0x{b0:02x} dm=0x{dm:02x} b3=0x{b3:02x} → {ww}x{hh} mips={mc}")

# ── 3. Check bytes at key BC7-chain-size-aligned positions ───────────────────
print(f"\n=== Bytes at PIX_START_1 + N * BC7_2048_10mip_chain ===")
CHAIN_BC7_2048 = 5_592_400
for n in range(1, 5):
    pos = PIX_START_1 + n * CHAIN_BC7_2048
    if pos + 20 >= N:
        print(f"  n={n}: 0x{pos:x} -- out of file")
        break
    b = data[pos:pos+20]
    hs = " ".join(f"{x:02x}" for x in b)
    fc = b[1] if len(b) > 1 else 0
    print(f"  n={n}: 0x{pos:x}  {hs}  [fc=0x{fc:02x}]")

# ── 4. Check with no-extblock assumption (pixStart = ep+8) ──────────────────
print(f"\n=== Bytes at ep_1+8+N*BC7_2048_10mip_chain (no ext block) ===")
PIX_8 = EP_1 + 8
for n in range(1, 5):
    pos = PIX_8 + n * CHAIN_BC7_2048
    if pos + 20 >= N:
        print(f"  n={n}: 0x{pos:x} -- out of file")
        break
    b = data[pos:pos+20]
    hs = " ".join(f"{x:02x}" for x in b)
    fc = b[1] if len(b) > 1 else 0
    print(f"  n={n}: 0x{pos:x}  {hs}  [fc=0x{fc:02x}]")

# ── 5. Analyze distribution of byte[1] values in the remaining data ──────────
print(f"\n=== Format code byte [+1] histogram in remaining 16MB (at 4B alignment) ===")
freq = {}
for off in range(0, remaining - 4, 4):
    fc = data[PIX_START_1 + off + 1]
    freq[fc] = freq.get(fc, 0) + 1
top = sorted(freq.items(), key=lambda x: -x[1])[:20]
print(f"  Top 20 values at byte[+1] offsets (fc=byte[1]):")
for fc, cnt in top:
    pct = cnt * 400 / remaining  # approx % at 4B alignment
    print(f"    0x{fc:02x}: {cnt:,}  (~{pct:.1f}%)")

# ── 6. Dump 32 bytes at PIX_START_1 to cross-check against ep_1 header ───────
print(f"\n=== Dump 128 bytes at 0xaaaaec (PIX_START_1 + BC7_2048_chain) ===")
ZERO_POS = PIX_START_1 + CHAIN_BC7_2048
b = data[ZERO_POS:ZERO_POS+128]
for row in range(0, 128, 16):
    print("  " + " ".join(f"{x:02x}" for x in b[row:row+16]))

# How many consecutive zeros?
nz = 0
while ZERO_POS + nz < N and data[ZERO_POS + nz] == 0:
    nz += 1
print(f"  Consecutive zeros from 0x{ZERO_POS:x}: {nz}")
print(f"  First non-zero at: 0x{ZERO_POS+nz:x}  byte=0x{data[ZERO_POS+nz]:02x}")

# Sequential parse from 0xaaaaec treating format 0x00 as null (8-byte header, 0 chain)
print(f"\n=== Sequential parse from 0x{ZERO_POS:x} (treat fmt=0 as null skip) ===")
ep = ZERO_POS
for i in range(1, 12):
    if ep + 12 > N:
        print(f"  [{i}] ep=0x{ep:x} -- out of bounds")
        break
    b0 = data[ep]; fc = data[ep+1]; dm = data[ep+2]; b3 = data[ep+3]
    mc = (b0>>4)&0xF or 1
    ww = 1<<((dm>>4)&0xF); hh = 1<<(dm&0xF)
    if fc == 0:
        ext = 0
    else:
        ext_raw = struct.unpack_from("<I", data, ep+8)[0]
        ext = ext_raw if ext_raw <= 0x400 else 0x0C
    pix = ep + 8 + ext
    from_fmts = "(KNOWN)" if fc in {"BC7":0x5f,"BC5":0x5d,"BC1":0x59,"BC4":0x5c}.values() else ""
    is_04c = " ***0x4c***" if fc == 0x4c else ""
    print(f"  [{i}] ep=0x{ep:x}  fc=0x{fc:02x}  {ww}x{hh}  mips={mc}  b3=0x{b3:02x}  ext={ext}  pixStart=0x{pix:x}{from_fmts}{is_04c}")
    hex20 = " ".join(f"{data[ep+k]:02x}" for k in range(min(20, N-ep)))
    print(f"       hdr: {hex20}")
    if fc == 0:
        ep = pix  # null: skip 8-byte header
    elif fc == 0x4c:
        ep = pix + CHAIN_BC7_2048  # assume same chain as BC7 2048x2048
    else:
        break

print(f"\n=== Last 64 bytes of file ===")
b = data[N-64:]
for row in range(0, 64, 16):
    print("  " + " ".join(f"{x:02x}" for x in b[row:row+16]))

print("\nDone.")
