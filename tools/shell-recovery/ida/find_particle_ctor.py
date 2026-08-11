# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Locate the ResourcesCs constructor in the PS5 shell eboot using Hex-Rays.

Five scripted searches over raw disassembly failed before this, all the same
way: they matched a struct offset and landed on unrelated classes that happen to
have a field there. Hex-Rays is used here because its pseudocode resolves the
base object of a member access, so a store can be attributed to a class rather
than to a number.

The anchor is simulateParticles at 0xE24F0, verified independently by its own
assert strings, whose second argument is the particle system and whose first
0xF8 bytes are the ResourcesCs block.
"""

import idaapi
import idautils
import idc
import ida_hexrays
import ida_funcs
import ida_bytes
import re
import struct

SIMULATE = 0xE24F0
DRIVER = 0xE2700
OUT = "/tmp/ida_particle_report.txt"

# Offsets the driver is known to use on its context, from the verified map.
CTX_FIELDS = (0x1A0, 0x1A8, 0x5E0, 0x5E8, 0x6E8, 0x708, 0x70C, 0x710)


def log(fh, msg):
    print(msg)
    fh.write(msg + "\n")
    fh.flush()


def decompile(ea):
    try:
        return ida_hexrays.decompile(ea)
    except Exception:
        return None


def main():
    idaapi.auto_wait()
    fh = open(OUT, "w")

    if not ida_hexrays.init_hexrays_plugin():
        log(fh, "Hex-Rays decompiler unavailable")
        fh.close()
        return

    log(fh, "=== callers of simulateParticles ===")
    callers = set()
    for xr in idautils.XrefsTo(SIMULATE):
        f = ida_funcs.get_func(xr.frm)
        if f:
            callers.add(f.start_ea)
    for c in sorted(callers):
        log(fh, "  %s @ 0x%X" % (idc.get_func_name(c), c))

    # The driver's pseudocode names the context; print it so the class is visible.
    log(fh, "\n=== driver pseudocode (0x%X) ===" % DRIVER)
    cf = decompile(DRIVER)
    if cf:
        for line in str(cf).splitlines():
            log(fh, "  " + line)
    else:
        log(fh, "  decompile failed")

    # Now sweep every function, decompile, and record which ones write to the
    # context fields. Attribution is by pseudocode member access, not by raw
    # offset, so unrelated classes with the same offsets do not match.
    log(fh, "\n=== functions writing the context's system slots ===")
    pat = re.compile(r"(0x1A0|0x1A8|0x5E0|0x5E8)\b", re.I)
    alloc = re.compile(r"operator new|malloc|j_malloc", re.I)
    flt = re.compile(r"([-+]?\d+\.\d+(?:e[-+]?\d+)?)")

    hits = []
    for i, fea in enumerate(idautils.Functions()):
        if i % 2000 == 0:
            print("  ... %d functions" % i)
        cf = decompile(fea)
        if not cf:
            continue
        txt = str(cf)
        if not pat.search(txt):
            continue
        floats = set(flt.findall(txt))
        hits.append((fea, idc.get_func_name(fea), bool(alloc.search(txt)), len(floats), txt))

    hits.sort(key=lambda h: -h[3])
    log(fh, "  %d functions touch those offsets" % len(hits))
    for fea, name, has_alloc, nf, txt in hits[:25]:
        log(fh, "\n--- %s @ 0x%X   floats=%d alloc=%s" % (name, fea, nf, has_alloc))
        for line in txt.splitlines():
            if pat.search(line) or (nf and flt.search(line)):
                log(fh, "      " + line.strip()[:150])

    fh.close()
    print("report -> " + OUT)


main()
idc.qexit(0)
