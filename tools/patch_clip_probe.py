import re, os
S = os.environ["S"]
src = open(f"{S}/lvs.txt").read()
have = set(re.findall(r"(%uint_\d+) = OpConstant %uint", src))
extra = "".join(f"\n {n} = OpConstant %uint {n.split('_')[1]}"
                for n in (f"%uint_{i}" for i in (0, 1, 2, 3, 4)) if n not in have)
src = src.replace("    %float_1 = OpConstant %float 1", "    %float_1 = OpConstant %float 1" + extra)
m = re.search(r"%\d+ = OpCompositeConstruct %v4float (%\d+) (%\d+) (%\d+) (%\d+)\n\s+%\d+ = OpLoad %bool", src)
comps = m.groups()
sel = re.search(r"(%\d+) = OpSelect %v4float %\d+ %\d+ %\d+\n\s+OpStore %gl_Position", src).group(1)
out, i = [], 9100
out.append(f"       %{i} = OpLoad %uint %gl_VertexIndex"); vid = i; i += 1
out.append(f"       %{i} = OpIMul %uint %{vid} %uint_4"); base = i; i += 1
for k, comp in enumerate(comps):
    out.append(f"       %{i} = OpIAdd %uint %{base} %uint_{k}"); idx = i; i += 1
    out.append(f"       %{i} = OpBitcast %uint {comp}"); val = i; i += 1
    out.append(f"       %{i} = OpAccessChain %_ptr_StorageBuffer_uint %guestBuffers %uint_2 %uint_0 %{idx}"); ptr = i; i += 1
    out.append(f"               OpStore %{ptr} %{val}")
src = src.replace("               OpStore %gl_Position " + sel,
                  "\n".join(out) + "\n               OpStore %gl_Position " + sel)
open(f"{S}/lvs_probe.txt", "w").write(src)
