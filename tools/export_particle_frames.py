#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Export the PS5 background's authored particle resource blocks per frame.

The blocks are Sony's own: they come out of the serialized pattern blobs
embedded in NPXS40087, replayed at an authored time by the decoder in
ps5oracle/sharpemu/scripts/ps5_particle_patterns.py. Nothing here is modelled
or fitted; the only choices are which pattern and which times to sample.

Frame file layout (little endian):

    u32 magic 'PFRM'
    u32 groupCount
    f32 time
    u32 reserved
    per group:
        u32 kind        bits 0..7: 0 = small, 1 = large
                        bits 8..15: native transition instance (0 or 1)
        u32 index
        u32 computeLen  (0xF8)
        u32 drawLen     (0x140 small, 0xEC large)
        computeLen bytes   ResourcesCs
        drawLen bytes      ResourcesVsPs
"""
from __future__ import annotations

import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, os.path.join(ROOT, "ps5oracle", "sharpemu", "scripts"))
import ps5_particle_patterns as pp  # noqa: E402

# Recovered from the 12.40 loader at 0xE14F0: it rejects a selector above 6,
# indexes the name table, and passes the blob and length tables to the parser
# at 0xE52F0. See docs/sony-shell/particle-live-simulation.md.
BLOB_LENGTHS = 0xFF18A0
BLOBS = [0xFF18E0, 0xFF3890, 0xFF5690, 0xFF7E00, 0xFFA660, 0xFFCD70, 0xFFF6D0]
VA_DELTA = 0x4000


def load_pattern(eboot: str, selector: int) -> pp.PatternRecord:
    data = open(eboot, "rb").read()
    lengths = struct.unpack_from("<7Q", data, BLOB_LENGTHS + VA_DELTA)
    va, length = BLOBS[selector], lengths[selector]
    blob = data[va + VA_DELTA: va + VA_DELTA + length]
    header = pp.decode_blob_header(blob)
    layout = pp.decode_payload_layout(blob, header)
    return pp.PatternRecord(
        selector, header.embedded_name, 0, va, va + VA_DELTA, length,
        header.embedded_name, header.version, header.vector_counts,
        header.payload_offset, header.first_payload_u32,
        header.first_payload_float, layout.end_offset, layout.event_fields,
        layout.field23_records, layout.standalone_floats,
        layout.string_records, layout.float_locations)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--selector", type=int, default=0)
    parser.add_argument("--start", type=float, default=0.0)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--frames", type=int, default=1)
    parser.add_argument("--transition-selector", type=int, default=None,
                        help="second pattern that becomes current at --transition-at")
    parser.add_argument("--transition-at", type=float, default=6.0)
    parser.add_argument("--previous-until", type=float, default=8.5,
                        help="keep the first pattern as the previous instance until this time")
    args = parser.parse_args()

    record = load_pattern(args.eboot, args.selector)
    transition_record = (load_pattern(args.eboot, args.transition_selector)
                         if args.transition_selector is not None else None)
    os.makedirs(args.out, exist_ok=True)
    print(f"pattern {args.selector} '{record.embedded_name}' "
          f"0x{record.blob_vaddr:X} {record.byte_length} bytes")

    for frame in range(args.frames):
        time = args.start + frame / args.fps
        groups = []
        instances = []
        if transition_record is None or time < args.transition_at:
            instances.append((0, record, time))
        else:
            if time <= args.previous_until:
                instances.append((0, record, time))
            instances.append((1, transition_record, time - args.transition_at))

        for instance, instance_record, local_time in instances:
            state = pp.sample_resource_state(instance_record, local_time)
            for kind, compute_family, draw_family in (
                    (0, "small_compute", "small_draw"),
                    (1, "large_compute", "large_draw")):
                indices = sorted({i for (f, i) in state if f == compute_family})
                for index in indices:
                    compute = state.get((compute_family, index))
                    draw = state.get((draw_family, index))
                    if compute is None:
                        continue
                    if draw is None:
                        draw = bytes(pp.RESOURCE_SIZES[draw_family])
                    encoded_kind = kind | (instance << 8)
                    groups.append((encoded_kind, index, compute, draw))

        with open(os.path.join(args.out, f"{frame:05d}.bin"), "wb") as stream:
            stream.write(struct.pack("<4sIfI", b"PFRM", len(groups), time, 0))
            for kind, index, compute, draw in groups:
                stream.write(struct.pack("<IIII", kind, index, len(compute), len(draw)))
                stream.write(compute)
                stream.write(draw)

        if frame == 0 or frame == args.frames - 1:
            live = [(k, i, struct.unpack_from("<I", c, 0x28)[0])
                    for k, i, c, _ in groups]
            print(f"  frame {frame:5d} t={time:7.3f} groups={len(groups)} "
                  f"counts={[(k, i, n) for k, i, n in live if n]}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
