#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Recover the WaveColourPreset table by replaying its seeder.

The table lives at vaddr 0x137CFC0, 128 bytes per record, and the crossfade at
0xEA0C2 indexes it with the light object's field at +0xE0. Its contents are
*not* in the file: it is runtime-initialised memory, seeded by a straight-line
block of vector stores starting at 0xEA786 — the same arrangement as
FirstWave::Initialize's palette records.

So the values are recovered the way that block produces them: walk the
instructions, track which RIP-relative constant each vector register last
loaded, and replay every store into the table. Nothing is fitted; every float
is a constant the seeder itself names.

Each record is a BackgroundLayer light `ColorCb`:

    0x00 lightCol            0x40 pointLightCol       0x70 gamma
    0x10 lightColOnFloor     0x50 pointLightAmbCol    0x74 gintensity
    0x20 light2Col           0x60 themedColor         0x78 noise
    0x30 light2ColOnFloor

Records 20 and 21 (ThemeFlow6, ThemeFlow7) are **not** written by this block and
are reported as unwritten. Widening the window past 0xED000 makes them appear to
fill, but with values another function happens to store into the same range —
`gamma = 200.0` where every genuine record carries 1/2.2. The narrow window is
deliberate: a blank is honest, a plausible wrong number is not.
"""
from __future__ import annotations

import argparse
import re
import struct

import capstone

VA_DELTA = 0x4000
TABLE = 0x137CFC0
RECORD = 0x80
COUNT = 22
SEEDER = 0xEA700
SEEDER_END = 0xED000

NAMES = [
    "InitialSetup", "MiniApp", "SystemArea", "MusicUnlimited", "HomeScreen",
    "NoWave", "Black", "WhatsNew", "MusicUnlimitedSplash", "Login",
    "LoginNoUserLogined", "Boot", "Store", "PsVideo",
    "ThemeFlow0", "ThemeFlow1", "ThemeFlow2", "ThemeFlow3",
    "ThemeFlow4", "ThemeFlow5", "ThemeFlow6", "ThemeFlow7",
]

FIELDS = [
    ("lightCol", 0x00, 4), ("lightColOnFloor", 0x10, 4),
    ("light2Col", 0x20, 4), ("light2ColOnFloor", 0x30, 4),
    ("pointLightCol", 0x40, 4), ("pointLightAmbCol", 0x50, 4),
    ("themedColor", 0x60, 4),
    ("gamma", 0x70, 1), ("gintensity", 0x74, 1), ("noise", 0x78, 1),
]


def replay(buf: bytes) -> tuple[bytearray, bytearray]:
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    regs: dict[str, object] = {}
    flat = bytearray(COUNT * RECORD)
    have = bytearray(COUNT * RECORD)

    def store(dst: int, data: bytes) -> None:
        off = dst - TABLE
        if 0 <= off < len(flat):
            end = min(off + len(data), len(flat))
            flat[off:end] = data[:end - off]
            have[off:end] = b"\x01" * (end - off)

    for ins in md.disasm(buf[SEEDER + VA_DELTA:SEEDER_END + VA_DELTA], SEEDER):
        o = ins.op_str
        target = lambda g: ins.address + ins.size + int(g, 16)  # noqa: E731

        m = re.match(r"^([xy]mm\d+), [xy]mmword ptr \[rip \+ (0x[0-9a-f]+)\]$", o)
        if ins.mnemonic in ("vmovups", "vmovaps") and m:
            regs[m.group(1)] = ("m", target(m.group(2)))
            continue
        m = re.match(r"^(xmm\d+), qword ptr \[rip \+ (0x[0-9a-f]+)\]$", o)
        if ins.mnemonic in ("vmovsd", "vmovlps") and m:
            regs[m.group(1)] = ("m", target(m.group(2)))
            continue
        m = re.match(r"^([xy]mm\d+), (?:dword|[xy]mmword) ptr \[rip \+ (0x[0-9a-f]+)\]$", o)
        if ins.mnemonic == "vbroadcastss" and m:
            regs[m.group(1)] = ("b", target(m.group(2)))
            continue
        m = re.match(r"^([xy]mm\d+), ([xy]mm\d+)$", o)
        if ins.mnemonic in ("vmovups", "vmovaps", "vmovapd") and m:
            if m.group(2) in regs:
                regs[m.group(1)] = regs[m.group(2)]
            continue

        m = re.match(r"^([xy]mmword) ptr \[rip \+ (0x[0-9a-f]+)\], ([xy]mm\d+)$", o)
        if ins.mnemonic in ("vmovups", "vmovaps") and m:
            src = regs.get(m.group(3))
            if src is None:
                continue
            n = 32 if m.group(1) == "ymmword" else 16
            kind, va = src
            data = buf[va + VA_DELTA:va + VA_DELTA + 4] * (n // 4) if kind == "b" \
                else buf[va + VA_DELTA:va + VA_DELTA + n]
            store(target(m.group(2)), data)
            continue
        m = re.match(r"^qword ptr \[rip \+ (0x[0-9a-f]+)\], (xmm\d+)$", o)
        if ins.mnemonic in ("vmovsd", "vmovlps") and m:
            src = regs.get(m.group(2))
            if src is None:
                continue
            kind, va = src
            data = buf[va + VA_DELTA:va + VA_DELTA + 4] * 2 if kind == "b" \
                else buf[va + VA_DELTA:va + VA_DELTA + 8]
            store(target(m.group(1)), data)
            continue
        m = re.match(r"^dword ptr \[rip \+ (0x[0-9a-f]+)\], (0x[0-9a-f]+)$", o)
        if ins.mnemonic == "mov" and m:
            store(target(m.group(1)), struct.pack("<I", int(m.group(2), 16)))
            continue

    return flat, have


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", required=True)
    parser.add_argument("--preset", type=int, default=None)
    parser.add_argument("--raw", action="store_true",
                        help="emit the selected record as a 0x7C-byte ColorCb")
    args = parser.parse_args()

    buf = open(args.eboot, "rb").read()
    flat, have = replay(buf)

    if args.raw:
        if args.preset is None:
            raise SystemExit("--raw needs --preset")
        import sys
        sys.stdout.buffer.write(bytes(flat[args.preset * RECORD:args.preset * RECORD + 0x7C]))
        return 0

    covered = sum(have) / len(have) * 100
    print(f"WaveColourPreset table at 0x{TABLE:X}, {COUNT} x 0x{RECORD:X}, "
          f"{covered:.1f}% written by the seeder\n")
    for i, name in enumerate(NAMES):
        if args.preset is not None and i != args.preset:
            continue
        base = i * RECORD
        print(f"[{i:2}] {name}")
        for field, off, count in FIELDS:
            if not all(have[base + off:base + off + 4 * count]):
                print(f"      {field:18} <not written>")
                continue
            values = struct.unpack_from(f"<{count}f", bytes(flat), base + off)
            print(f"      {field:18} {[round(v, 5) for v in values]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
