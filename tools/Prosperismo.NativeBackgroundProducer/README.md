# Prosperismo native-background producer

Local development helper for the RNW two-slot background surface. It renders
the user's recovered BGLayer particle draw through SharpEmu's persistent Vulkan
renderer and publishes frames through `Local\ProsperismoShellBackground`.

No firmware shader, texture, draw-cache, or rendered frame is part of this
project. Point `ProsperismoRendererSourceRoot` at a checkout of the preserved
`codex/ps5-shell-integration` renderer and pass the oracle paths at runtime:

```powershell
dotnet build .\tools\Prosperismo.NativeBackgroundProducer `
  -p:ProsperismoRendererSourceRoot=C:\path\to\sharpemu-renderer

dotnet run --project .\tools\Prosperismo.NativeBackgroundProducer `
  -p:ProsperismoRendererSourceRoot=C:\path\to\sharpemu-renderer -- `
  --cache-root C:\path\to\native-small-bottom\draw-cache `
  --firmware-root C:\path\to\PS5_4.03_reconstructed
```

For the canonical migrated workspace the corresponding runtime inputs are:

```powershell
C:\dotnet\dotnet.exe `
  .\tools\Prosperismo.NativeBackgroundProducer\bin\Release\net10.0\Prosperismo.NativeBackgroundProducer.dll `
  --cache-root C:\prosperismo\ps5oracle\evidence\shell-rendering\native-small-bottom\draw-cache `
  --firmware-root C:\prosperismo\ps5oracle\sony\PS5_4.03_reconstructed
```

The explicit `C:\dotnet\dotnet.exe` matters on the current development machine:
the system-wide host exposes .NET 8 while this helper targets .NET 10. The
launcher still discovers the oracle from `PROSPERISMO_PS5_ORACLE` first and
then the canonical sibling `C:\prosperismo\ps5oracle`; no asset path is compiled
into the executable.

Add `--frame-limit 2` for a bounded renderer/protocol smoke test.

The producer publishes only the blue ripple/dust **overlay**. It subtracts the
renderer clear `(1,1,9)` and tags `FrameHeader.reserved0` with layer kind `2`.
This stream does not contain a persistent plate or room/ray base. The preserved
4.03 code proves Plane2 record 2 is blue, while the warm folded DDS candidates
are per-title hub artwork; neither is relabelled as the system Settings base.
The RNW consumer must use additive composition for these zero-alpha colour
deltas and hide this overlay in Settings. The room/ray owner remains an explicit
recovery gap rather than a host-authored substitute.

## Presentation-state contract

Frame transport and presentation state are deliberately separate. The shell
owns a 64-byte, versioned control mapping named
`Local\ProsperismoShellBackgroundControl` and signals
`Local\ProsperismoShellBackgroundControlChanged` after publishing a change.
The only valid layer masks are:

| Surface | Layer mask | Meaning |
| --- | ---: | --- |
| Home | `3` | persistent `FirstWaveBase` plus `ParticleOverlay` |
| Settings | `1` | persistent `FirstWaveBase`; particles suppressed |

There is intentionally no valid zero-layer state and no particle-only state.
That invariant prevents route changes from unmounting or blanking the native
FirstWave owner. The particle producer reads this mapping and suspends rendering
without advancing its recovered animation clock while Settings is active. If a
legacy shell does not expose the mapping, the producer defaults to Home for
backwards compatibility.

The control page layout is little-endian:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 8 | `PS5BGCT\0` |
| 8 | 4 | protocol version (`1`) |
| 12 | 4 | header bytes (`64`) |
| 16 | 4 | layer mask |
| 20 | 4 | reserved |
| 24 | 8 | seqlock counter |
| 32 | 8 | QPC timestamp |
| 40 | 24 | reserved |

The shell writer increments the aligned sequence to an odd value, writes the
mask and timestamp, increments it again to an even value with interlocked
publication, then signals the changed event. Readers accept only equal even
sequence values before and after copying the page. The native compositor must
also hide its retained particle visual immediately on mask `1`; producer
suspension alone cannot erase the last already-consumed overlay frame.

Run the asset-free contract test with:

```powershell
dotnet run --project .\tools\Prosperismo.NativeBackgroundProducer `
  -p:ProsperismoRendererSourceRoot=C:\path\to\sharpemu-renderer -- --self-test
```
