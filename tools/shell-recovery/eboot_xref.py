#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Cross-reference finder for decrypted PS5 shell eboots.

The shell eboot is a plain ELF64/x86-64 image once the firmware dump is
unpacked, so the scene-renderer code can be read directly - no key material and
no console involved. This locates the code that references a given rodata
string, which is how the background scene's builders are found: Sony left the
parameter names ("TextureIBLDiffuse", "CreateLightShaftModel") in the binary,
and the functions that bind them are whatever loads those addresses.

x86-64 reaches rodata through RIP-relative LEA, so a reference to a string at
virtual address V appears as `lea reg, [rip+disp32]` where

    V == address_of_next_instruction + disp32

Scanning for that encoding is far cheaper than linearly disassembling 13 MB of
text, and it does not require correct instruction boundaries to start with.
"""

import argparse
import struct
import sys

# REX.W prefixes that can introduce a 64-bit LEA.
REX_W = {0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F}
# ModRM bytes with mod=00, rm=101 (RIP-relative), for each destination register.
RIP_MODRM = {0x05, 0x0D, 0x15, 0x1D, 0x25, 0x2D, 0x35, 0x3D}
LEA_LEN = 7  # REX + opcode + modrm + disp32


class Image:
    """A loaded ELF with the file-offset <-> virtual-address mapping it needs."""

    def __init__(self, path):
        self.data = open(path, "rb").read()
        if self.data[:4] != b"\x7fELF":
            raise SystemExit(f"{path}: not an ELF")
        phoff, _ = struct.unpack("<QQ", self.data[32:48])
        phentsize, phnum = struct.unpack("<HH", self.data[54:58])
        self.segments = []  # (offset, vaddr, filesz, flags)
        for i in range(phnum):
            o = phoff + i * phentsize
            ptype, flags = struct.unpack("<II", self.data[o:o + 8])
            off, va, _, fsz, _, _ = struct.unpack("<QQQQQQ", self.data[o + 8:o + 56])
            if ptype == 1 and fsz:  # PT_LOAD
                self.segments.append((off, va, fsz, flags))

    def off_to_va(self, off):
        for s_off, va, fsz, _ in self.segments:
            if s_off <= off < s_off + fsz:
                return va + (off - s_off)
        return None

    def va_to_off(self, va):
        for s_off, s_va, fsz, _ in self.segments:
            if s_va <= va < s_va + fsz:
                return s_off + (va - s_va)
        return None

    @property
    def text(self):
        """The executable segment, as (file_offset, vaddr, bytes)."""
        for off, va, fsz, flags in self.segments:
            if flags & 1:
                return off, va, self.data[off:off + fsz]
        raise SystemExit("no executable segment")


def find_lea_targets(image):
    """Map every RIP-relative LEA target VA -> list of referencing VAs."""
    off, base_va, text = image.text
    targets = {}
    n = len(text)
    for i in range(n - LEA_LEN):
        if text[i] in REX_W and text[i + 1] == 0x8D and text[i + 2] in RIP_MODRM:
            disp = struct.unpack("<i", text[i + 3:i + 7])[0]
            insn_va = base_va + i
            targets.setdefault(insn_va + LEA_LEN + disp, []).append(insn_va)
    return targets


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("eboot")
    ap.add_argument("strings", nargs="+", help="rodata strings to cross-reference")
    args = ap.parse_args()

    img = Image(args.eboot)
    print(f"{args.eboot}: {len(img.data):,} bytes, {len(img.segments)} PT_LOAD")
    for off, va, fsz, flags in img.segments:
        perm = "".join(c if flags & b else "-" for c, b in (("R", 4), ("W", 2), ("X", 1)))
        print(f"  off=0x{off:08X} va=0x{va:010X} size=0x{fsz:08X} {perm}")

    print("\nscanning text for RIP-relative LEA ...")
    targets = find_lea_targets(img)
    print(f"  {sum(len(v) for v in targets.values()):,} LEAs -> {len(targets):,} distinct targets\n")

    for needle in args.strings:
        raw = needle.encode()
        results = []
        start = 0
        while True:
            hit = img.data.find(raw, start)
            if hit < 0:
                break
            start = hit + 1
            va = img.off_to_va(hit)
            if va is None:
                continue
            refs = targets.get(va, [])
            results.append((hit, va, refs))
        if not results:
            print(f'"{needle}": not present')
            continue
        for hit, va, refs in results:
            tag = ", ".join(f"0x{r:X}" for r in refs[:8]) if refs else "(no direct LEA)"
            more = f" +{len(refs) - 8} more" if len(refs) > 8 else ""
            print(f'"{needle}" @file 0x{hit:08X} va 0x{va:08X}  <- {len(refs)} ref(s): {tag}{more}')


if __name__ == "__main__":
    sys.exit(main())
