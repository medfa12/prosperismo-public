#!/usr/bin/env python3
# Copyright (C) 2026 Prosperismo Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Render the recovered PS5 cold-boot and its ambient continuation.

The visual schedule defaults are the cold-boot POC accepted on 2026-08-08:
Boot light until 6.5 s, coldboot particles transitioning at 8.5 s, and the
previous particle instance retained through 11 s.  The continuation is Sony's
selector-1 spread/Bottom field and the HomeScreen light preset.  Shader code,
particle pattern blocks, sprites, IES textures, and colour records all come
from NPXS40087's eboot; this script only orchestrates their existing replay.
"""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
EXPORTER = ROOT / "tools" / "export_particle_frames.py"
COLOUR_TOOL_DIR = ROOT / "tools"
PROJECT = (ROOT / "frontend" / "ProsperismoShell" /
           "Prosperismo.Shell.BackgroundPoc" /
           "Prosperismo.Shell.BackgroundPoc.csproj")
RENDERER = (ROOT / "frontend" / "ProsperismoShell" / "artifacts" /
            "bin" / "Release" / "net10.0" / "BackgroundPoc.dll")


def run(command: list[str], *, env: dict[str, str] | None = None) -> None:
    print("+", " ".join(command), flush=True)
    subprocess.run(command, cwd=ROOT, env=env, check=True)


def write_colour_records(eboot: Path, boot_path: Path, ambient_path: Path) -> None:
    sys.path.insert(0, str(COLOUR_TOOL_DIR))
    import dump_wave_colour_presets as colours  # noqa: PLC0415

    flat, have = colours.replay(eboot.read_bytes())
    for preset, destination in ((11, boot_path), (4, ambient_path)):
        base = preset * colours.RECORD
        record = bytes(flat[base:base + 0x7C])
        coverage = have[base:base + 0x7C]
        if not all(coverage):
            raise RuntimeError(f"preset {preset} is incomplete in the firmware seeder")
        destination.write_bytes(record)


def render_environment(boot_cb: Path, ambient_cb: Path,
                       particle_transition: float,
                       light_transition: float) -> dict[str, str]:
    env = os.environ.copy()
    molten = Path("/opt/homebrew/lib")
    icd = Path("/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json")
    if molten.exists():
        prior = env.get("DYLD_LIBRARY_PATH")
        env["DYLD_LIBRARY_PATH"] = f"{molten}:{prior}" if prior else str(molten)
    if icd.exists():
        env["VK_ICD_FILENAMES"] = str(icd)
    env.update({
        "PATTERN_TRANSITION_AT": f"{particle_transition:g}",
        "LIGHT_COLORCB": str(boot_cb),
        "LIGHT_COLORCB_AFTER": str(ambient_cb),
        "LIGHT_TRANSITION_AT": f"{light_transition:g}",
    })
    # LightLayerProbe now executes rect_uv_vv directly from the eboot. An old
    # caller-provided fullscreen shader must never leak into this render.
    env.pop("FULLSCREEN_VS", None)
    return env


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--eboot", required=True, type=Path)
    parser.add_argument("--out", type=Path,
                        default=ROOT / "out" /
                        "ps5-background-native-coldboot-ambient-720p.mp4")
    parser.add_argument("--width", type=int, default=1280)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--duration", type=float, default=18.0)
    parser.add_argument("--light-transition", type=float, default=6.5)
    parser.add_argument("--particle-transition", type=float, default=8.5)
    parser.add_argument("--previous-until", type=float, default=11.0)
    parser.add_argument("--work", type=Path,
                        help="persistent work directory for inspecting or resuming frames")
    parser.add_argument("--keep-work", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()

    eboot = args.eboot.resolve()
    if not eboot.is_file():
        parser.error(f"eboot not found: {eboot}")
    if args.width <= 0 or args.height <= 0 or args.fps <= 0 or args.duration <= 0:
        parser.error("width, height, fps, and duration must be positive")
    if not (args.light_transition <= args.particle_transition <= args.previous_until):
        parser.error("expected light-transition <= particle-transition <= previous-until")

    temporary = args.work is None
    work = (Path(tempfile.mkdtemp(prefix="prosperismo-native-coldboot-ambient-"))
            if temporary else args.work.resolve())
    blocks = work / "blocks"
    frames = work / "png"
    boot_cb = work / "boot-colorcb.bin"
    ambient_cb = work / "ambient-colorcb.bin"
    blocks.mkdir(parents=True, exist_ok=True)
    frames.mkdir(parents=True, exist_ok=True)
    frame_count = round(args.duration * args.fps)
    output = args.out.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    partial = output.with_suffix(".partial.mp4")

    try:
        write_colour_records(eboot, boot_cb, ambient_cb)
        run([
            sys.executable, str(EXPORTER), "--eboot", str(eboot),
            "--out", str(blocks), "--selector", "0",
            "--transition-selector", "1",
            "--transition-at", f"{args.particle_transition:g}",
            "--previous-until", f"{args.previous_until:g}",
            "--fps", f"{args.fps:g}", "--frames", str(frame_count),
        ])
        if not args.no_build:
            run(["dotnet", "build", str(PROJECT), "-c", "Release", "--no-restore"])
        run([
            "dotnet", str(RENDERER), "--eboot", str(eboot),
            "--render-particles", "--blocks", str(blocks),
            "--out", str(frames), "--width", str(args.width),
            "--height", str(args.height), "--fps", f"{args.fps:g}",
        ], env=render_environment(
            boot_cb, ambient_cb, args.particle_transition, args.light_transition))

        ffmpeg = shutil.which("ffmpeg")
        if ffmpeg is None:
            raise RuntimeError("ffmpeg is not installed")
        if partial.exists():
            partial.unlink()
        run([
            ffmpeg, "-hide_banner", "-y", "-framerate", f"{args.fps:g}",
            "-i", str(frames / "%05d.png"), "-c:v", "libx264",
            "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", str(partial),
        ])
        partial.replace(output)
        digest = hashlib.sha256(output.read_bytes()).hexdigest()
        print(f"output : {output}")
        print(f"sha256 : {digest}")
        print(f"work   : {work}")
    except BaseException:
        print(f"work preserved after failure: {work}", file=sys.stderr)
        raise
    else:
        if temporary and not args.keep_work:
            shutil.rmtree(work)
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
