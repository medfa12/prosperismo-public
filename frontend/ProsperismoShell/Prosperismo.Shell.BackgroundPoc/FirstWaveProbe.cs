// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Decodes the FirstWave background stages straight out of an eboot.
///
/// <para>Unlike the ripple path these are <em>raw</em> shader text, not ELF
/// images: each begins at an <c>s_inst_prefetch</c> and runs to
/// <c>s_endpgm</c>, so there is no <c>.shader_text</c> section to find. The
/// slices come from
/// particle programs located by opcode profile.</para>
///
/// <para>This answers one question only: does <see cref="Gen5ShaderTranslator"/>
/// it, so it is worth failing fast and loudly.</para>
/// </summary>
internal static class FirstWaveProbe
{
    private const ulong ProgramAddress = 0x0010_0000;

    internal readonly record struct Slice(string Name, long Offset, int Length, string Role);

    /// <summary>The 12.40 background stages, in pipeline order where known.</summary>
    internal static readonly Slice[] Stages =
    [
        new("fw_background_p", 0x11F9300, 0x230, "pixel: dark-room base plate"),
        new("fw_flow_vl", 0x11F6900, 0x72C, "vertex/local: wave control points"),
        new("fw_flow_h", 0x11F6600, 0x108, "hull: tessellation factors"),
        new("fw_flow_dv", 0x11F5500, 0xF68, "domain: folded-wave surface"),
        new("fw_oit_p", 0x11F7200, 0xC84, "pixel: OIT node capture"),
        new("fw_comp_oit_p", 0x11F8100, 0x3D0, "pixel: OIT resolve"),
        new("fw_blur_vv", 0x11F5200, 0xCC, "vertex: fullscreen blur geometry"),
        new("fw_blurh_p", 0x11F4800, 0x35C, "pixel: horizontal blur"),
        new("fw_blurv_p", 0x11F4D00, 0x35C, "pixel: vertical blur"),
        new("fw_fxaa_p", 0x11F8700, 0xA00, "pixel: FXAA resolve"),
        new("rect_uv_vv", 0x11EEE00, 0xCC, "vertex: light compositor fullscreen rectangle"),

        // Located by opcode profile, not by name table: particle_c is the only
        // program in the region with buffer traffic and no image reads, and it
        // loads the particle count from +0x28 exactly as ResourcesCs stores it.
        new("particle_c", 0x11FA100, 0x71A4, "compute: particle simulation"),

        // revision of this table put these names on 0x11F0400/0x11F1500/
        // 0x11F2600/0x11F2E00, which are a different family of pixel programs;
        // that labelling was a guess from opcode profile and was wrong.
        new("particle_p", 0x1201500, 0x61C, "pixel: procedural point, 7-entry palette"),
        new("particle_vv", 0x1201D00, 0x4FC, "vertex: small-point billboard"),
        new("large_particle_p", 0x1202400, 0x5E4, "pixel: textured soft disc"),
        new("large_particle_vv", 0x1202C00, 0x460, "vertex: large-disc billboard"),
    ];

    internal static int Run(string eboot)
    {
        var image = File.ReadAllBytes(eboot);
        Console.WriteLine($"eboot  : {eboot} ({image.Length:N0} bytes)");
        Console.WriteLine();
        Console.WriteLine($"{"stage",-20} {"offset",-11} {"bytes",-8} {"instr",-7} result");

        var ok = 0;
        foreach (var slice in Stages)
        {
            var text = new byte[slice.Length];
            Array.Copy(image, slice.Offset, text, 0, slice.Length);

            var memory = new FlatMemory();
            memory.AddRegion(ProgramAddress, text);
            var context = new CpuContext(memory, Generation.Gen5);

            string result;
            var instructions = 0;
            if (Gen5ShaderTranslator.TryDecodeProgram(
                    context, ProgramAddress, out var decoded, out var error))
            {
                instructions = decoded.Instructions.Count;
                result = "DECODED";
                ok++;
            }
            else
            {
                result = $"FAILED  {error}";
            }

            Console.WriteLine(
                $"{slice.Name,-20} 0x{slice.Offset:X8}  0x{slice.Length,-6:X} " +
                $"{instructions,-7:N0} {result}");
        }

        Console.WriteLine();
        Console.WriteLine($"{ok}/{Stages.Length} stages decoded");
        return ok == Stages.Length ? 0 : 1;
    }

