#!/bin/sh
# Dump particle_vv's clip-space output by patching a storage-buffer write into
# the translated SPIR-V. Diagnostic only.
set -e
S="$1"; BLOCKS="$2"
cd /Users/gera/Desktop/prosperismo
export DYLD_LIBRARY_PATH=/opt/homebrew/lib VK_ICD_FILENAMES=/opt/homebrew/etc/vulkan/icd.d/MoltenVK_icd.json
DRAW_SPIRV_OUT="$S/live" dotnet run --project frontend/ProsperismoShell/Prosperismo.Shell.BackgroundPoc -c Release -- \
  --eboot ps5oracle/fwdb/12.40/NPXS40087-eboot.bin --render-particles --blocks "$BLOCKS" --out "$S/ignore" --fps 30 >/dev/null 2>&1
spirv-dis "$S/live.vs.spv" > "$S/lvs.txt"
S="$S" python3 tools/patch_clip_probe.py
spirv-as --target-env vulkan1.2 "$S/lvs_probe.txt" -o "$S/lvs_probe.spv"
CLIP_OUT="$S/clip.bin" DEBUG_VS="$S/lvs_probe.spv" DEBUG_FS="$S/white.frag.spv" \
  dotnet run --project frontend/ProsperismoShell/Prosperismo.Shell.BackgroundPoc -c Release -- \
  --eboot ps5oracle/fwdb/12.40/NPXS40087-eboot.bin --render-particles --blocks "$BLOCKS" --out "$S/ignore" --fps 30 2>&1 | grep "frame 00009"
S="$S" python3 - <<'PY'
import struct, os
d=open(os.environ["S"]+"/clip.bin","rb").read()
for v in range(0,8):
    x,y,z,w=struct.unpack_from("<4f",d,v*16)
    n = f"  ndc=({x/w:9.5f},{y/w:9.5f},{z/w:9.5f})" if w else ""
    print(f"  vtx {v:3d}  x={x:12.6g} y={y:12.6g} z={z:12.6g} w={w:12.6g}{n}")
PY
