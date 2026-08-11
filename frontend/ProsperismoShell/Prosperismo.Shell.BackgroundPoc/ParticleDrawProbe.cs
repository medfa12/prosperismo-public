// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.HLE;
using Prosperismo.Libs.Textures;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// programs: <c>particle_c</c> moves the particles, <c>particle_vv</c> expands
/// each one into a billboard quad, and <c>particle_p</c> shades it.
///
/// programs from the eboot's instruction stream, the parameters from the
/// serialized <c>coldboot</c> pattern blob replayed at the frame's authored
/// time (see <c>tools/export_particle_frames.py</c>), and the particle ID
/// permutation from the allocator's own xorshift128+. Nothing is modelled.
/// </para>
/// </summary>
internal static class ParticleDrawProbe
{
    private const ulong ProgramAddress = 0x1000_0000;
    private const ulong SrtCsAddress = 0x0200_0000;
    private const ulong ResourcesCsAddress = 0x0300_0000;
    private const ulong PropertyAddress = 0x0400_0000;
    private const ulong IdAddress = 0x0500_0000;
    private const ulong SrtVsPsAddress = 0x0600_0000;
    private const ulong ResourcesVsPsAddress = 0x0700_0000;
    private const ulong LargeImage0Address = 0x1000_0000_0UL;
    private const ulong LargeImage1Address = 0x2000_0000_0UL;

    private const int RecordStride = 0x44;
    private const int RecordCount = 0x1770;

    // The slice runs past the first s_endpgm on purpose: both vertex programs
    // read a corner table embedded after their code through s_getpc_b64, and
    // particle_p's palette sits after its discard epilogue.
    private const int ParticleComputeOffset = 0x11FA100;
    private const int ParticleComputeLength = 0x71A4;
    private const int ParticleVsOffset = 0x1201D00;
    private const int ParticleVsLength = 0x700;
    private const int ParticlePsOffset = 0x1201500;
    private const int ParticlePsLength = 0x800;
    private const int LargeParticleVsOffset = 0x1202C00;
    private const int LargeParticleVsLength = 0x600;
    private const int LargeParticlePsOffset = 0x1202400;
    private const int LargeParticlePsLength = 0x600;
    private const int PlatePsOffset = 0x11F9300;
    private const int PlatePsLength = 0x230;
    private const ulong PlateConstantsAddress = 0x0800_0000;

    private readonly record struct Group(int EncodedKind, int Index, byte[] Compute, byte[] Draw)
    {
        internal int Kind => EncodedKind & 0xFF;
        internal int Instance => (EncodedKind >> 8) & 0xFF;
    }

    private sealed record LargeParticleTextures(
        byte[] Gnf0, byte[] Gnf1,
        byte[] Rgba0, byte[] Rgba1,
        uint Width, uint Height);

