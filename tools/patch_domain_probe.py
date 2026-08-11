#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Patch a domain stage's SPIR-V to publish its clip output.

Same technique as patch_clip_probe.py, aimed at the tessellation evaluation
stage: find the OpCompositeConstruct that feeds gl_Position, and store its four
components -- plus the bool that gates the export -- into a scratch storage
buffer appended past the shader's own bindings.
"""
import os
import re

S = os.environ["S"]
# The module declares a fixed three-element buffer array, so the probe cannot
# append a fourth. It writes into the tail of the patch ring instead: the hull
# fills 16 control points at 512-byte stride (8 KB) and the ring is 16 KB, so
# everything past dword 2048 is untouched by either stage.
SCRATCH = os.environ.get("PROBE_BUFFER", "2")
PROBE_BASE = int(os.environ.get("PROBE_BASE", "2048"))

src = open(f"{S}/dom.txt").read()

have = set(re.findall(r"(%uint_\d+) = OpConstant %uint", src))
want = [f"%uint_{i}" for i in (0, 1, 2, 3, 4, 5, 6, 8, 15, 16)] + [f"%uint_{PROBE_BASE}"]
extra = "".join(f"\n {n} = OpConstant %uint {n.split('_')[1]}"
                for n in want if n not in have)
anchor = re.search(r"^\s+(%\w+) = OpConstant %float 1\n", src, re.M)
src = src[:anchor.end()] + extra.lstrip("\n") + "\n" + src[anchor.end():]

store = re.search(
    r"%(\d+) = OpCompositeConstruct %v4float (%\d+) (%\d+) (%\d+) (%\d+)\n"
    r"\s+%\d+ = OpLoad %bool (%\d+)\n"
    r"\s+%\d+ = OpLoad %v4float %gl_Position\n"
    r"\s+%\d+ = OpSelect %v4float %\d+ %\d+ %\d+\n"
    r"\s+OpStore %gl_Position (%\d+)", src)
comps = store.group(2, 3, 4, 5)
gate = store.group(6)
sel = store.group(7)

out, i = [], 91000
out.append(f"      %{i} = OpLoad %uint %gl_VertexIndex"); vraw = i; i += 1
out.append(f"      %{i} = OpLoad %uint %gl_InstanceIndex"); inst = i; i += 1
# one slot per (instance, vertex): instance-major so patches stay separable
# Compact slots: 16 samples per instance, spread across the patch, so a
# large patch count still fits the scratch region.
out.append(f"      %{i} = OpIMul %uint %{inst} %uint_16"); im = i; i += 1
out.append(f"      %{i} = OpShiftRightLogical %uint %{vraw} %uint_6"); sh = i; i += 1
out.append(f"      %{i} = OpBitwiseAnd %uint %{sh} %uint_15"); sm = i; i += 1
out.append(f"      %{i} = OpIAdd %uint %{im} %{sm}"); vid = i; i += 1
out.append(f"      %{i} = OpIMul %uint %{vid} %uint_8"); off = i; i += 1
out.append(f"      %{i} = OpIAdd %uint %{off} %uint_{PROBE_BASE}"); base = i; i += 1


def emit(slot: int, value: str) -> None:
    global i
    out.append(f"      %{i} = OpIAdd %uint %{base} %uint_{slot}"); idx = i; i += 1
    out.append(f"      %{i} = OpAccessChain %_ptr_StorageBuffer_uint "
               f"%guestBuffers %uint_{SCRATCH} %uint_0 %{idx}"); ptr = i; i += 1
    out.append(f"              OpStore %{ptr} {value}")


for k, comp in enumerate(comps):
    out.append(f"      %{i} = OpBitcast %uint {comp}"); val = i; i += 1
    emit(k, f"%{val}")

# slot 4: the export gate, so a dead lane is distinguishable from a bad number
out.append(f"      %{i} = OpLoad %bool {gate}"); g = i; i += 1
out.append(f"      %{i} = OpSelect %uint %{g} %uint_1 %uint_0"); gv = i; i += 1
emit(4, f"%{gv}")
emit(5, f"%{inst}")

tail = f"               OpStore %gl_Position {sel}"
if tail not in src:
    raise SystemExit(f"anchor not found: {tail!r}")
src = src.replace(tail, "\n".join(out) + "\n" + tail)
open(f"{S}/dom_probe.txt", "w").write(src)
