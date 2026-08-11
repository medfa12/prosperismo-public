#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Dump the hardware register set the firmware programs for each background shader.

The eboot carries a descriptor per shader — {name, header, code, reflection},
0x20 stride — and the header ends in a flat (register, value) pair list. Those
are the registers the driver writes for that shader, so they settle host state
that would otherwise be guessed: pixel input masks, interpolant counts, user
SGPR counts, wave and LDS sizing, colour formats.

The descriptor fields are pointers filled by R_X86_64_RELATIVE relocations, so
they read as zero in the file; this resolves them from the RELA table rather
than scanning for values.
"""
from __future__ import annotations

import argparse
import struct

VA_DELTA = 0x4000

# Context registers, offsets in dwords from the GFX10 context base.
CONTEXT = {
    0x08F: "CB_SHADER_MASK",
    0x1B0: "SPI_SHADER_POS_FORMAT",
    0x1B1: "SPI_VS_OUT_CONFIG",
    0x1B3: "SPI_PS_INPUT_ENA",
    0x1B4: "SPI_PS_INPUT_ADDR",
    0x1B6: "SPI_PS_IN_CONTROL",
    0x1B8: "SPI_BARYC_CNTL",
    0x1C3: "SPI_SHADER_IDX_FORMAT",
    0x1C4: "SPI_SHADER_Z_FORMAT",
    0x1C5: "SPI_SHADER_COL_FORMAT",
    0x203: "DB_SHADER_CONTROL",
    0x207: "PA_CL_CLIP_CNTL",
    0x286: "VGT_LS_HS_CONFIG",
    0x290: "VGT_TF_PARAM",
    0x2AB: "VGT_ESGS_RING_ITEMSIZE",
}

# SH registers, offsets in dwords from the GFX10 SH base.
SH = {
    0x08: "SPI_SHADER_PGM_LO",
    0x09: "SPI_SHADER_PGM_HI",
    0x0A: "SPI_SHADER_PGM_RSRC1",
    0x0B: "SPI_SHADER_PGM_RSRC2",
    0x0C: "SPI_SHADER_PGM_RSRC3",
}


def load_relocations(buf: bytes) -> dict[int, int]:
    """Resolve R_X86_64_RELATIVE entries: slot vaddr -> addend."""
    phoff, = struct.unpack_from("<Q", buf, 0x20)
    phentsize, phnum = struct.unpack_from("<HH", buf, 0x36)
    segments, dynamic = [], None
    for i in range(phnum):
        o = phoff + i * phentsize
        p_type, = struct.unpack_from("<I", buf, o)
        p_offset, p_vaddr, _, p_filesz, _, _ = struct.unpack_from("<QQQQQQ", buf, o + 8)
        if p_type == 1:
            segments.append((p_vaddr, p_vaddr + p_filesz, p_offset))
        elif p_type == 2:
            dynamic = (p_offset, p_filesz)
    if dynamic is None:
        raise SystemExit("no PT_DYNAMIC")

    tags = {}
    off, size = dynamic
    for i in range(size // 16):
        tag, val = struct.unpack_from("<QQ", buf, off + i * 16)
        if tag == 0:
            break
        tags.setdefault(tag, val)

    def to_file(vaddr: int) -> int:
        for lo, hi, base in segments:
            if lo <= vaddr < hi:
                return base + (vaddr - lo)
        raise SystemExit(f"vaddr 0x{vaddr:X} outside every LOAD segment")

    rela = to_file(tags[7])
    count = tags[8] // tags[9]
    out = {}
    for i in range(count):
        r_off, r_info, r_add = struct.unpack_from("<QQq", buf, rela + i * 0x18)
        if (r_info & 0xFFFFFFFF) == 8:
            out[r_off] = r_add
    return out


def cstring(buf: bytes, vaddr: int) -> str:
    i = vaddr + VA_DELTA
    return buf[i:buf.index(b"\0", i)].decode("latin1")


def pairs(buf: bytes, header: int, limit: int) -> list[tuple[int, int]]:
    """Read the (register, value) list.

    It is anchored on SPI_SHADER_PGM_LO/HI, which every shader programs and
    which the firmware leaves zero because the code address is bound at load
    time. Reading from a fixed offset instead would drift with header size.
    """
    words = [struct.unpack_from("<I", buf, header + VA_DELTA + k * 4)[0]
             for k in range(limit // 4)]
    # PGM_LO/HI sit at different SH offsets per stage (0x08 for PS and CS,
    # 0x47/0x4C for the GS- and HS-based stages), so anchor on the shape -
    # consecutive register numbers whose values are both zero - rather than on
    # one offset.
    start = None
    for k in range(len(words) - 3):
        if (words[k] < 0x100 and words[k + 1] == 0 and
                words[k + 2] == words[k] + 1 and words[k + 3] == 0):
            start = k
            break
    if start is None:
        return []
    out = []
    k = start
    while k + 1 < len(words):
        register, value = words[k], words[k + 1]
        if register == 0xFFFFFFFF:
            break
        if register not in CONTEXT and register not in SH and register > 0x400:
            break
        out.append((register, value))
        k += 2
    return out


def describe(register: int, value: int) -> str:
    if register in SH:
        name = SH[register]
        if register == 0x0B:
            return f"{name} = 0x{value:08X}  user_sgpr={(value >> 1) & 0x1F}"
        return f"{name} = 0x{value:08X}"
    name = CONTEXT.get(register, f"reg_0x{register:03X}")
    if register == 0x1B6:
        return f"{name} = 0x{value:08X}  num_interp={value & 0x3F}"
    return f"{name} = 0x{value:08X}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", required=True)
    parser.add_argument("--table", default="0x113EFB0",
                        help="vaddr of the first shader descriptor")
    parser.add_argument("--count", type=int, default=14)
    args = parser.parse_args()

    buf = open(args.eboot, "rb").read()
    reloc = load_relocations(buf)
    base = int(args.table, 16)

    entries = []
    for i in range(args.count):
        slot = base + i * 0x20
        name, header, code = reloc.get(slot), reloc.get(slot + 8), reloc.get(slot + 0x10)
        if name is None or header is None or code is None:
            continue
        entries.append((cstring(buf, name), header, code))

    for i, (name, header, code) in enumerate(entries):
        limit = (entries[i + 1][1] - header) if i + 1 < len(entries) else 0x180
        print(f"{name}  code=0x{code + VA_DELTA:X} (file)  header=0x{header:X}")
        seen = set()
        for register, value in pairs(buf, header, limit):
            if register in CONTEXT or register in SH:
                if register in seen:
                    continue
                seen.add(register)
                print(f"    {describe(register, value)}")
        print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
