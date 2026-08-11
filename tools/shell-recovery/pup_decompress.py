#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Diagnostic chunked-zlib walker for PS5 .PUP.dec update files.

This is a target-presence probe, not an extractor. It deliberately scans for
zlib streams and therefore misses incompressible blocks stored verbatim. It
also does not preserve outer PUP segment boundaries. Use
``pup_exfat_extract.py`` for exact filesystem reconstruction.

A PUP whose outer container has been decrypted still looks like noise to an
entropy test, because its payload is stored as a long run of independently
zlib-compressed 512 KB chunks - and compressed data is as incompressible as
ciphertext. Measuring entropy therefore cannot tell the two apart; the only
reliable test is to try decompressing.

The chunks use CMF 0x48 (deflate, 4 KB window) rather than the far more common
0x78, so scanning for the usual "78 9c"/"78 da" zlib magic finds nothing. This
walks the file looking for any byte pair that is a structurally valid zlib
header - low nibble of CMF is 8, and (CMF<<8 | FLG) is divisible by 31 - then
attempts a real decompression and only accepts streams that yield a meaningful
amount of output.

Streams are consumed back to back, so after a successful decode the scan
resumes at the end of that stream rather than re-testing every byte inside it.
"""

import argparse
import collections
import sys
import zlib

DEFAULT_TARGETS = [
    b"vsh_asset", b".gnf", b"Particle0", b"Particle1", b"shutdown_ramp",
    b"diffuse_default", b"GNF ", b"EXFAT", b"NPXS40087", b"PUI_UI3",
]

MIN_STREAM_OUTPUT = 4096  # ignore tiny incidental matches
READ_WINDOW = 64 << 20
OVERLAP = 1 << 20         # so a stream spanning a read boundary is still found

# A chunk expands to 512 KB, so its compressed form cannot exceed that by much.
# Capping the slice handed to zlib matters more than it looks: without it every
# rejected candidate copies the rest of the read window, which at ~1.8M
# candidates over a 900 MB file is the difference between minutes and hours.
MAX_STREAM_INPUT = 2 << 20


def is_zlib_header(cmf, flg):
    return (cmf & 0x0F) == 8 and (((cmf << 8) | flg) % 31) == 0


def walk(path, targets, out_dir=None, progress_every=200):
    hits = collections.Counter()
    where = collections.defaultdict(list)
    fh = open(path, "rb")
    size = fh.seek(0, 2)
    fh.seek(0)

    pos = 0
    streams = 0
    produced = 0
    print(f"scanning {size:,} bytes for chunked zlib ...\n", flush=True)

    while pos < size:
        fh.seek(pos)
        buf = fh.read(READ_WINDOW)
        if not buf:
            break
        view = memoryview(buf)
        o = 0
        limit = len(buf) - 2
        while o < limit:
            if is_zlib_header(buf[o], buf[o + 1]):
                window = view[o:o + MAX_STREAM_INPUT]
                try:
                    dec = zlib.decompressobj()
                    out = dec.decompress(window)
                except zlib.error:
                    o += 1
                    continue
                if len(out) >= MIN_STREAM_OUTPUT:
                    used = len(window) - len(dec.unused_data)
                    streams += 1
                    produced += len(out)
                    for t in targets:
                        n = out.count(t)
                        if n:
                            hits[t] += n
                            where[t].append(pos + o)
                    if out_dir:
                        with open(f"{out_dir}/chunk_{pos + o:010X}.bin", "wb") as w:
                            w.write(out)
                    if streams % progress_every == 0:
                        found = {k.decode(): v for k, v in hits.items()}
                        print(f"  {streams:6} streams  at 0x{pos + o:010X}  "
                              f"out={produced / 1e6:9.1f} MB  {found}", flush=True)
                    o += max(used, 1)
                    continue
            o += 1
        step = max(len(buf) - OVERLAP, 1)
        if len(buf) < READ_WINDOW:
            break
        pos += step

    return streams, produced, hits, where


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("pup")
    ap.add_argument("--extract-to", help="write every decompressed chunk here")
    ap.add_argument("--target", action="append", default=None,
                    help="extra byte string to count (repeatable)")
    args = ap.parse_args()

    targets = list(DEFAULT_TARGETS)
    if args.target:
        targets += [t.encode() for t in args.target]

    streams, produced, hits, where = walk(args.pup, targets, args.extract_to)

    print("\n=== RESULT ===")
    print(f"  streams      : {streams:,}")
    print(f"  decompressed : {produced:,} bytes ({produced / 1e9:.2f} GB)")
    for t in targets:
        if hits[t]:
            locs = ", ".join(f"0x{x:X}" for x in where[t][:5])
            print(f"  {t.decode():18} x{hits[t]:<8} first at {locs}")
        else:
            print(f"  {t.decode():18} 0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
