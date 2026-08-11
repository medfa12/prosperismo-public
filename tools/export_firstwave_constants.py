#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Build the FirstWave constant buffer from the recovered host constants.

Every word comes from
docs/sony-shell/evidence/firstwave-host-constants-12.40.json, which was
captured by executing NPXS40087's own constant-buffer builder at VA 0x000c5d00
through its 0x19c-byte upload boundary. Nothing here is authored: this script
only lays the recovered bit patterns out at the offsets the shaders read, and
advances `time`.

The layout is the one in the evidence file's `constantBuffer.fields`, which
matches the offsets fw_background_p, fw_oit_p and fw_flow_vl load.
"""
from __future__ import annotations

import argparse
import json
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EVIDENCE = os.path.join(
    ROOT, "docs", "sony-shell", "evidence", "firstwave-host-constants-12.40.json")

SIZE = 412


def build(evidence: dict, time: float, palette_index: int) -> bytes:
    fields = {k: int(v, 16) for k, v in evidence["constantBuffer"]["fields"].items()}
    reset = evidence["resetHostUpload"]
    buffer = bytearray(SIZE)

    def put_words(offset: int, words: list[str]) -> None:
        for i, word in enumerate(words):
            struct.pack_into("<I", buffer, offset + i * 4, int(word, 16))

    for name in ("worldViewMatrix", "worldProjectionMatrix",
                 "worldViewProjectionMatrix", "worldMatrix"):
        rows = reset[name]["bits"]
        flat = [w for row in rows for w in row]
        put_words(fields[name], flat)

    put_words(fields["cameraPosition"], reset["cameraPosition"]["bits"])

    palette = next(p for p in evidence["palettes"] if p["index"] == palette_index)
    for field in palette["fields"]:
        put_words(fields[field["name"]], field["bits"])

    inputs = reset["objectResetInputs"]
    struct.pack_into("<f", buffer, fields["opacity"], 1.0)
    struct.pack_into("<f", buffer, fields["time"], time)
    struct.pack_into("<f", buffer, fields["waveOpacity"], float(inputs["waveOpacity"]))
    struct.pack_into("<f", buffer, fields["oitSliceOffset"], 0.0)
    put_words(fields["screenDim"], reset["screenDimWords"])
    return bytes(buffer)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", required=True)
    parser.add_argument("--start", type=float, default=0.0)
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--frames", type=int, default=1)
    parser.add_argument("--palette", type=int, default=None)
    args = parser.parse_args()

    evidence = json.load(open(EVIDENCE))
    palette_index = (args.palette if args.palette is not None
                     else evidence["paletteTransition"]["selectedResetPalette"])
    os.makedirs(args.out, exist_ok=True)

    render = evidence["resetHostUpload"]["renderSize"]
    print(f"palette record {palette_index}, native render "
          f"{render['width']}x{render['height']}, {SIZE}-byte buffer")

    for frame in range(args.frames):
        time = args.start + frame / args.fps
        with open(os.path.join(args.out, f"{frame:05d}.bin"), "wb") as stream:
            stream.write(build(evidence, time, palette_index))

    print(f"wrote {args.frames} frame(s) to {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
