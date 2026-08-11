#!/usr/bin/env python3
"""Build, test, and stage the active Prosperismo release payload.

The release is deliberately limited to the C++ emulator and the Avalonia shell.
The shell's desktop launcher and Big Picture routes live in one apphost and are
both staged beside the same native backend.
"""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile


ROOT = Path(__file__).resolve().parents[1]
SHELL = ROOT / "frontend" / "ProsperismoShell"
SOLUTION = SHELL / "Prosperismo.Shell.slnx"
TEST_PROJECT = SHELL / "Prosperismo.Shell.Runtime.Tests" / "Prosperismo.Shell.Runtime.Tests.csproj"
APP_PROJECT = SHELL / "Prosperismo.Shell.App" / "Prosperismo.Shell.App.csproj"

MOLTENVK_VERSION = "v1.4.2"
MOLTENVK_SHA256 = "f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e"
MOLTENVK_URL = (
    "https://github.com/KhronosGroup/MoltenVK/releases/download/"
    f"{MOLTENVK_VERSION}/MoltenVK-macos.tar"
)
MOLTENVK_LIBRARY_MEMBER = "MoltenVK/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib"
MOLTENVK_LICENSE_MEMBER = "MoltenVK/LICENSE"

PLATFORMS = {
    "windows": {
        "rid": "win-x64",
        "exe": "prosperismo_emulator.exe",
        "app": "Prosperismo.exe",
        "cmake": ["-DCMAKE_C_COMPILER=clang-cl", "-DCMAKE_CXX_COMPILER=clang-cl"],
        "native_targets": [
            "prosperismo_emulator", "cli_options_tests", "audio_out2_port_tests",
        ],
        "native_tests": "^(cli_options|audio_out2_port)$",
    },
    "linux": {
        "rid": "linux-x64",
        "exe": "prosperismo_emulator",
        "app": "Prosperismo",
        "cmake": ["-DCMAKE_C_COMPILER=clang", "-DCMAKE_CXX_COMPILER=clang++"],
        "native_targets": [
            "prosperismo_emulator", "cli_options_tests", "page_manager_tests",
            "memory_tracker_tests", "audio_out2_port_tests", "virtual_memory_allocation_tests",
        ],
        "native_tests": "^(cli_options|page_manager|memory_tracker|audio_out2_port|virtual_memory_allocation)$",
    },
    "macos": {
        "rid": "osx-x64",
        "exe": "prosperismo_emulator",
        "app": "Prosperismo",
        "cmake": [
            "-DCMAKE_OSX_ARCHITECTURES=x86_64",
            "-DCMAKE_C_COMPILER=clang",
            "-DCMAKE_CXX_COMPILER=clang++",
        ],
        "native_targets": [
            "prosperismo_emulator", "cli_options_tests", "audio_out2_port_tests",
            "virtual_memory_allocation_tests",
        ],
        "native_tests": "^(cli_options|audio_out2_port|virtual_memory_allocation)$",
    },
}

REQUIRED_PACKAGE_ASSETS = (
    Path("assets/big-picture/3.00/audio/bgm_home.wav"),
    Path("assets/big-picture/3.00/audio/sfx_coldboot.wav"),
    Path("assets/big-picture/3.00/textures/Sce.Vsh.ShellUI.BGLayer.Particle0.png"),
    Path("assets/big-picture/3.00/textures/Sce.Vsh.ShellUI.BGLayer.Particle1.png"),
    Path("assets/big-picture/3.00/descriptors/bglayer-particle.bin"),
    Path("assets/big-picture/3.00/textures/tex_default_game.png"),
    Path("assets/big-picture/3.00/background/plane2-records.bin"),
    Path("assets/big-picture/3.00/transitions/ripple-p.spv"),
    Path("assets/big-picture/3.20/focus/area-vv.spv"),
    Path("assets/big-picture/3.20/focus/line-vv.spv"),
    Path("assets/big-picture/3.20/focus/area-p.spv"),
    Path("assets/big-picture/3.20/focus/line-p.spv"),
    Path("assets/big-picture/12.40/ui3/button-base.png"),
    Path("assets/big-picture/12.40/ui3/focus-noise.png"),
    Path("assets/big-picture/12.40/ui-sounds/snd_focus_move.wav"),
    Path("assets/big-picture/12.40/ui3-raster/image_keyguide_cross.png"),
    Path("assets/big-picture/12.40/background/textures/light-floor.png"),
    Path("assets/big-picture/12.40/background/textures/light-volume.png"),
    Path("assets/big-picture/12.40/background/descriptors/light-texture.bin"),
)

