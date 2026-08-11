# Prosperismo

<p align="center">
  <img src="assets/branding/ps-iOS-Dark-1024@1x.png" width="160" alt="Prosperismo icon">
</p>

Prosperismo is a free and open-source PlayStation 5 emulator written in C++. It
began as a fork of [Kyty](https://github.com/InoriRus/Kyty) and has since added
its own compatibility work, Vulkan renderer changes, and desktop shell.

It is early software: game compatibility is limited and behaviour can change
between builds. Prosperismo is not affiliated with Sony Interactive
Entertainment or PlayStation, and users must supply only content they are
legally entitled to use.

## User interface

The active interface is the Avalonia shell in
[`frontend/ProsperismoShell`](frontend/ProsperismoShell). It packages two routes
in one `Prosperismo` application:

- **Desktop** for managing and launching games.
- **Big Picture** for a controller-first interface.

The native C++ emulator is built as the sibling `prosperismo_emulator` backend.
[`frontend/ProsperismoLauncher`](frontend/ProsperismoLauncher) is a retained
React Native for Windows migration prototype; it is not built or released by
the current build pipeline.

## Build

Clone with the required submodules:

```bash
git clone --recurse-submodules https://github.com/medfa12/prosperismo-public.git
cd prosperismo-public
```

The build requires Git, Python 3, CMake, Ninja, Clang, .NET SDK 10, and
`glslangValidator`.

### Windows

Use a Visual Studio developer shell with the Desktop C++ workload and Clang
tools installed, then run:

```powershell
python scripts/build_release.py --platform windows
```

### Linux

Install Clang, CMake, Ninja, `glslang-tools`, and the SDL2 development packages
for OpenGL, X11, Wayland, ALSA, PulseAudio, udev, and D-Bus, then run:

```bash
python3 scripts/build_release.py --platform linux
```

### macOS

macOS builds target x86-64 and run on Apple Silicon through Rosetta 2. Install
Xcode (or its command-line tools), CMake, Ninja, and glslang, then run:

```bash
python3 scripts/build_release.py --platform macos
```

Each command builds the emulator and Avalonia application, runs the selected
tests, and stages a portable payload in `_Build/<platform>/install`.

## License and third-party assets

Prosperismo is licensed under [GPL-2.0-only](LICENSE). Some bundled interface
assets have their own status notice in
[`LICENSES/LicenseRef-PlayStation-UI-Assets.txt`](LICENSES/LicenseRef-PlayStation-UI-Assets.txt);
review it before redistributing those assets.