    internal static int Render(
        string eboot, string framesDirectory, string outputDirectory, uint width, uint height, float fps)
    {
        var image = File.ReadAllBytes(eboot);
        var largeTextures = TryLoadLargeParticleTextures(eboot);

        // The plate is the layer the particles sit on: fw_background_p, fed the
        // particle field can still be rendered alone.
        var plateConstantsPath = Environment.GetEnvironmentVariable("PLATE_CONSTANTS");
        var fullscreenVsPath = Environment.GetEnvironmentVariable("FULLSCREEN_VS");
        ParticleComputeRunner.ParticleDraw? plate = null;
        if (!string.IsNullOrEmpty(plateConstantsPath) && !string.IsNullOrEmpty(fullscreenVsPath) &&
            File.Exists(plateConstantsPath) && File.Exists(fullscreenVsPath))
        {
            if (!BuildPlate(image, plateConstantsPath, fullscreenVsPath, out var built, out var plateError))
            {
                Console.Error.WriteLine($"plate: {plateError}");
                return 1;
            }

            plate = built;
            Console.WriteLine($"plate   : fw_background_p from {Path.GetFileName(plateConstantsPath)}");
        }

        Directory.CreateDirectory(outputDirectory);

        var frameFiles = Directory.GetFiles(framesDirectory, "*.bin").OrderBy(x => x).ToArray();
        if (frameFiles.Length == 0)
        {
            Console.Error.WriteLine($"no frame blocks in {framesDirectory}");
            return 2;
        }

        // allocates it: 6000 records, zeroed, shared by every group. Each group
        // owns the slice [offsetParticle, offsetParticle + numParticles).
        var properties = new byte[RecordStride * RecordCount];
        var ids = FirstWaveProbe.BuildParticleIds(RecordCount);

        using var runner = new ParticleComputeRunner();
        Console.WriteLine($"device  : {runner.DeviceName}");
        Console.WriteLine($"frames  : {frameFiles.Length} at {fps} fps into {outputDirectory}");

        for (var frame = 0; frame < frameFiles.Length; frame++)
        {
            var (time, groups) = ReadFrame(frameFiles[frame]);
            var currentInstance = groups.Count == 0 ? 0 : groups.Max(group => group.Instance);
            var transitionAt = float.TryParse(
                Environment.GetEnvironmentVariable("PATTERN_TRANSITION_AT"), out var transition)
                ? transition
                : 6f;
            var draws = new List<ParticleComputeRunner.ParticleDraw>();
            if (plate is { } basePlate)
            {
                draws.Add(basePlate);
            }
            var smallDraws = new List<ParticleComputeRunner.ParticleDraw>();
            var largeDraws = new List<ParticleComputeRunner.ParticleDraw>();

            var drawn = 0;

            foreach (var group in groups)
            {
                var count = BitConverter.ToUInt32(group.Compute, 0x28);
                if (count == 0)
                {
                    continue;
                }

                var localTime = group.Instance == 0 ? time : Math.Max(0f, time - transitionAt);
                if (!Simulate(
                        image, group, localTime, fps, currentInstance,
                        properties, ids, runner, out var error))
                {
                    Console.Error.WriteLine($"frame {frame} group {group.Index}: {error}");
                    return 1;
                }

                ParticleComputeRunner.ParticleDraw draw = default;
                var built = group.Kind == 0
                    ? BuildDraw(
                        image, group, properties, ids, currentInstance,
                        out draw, out error)
                    : largeTextures is not null && BuildLargeDraw(
                        image, group, properties, ids, largeTextures,
                        width, height, localTime, 1f / fps, currentInstance,
                        out draw, out error);
                if (!built)
                {
                    if (group.Kind != 0 && largeTextures is null)
                    {
                        error = "large-particle GNF assets are missing or unsupported";
                    }
                    Console.Error.WriteLine($"frame {frame} group {group.Index}: {error}");
                    return 1;
                }

                (group.Kind == 0 ? smallDraws : largeDraws).Add(draw);
                drawn += (int)count;
            }

            // Every particle pass is ONE/ONE additive, so this grouping is
            // colour-equivalent to the instance walk.  Keeping procedural
            // draws before textured large-particle draws also avoids a
            // MoltenVK state leak where a sampled-image pipeline prevents a
            // later vertex storage-buffer life latch from becoming visible.
            draws.AddRange(smallDraws);
            draws.AddRange(largeDraws);

            byte[][][] after = [];
            var rgba = draws.Count == 0
                ? new byte[width * height * 4]
                : runner.RenderParticleFrame(draws, width, height, out after);

            // particle_vv latches renLife into the record for corner 0 when it
            // is still negative, and particle_p's life fade is
            //   smoothstep(sat(2*curLife)) * smoothstep(sat(2*(renLife - curLife)))
            // so an unlatched record shades to exactly black. The latch is a
            // guest-memory write: fold it back into the bank or every frame
            // stays dark.
            for (var d = plate is null ? 0 : 1; d < after.Length; d++)
            {
                for (var b = 0; b < after[d].Length; b++)
                {
                    if (after[d][b].Length != properties.Length ||
                        draws[d].Buffers[b].Length != properties.Length)
                    {
                        continue;
                    }

                    // Each group only writes its own record range, so merge the
                    // dwords the shader actually changed. Copying a whole
                    // readback would discard every other group's latch.
                    var before = draws[d].Buffers[b];
                    for (var k = 0; k < properties.Length; k++)
                    {
                        if (after[d][b][k] != before[k])
                        {
                            properties[k] = after[d][b][k];
                        }
                    }

                    break;
                }
            }

            if (Environment.GetEnvironmentVariable("TRACE_SIM") == "1")
            {
                var liveByTag = new int[16];
                var latchedByTag = new int[16];
                var shadeableByTag = new int[16];
                for (var r = 0; r < RecordCount; r++)
                {
                    var record = r * RecordStride;
                    var tag = (int)(BitConverter.ToUInt32(properties, record + 0x28) & 0xF);
                    var curLife = BitConverter.ToSingle(properties, record + 0x38);
                    var renLife = BitConverter.ToSingle(properties, record + 0x40);
                    if (curLife == 0f)
                    {
                        continue;
                    }

                    liveByTag[tag]++;
                    if (renLife >= 0f)
                    {
                        latchedByTag[tag]++;
                    }
                    if (curLife > 0f && renLife > curLife)
                    {
                        shadeableByTag[tag]++;
                    }
                }

                var lifeSummary = string.Join(", ", Enumerable.Range(0, 16)
                    .Where(tag => liveByTag[tag] != 0)
                    .Select(tag => $"{tag}:live={liveByTag[tag]:N0}/latched={latchedByTag[tag]:N0}/shade={shadeableByTag[tag]:N0}"));
                Console.WriteLine($"    post-draw life [{lifeSummary}]");
            }

            var clipDump = Environment.GetEnvironmentVariable("CLIP_OUT");
            var clipBuffer = int.TryParse(Environment.GetEnvironmentVariable("CLIP_BUFFER"), out var cb) ? cb : 2;
            if (!string.IsNullOrEmpty(clipDump) && after.Length > 0 && after[0].Length > clipBuffer)
            {
                File.WriteAllBytes(clipDump, after[0][clipBuffer]);
            }

            if (Environment.GetEnvironmentVariable("TRACE_VS") == "1" && after.Length > 0)
            {
                for (var d = 0; d < after.Length; d++)
                {
                    for (var b = 0; b < after[d].Length; b++)
                    {
                        var changed = 0;
                        for (var k = 0; k < after[d][b].Length; k++)
                        {
                            if (after[d][b][k] != draws[d].Buffers[b][k])
                            {
                                changed++;
                            }
                        }

                        Console.WriteLine(
                            $"    draw[{d}] buffer[{b}] {after[d][b].Length,8:N0} bytes {changed,7:N0} changed");
                    }
                }
            }

            // The light layer is a compositor: it takes the particle target as
            // texP and adds the shafts. Its relationship to fw_background_p is
            // still selectable below because the upstream render-context order
            // has not yet been recovered.
            var colorCbPath = Environment.GetEnvironmentVariable("LIGHT_COLORCB");
            var colorCbAfterPath = Environment.GetEnvironmentVariable("LIGHT_COLORCB_AFTER");
            var lightTransitionAt = float.TryParse(
                Environment.GetEnvironmentVariable("LIGHT_TRANSITION_AT"), out var lightTransition)
                ? lightTransition
                : transitionAt;
            if (time >= lightTransitionAt && !string.IsNullOrEmpty(colorCbAfterPath))
            {
                colorCbPath = colorCbAfterPath;
            }
            if (!string.IsNullOrEmpty(colorCbPath))
            {
                if (!LightLayerProbe.TryBuildDraw(
                        image, colorCbPath, rgba, width, height, time,
                        out var lightDraw, out var lightError))
                {
                    Console.Error.WriteLine($"frame {frame}: {lightError}");
                    return 1;
                }

                // With a plate present the light layer accumulates over it;
                // fw_background_p and light_p is not recovered, so this is
                // selectable rather than asserted.
                if (plate is { } under && Environment.GetEnvironmentVariable("LIGHT_OVER_PLATE") == "1")
                {
                    rgba = runner.RenderParticleFrame(
                        [under, lightDraw with { Additive = true }], width, height);
                }
                else
                {
                    rgba = runner.RenderParticleFrame([lightDraw], width, height);
                }
            }

            var path = Path.Combine(outputDirectory, $"{frame:D5}.png");
            FirstWaveProbe.WritePngPublic(path, (int)width, (int)height, rgba);

            var lit = 0;
            for (var i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
                {
                    lit++;
                }
            }

            var touched = 0;
            for (var i = 3; i < rgba.Length; i += 4)
            {
                if (rgba[i] != 255)
                {
                    touched++;
                }
            }

            Console.WriteLine(
                $"  frame {frame:D5} t={time,7:F3} groups={draws.Count - (plate is null ? 0 : 1)} particles={drawn,5} " +
                $"lit={lit,9:N0} touched={touched,9:N0}");
        }

        return 0;
    }