REQUIRED_ASSET_DIRECTORY_COUNTS = {
    Path("assets/big-picture/12.40/ui-sounds"): 26,
    Path("assets/big-picture/12.40/ui3-raster"): 20,
    Path("assets/big-picture/3.20/focus"): 4,
}


def run(command: list[str]) -> None:
    print("+", subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def run_with_log(command: list[str], log_path: Path) -> None:
    print("+", subprocess.list2cmdline(command), flush=True)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    with log_path.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(
            command,
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )
        assert process.stdout is not None
        for line in process.stdout:
            sys.stdout.write(line)
            log.write(line)
        if process.wait() != 0:
            raise subprocess.CalledProcessError(process.returncode, command)


def verify_linux_sdl_backends(configure_log: Path) -> None:
    text = configure_log.read_text(encoding="utf-8")
    missing = [
        feature
        for feature in ("SDL_ALSA", "SDL_PULSEAUDIO", "SDL_WAYLAND", "SDL_X11", "SDL_LIBUDEV", "SDL_DBUS")
        if not re.search(rf"--\s+{re.escape(feature)}\s+\(Wanted: ON\): ON", text)
    ]
    if missing:
        raise RuntimeError(f"Linux SDL backends are disabled: {', '.join(missing)}")


def verify_package(package_dir: Path, platform: str) -> None:
    details = PLATFORMS[platform]
    required = [
        package_dir / details["app"],
        package_dir / details["exe"],
        *(package_dir / asset for asset in REQUIRED_PACKAGE_ASSETS),
    ]
    missing = [path for path in required if not path.is_file()]
    if missing:
        formatted = "\n  ".join(str(path.relative_to(ROOT)) for path in missing)
        raise RuntimeError(f"Release payload is missing required files:\n  {formatted}")

    for relative_directory, expected_count in REQUIRED_ASSET_DIRECTORY_COUNTS.items():
        directory = package_dir / relative_directory
        actual_count = sum(1 for path in directory.iterdir() if path.is_file()) \
            if directory.is_dir() else 0
        if actual_count != expected_count:
            raise RuntimeError(
                f"Release payload has {actual_count} files in {relative_directory}; "
                f"expected {expected_count}")

    if platform != "windows":
        for path in required[:2]:
            if not os.access(path, os.X_OK):
                raise RuntimeError(f"Release executable is not marked executable: {path.relative_to(ROOT)}")

    if platform == "macos":
        moltenvk = package_dir / "libMoltenVK.dylib"
        license_path = package_dir / "LICENSE.MoltenVK"
        missing_runtime = [path for path in (moltenvk, license_path) if not path.is_file()]
        if missing_runtime:
            formatted = "\n  ".join(str(path.relative_to(ROOT)) for path in missing_runtime)
            raise RuntimeError(f"macOS package is missing its Vulkan runtime:\n  {formatted}")
        run(["lipo", str(moltenvk), "-verify_arch", "x86_64"])
        run(["codesign", "--verify", "--strict", str(moltenvk)])


def stage_moltenvk(package_dir: Path) -> None:
    """Download, verify, and stage the pinned universal macOS Vulkan runtime."""
    dependency_dir = ROOT / "_Build" / "dependencies"
    archive = dependency_dir / f"MoltenVK-{MOLTENVK_VERSION}-macos.tar"
    dependency_dir.mkdir(parents=True, exist_ok=True)

    if not archive.is_file() or hashlib.sha256(archive.read_bytes()).hexdigest() != MOLTENVK_SHA256:
        print(f"+ download {MOLTENVK_URL}", flush=True)
        temporary = archive.with_suffix(".download")
        run([
            "curl", "--fail", "--location", "--retry", "3",
            "--output", str(temporary), MOLTENVK_URL,
        ])
        digest = hashlib.sha256(temporary.read_bytes()).hexdigest()
        if digest != MOLTENVK_SHA256:
            temporary.unlink(missing_ok=True)
            raise RuntimeError(
                f"MoltenVK archive hash mismatch: expected {MOLTENVK_SHA256}, got {digest}")
        temporary.replace(archive)

    package_dir.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive) as source:
        members = {
            MOLTENVK_LIBRARY_MEMBER: package_dir / "libMoltenVK.dylib",
            MOLTENVK_LICENSE_MEMBER: package_dir / "LICENSE.MoltenVK",
        }
        for member_name, destination in members.items():
            extracted = source.extractfile(member_name)
            if extracted is None:
                raise RuntimeError(f"MoltenVK archive is missing {member_name}")
            with extracted, destination.open("wb") as output:
                shutil.copyfileobj(extracted, output)

    (package_dir / "libMoltenVK.dylib").chmod(0o755)
    run(["codesign", "--force", "--sign", "-", "--timestamp=none",
         str(package_dir / "libMoltenVK.dylib")])


