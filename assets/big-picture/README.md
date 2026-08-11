# Big Picture runtime assets

This directory is the only repository location for Big Picture's narrow,
firmware-derived runtime dependencies. The ignored `ps5oracle` tree is an
extraction and reverse-engineering input; application code must not require it
at runtime.

Store files by firmware source version:

```text
assets/big-picture/
  3.00/
    manifest.json
    audio/*.wav
    background/plane2-records.bin
    control-center-icons/...
    transitions/ripple-p.spv
  3.20/
    manifest.json
    focus/*.spv
  12.40/
    manifest.json
    background/
      colors/...
      patterns/...
      shaders/...
      textures/...
    system-icons/...
    ui-sounds/*.wav
    ui3/*.png
    ui3-raster/*.png
```

The `3.00` directory is recovered directly from the paired installable
`PS5UPDATE1.PUP.dec` / `PS5UPDATE2.PUP.dec` update set. Its manifest pins both
outer-package hashes and the exact PUP segment used. A later-version runtime
asset must live in a separate version directory; do not relabel an older update
asset to match the firmware version whose code consumes it.

The `12.40/background` package is a narrow projection of NPXS40087: two
serialized pattern blobs, the exact shader spans consumed by the live
C#/Avalonia renderer, two embedded light textures, and two color buffers
materialized by replaying the executable seeder. It deliberately excludes the
complete eboot and reconstructs only the original file offsets in memory for
the existing audited decoders.

Every version directory must contain `manifest.json`. The manifest records the
firmware/update identity, extraction route, and maintained evidence once at the
top level. Each asset entry records:

- `path`: repository-relative packaged path, or a path relative to an explicit
  top-level `path_base`;
- `source_path`: exact path inside the firmware/oracle input;
- `sha256` and byte `size`;
- `consumers`: runtime source files that load it or own its integration; and
- `planned_consumers` when a bounded recovery slice intentionally precedes the
  owning integration group.

Copy only files actually consumed by the application. Console captures, Figma
references, complete firmware trees, decompiler databases, and intermediate
frame caches do not belong here. Never rename a firmware asset without retaining
its original name in the manifest.

The shell audio files are decoded from the recovered AT9 inputs into stereo
PCM16 WAVE at 48 kHz using the same 5.1 downmix coefficients as the original
runtime decoder. `audio/loops.json` preserves the source `smpl` boundaries so
Home and onboarding keep their authored intros and loop bodies. This changes
the application-facing container, not the ownership of the underlying
recording.

The packaged particle textures are lossless RGBA8 PNG derivatives. The source
BC7 pixels are decoded, recoloured to cyan/violet/magenta, and transcoded by
`tools/shell-recovery/GnfPrismRecolor`; the two 12.40 light textures are
untiled from R8 and transcoded the same way. Only the 32-byte Gen5 descriptor
templates needed to translate the recovered shader ABI remain as separate
`.bin` metadata. GNF containers are extraction inputs, not shipped runtime
assets. The manifests record source, intermediate, PNG, and decoded-pixel
hashes where applicable.

The `12.40/ui3` files are named PNG derivatives of the small UI3 RCO entries
used by the shell's button, menu, dialog, switch, progress, busy-indicator,
and focus readers. Product runtime reads this package directly; explicit RCO
paths are retained only for extraction tests and recovery tooling. The bounded
`profile-icons`, `utility-icons`, and `system-icons` directories contain only
the SVG ids consumed by shipped standalone surfaces. `ui3-raster` closes the
keyguide/emoji bitmap set, while `ui-sounds` closes all 26 interaction cues.
The complete UI3 registry remains research data and is not a shipping runtime
dependency.

The 3.20 focus directory contains four exact embedded shader ELFs, not the
complete `libScePsm.sprx`. The 3.00 transition and Plane2 files follow the same
narrow-slice rule. Together with the packaged title fallback, these make both
launcher presentations independent of a firmware dump or `ps5oracle` checkout.