    /// <summary>
    /// Dumps every scalar-memory read a stage performs, plus its float
    /// immediates.
    ///
    /// <para>The offsets are the stage's own view of its resource block, so
    /// about the host structure. The immediates are the tuning constants: they
    /// live in the instruction stream, which is why searching the CPU side for
    /// them never worked.</para>
    /// </summary>
    internal static int Dump(string eboot, string stageName)
    {
        var slice = Array.Find(Stages, s => s.Name.TrimEnd('?') == stageName.TrimEnd('?'));
        if (slice.Name is null)
        {
            Console.Error.WriteLine($"unknown stage '{stageName}'");
            return 2;
        }

        var image = File.ReadAllBytes(eboot);
        var text = new byte[slice.Length];
        Array.Copy(image, slice.Offset, text, 0, slice.Length);
        var memory = new FlatMemory();
        memory.AddRegion(ProgramAddress, text);
        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var decoded, out var error))
        {
            Console.Error.WriteLine($"decode failed: {error}");
            return 1;
        }

        Console.WriteLine($"{slice.Name}  0x{slice.Offset:X}  {decoded.Instructions.Count:N0} instructions");
        Console.WriteLine();
        Console.WriteLine("-- prologue --");
        var dumpCount = int.TryParse(Environment.GetEnvironmentVariable("DUMP_INSTRUCTIONS"), out var dc) ? dc : 56;
        foreach (var instruction in decoded.Instructions.Take(dumpCount))
        {
            var destinations = string.Join(", ", instruction.Destinations.Select(d => d.ToString()));
            var operands = string.Join(", ", instruction.Sources.Select(s => s.ToString()));
            Console.WriteLine(
                $"  pc=0x{instruction.Pc:X4} {instruction.Opcode,-26} " +
                $"{(destinations.Length == 0 ? "-" : destinations),-16} <- " +
                $"{operands}{DescribeInstructionControl(instruction.Control)}");
        }

        Console.WriteLine();
        Console.WriteLine("-- scalar / buffer memory access (resource block layout) --");
        foreach (var instruction in decoded.Instructions)
        {
            if (instruction.Encoding is not (Gen5ShaderEncoding.Smem or
                Gen5ShaderEncoding.Smrd or Gen5ShaderEncoding.Mubuf))
            {
                continue;
            }

            var destinations = string.Join(", ", instruction.Destinations.Select(d => d.ToString()));
            var operands = string.Join(", ", instruction.Sources.Select(s => s.ToString()));
            Console.WriteLine(
                $"  pc=0x{instruction.Pc:X4} {instruction.Opcode,-26} " +
                $"{(destinations.Length == 0 ? "-" : destinations),-12} <- " +
                $"{operands}{DescribeInstructionControl(instruction.Control)}");
        }

        Console.WriteLine();
        Console.WriteLine("-- literal constants, by first use --");

        // Only true LiteralConstant operands: reinterpreting every trailing
        // word as a float invents values out of operand encodings.
        var seen = new Dictionary<uint, uint>();
        foreach (var instruction in decoded.Instructions)
        {
            foreach (var source in instruction.Sources)
            {
                if (source.Kind == Gen5OperandKind.LiteralConstant)
                {
                    seen.TryAdd(source.Value, instruction.Pc);
                }
            }
        }

        foreach (var (bits, pc) in seen.OrderBy(static e => e.Value))
        {
            var asFloat = BitConverter.UInt32BitsToSingle(bits);
            var floatText = float.IsFinite(asFloat) && Math.Abs(asFloat) is 0f or (> 1e-12f and < 1e12f)
                ? $"{asFloat:G9}"
                : "-";
            Console.WriteLine($"  pc=0x{pc:X4}  0x{bits:X8}  {floatText,-16} (u32 {bits})");
        }

        Console.WriteLine();
        Console.WriteLine($"{seen.Count} distinct literals");
        return 0;
    }

    private static string DescribeInstructionControl(Gen5InstructionControl? control) => control switch
    {
        Gen5BufferMemoryControl buffer =>
            $" offset={buffer.OffsetBytes}" +
            $" idxen={(buffer.IndexEnabled ? 1 : 0)}" +
            $" offen={(buffer.OffsetEnabled ? 1 : 0)}",
        Gen5ScalarMemoryControl scalar =>
            $" offset={scalar.ImmediateOffsetBytes}" +
            (scalar.DynamicOffsetRegister is uint register ? $" +s{register}" : string.Empty),
        Gen5DataShareControl dataShare =>
            $" offset0={dataShare.Offset0} offset1={dataShare.Offset1}" +
            $" gds={(dataShare.Gds ? 1 : 0)}",
        _ => string.Empty,
    };

    /// <summary>
    /// Compiles <c>particle_c</c> to SPIR-V.
    ///
    /// <para>The shader reaches its data through two indirections: a constant
    /// buffer described by a V# in user data holds a pointer to
    /// <c>ResourcesCs</c>, and <c>ResourcesCs</c> is a table of further V#
    /// descriptors. The scalar evaluator chases that chain, so the whole
    /// structure has to exist in memory before translation can resolve a single
    /// binding. Offsets are the ones the shader itself loads from — see
    /// </summary>
    internal static int Compile(string eboot)
    {
        const ulong srtAddress = 0x0200_0000;
        const ulong resourcesAddress = 0x0300_0000;
        const ulong propertyBufferAddress = 0x0400_0000;
        const ulong idBufferAddress = 0x0500_0000;

        // 6000 records of 0x44 bytes: the allocator at 0xE02AB asks for
        // 0x1770 * 0x44 and hands the result to memset(0). See
        const int recordStride = 0x44;
        const int recordCount = 0x1770;

        var waveLanes = uint.TryParse(Environment.GetEnvironmentVariable("WAVE_LANES"), out var w) ? w : 64u;

        // transPatternFlag: SRTCs + 0x18 packs the previous pattern index in
        // bits 7:4 and the current one in bits 3:0, and pc=0x00B8 retires every
        // lane whose record's own transPatternFlag nibble differs.
        var pattern = uint.TryParse(Environment.GetEnvironmentVariable("PATTERN"), out var p) ? p : 0u;
        var options = ParseU32(Environment.GetEnvironmentVariable("PARTICLE_OPTIONS"), 0x1101u);
        var numParticles = uint.TryParse(Environment.GetEnvironmentVariable("NUM_PARTICLES"), out var n)
            ? n
            : (uint)recordCount;
        var time = float.TryParse(Environment.GetEnvironmentVariable("SIM_TIME"), out var st) ? st : 0f;

        var image = File.ReadAllBytes(eboot);
        var slice = Array.Find(Stages, s => s.Name.StartsWith("particle_c", StringComparison.Ordinal));
        var text = new byte[slice.Length];
        Array.Copy(image, slice.Offset, text, 0, slice.Length);

        // BackgroundLayer::SRTCs. +0x00 is the ResourcesCs pointer the shader
        // dereferences at pc=0x000C; the rest are the per-frame scalars.
        var srt = new byte[256];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), resourcesAddress);
        BitConverter.TryWriteBytes(srt.AsSpan(0x08, 4), time);          // time
        BitConverter.TryWriteBytes(srt.AsSpan(0x0C, 4), 1f / 60f);      // timeStep
        BitConverter.TryWriteBytes(srt.AsSpan(0x10, 4), 1f);            // timeRateForLifeCountDown
        BitConverter.TryWriteBytes(srt.AsSpan(0x14, 4), 0u);            // isPreSimulation
        BitConverter.TryWriteBytes(srt.AsSpan(0x18, 4), (pattern << 4) | pattern);

        // BackgroundLayer::ResourcesCs. Exactly two of its dwordx4 loads are
        // buffer descriptors; every other wide load is a run of scalars. The
        // names come from the reflection string table at 0x1126160 and the
        // widths from the shader's own s_load offsets.
        var resources = new byte[0x100];

        // replayed at an authored time. Everything from +0x20 up is authored
        // data, so it is laid down first and only the two descriptors — which
        // the blob never touches, because they are runtime allocations — are
        // written over it.
        var authored = Environment.GetEnvironmentVariable("RESOURCES_BIN");
        if (!string.IsNullOrEmpty(authored) && File.Exists(authored))
        {
            var block = File.ReadAllBytes(authored);
            block.AsSpan(0, Math.Min(block.Length, resources.Length)).CopyTo(resources);
            Console.WriteLine($"resources: {block.Length} authored bytes from {Path.GetFileName(authored)}");
        }

        WriteBufferDescriptor(resources.AsSpan(0x00, 16), idBufferAddress, 4, recordCount);
        WriteBufferDescriptor(resources.AsSpan(0x10, 16), propertyBufferAddress, recordStride, recordCount);

        if (string.IsNullOrEmpty(authored))
        {
            BitConverter.TryWriteBytes(resources.AsSpan(0x20, 4), options);        // particleOptions
            BitConverter.TryWriteBytes(resources.AsSpan(0x24, 4), 0u);             // randSeed
            BitConverter.TryWriteBytes(resources.AsSpan(0x28, 4), numParticles);   // numParticles
            BitConverter.TryWriteBytes(resources.AsSpan(0x2C, 4), (uint)recordCount); // maxParticleId
            BitConverter.TryWriteBytes(resources.AsSpan(0x30, 4), 0u);             // offsetParticle
            BitConverter.TryWriteBytes(resources.AsSpan(0x34, 4), 1u);             // indexStridePerParticle
        }
        else
        {
            numParticles = BitConverter.ToUInt32(resources, 0x28);
        }

        Console.WriteLine(
            $"resources: particleOptions=0x{BitConverter.ToUInt32(resources, 0x20):X} " +
            $"numParticles={BitConverter.ToUInt32(resources, 0x28)} " +
            $"maxParticleId={BitConverter.ToUInt32(resources, 0x2C)} " +
            $"offsetParticle={BitConverter.ToUInt32(resources, 0x30)} " +
            $"stride={BitConverter.ToUInt32(resources, 0x34)} " +
            $"life={BitConverter.ToSingle(resources, 0x38)}..{BitConverter.ToSingle(resources, 0x3C)}");

        var memory = new FlatMemory();
        memory.AddRegion(ProgramAddress, text);
        memory.AddRegion(srtAddress, srt);
        memory.AddRegion(resourcesAddress, resources);
        memory.AddRegion(propertyBufferAddress, new byte[recordStride * recordCount]);
        memory.AddRegion(idBufferAddress, BuildParticleIds(recordCount));

        const ulong constantBufferAddress = srtAddress;
        const int particleStride = recordStride;
        var particleCount = (int)numParticles;
        var constantBuffer = srt;

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var decoded, out var error))
        {
            Console.Error.WriteLine($"decode : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"decode : OK - {decoded.Instructions.Count:N0} instructions");

        // s[0:3] is the constant buffer the shader dereferences at offset 0.
        // Exactly four: the evaluator seeds scalarRegisters[base + i] for every
        // entry, so a 32-slot array pins s4..s31 to zero — and pc=0x0004 reads
        // s4 as the workgroup id. Oversizing it silently collapses every
        // thread index to zero and the bounds test retires the dispatch.
        var userData = new uint[4];
        WriteBufferDescriptor(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
            constantBufferAddress,
            constantBuffer.Length);

        // s4 is the first system SGPR after the four user-data words, and
        // pc=0x0004 does v1 = (s4 << 6) + v0 — the workgroup id. Leaving
        // ComputeSystemRegisters null pins s4 to a static zero, so every
        // workgroup collapses onto records 0..63.
        var state = new Gen5ShaderState(
            decoded,
            userData,
            Metadata: null,
            ComputeSystemRegisters: new Gen5ComputeSystemRegisters(4, null, null, null),
            UserDataScalarRegisterBase: 0,
            ProgramResource1: 0x0000_0090);

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            Console.Error.WriteLine($"evaluate: FAILED {error}");
            return 1;
        }

        Console.WriteLine(
            $"evaluate: OK - {evaluation.GlobalMemoryBindings.Count} buffer binding(s), " +
            $"{evaluation.ImageBindings.Count} image binding(s)");
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.WriteLine($"         buffer len={binding.DataLength:N0}");
        }

        if (!Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                64,
                1,
                1,
                out var compiled,
                out error,
                // pc=0x0004 shifts the workgroup id left by 6, so the group is
                // 64 threads, and the prologue manipulates exec with
                // s_mov_b64/s_and_b64. This is a wave64 program; translating it
                // as wave32 mismatches every exec-mask operation.
                waveLaneCount: waveLanes))
        {
            Console.Error.WriteLine($"spirv   : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"spirv   : OK - {compiled.Spirv.Length:N0} bytes");
        var spirvPath = Environment.GetEnvironmentVariable("SPIRV_OUT");
        if (!string.IsNullOrEmpty(spirvPath))
        {
            File.WriteAllBytes(spirvPath, compiled.Spirv);
        }


        // Seed the particle records so movement is detectable: a run that
        // leaves an all-zero buffer all-zero proves nothing.
        var uploads = new byte[compiled.GlobalMemoryBindings.Count][];
        for (var i = 0; i < uploads.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            var length = binding.DataLength;
            var data = new byte[length];

            // Identify by resolved address, not by size. Two of these blocks
            // are the same length, and ResourcesCs is reached through a raw
            // pointer rather than a descriptor, so size-matching silently put
            // the constant buffer's bytes where the dispatch bounds belong and
            // the shader retired every lane.
            // allocator memsets all 6000 records to zero, so every particle is
            // dead and the shader's own spawn path has to create them. Seeding
            // it with plausible-looking noise was the mistake that made the
            // earlier runs unreadable.
            var (source, what) = binding.BaseAddress switch
            {
                srtAddress => (srt, "SRTCs"),
                resourcesAddress => (resources, "ResourcesCs"),
                idBufferAddress => (BuildParticleIds(recordCount), "particleIds1"),
                propertyBufferAddress => (new byte[recordStride * recordCount], "particleProperties (zeroed)"),
                _ => (null, "unknown"),
            };

            if (source is not null)
            {
                source.AsSpan(0, Math.Min(source.Length, length)).CopyTo(data);
            }

            Console.WriteLine(
                $"  upload[{i}] base=0x{binding.BaseAddress:X8} {length,9:N0} bytes  {what}" +
                $"{(binding.Writable ? " (writable)" : string.Empty)}");
            uploads[i] = data;
        }

        // A control shader with the same descriptor layout isolates a runner
        // bug from a shader that simply retires every lane.
        var control = Environment.GetEnvironmentVariable("CONTROL_SPIRV");
        var spirv = !string.IsNullOrEmpty(control) && File.Exists(control)
            ? File.ReadAllBytes(control)
            : compiled.Spirv;
        if (spirv != compiled.Spirv)
        {
            Console.WriteLine($"spirv   : REPLACED by control {control}");
        }

        byte[][] results;
        try
        {
            using var runner = new ParticleComputeRunner();
            Console.WriteLine($"device  : {runner.DeviceName}");
            results = runner.Dispatch(
                spirv, uploads, (uint)((particleCount + 63) / 64), (uint)particleCount);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"dispatch: FAILED {exception.GetType().Name}: {exception.Message}");
            return 1;
        }

        Console.WriteLine("dispatch: OK");
        for (var i = 0; i < results.Length; i++)
        {
            var changed = 0;
            for (var b = 0; b < results[i].Length; b++)
            {
                if (results[i][b] != uploads[i][b])
                {
                    changed++;
                }
            }

            Console.WriteLine(
                $"  buffer[{i}] {results[i].Length,9:N0} bytes  {changed,9:N0} changed " +
                $"({100.0 * changed / Math.Max(1, results[i].Length):F1}%)");

            if (compiled.GlobalMemoryBindings[i].BaseAddress != propertyBufferAddress)
            {
                continue;
            }

            // A record counts as populated when the simulation gave it a
            // lifetime: curLife is at record offset 0x38.
            var populated = 0;
            var records = results[i].Length / recordStride;
            for (var r = 0; r < records; r++)
            {
                if (BitConverter.ToSingle(results[i], (r * recordStride) + 0x38) != 0f)
                {
                    populated++;
                }
            }

            Console.WriteLine($"             {populated:N0} of {records:N0} records have a non-zero curLife");
            var propertyDump = Environment.GetEnvironmentVariable("PROPERTY_OUT");
            if (!string.IsNullOrEmpty(propertyDump))
            {
                File.WriteAllBytes(propertyDump, results[i]);
            }
        }

        return 0;
    }

    /// <summary>
    ///
    /// <para>The loop at <c>0xE03CD</c>–<c>0xE042E</c> zeroes a 6000-entry
    /// array and runs an inside-out Fisher-Yates shuffle of the IDs
    /// <c>0..5999</c>, drawing from the renderer-global xorshift128+ whose
    /// state lives at <c>0x1275288</c>/<c>0x1275290</c> and starts at
    /// <c>0x112210F47DE98115</c> / <c>0x7B</c>. The sum is taken 32-bit
    /// (<c>lea eax, [rsi + rcx]</c>) before the modulo.</para>
    /// </summary>
    internal static byte[] BuildParticleIds(int count)
    {
        var ids = new uint[count];
        var s0 = 0x1122_10F4_7DE9_8115UL;
        var s1 = 0x7BUL;

        for (var i = 0; i < count; i++)
        {
            var a = (s0 << 23) ^ s0;
            var b = ((s1 >> 26) ^ s1) ^ a;
            var next = (a >> 17) ^ b;
            var draw = (uint)(s1 + next);
            s0 = s1;
            s1 = next;

            var j = draw % (uint)(i + 1);
            if (j != i)
            {
                ids[i] = ids[j];
            }

            ids[j] = (uint)i;
        }

        var bytes = new byte[count * 4];
        Buffer.BlockCopy(ids, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static uint ParseU32(string? text, uint fallback)
    {
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(text[2..], 16)
            : uint.Parse(text);
    }


    /// <summary>
    /// Decodes the merged local+hull program the wave surface actually runs.
    ///
    /// <para>On GFX10 <c>fw_flow_vl</c> and <c>fw_flow_h</c> are one hardware
    /// shader. The local section ends with <c>s_swappc_b64 null, s[6:7]</c> —
    /// an absolute jump the driver points at the hull entry, laid out directly
    /// after the local code. Concatenating the two slices and letting the tail
    /// call fall through is that layout; branches inside either section are
    /// PC-relative, so contiguity is all the jump needs.</para>
    /// </summary>
    internal static int MergeFlow(string eboot)
    {
        var image = File.ReadAllBytes(eboot);
        var local = Array.Find(Stages, x => x.Name == "fw_flow_vl");
        var hull = Array.Find(Stages, x => x.Name == "fw_flow_h");

        var merged = new byte[local.Length + hull.Length];
        Array.Copy(image, local.Offset, merged, 0, local.Length);
        Array.Copy(image, hull.Offset, merged, local.Length, hull.Length);

        var memory = new FlatMemory();
        memory.AddRegion(ProgramAddress, merged);
        var context = new CpuContext(memory, Generation.Gen5);

        if (!Gen5ShaderTranslator.TryDecodeMergedProgram(
                context, ProgramAddress, out var decoded, out var error))
        {
            Console.Error.WriteLine($"decode : FAILED {error}");
            return 1;
        }

        var tail = -1;
        for (var i = 0; i < decoded.Instructions.Count; i++)
        {
            if (decoded.Instructions[i].Opcode == "SSwappcB64")
            {
                tail = i;
                break;
            }
        }
        Console.WriteLine(
            $"merged : fw_flow_vl (0x{local.Length:X}) + fw_flow_h (0x{hull.Length:X}) " +
            $"= 0x{merged.Length:X} bytes");
        Console.WriteLine(
            $"decode : OK - {decoded.Instructions.Count} instructions " +
            $"(local {tail + 1}, hull {decoded.Instructions.Count - tail - 1})");
        Console.WriteLine(
            $"tail   : pc=0x{decoded.Instructions[tail].Pc:X} {decoded.Instructions[tail].Opcode} " +
            $"-> falls through to the hull entry at pc=0x{local.Length:X}");

        // The hull's only authored output is the tessellation factor set, and
        // every one of them is the inline constant 12. Reading them back out of
        // the decode is a cheap check that the concatenation landed on
        // instruction boundaries rather than mid-stream.
        var factors = decoded.Instructions
            .Where(i => i.Pc >= (uint)local.Length && i.Opcode == "VCvtF32I32")
            .Select(i => i.Sources.Count > 0 ? i.Sources[0].Value : 0u)
            .ToArray();
        Console.WriteLine(
            $"factors: {factors.Length} v_cvt_f32_i32 from inline constants " +
            $"[{string.Join(", ", factors.Select(v => v >= 128 && v <= 192 ? (v - 128).ToString() : $"src{v}"))}]");
        return 0;
    }

    /// <summary>
    /// Renders <c>fw_background_p</c>, the background's base plate, by
    ///
    /// <para>Constant-buffer offsets are the ones
    /// firstwave-12.40-shader-contracts.json records; the colours are the
    /// fragment stage is the console's instruction stream.</para>
    /// </summary>
    internal static int RenderPlate(
        string eboot, string outPath, float time, string? constantsPath, uint width, uint height)
    {
        const ulong constantBufferAddress = 0x0200_0000;

        var image = File.ReadAllBytes(eboot);
        var slice = Array.Find(Stages, x => x.Name == "fw_background_p");
        var text = new byte[slice.Length];
        Array.Copy(image, slice.Offset, text, 0, slice.Length);

        // The 412-byte FirstWave constant buffer, produced by executing the
        // tools/export_firstwave_constants.py. Two fields are easy to get wrong
        // and both flatten the plate completely:
        //   +0x40  worldProjectionMatrix. The shader takes v_rcp_f32 of m00 and
        //          m11 to rebuild the view ray; zeros give infinities.
        //   +0x190 screenDim, read with v_cvt_f32_u32 — *unsigned integers*,
        //          not floats. 1920.0f reinterpreted as a uint is ~1.14e9.
        var constantBuffer = new byte[0x200];
        if (string.IsNullOrEmpty(constantsPath) || !File.Exists(constantsPath))
        {
            Console.Error.WriteLine(
                "no constant buffer: build one with tools/export_firstwave_constants.py");
            return 2;
        }

        var constants = File.ReadAllBytes(constantsPath);
        constants.AsSpan(0, Math.Min(constants.Length, constantBuffer.Length)).CopyTo(constantBuffer);
        if (time != 0f)
        {
            BitConverter.TryWriteBytes(constantBuffer.AsSpan(0x184, 4), time);
        }

        Console.WriteLine(
            $"constants: {constants.Length} bytes, proj m00={BitConverter.ToSingle(constantBuffer, 0x40):G6} " +
            $"screenDim={BitConverter.ToUInt32(constantBuffer, 0x190)}x{BitConverter.ToUInt32(constantBuffer, 0x194)} " +
            $"time={BitConverter.ToSingle(constantBuffer, 0x184):G6}");

        var memory = new FlatMemory();
        memory.AddRegion(ProgramAddress, text);
        memory.AddRegion(constantBufferAddress, constantBuffer);
        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var decoded, out var error))
        {
            Console.Error.WriteLine($"decode  : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"decode  : OK - {decoded.Instructions.Count} instructions");

        var pixEnable = Convert.ToUInt32(Environment.GetEnvironmentVariable("PIX_ENABLE") ?? "2", 16);
        var pixAddr = Convert.ToUInt32(Environment.GetEnvironmentVariable("PIX_ADDR") ?? "2", 16);
        var userData = new uint[4];
        WriteBufferDescriptor(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
            constantBufferAddress,
            constantBuffer.Length);
        var state = new Gen5ShaderState(
            decoded, userData, Metadata: null,
            UserDataScalarRegisterBase: 0, ProgramResource1: 0x0000_0090);

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            Console.Error.WriteLine($"evaluate: FAILED {error}");
            return 1;
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                state, evaluation, Gen5PixelOutputKind.Float, out var compiled, out error,
                pixelInputEnable: pixEnable, pixelInputAddress: pixAddr))
        {
            Console.Error.WriteLine($"spirv   : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"spirv   : OK - {compiled.Spirv.Length:N0} bytes, " +
            $"{compiled.GlobalMemoryBindings.Count} buffer(s)");

        var uploads = new byte[compiled.GlobalMemoryBindings.Count][];
        for (var i = 0; i < uploads.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            uploads[i] = new byte[binding.DataLength];
            constantBuffer.AsSpan(0, Math.Min(constantBuffer.Length, binding.DataLength))
                .CopyTo(uploads[i]);
        }

        var vertexPath = Environment.GetEnvironmentVariable("FULLSCREEN_VS")
            ?? throw new InvalidOperationException("FULLSCREEN_VS not set");

        using var runner = new ParticleComputeRunner();
        Console.WriteLine($"device  : {runner.DeviceName}");
        var rgba = runner.RenderFragment(
            File.ReadAllBytes(vertexPath), compiled.Spirv, uploads, width, height);

        var distinct = new HashSet<uint>();
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            distinct.Add(BitConverter.ToUInt32(rgba, i));
        }

        Console.WriteLine($"render  : OK - {width}x{height}, {distinct.Count:N0} distinct colours");
        WritePng(outPath, (int)width, (int)height, rgba);
        Console.WriteLine($"output  : {outPath}");
        return distinct.Count > 1 ? 0 : 3;
    }

    internal static void WritePngPublic(string path, int width, int height, byte[] rgba)
        => WritePng(path, width, height, rgba);

    internal static void WriteBufferDescriptorPublic(
        Span<byte> destination, ulong address, int stride, int records)
        => WriteBufferDescriptor(destination, address, stride, records);

    private static void WritePng(string path, int width, int height, byte[] rgba)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
        }

        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(file, "IHDR", ihdr);
        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
                   deflated, System.IO.Compression.CompressionLevel.Fastest, true))
        {
            zlib.Write(raw);
        }

        Chunk(file, "IDAT", deflated.ToArray());
        Chunk(file, "IEND", []);

        static void Chunk(Stream stream, string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            stream.Write(length);
            var payload = new byte[4 + data.Length];
            for (var i = 0; i < 4; i++)
            {
                payload[i] = (byte)type[i];
            }

            data.CopyTo(payload, 4);
            stream.Write(payload);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(payload));
            stream.Write(crc);
        }

        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }

    private static void WriteBufferDescriptor(Span<byte> destination, ulong address, int bytes)
        => WriteBufferDescriptor(destination, address, 0, bytes);

    /// <summary>
    /// Writes a buffer V#.
    ///
    /// <para><paramref name="stride"/> is load-bearing for every access the
    /// particle shader makes: those are <c>idxen</c> MUBUF instructions, so the
    /// hardware multiplies the index by the descriptor's stride. A stride of
    /// zero collapses all 6000 records onto record 0, which is exactly the
    /// "one record written" result the earlier runs produced. When the stride
    /// is non-zero <c>num_records</c> counts elements, not bytes.</para>
    /// </summary>
    private static void WriteBufferDescriptor(
        Span<byte> destination, ulong address, int stride, int records)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..], (uint)(address >> 32) | (((uint)stride & 0x3FFF) << 16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)records);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
    }

    internal sealed class FlatMemory : ICpuMemory
    {
        private readonly List<(ulong Base, byte[] Data)> _regions = [];

        public void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress >= address &&
                    virtualAddress + (ulong)destination.Length <= address + (ulong)data.Length)
                {
                    data.AsSpan((int)(virtualAddress - address), destination.Length)
                        .CopyTo(destination);
                    return true;
                }
            }

            return false;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress >= address &&
                    virtualAddress + (ulong)source.Length <= address + (ulong)data.Length)
                {
                    source.CopyTo(data.AsSpan((int)(virtualAddress - address), source.Length));
                    return true;
                }
            }

            return false;
        }
    }
}