def build(platform: str) -> None:
    details = PLATFORMS[platform]
    build_dir = ROOT / "_Build" / platform
    package_dir = build_dir / "install"
    dotnet_properties = (
        ["-p:Configuration=Release", "-p:Platform=Any CPU"]
        if platform == "windows"
        else []
    )

    run_with_log([
        "cmake", "-S", "src", "-B", str(build_dir), "-G", "Ninja",
        "-DCMAKE_BUILD_TYPE=Release", *details["cmake"],
    ], build_dir / "configure.log")
    if platform == "linux":
        verify_linux_sdl_backends(build_dir / "configure.log")
    run([
        "cmake", "--build", str(build_dir), "--target", *details["native_targets"], "--parallel",
    ])
    run([
        "ctest", "--test-dir", str(build_dir), "--output-on-failure",
        "--tests-regex", details["native_tests"],
    ])
    run(["cmake", "--install", str(build_dir), "--prefix", str(package_dir)])

    run(["dotnet", "restore", str(SOLUTION), *dotnet_properties])
    run([
        "dotnet", "build", str(SOLUTION), "-c", "Release", "--no-restore",
        *dotnet_properties,
    ])
    run([
        "dotnet", "test", str(TEST_PROJECT), "-c", "Release", "--no-build", "--no-restore",
        *dotnet_properties,
    ])
    run([
        "dotnet", "publish", str(APP_PROJECT), "-c", "Release", "-r", details["rid"],
        "--self-contained", "true", "--no-restore", "--output", str(package_dir),
        *dotnet_properties,
    ])
    if platform == "macos":
        stage_moltenvk(package_dir)
    verify_package(package_dir, platform)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=PLATFORMS, required=True)
    parser.add_argument(
        "--verify-package-only",
        action="store_true",
        help="Verify a previously staged payload without rebuilding it.",
    )
    args = parser.parse_args()

    package_dir = ROOT / "_Build" / args.platform / "install"
    if args.verify_package_only:
        verify_package(package_dir, args.platform)
    else:
        build(args.platform)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (subprocess.CalledProcessError, RuntimeError) as error:
        print(f"build_release.py: {error}", file=sys.stderr)
        raise SystemExit(1)