    /// <summary>
    /// Runs <c>particle_c</c> for one group and folds the result back into the
    /// shared property bank.
    /// </summary>
    private static bool Simulate(
        byte[] image,
        Group group,
        float time,
        float fps,
        int currentInstance,
        byte[] properties,
        byte[] ids,
        ParticleComputeRunner runner,
        out string error)
    {
        var srt = new byte[0x1000];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesCsAddress);
        BitConverter.TryWriteBytes(srt.AsSpan(0x08, 4), time);
        BitConverter.TryWriteBytes(srt.AsSpan(0x0C, 4), 1f / fps);
        // but advances its life counter at the rate authored in Resources+3c
        // (normalised against the 6.5 second pattern clock).  The incoming
        // instance remains the current one and advances at real time.
        var timeRate = group.Instance == currentInstance
            ? 1f
            : BitConverter.ToSingle(group.Compute, 0x3C) * (1f / 6.5f);
        BitConverter.TryWriteBytes(srt.AsSpan(0x10, 4), timeRate);
        BitConverter.TryWriteBytes(srt.AsSpan(0x14, 4), 0u); // isPreSimulation
        BitConverter.TryWriteBytes(
            srt.AsSpan(0x18, 4), (uint)(group.Instance | (currentInstance << 4)));

        var resources = new byte[0x1000];
        group.Compute.AsSpan(0, Math.Min(group.Compute.Length, resources.Length)).CopyTo(resources);
        FirstWaveProbe.WriteBufferDescriptorPublic(resources.AsSpan(0x00, 16), IdAddress, 4, RecordCount);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            resources.AsSpan(0x10, 16), PropertyAddress, RecordStride, RecordCount);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, Slice(image, ParticleComputeOffset, ParticleComputeLength));
        memory.AddRegion(SrtCsAddress, srt);
        memory.AddRegion(ResourcesCsAddress, resources);
        memory.AddRegion(PropertyAddress, properties);
        memory.AddRegion(IdAddress, ids);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(context, ProgramAddress, out var decoded, out error))
        {
            return false;
        }

        var userData = new uint[4];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
            SrtCsAddress, 0, srt.Length);

        var state = new Gen5ShaderState(
            decoded,
            userData,
            Metadata: null,
            ComputeSystemRegisters: new Gen5ComputeSystemRegisters(4, null, null, null),
            UserDataScalarRegisterBase: 0,
            ProgramResource1: 0x0000_0090);

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            return false;
        }

        if (!Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 64, 1, 1, out var compiled, out error, waveLaneCount: 64))
        {
            return false;
        }

        var count = BitConverter.ToUInt32(group.Compute, 0x28);
        var uploads = new byte[compiled.GlobalMemoryBindings.Count][];
        var propertyIndex = -1;
        for (var i = 0; i < uploads.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            var data = new byte[binding.DataLength];
            var source = binding.BaseAddress switch
            {
                SrtCsAddress => srt,
                ResourcesCsAddress => resources,
                PropertyAddress => properties,
                IdAddress => ids,
                _ => null,
            };

            if (binding.BaseAddress == PropertyAddress)
            {
                propertyIndex = i;
            }

            source?.AsSpan(0, Math.Min(source.Length, data.Length)).CopyTo(data);
            uploads[i] = data;
        }

        var results = runner.Dispatch(compiled.Spirv, uploads, (count + 63) / 64, count);
        if (propertyIndex >= 0)
        {
            results[propertyIndex].AsSpan(0, Math.Min(results[propertyIndex].Length, properties.Length))
                .CopyTo(properties);
        }

        if (Environment.GetEnvironmentVariable("TRACE_SIM") == "1")
        {
            var live = 0;
            var tags = new Dictionary<uint, int>();
            for (var r = 0; r < RecordCount; r++)
            {
                if (BitConverter.ToSingle(properties, (r * RecordStride) + 0x38) != 0f)
                {
                    live++;
                    var tag = BitConverter.ToUInt32(properties, (r * RecordStride) + 0x28);
                    tags[tag] = tags.GetValueOrDefault(tag) + 1;
                }
            }

            var tagSummary = string.Join(", ", tags.OrderBy(pair => pair.Key)
                .Select(pair => $"0x{pair.Key:X}:{pair.Value:N0}"));
            Console.WriteLine(
                $"    sim group {group.Index} count={count} offset={BitConverter.ToUInt32(group.Compute, 0x30)}" +
                $" rate={timeRate:F4} -> {live:N0} live records tags=[{tagSummary}]");
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Translates <c>particle_vv</c> and <c>particle_p</c> for one group.
    ///
    /// <para>The two stages reach their data differently. The vertex program
    /// takes the record buffer as a V# straight out of user data at
    /// <c>s[0:3]</c> and the SRT at <c>s[8:11]</c> — its buffer loads name
    /// <c>s[0:3]</c> with record offsets 0x00/0x1C/0x28/0x2C/0x40, which is
    /// <c>pos</c>/<c>fore</c>/<c>transPatternFlag</c>/<c>right</c>/<c>renLife</c>.
    /// The pixel program takes only the SRT, at <c>s[0:3]</c>.</para>
    /// </summary>
    private static bool BuildDraw(
        byte[] image,
        Group group,
        byte[] properties,
        byte[] ids,
        int currentInstance,
        out ParticleComputeRunner.ParticleDraw draw,
        out string error)
    {
        draw = default;

        var resources = new byte[0x1000];
        group.Draw.AsSpan(0, Math.Min(group.Draw.Length, resources.Length)).CopyTo(resources);

        // +0x00 the record buffer, +0x10 the per-slot u32 the size lottery
        // seeds from. Both are runtime allocations, so the blob never writes
        FirstWaveProbe.WriteBufferDescriptorPublic(
            resources.AsSpan(0x00, 16), PropertyAddress, RecordStride, RecordCount);
        FirstWaveProbe.WriteBufferDescriptorPublic(resources.AsSpan(0x10, 16), IdAddress, 4, RecordCount);

        var srt = new byte[0x1000];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesVsPsAddress);
        BitConverter.TryWriteBytes(
            srt.AsSpan(0x10, 4), (uint)(group.Instance | (currentInstance << 4)));
        BitConverter.TryWriteBytes(srt.AsSpan(0x14, 4), 0u);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, Slice(image, ParticleVsOffset, ParticleVsLength));
        memory.AddRegion(SrtVsPsAddress, srt);
        memory.AddRegion(ResourcesVsPsAddress, resources);
        memory.AddRegion(PropertyAddress, properties);
        memory.AddRegion(IdAddress, ids);

        var vertexContext = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out error))
        {
            error = $"vertex decode: {error}";
            return false;
        }

        // s[0:3] is NOT user data: pc=0x008C loads it from ResourcesVsPs+0x00
        // (the record buffer V#), and before that pc=0x0004 reads s3 as the NGG
        // merged wave info — vertex count in bits 7:0, primitive count in 15:8.
        // The prologue turns those into EXEC with
        // `s[126:127] = -1 >> (64 - count)`, so a zero there disables the wave.
        // The only real user data is the SRT V# at s[8:11].
        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            SrtVsPsAddress, 0, srt.Length);

        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            error = $"vertex evaluate: {error}";
            return false;
        }

        var pixelMemory = new FirstWaveProbe.FlatMemory();
        pixelMemory.AddRegion(ProgramAddress, Slice(image, ParticlePsOffset, ParticlePsLength));
        pixelMemory.AddRegion(SrtVsPsAddress, srt);
        pixelMemory.AddRegion(ResourcesVsPsAddress, resources);
        pixelMemory.AddRegion(PropertyAddress, properties);
        pixelMemory.AddRegion(IdAddress, ids);

        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            error = $"pixel decode: {error}";
            return false;
        }

        var pixelUserData = new uint[4];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan(0, 4)),
            SrtVsPsAddress, 0, srt.Length);

        var pixelState = new Gen5ShaderState(
            pixelProgram, pixelUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            error = $"pixel evaluate: {error}";
            return false;
        }

        // The two stages share one storage-buffer array, so each is told where
        // its own slice starts and how long the whole array is.
        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = pixelEvaluation.GlobalMemoryBindings.Count;
        var total = vertexBufferCount + pixelBufferCount;

        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState,
                vertexEvaluation,
                out var vertexShader,
                out error,
                globalBufferBase: 0,
                totalGlobalBufferCount: total,
                requiredVertexOutputCount: 6))
        {
            error = $"vertex spirv: {error}";
            return false;
        }

        // SPI_PS_INPUT_ENA/ADDR = 0x2 and SPI_PS_IN_CONTROL.NUM_INTERP = 6 are
        // its shader header with tools/dump_shader_registers.py, not chosen.
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState,
                pixelEvaluation,
                Gen5PixelOutputKind.Float,
                out var pixelShader,
                out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: total,
                pixelInputEnable: 0x2,
                pixelInputAddress: 0x2))
        {
            error = $"pixel spirv: {error}";
            return false;
        }

        var spirvOut = Environment.GetEnvironmentVariable("DRAW_SPIRV_OUT");
        if (!string.IsNullOrEmpty(spirvOut))
        {
            File.WriteAllBytes($"{spirvOut}.vs.spv", vertexShader.Spirv);
            File.WriteAllBytes($"{spirvOut}.ps.spv", pixelShader.Spirv);
        }

        // Both programs reach data embedded in their own image through
        // s_getpc_b64: particle_vv's 48-byte billboard corner table at +0x500
        // and particle_p's 84-byte palette at +0x630. Those bindings resolve to
        // an address inside the program, so they have to be served from the
        // shader bytes. Uploading zeros there collapses all six corners of
        // every quad onto one point and nothing rasterises.
        var vertexText = Slice(image, ParticleVsOffset, ParticleVsLength);
        var pixelText = Slice(image, ParticlePsOffset, ParticlePsLength);

        var buffers = new byte[total][];
        var alias = new int[total];
        var byAddress = new Dictionary<ulong, int>();
        for (var i = 0; i < total; i++)
        {
            alias[i] = -1;
            var fromVertex = i < vertexBufferCount;
            var binding = fromVertex
                ? vertexShader.GlobalMemoryBindings[i]
                : pixelShader.GlobalMemoryBindings[i - vertexBufferCount];
            var data = new byte[binding.DataLength];
            var text = fromVertex ? vertexText : pixelText;

            byte[]? source;
            var offset = 0;
            if (binding.BaseAddress >= ProgramAddress &&
                binding.BaseAddress < ProgramAddress + (ulong)text.Length)
            {
                source = text;
                offset = (int)(binding.BaseAddress - ProgramAddress);
            }
            else
            {
                source = binding.BaseAddress switch
                {
                    SrtVsPsAddress => srt,
                    ResourcesVsPsAddress => resources,
                    PropertyAddress => properties,
                    IdAddress => ids,
                    _ => null,
                };
            }

            if (source is null)
            {
                error = $"unmapped binding base 0x{binding.BaseAddress:X}";
                return false;
            }

            source.AsSpan(offset, Math.Min(source.Length - offset, data.Length)).CopyTo(data);
            buffers[i] = data;

            // One guest allocation, one GPU buffer, however many descriptor
            // slots address it.
            if (byAddress.TryGetValue(binding.BaseAddress, out var firstSlot) &&
                buffers[firstSlot].Length == data.Length)
            {
                alias[i] = firstSlot;
            }
            else
            {
                byAddress[binding.BaseAddress] = i;
            }
        }

        var count = BitConverter.ToUInt32(group.Draw, 0x20);

        // DEBUG_FS swaps in a trivial fragment stage. It isolates "the vertex
        // program produced no geometry" from "the pixel program killed every
        // fragment"; it is a diagnostic, never part of a render.
        var debugFragment = Environment.GetEnvironmentVariable("DEBUG_FS");
        var fragmentSpirv = !string.IsNullOrEmpty(debugFragment) && File.Exists(debugFragment)
            ? File.ReadAllBytes(debugFragment)
            : pixelShader.Spirv;

        var debugPixel = Environment.GetEnvironmentVariable("DEBUG_PS_SPIRV");
        if (!string.IsNullOrEmpty(debugPixel) && File.Exists(debugPixel))
        {
            fragmentSpirv = File.ReadAllBytes(debugPixel);
        }

        var debugVertex = Environment.GetEnvironmentVariable("DEBUG_VS");
        var vertexSpirv = !string.IsNullOrEmpty(debugVertex) && File.Exists(debugVertex)
            ? File.ReadAllBytes(debugVertex)
            : vertexShader.Spirv;

        draw = new ParticleComputeRunner.ParticleDraw(
            vertexSpirv, fragmentSpirv, buffers, count * 6, alias);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// procedural small points, this pair samples the two original BGLayer GNF
    /// light fields and applies the authored HSV, focus and size resources in
    /// the coldboot blob.
    /// </summary>
    private static bool BuildLargeDraw(
        byte[] image,
        Group group,
        byte[] properties,
        byte[] ids,
        LargeParticleTextures textures,
        uint width,
        uint height,
        float time,
        float timeStep,
        int currentInstance,
        out ParticleComputeRunner.ParticleDraw draw,
        out string error)
    {
        draw = default;

        var resources = new byte[0x1000];
        group.Draw.AsSpan(0, Math.Min(group.Draw.Length, resources.Length)).CopyTo(resources);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            resources.AsSpan(0x00, 16), PropertyAddress, RecordStride, RecordCount);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            resources.AsSpan(0x10, 16), IdAddress, sizeof(uint), RecordCount);
        WriteImageDescriptor(resources.AsSpan(0x20, 32), textures.Gnf0, LargeImage0Address);
        WriteImageDescriptor(resources.AsSpan(0x40, 32), textures.Gnf1, LargeImage1Address);
        BitConverter.TryWriteBytes(resources.AsSpan(0x78, sizeof(float)), (float)width / height);

        var srt = new byte[0x1000];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesVsPsAddress);
        BitConverter.TryWriteBytes(srt.AsSpan(0x08, sizeof(float)), time);
        BitConverter.TryWriteBytes(srt.AsSpan(0x0C, sizeof(float)), timeStep);
        BitConverter.TryWriteBytes(
            srt.AsSpan(0x10, sizeof(uint)),
            (uint)(group.Instance | (currentInstance << 4)));

        var vertexText = Slice(image, LargeParticleVsOffset, LargeParticleVsLength);
        var vertexMemory = new FirstWaveProbe.FlatMemory();
        vertexMemory.AddRegion(ProgramAddress, vertexText);
        vertexMemory.AddRegion(SrtVsPsAddress, srt);
        vertexMemory.AddRegion(ResourcesVsPsAddress, resources);
        vertexMemory.AddRegion(PropertyAddress, properties);
        vertexMemory.AddRegion(IdAddress, ids);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out error))
        {
            error = $"large vertex decode: {error}";
            return false;
        }

        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            SrtVsPsAddress, 0, srt.Length);
        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            error = $"large vertex evaluate: {error}";
            return false;
        }

        var pixelText = Slice(image, LargeParticlePsOffset, LargeParticlePsLength);
        var pixelMemory = new FirstWaveProbe.FlatMemory();
        pixelMemory.AddRegion(ProgramAddress, pixelText);
        pixelMemory.AddRegion(SrtVsPsAddress, srt);
        pixelMemory.AddRegion(ResourcesVsPsAddress, resources);
        pixelMemory.AddRegion(PropertyAddress, properties);
        pixelMemory.AddRegion(IdAddress, ids);
        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            error = $"large pixel decode: {error}";
            return false;
        }

        var pixelUserData = new uint[4];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan()),
            SrtVsPsAddress, 0, srt.Length);
        var pixelState = new Gen5ShaderState(
            pixelProgram, pixelUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            error = $"large pixel evaluate: {error}";
            return false;
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = pixelEvaluation.GlobalMemoryBindings.Count;
        var total = vertexBufferCount + pixelBufferCount;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState, vertexEvaluation, out var vertexShader, out error,
                globalBufferBase: 0, totalGlobalBufferCount: total,
                requiredVertexOutputCount: 5))
        {
            error = $"large vertex spirv: {error}";
            return false;
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState, pixelEvaluation, Gen5PixelOutputKind.Float,
                out var pixelShader, out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: total,
                pixelInputEnable: 0x2, pixelInputAddress: 0x2))
        {
            error = $"large pixel spirv: {error}";
            return false;
        }

        var buffers = new byte[total][];
        var aliases = new int[total];
        var byAddress = new Dictionary<ulong, int>();
        for (var i = 0; i < total; i++)
        {
            aliases[i] = -1;
            var fromVertex = i < vertexBufferCount;
            var binding = fromVertex
                ? vertexShader.GlobalMemoryBindings[i]
                : pixelShader.GlobalMemoryBindings[i - vertexBufferCount];
            var data = new byte[binding.DataLength];
            var text = fromVertex ? vertexText : pixelText;
            byte[]? source;
            var offset = 0;
            if (binding.BaseAddress >= ProgramAddress &&
                binding.BaseAddress < ProgramAddress + (ulong)text.Length)
            {
                source = text;
                offset = (int)(binding.BaseAddress - ProgramAddress);
            }
            else
            {
                source = binding.BaseAddress switch
                {
                    SrtVsPsAddress => srt,
                    ResourcesVsPsAddress => resources,
                    PropertyAddress => properties,
                    IdAddress => ids,
                    _ => null,
                };
            }

            if (source is null)
            {
                error = $"large unmapped binding base 0x{binding.BaseAddress:X}";
                return false;
            }

            source.AsSpan(offset, Math.Min(source.Length - offset, data.Length)).CopyTo(data);
            buffers[i] = data;
            if (byAddress.TryGetValue(binding.BaseAddress, out var firstSlot) &&
                buffers[firstSlot].Length == data.Length)
            {
                aliases[i] = firstSlot;
            }
            else
            {
                byAddress[binding.BaseAddress] = i;
            }
        }

        var sampledImages = pixelShader.ImageBindings.Select(binding =>
            (binding.ResourceDescriptor[1] & 0xFF) == (uint)(LargeImage1Address >> 32)
                ? new ParticleComputeRunner.GuestImage(
                    textures.Rgba1, textures.Width, textures.Height,
                    Silk.NET.Vulkan.Format.R8G8B8A8Unorm)
                : new ParticleComputeRunner.GuestImage(
                    textures.Rgba0, textures.Width, textures.Height,
                    Silk.NET.Vulkan.Format.R8G8B8A8Unorm)).ToArray();
        var count = BitConverter.ToUInt32(group.Draw, 0xAC);
        draw = new ParticleComputeRunner.ParticleDraw(
            vertexShader.Spirv, pixelShader.Spirv, buffers,
            count * 6, aliases, Additive: true, sampledImages);
        error = string.Empty;
        return true;
    }

    private static LargeParticleTextures? TryLoadLargeParticleTextures(string eboot)
    {
        var asset0 = Environment.GetEnvironmentVariable("PARTICLE0_GNF");
        var asset1 = Environment.GetEnvironmentVariable("PARTICLE1_GNF");
        if (string.IsNullOrEmpty(asset0) || string.IsNullOrEmpty(asset1))
        {
            var directory = new FileInfo(Path.GetFullPath(eboot)).Directory;
            while (directory is not null &&
                   !string.Equals(directory.Name, "system_ex", StringComparison.Ordinal))
            {
                directory = directory.Parent;
            }

            if (directory is not null)
            {
                var assets = Path.Combine(directory.FullName, "vsh_asset");
                asset0 ??= Path.Combine(
                    assets, "Sce.Vsh.ShellUI.BGLayer.Particle0.gnf");
                asset1 ??= Path.Combine(
                    assets, "Sce.Vsh.ShellUI.BGLayer.Particle1.gnf");
            }
        }

        if (string.IsNullOrEmpty(asset0) || string.IsNullOrEmpty(asset1) ||
            !File.Exists(asset0) || !File.Exists(asset1))
        {
            return null;
        }

        var rgba0 = GnfImage.TryLoadRgba(asset0, out var width0, out var height0);
        var rgba1 = GnfImage.TryLoadRgba(asset1, out var width1, out var height1);
        if (rgba0 is null || rgba1 is null || width0 != width1 || height0 != height1)
        {
            return null;
        }

        Console.WriteLine($"large  : two native GNF fields, {width0}x{height0}");
        return new LargeParticleTextures(
            File.ReadAllBytes(asset0), File.ReadAllBytes(asset1),
            rgba0, rgba1, (uint)width0, (uint)height0);
    }

    private static void WriteImageDescriptor(
        Span<byte> destination, byte[] gnf, ulong address)
    {
        gnf.AsSpan(0x10, 32).CopyTo(destination);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            destination, (uint)(address & uint.MaxValue));
        var high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[4..]);
        high = (high & 0xFFFF_FF00u) | (uint)(address >> 32);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], high);
    }

    /// <summary>
    /// Translates <c>fw_background_p</c> as the frame's base layer.
    ///
    /// <para>It reads <c>FragCoord</c> out of v2/v3, so the pixel-input
    /// registers have to name PERSP_CENTER plus POS_X_FLOAT and POS_Y_FLOAT —
    /// 0x302. With PERSP_CENTER consuming v0 and v1, the position lands exactly
    /// where the shader looks. See firstwave-plate-executed.md.</para>
    /// </summary>
    private static bool BuildPlate(
        byte[] image,
        string constantsPath,
        string fullscreenVsPath,
        out ParticleComputeRunner.ParticleDraw draw,
        out string error)
    {
        draw = default;

        var constants = new byte[0x200];
        var recovered = File.ReadAllBytes(constantsPath);
        recovered.AsSpan(0, Math.Min(recovered.Length, constants.Length)).CopyTo(constants);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, Slice(image, PlatePsOffset, PlatePsLength));
        memory.AddRegion(PlateConstantsAddress, constants);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(context, ProgramAddress, out var program, out error))
        {
            error = $"decode: {error}";
            return false;
        }

        var userData = new uint[4];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
            PlateConstantsAddress, 0, constants.Length);

        var state = new Gen5ShaderState(program, userData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            error = $"evaluate: {error}";
            return false;
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var compiled,
                out error,
                pixelInputEnable: 0x302,
                pixelInputAddress: 0x302))
        {
            error = $"spirv: {error}";
            return false;
        }

        var buffers = new byte[compiled.GlobalMemoryBindings.Count][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var data = new byte[compiled.GlobalMemoryBindings[i].DataLength];
            constants.AsSpan(0, Math.Min(constants.Length, data.Length)).CopyTo(data);
            buffers[i] = data;
        }

        draw = new ParticleComputeRunner.ParticleDraw(
            File.ReadAllBytes(fullscreenVsPath), compiled.Spirv, buffers, 3, null, Additive: false);
        return true;
    }

    private static byte[] Slice(byte[] image, int offset, int length)
    {
        var text = new byte[length];
        Array.Copy(image, offset, text, 0, length);
        return text;
    }

    private static (float Time, List<Group> Groups) ReadFrame(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadUInt32();
        if (magic != 0x4D524650)
        {
            throw new InvalidDataException($"{path}: not a PFRM frame block");
        }

        var groupCount = reader.ReadUInt32();
        var time = reader.ReadSingle();
        reader.ReadUInt32();

        var groups = new List<Group>((int)groupCount);
        for (var i = 0; i < groupCount; i++)
        {
            var kind = reader.ReadInt32();
            var index = reader.ReadInt32();
            var computeLength = reader.ReadInt32();
            var drawLength = reader.ReadInt32();
            groups.Add(new Group(
                kind, index, reader.ReadBytes(computeLength), reader.ReadBytes(drawLength)));
        }

        return (time, groups);
    }
}
