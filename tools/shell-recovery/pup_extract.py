#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Carve container files out of a chunked-zlib PS5 .PUP.dec.

This is a historical diagnostic carver. It does not preserve outer PUP segment
boundaries and cannot recover files through exFAT cluster chains. Use
``pup_exfat_extract.py`` for exact files; a carve is never provenance for a
runtime asset.

The early diagnostic pass established that many payload blocks are independent
zlib streams. This tool decompresses discovered streams in raw-file order into
a sliding buffer and carves across adjacent stream seams. That ordering is not
a filesystem contract: it can cross PUP segment boundaries and omits stored
blocks, which is why its output is evidence-only.

Carved formats are identified by magic and self-describing length:
  GNF  - 'GNF ' + uint32 contentsSize
  RCOF - PS5 UI resource container
  Files whose declared size is implausible are skipped rather than guessed at.
"""

import argparse
import os
import struct
import sys
import zlib

MAX_STREAM_INPUT = 2 << 20
KEEP_TAIL = 8 << 20      # sliding overlap; larger than any expected carve
GNF_MAGIC = b"GNF "
RCO_MAGIC = b"RCOF"


def is_zlib_header(cmf, flg):
    return (cmf & 0x0F) == 8 and (((cmf << 8) | flg) % 31) == 0


def iter_chunks(path):
    """Yield decompressed chunks in file order."""
    fh = open(path, "rb")
    size = fh.seek(0, 2)
    fh.seek(0)
    pos = 0
    window = 64 << 20
    while pos < size:
        fh.seek(pos)
        buf = fh.read(window)
        if not buf:
            break
        view = memoryview(buf)
        o = 0
        while o < len(buf) - 2:
            if is_zlib_header(buf[o], buf[o + 1]):
                sl = view[o:o + MAX_STREAM_INPUT]
                try:
                    dec = zlib.decompressobj()
                    out = dec.decompress(sl)
                except zlib.error:
                    o += 1
                    continue
                if len(out) >= 4096:
                    yield pos + o, out
                    o += max(len(sl) - len(dec.unused_data), 1)
                    continue
            o += 1
        if len(buf) < window:
            break
        pos += max(len(buf) - (1 << 20), 1)


def carve(path, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    blob = bytearray()
    base = 0          # image offset corresponding to blob[0]
    found = {"gnf": 0, "rco": 0}
    total = 0

    def scan(final=False):
        nonlocal blob, base
        i = 0
        while True:
            gi = blob.find(GNF_MAGIC, i)
            ri = blob.find(RCO_MAGIC, i)
            cands = [(x, k) for x, k in ((gi, "gnf"), (ri, "rco")) if x >= 0]
            if not cands:
                break
            at, kind = min(cands)
            if kind == "gnf":
                if at + 8 > len(blob):
                    break
                size, = struct.unpack("<I", blob[at + 4:at + 8])
                total_size = size + 8
                if not (0x100 <= total_size <= (64 << 20)):
                    i = at + 4
                    continue
            else:
                # RCOF length lives further in; take a generous fixed window.
                total_size = min(len(blob) - at, 64 << 20)
            if at + total_size > len(blob) and not final:
                break  # wait for more data
            data = bytes(blob[at:at + total_size])
            if len(data) < 0x100:
                i = at + 4
                continue
            name = f"{kind}_{base + at:012X}.{kind}"
            with open(os.path.join(out_dir, name), "wb") as w:
                w.write(data)
            found[kind] += 1
            print(f"  carved {name}  {len(data):,} bytes", flush=True)
            i = at + 4

    for n, (src_off, chunk) in enumerate(iter_chunks(path)):
        blob += chunk
        total += len(chunk)
        if len(blob) > KEEP_TAIL * 2:
            scan()
            drop = len(blob) - KEEP_TAIL
            blob = blob[drop:]
            base += drop
        if n % 200 == 0:
            print(f"  ...{n} chunks, {total/1e6:.0f} MB, found {found}", flush=True)
    scan(final=True)
    print(f"\ndone: {total:,} bytes scanned, carved {found}")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pup")
    ap.add_argument("out_dir")
    args = ap.parse_args()
    carve(args.pup, args.out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
