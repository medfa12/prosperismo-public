// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Runs the wave surface's merged local+hull program.
///
/// <para><c>fw_flow_vl</c> and <c>fw_flow_h</c> are one hardware shader: the
/// local section fetches control points, displaces them by 3D simplex noise
/// driven by <c>time</c>, and writes 32 bytes per point to LDS; the hull copies
/// LDS into the patch ring and writes six tessellation factors. On GFX10 that
/// wave is compute-like, so it runs here as a compute dispatch — one workgroup
/// per patch, one invocation per control point, which is the hardware's own
/// arrangement.</para>
///
/// <para>Resource map, decoded from the two programs:</para>
/// <list type="bullet">
/// <item><c>s[8:11]</c> — the FirstWave constant buffer; the local section
/// reads <c>time</c> at <c>+0x184</c>.</item>
/// <item><c>s[12:13]</c> — a table of two vertex-buffer V#s at <c>+0x00</c> and
/// <c>+0x10</c>: the control lattice and the boundary ring.</item>
/// <item><c>s[0:1]</c> — a global table whose first pointer leads to a
/// descriptor block holding the tessellation-factor V# at <c>+0x20</c> and the
/// patch-ring V# at <c>+0x30</c>.</item>
/// </list>
/// </summary>
internal static class WaveSurfaceProbe
{
    private const ulong ProgramAddress = 0x1000_0000;
    private const ulong ConstantsAddress = 0x0200_0000;
    private const ulong VertexTableAddress = 0x0300_0000;
    private const ulong LatticeAddress = 0x0400_0000;
    private const ulong RingAddress = 0x0500_0000;
    private const ulong GlobalTableAddress = 0x0600_0000;
    private const ulong DescriptorBlockAddress = 0x0700_0000;
    private const ulong PatchRingAddress = 0x0800_0000;
    private const ulong TessFactorAddress = 0x0900_0000;

    private const int LocalOffset = 0x11F6900;
    private const int LocalLength = 0x72C;
    private const int HullOffset = 0x11F6600;
    private const int HullLength = 0x108;

    private const int LatticeEntries = 165;
    private const uint ControlPoints = 16;

    // The domain proves the ring contract directly with v7 << 9: sixteen
    // 32-byte output control points occupy one contiguous 512-byte patch slot.
    private static uint PatchRingStride =>
        uint.TryParse(Environment.GetEnvironmentVariable("RING_STRIDE"), out var rs) && rs > 0
            ? rs
            : 512;

    // Six factors of four bytes, padded to the hardware's 64-byte record.
    private const uint TessFactorStride = 64;

    // NUM_PATCHES is 15, but the console gives a threadgroup 61,440 bytes of LDS
    // where Apple caps it at 32,768, so the draw is split into groups that fit.
    // PATCHES_PER_GROUP overrides it.
    private static uint PatchesPerGroup =>
        uint.TryParse(Environment.GetEnvironmentVariable("PATCHES_PER_GROUP"), out var n) && n > 0
            ? n
            // The console fits all fifteen in one group; Apple's 32 KB ceiling
            // fits one, and the per-group offchip offset makes that equivalent.
            : 1;

    // VGT_LS_HS_CONFIG in fw_flow_h's header is 0x0004100F: HS_NUM_INPUT_CP and
    // HS_NUM_OUTPUT_CP are both 16 and NUM_PATCHES is 15. The seeded lattice is
    // 165 vec4 entries, so a non-indexed patch list of 16 input control points
    // covers ten patches. PATCH_COUNT overrides it for probing.
    private const ulong OitAddress = 0x11F7200UL;
    private const ulong OitNodeAddress = 0x0A00_0000;
    private const ulong OitCounterAddress = 0x0B00_0000;
    private const ulong ResolveVertexTableAddress = 0x0C00_0000;
    private const ulong ResolvePositionAddress = 0x0D00_0000;
    private const ulong ResolveUvAddress = 0x0E00_0000;
    private const ulong ResolveMatrixAddress = 0x0F00_0000;
    private const ulong PostConstantsAddress = 0x1100_0000;
    private const ulong PostImageAddress = 0x4000_0000_0UL;

    // The stage table's 0xC84 is the program's own extent; the decoder is
    // given slack past it and stops at s_endpgm.
    private const int OitLength = 0x1400;
    private const int ResolveOffset = 0x11F8100;
    private const int ResolveLength = 0x3D0;
    private const int FullscreenVertexOffset = 0x11F5200;
    private const int FullscreenVertexLength = 0xCC;
    private const int BlurHorizontalOffset = 0x11F4800;
    private const int BlurVerticalOffset = 0x11F4D00;
    private const int BlurLength = 0x35C;
    private const int FxaaOffset = 0x11F8700;
    private const int FxaaLength = 0xA00;
    private const int TextureDescriptorBlob = 0x10029E0 + 0x4000;

    /// <summary>
    /// Compiles <c>fw_oit_p</c>, the wave surface's own fragment stage.
    /// </summary>
    /// <remarks>
    /// Its header carries <c>SPI_PS_INPUT_ENA</c> and <c>SPI_PS_INPUT_ADDR</c>
    /// of <c>0x302</c> and <c>SPI_SHADER_PGM_RSRC2 = 0x18</c>, so twelve user
    /// SGPRs. The program reads the colour/light block and the blur parameters
    /// out of the same FirstWave constant buffer the geometry stages use, so it
    /// is handed the same descriptor block rather than a private one.
    /// </remarks>
    private static bool TryCompileOit(
        byte[] image, byte[] constants, byte[] descriptorBlock,
        uint width, uint height, int globalBufferBase, int totalGlobalBufferCount,
        out byte[] spirv, out byte[][] buffers, out string error)
    {
        spirv = [];
        buffers = [];
        var text = new byte[OitLength];
        // The stage table's values are already file offsets, as the local and
        // hull slices above use them.
        image.AsSpan((int)OitAddress, OitLength).CopyTo(text);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(OitAddress, text);
        memory.AddRegion(ConstantsAddress, constants);
        memory.AddRegion(DescriptorBlockAddress, descriptorBlock);
        var pixelCount = checked((int)(width * height));
        var oitNodeBytes = int.TryParse(
                Environment.GetEnvironmentVariable("OIT_NODE_BYTES"), out var nodeBytes)
            && nodeBytes > 0
            ? nodeBytes
            // Four depth-sorted fragments, each two dwords, per pixel.
            : checked(pixelCount * 8 * sizeof(uint));
        var oitCounterBytes = int.TryParse(
                Environment.GetEnvironmentVariable("OIT_COUNTER_BYTES"), out var counterBytes)
            && counterBytes > 0
            ? counterBytes
            : checked(pixelCount * sizeof(uint));
        memory.AddRegion(OitNodeAddress, new byte[oitNodeBytes]);
        memory.AddRegion(OitCounterAddress, new byte[oitCounterBytes]);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(context, OitAddress, out var program, out error))
        {
            return false;
        }

        Console.WriteLine($"oit     : OK - {program.Instructions.Count} instructions");

        if (Environment.GetEnvironmentVariable("OIT_DUMP") == "1")
        {
            foreach (var i in program.Instructions)
            {
                var op = i.Opcode;
                if (i.Encoding is not Gen5ShaderEncoding.Vop1 and not Gen5ShaderEncoding.Vop2
                    and not Gen5ShaderEncoding.Vop3 and not Gen5ShaderEncoding.Vopc
                    and not Gen5ShaderEncoding.Sop1 and not Gen5ShaderEncoding.Sop2
                    and not Gen5ShaderEncoding.Sopk and not Gen5ShaderEncoding.Sopp
                    and not Gen5ShaderEncoding.Sopc)
                {
                    var src = string.Join(", ", i.Sources);
                    var dst = string.Join(", ", i.Destinations);
                    Console.WriteLine($"          {i.Pc:X4}  {i.Encoding,-10} {op,-26} dst[{dst}] src[{src}]");
                }
            }
        }

        // The twelve user SGPRs are laid out by the program's own register use:
        // every SBufferLoad takes s8 as its base, the buffer_atomic_add at
        // 0x928 goes through s[4:7], and the node stores and loads at 0xB2C,
        // 0xB34, 0xB64..0xB7C, 0xC70 and 0xC78 go through s[0:3]. So s[0:3] is
        // the OIT node list, s[4:7] its counter, and s[8:11] the constants.
        //
        // The two OIT buffers are scratch: every byte in them is written by this
        // shader and read back by fw_comp_oit_p, so sizing them is host policy
        // in the same way the render target is, and none of their contents are
        // invented here.
        var userData = new uint[16];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
            OitNodeAddress, sizeof(uint), oitNodeBytes / sizeof(uint));
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(4, 4)),
            OitCounterAddress, sizeof(uint), oitCounterBytes / sizeof(uint));
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(8, 4)),
            ConstantsAddress, 0, constants.Length);

        var state = new Gen5ShaderState(program, userData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            return false;
        }

        // The general evaluator caps snapshots at 16 MiB. OIT scratch is
        // host-created, starts clear, and has a shader-proven per-pixel extent,
        // so size these two bindings to their descriptors without changing the
        // compiler-wide safety cap.
        var resizedBindings = evaluation.GlobalMemoryBindings.Select(binding =>
        {
            var size = binding.BaseAddress switch
            {
                OitNodeAddress => oitNodeBytes,
                OitCounterAddress => oitCounterBytes,
                _ => binding.DataLength,
            };
            if (size == binding.DataLength)
            {
                return binding;
            }

            var replacement = binding with
            {
                Data = new byte[size],
                DataLength = size,
                DataPooled = false,
            };
            replacement.Writable = binding.Writable;
            replacement.WriteBackToGuest = false;
            return replacement;
        }).ToArray();
        evaluation = evaluation with { GlobalMemoryBindings = resizedBindings };

        Console.WriteLine(
            $"oit     : {evaluation.GlobalMemoryBindings.Count} buffer(s), " +
            $"{evaluation.ImageBindings.Count} image(s)");
        foreach (var b in evaluation.GlobalMemoryBindings)
        {
            Console.WriteLine($"          base=0x{b.BaseAddress:X8} {b.DataLength,9:N0} bytes" +
                $"{(b.Writable ? " (writable)" : string.Empty)}");
        }

        // The vertex and fragment stages share one descriptor array, so the
        // fragment's buffers are placed after the vertex's rather than on top.
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                state, evaluation, Gen5PixelOutputKind.Float, out var compiled, out error,
                globalBufferBase: globalBufferBase,
                totalGlobalBufferCount: totalGlobalBufferCount,
                pixelInputEnable: 0x302, pixelInputAddress: 0x302,
                waveLaneCount: uint.TryParse(
                    Environment.GetEnvironmentVariable("OIT_WAVE"), out var ow) ? ow : 32u))
        {
            return false;
        }

        buffers = new byte[compiled.GlobalMemoryBindings.Count][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            var data = new byte[binding.DataLength];
            if (binding.BaseAddress == ConstantsAddress)
            {
                constants.AsSpan(0, Math.Min(constants.Length, data.Length)).CopyTo(data);
            }

            buffers[i] = data;
        }

        Console.WriteLine($"oit     : spirv OK - {compiled.Spirv.Length:N0} bytes");
        var oitOut = Environment.GetEnvironmentVariable("OIT_SPIRV_OUT");
        if (!string.IsNullOrEmpty(oitOut))
        {
            File.WriteAllBytes(oitOut, compiled.Spirv);
        }

        var oitPatched = Environment.GetEnvironmentVariable("OIT_FS");
        spirv = !string.IsNullOrEmpty(oitPatched) && File.Exists(oitPatched)
            ? File.ReadAllBytes(oitPatched)
            : compiled.Spirv;
        return true;
    }

    private static bool TryBuildOitResolve(
        byte[] image, byte[] constants, byte[] nodes, byte[] counters,
        out ParticleComputeRunner.ParticleDraw draw, out string error)
    {
        draw = default;

        // A three-vertex oversized triangle avoids a diagonal seam.
        var positions = new float[]
        {
            -1f, -1f, 0f, 1f,
             3f, -1f, 0f, 1f,
            -1f,  3f, 0f, 1f,
        };
        var uvs = new float[] { 0f, 0f, 2f, 0f, 0f, 2f };
        var positionBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            positions.AsSpan()).ToArray();
        var uvBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(uvs.AsSpan()).ToArray();
        var matrix = new byte[16 * sizeof(float)];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.TryWriteBytes(matrix.AsSpan((i * 4 + i) * sizeof(float)), 1f);
        }

        const uint selectXyzw = 4u | (5u << 3) | (6u << 6) | (7u << 9);
        const uint selectXy01 = 4u | (5u << 3) | (0u << 6) | (1u << 9);
        var vertexTable = new byte[32];
        WriteVertexDescriptor(
            vertexTable.AsSpan(0, 16), ResolvePositionAddress, 16, 3,
            selectXyzw | (77u << 12));
        WriteVertexDescriptor(
            vertexTable.AsSpan(16, 16), ResolveUvAddress, 8, 3,
            selectXy01 | (68u << 12));

        var vertexMemory = new FirstWaveProbe.FlatMemory();
        vertexMemory.AddRegion(ProgramAddress, Slice(
            image, FullscreenVertexOffset, FullscreenVertexLength));
        vertexMemory.AddRegion(ResolveVertexTableAddress, vertexTable);
        vertexMemory.AddRegion(ResolvePositionAddress, positionBytes);
        vertexMemory.AddRegion(ResolveUvAddress, uvBytes);
        vertexMemory.AddRegion(ResolveMatrixAddress, matrix);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out error))
        {
            error = $"resolve vertex decode: {error}";
            return false;
        }

        var vertexUserData = new uint[14];
        vertexUserData[3] = 0x4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            ResolveMatrixAddress, 0, matrix.Length);
        BitConverter.TryWriteBytes(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(12, 2)),
            ResolveVertexTableAddress);
        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            error = $"resolve vertex evaluate: {error}";
            return false;
        }

        var pixelMemory = new FirstWaveProbe.FlatMemory();
        pixelMemory.AddRegion(ProgramAddress, Slice(image, ResolveOffset, ResolveLength));
        pixelMemory.AddRegion(ConstantsAddress, constants);
        pixelMemory.AddRegion(OitNodeAddress, nodes);
        pixelMemory.AddRegion(OitCounterAddress, counters);
        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            error = $"resolve pixel decode: {error}";
            return false;
        }

        var pixelUserData = new uint[12];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan(0, 4)),
            OitNodeAddress, sizeof(uint), nodes.Length / sizeof(uint));
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan(4, 4)),
            OitCounterAddress, sizeof(uint), counters.Length / sizeof(uint));
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan(8, 4)),
            ConstantsAddress, 0, constants.Length);
        var pixelState = new Gen5ShaderState(
            pixelProgram, pixelUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            error = $"resolve pixel evaluate: {error}";
            return false;
        }

        var resizedPixelBindings = pixelEvaluation.GlobalMemoryBindings.Select(binding =>
        {
            var source = binding.BaseAddress switch
            {
                OitNodeAddress => nodes,
                OitCounterAddress => counters,
                _ => null,
            };
            if (source is null || binding.DataLength == source.Length)
            {
                return binding;
            }

            var replacement = binding with
            {
                Data = source,
                DataLength = source.Length,
                DataPooled = false,
            };
            replacement.Writable = binding.Writable;
            replacement.WriteBackToGuest = false;
            return replacement;
        }).ToArray();
        pixelEvaluation = pixelEvaluation with { GlobalMemoryBindings = resizedPixelBindings };

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = pixelEvaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelBufferCount;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState, vertexEvaluation, out var vertexShader, out error,
                globalBufferBase: 0, totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 1))
        {
            error = $"resolve vertex spirv: {error}";
            return false;
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState, pixelEvaluation, Gen5PixelOutputKind.Float,
                out var pixelShader, out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x302, pixelInputAddress: 0x302))
        {
            error = $"resolve pixel spirv: {error}";
            return false;
        }

        var contents = new byte[totalBufferCount][];
        for (var i = 0; i < vertexBufferCount; i++)
        {
            var binding = vertexShader.GlobalMemoryBindings[i];
            var source = binding.BaseAddress switch
            {
                ResolveVertexTableAddress => vertexTable,
                ResolvePositionAddress => positionBytes,
                ResolveUvAddress => uvBytes,
                ResolveMatrixAddress => matrix,
                _ => Array.Empty<byte>(),
            };
            contents[i] = new byte[binding.DataLength];
            source.AsSpan(0, Math.Min(source.Length, contents[i].Length)).CopyTo(contents[i]);
        }

        for (var i = 0; i < pixelBufferCount; i++)
        {
            var binding = pixelShader.GlobalMemoryBindings[i];
            var source = binding.BaseAddress switch
            {
                ConstantsAddress => constants,
                OitNodeAddress => nodes,
                OitCounterAddress => counters,
                _ => Array.Empty<byte>(),
            };
            var slot = vertexBufferCount + i;
            contents[slot] = new byte[binding.DataLength];
            source.AsSpan(0, Math.Min(source.Length, contents[slot].Length)).CopyTo(contents[slot]);
        }

        draw = new ParticleComputeRunner.ParticleDraw(
            vertexShader.Spirv, pixelShader.Spirv, contents, 3, null, Additive: false);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Builds one of FirstWave's native fullscreen image post-passes. The two
    /// blur stages take the 16-byte BlurParameters record at constants +0x170;
    /// FXAA takes only the source image and sampler. The input image itself is
    /// host render-target plumbing, while both shader programs and their
    /// </summary>
    private static bool TryBuildPostPass(
        byte[] image, byte[] constants, byte[] sourceRgba, uint width, uint height,
        int pixelOffset, int pixelLength, bool usesBlurParameters, string label,
        out ParticleComputeRunner.ParticleDraw draw, out string error)
    {
        draw = default;

        var positions = new float[]
        {
            -1f, -1f, 0f, 1f,
             3f, -1f, 0f, 1f,
            -1f,  3f, 0f, 1f,
        };
        var uvs = new float[] { 0f, 0f, 2f, 0f, 0f, 2f };
        var positionBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(
            positions.AsSpan()).ToArray();
        var uvBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(uvs.AsSpan()).ToArray();
        var matrix = new byte[16 * sizeof(float)];
        for (var i = 0; i < 4; i++)
        {
            BitConverter.TryWriteBytes(matrix.AsSpan((i * 4 + i) * sizeof(float)), 1f);
        }

        const uint selectXyzw = 4u | (5u << 3) | (6u << 6) | (7u << 9);
        const uint selectXy01 = 4u | (5u << 3) | (0u << 6) | (1u << 9);
        var vertexTable = new byte[32];
        WriteVertexDescriptor(
            vertexTable.AsSpan(0, 16), ResolvePositionAddress, 16, 3,
            selectXyzw | (77u << 12));
        WriteVertexDescriptor(
            vertexTable.AsSpan(16, 16), ResolveUvAddress, 8, 3,
            selectXy01 | (68u << 12));

        var vertexMemory = new FirstWaveProbe.FlatMemory();
        vertexMemory.AddRegion(ProgramAddress, Slice(
            image, FullscreenVertexOffset, FullscreenVertexLength));
        vertexMemory.AddRegion(ResolveVertexTableAddress, vertexTable);
        vertexMemory.AddRegion(ResolvePositionAddress, positionBytes);
        vertexMemory.AddRegion(ResolveUvAddress, uvBytes);
        vertexMemory.AddRegion(ResolveMatrixAddress, matrix);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out error))
        {
            error = $"{label} vertex decode: {error}";
            return false;
        }

        var vertexUserData = new uint[14];
        vertexUserData[3] = 0x4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            ResolveMatrixAddress, 0, matrix.Length);
        BitConverter.TryWriteBytes(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(12, 2)),
            ResolveVertexTableAddress);
        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            error = $"{label} vertex evaluate: {error}";
            return false;
        }

        var blurParameters = new byte[16];
        if (usesBlurParameters)
        {
            constants.AsSpan(0x170, blurParameters.Length).CopyTo(blurParameters);
        }

        var pixelMemory = new FirstWaveProbe.FlatMemory();
        pixelMemory.AddRegion(ProgramAddress, Slice(image, pixelOffset, pixelLength));
        if (usesBlurParameters)
        {
            pixelMemory.AddRegion(PostConstantsAddress, blurParameters);
        }

        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            error = $"{label} pixel decode: {error}";
            return false;
        }

        // s[0:7] = source T#, s[8:11] = sampler, and for blur only
        // all format/type fields native; only its host-side identity changes.
        var pixelUserData = new uint[20];
        for (var i = 0; i < 8; i++)
        {
            pixelUserData[i] = BitConverter.ToUInt32(
                image, TextureDescriptorBlob + 0x10 + (i * sizeof(uint)));
        }
        pixelUserData[0] = (uint)(PostImageAddress & uint.MaxValue);
        pixelUserData[1] = (pixelUserData[1] & 0xFFFF_FF00u) | (uint)(PostImageAddress >> 32);
        if (usesBlurParameters)
        {
            FirstWaveProbe.WriteBufferDescriptorPublic(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixelUserData.AsSpan(12, 4)),
                PostConstantsAddress, 0, blurParameters.Length);
        }

        var pixelState = new Gen5ShaderState(
            pixelProgram, pixelUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            error = $"{label} pixel evaluate: {error}";
            return false;
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = pixelEvaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelBufferCount;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState, vertexEvaluation, out var vertexShader, out error,
                globalBufferBase: 0, totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 1))
        {
            error = $"{label} vertex spirv: {error}";
            return false;
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState, pixelEvaluation, Gen5PixelOutputKind.Float,
                out var pixelShader, out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x2, pixelInputAddress: 0x2))
        {
            error = $"{label} pixel spirv: {error}";
            return false;
        }

        var contents = new byte[totalBufferCount][];
        for (var i = 0; i < vertexBufferCount; i++)
        {
            var binding = vertexShader.GlobalMemoryBindings[i];
            var source = binding.BaseAddress switch
            {
                ResolveVertexTableAddress => vertexTable,
                ResolvePositionAddress => positionBytes,
                ResolveUvAddress => uvBytes,
                ResolveMatrixAddress => matrix,
                _ => Array.Empty<byte>(),
            };
            contents[i] = new byte[binding.DataLength];
            source.AsSpan(0, Math.Min(source.Length, contents[i].Length)).CopyTo(contents[i]);
        }

        for (var i = 0; i < pixelBufferCount; i++)
        {
            var binding = pixelShader.GlobalMemoryBindings[i];
            var source = binding.BaseAddress == PostConstantsAddress
                ? blurParameters
                : Array.Empty<byte>();
            var slot = vertexBufferCount + i;
            contents[slot] = new byte[binding.DataLength];
            source.AsSpan(0, Math.Min(source.Length, contents[slot].Length)).CopyTo(contents[slot]);
        }

        var images = pixelShader.ImageBindings.Select(_ =>
            new ParticleComputeRunner.GuestImage(
                sourceRgba, width, height, Silk.NET.Vulkan.Format.R8G8B8A8Unorm)).ToArray();
        Console.WriteLine(
            $"{label,-8}: {pixelProgram.Instructions.Count} instructions, " +
            $"{images.Length} image(s), {pixelBufferCount} buffer(s)");

        draw = new ParticleComputeRunner.ParticleDraw(
            vertexShader.Spirv, pixelShader.Spirv, contents, 3, null, false, images);
        error = string.Empty;
        return true;
    }

    // Each patch owns 16 output control points at a 32-byte stride: 512 bytes.
    private static int GetPatchRingBytes(int patchCount) =>
        int.TryParse(Environment.GetEnvironmentVariable("RING_BYTES"), out var rb) && rb > 0
            ? rb
            : Math.Max(0x4000, patchCount * (int)PatchRingStride);
    private const int TessFactorBytes = 0x1000;

    private static void WriteVertexDescriptor(
        Span<byte> destination, ulong address, int stride, int records, uint word3)
    {
        FirstWaveProbe.WriteBufferDescriptorPublic(destination, address, stride, records);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], word3);
    }

    internal static int Run(
        string eboot, string constantsPath, string seedsPath, string? drawStreamPath)
    {
        var image = File.ReadAllBytes(eboot);

        var merged = new byte[LocalLength + HullLength];
        Array.Copy(image, LocalOffset, merged, 0, LocalLength);
        Array.Copy(image, HullOffset, merged, LocalLength, HullLength);

        var constants = new byte[0x200];
        if (File.Exists(constantsPath))
        {
            var recovered = File.ReadAllBytes(constantsPath);
            recovered.AsSpan(0, Math.Min(recovered.Length, constants.Length)).CopyTo(constants);
        }

        // FUN_c6c70 turns the constructor's compact seed tables into the actual
        // draw input: 400 patches x 16 control points, interleaved at 52 bytes.
        // The two shader descriptors view that one stream at offsets 0 and 16.
        const int nativeRecordCount = 6400;
        const int nativeRecordStride = 52;
        const int nativeStreamBytes = nativeRecordCount * nativeRecordStride;
        var hasDrawStream = !string.IsNullOrEmpty(drawStreamPath);
        var drawStream = Array.Empty<byte>();
        if (hasDrawStream)
        {
            if (!File.Exists(drawStreamPath))
            {
                Console.Error.WriteLine($"stream  : missing {drawStreamPath}");
                return 2;
            }

            drawStream = File.ReadAllBytes(drawStreamPath);
            if (drawStream.Length != nativeStreamBytes)
            {
                Console.Error.WriteLine(
                    $"stream  : expected {nativeStreamBytes:N0} bytes, got {drawStream.Length:N0}");
                return 2;
            }

            Console.WriteLine(
                $"stream  : {nativeRecordCount:N0} native records x {nativeRecordStride} bytes");
        }

        // Legacy seed binding remains available only as a diagnostic baseline.
        // Those 165 vec4s are constructor input, not fw_flow_vl's final stream.
        var seeds = !hasDrawStream ? File.ReadAllBytes(seedsPath) : Array.Empty<byte>();
        var latticeBytes = 165 * 16;
        var lattice = new byte[latticeBytes];
        // The seed block holds 13 pairs. RING_REPEAT tiles them across a wider
        // record count, to test whether the ring's 26 entries are what bounds
        // the surface - the local section fetches it with the same vertex index
        // as the lattice, so anything past entry 25 reads out of range.
        var ringRecords =
            int.TryParse(Environment.GetEnvironmentVariable("RING_RECORDS"), out var rr) && rr > 0
                ? rr
                : 26;
        var ring = new byte[ringRecords * 16];
        seeds.AsSpan(0, Math.Min(latticeBytes, seeds.Length)).CopyTo(lattice);
        if (seeds.Length >= latticeBytes + 26 * 16)
        {
            for (var i = 0; i < ringRecords; i++)
            {
                seeds.AsSpan(latticeBytes + (i % 26) * 16, 16).CopyTo(ring.AsSpan(i * 16, 16));
            }
        }

        // buffer_load_format_* fetches through the V#'s own FORMAT field, so a
        // descriptor with word 3 left at zero returns nothing and the local
        // section's normalise turns into a NaN. Word 3 is
        // dst_sel_x|y|z|w in bits 11:0 and the RDNA2 unified FORMAT in 18:12;
        // 74 is 32_32_32_FLOAT and 77 is 32_32_32_32_FLOAT.
        const uint selectXyzw = 4u | (5u << 3) | (6u << 6) | (7u << 9);
        const uint selectXyz1 = 4u | (5u << 3) | (6u << 6) | (1u << 9);
        var vertexTable = new byte[0x100];
        if (hasDrawStream)
        {
            WriteVertexDescriptor(
                vertexTable.AsSpan(0x00, 16), LatticeAddress,
                nativeRecordStride, nativeRecordCount, selectXyzw | (77u << 12));
            WriteVertexDescriptor(
                vertexTable.AsSpan(0x10, 16), LatticeAddress + 16,
                nativeRecordStride, nativeRecordCount, selectXyz1 | (74u << 12));
        }
        else
        {
            WriteVertexDescriptor(
                vertexTable.AsSpan(0x00, 16), LatticeAddress, 16, 165,
                selectXyzw | (77u << 12));
            WriteVertexDescriptor(
                vertexTable.AsSpan(0x10, 16),
                Environment.GetEnvironmentVariable("STREAM2") == "ring"
                    ? RingAddress
                    : LatticeAddress,
                16,
                Environment.GetEnvironmentVariable("STREAM2") == "ring"
                    ? ringRecords
                    : LatticeEntries,
                selectXyz1 | (74u << 12));
        }

        var maximumPatchCount = hasDrawStream ? nativeRecordCount / (int)ControlPoints : int.MaxValue;
        var patchCount = int.TryParse(Environment.GetEnvironmentVariable("PATCH_COUNT"), out var pc)
            && pc > 0
            ? Math.Min(pc, maximumPatchCount)
            : hasDrawStream ? maximumPatchCount : 15;
        var groupCount = (uint)((patchCount + PatchesPerGroup - 1) / PatchesPerGroup);
        var patchRingBytes = GetPatchRingBytes(patchCount);
        Console.WriteLine($"patches : {patchCount:N0}, ring stride {PatchRingStride} bytes");

        var descriptorBlock = new byte[0x100];
        FirstWaveProbe.WriteBufferDescriptorPublic(
            descriptorBlock.AsSpan(0x20, 16), TessFactorAddress, 0, TessFactorBytes);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            descriptorBlock.AsSpan(0x30, 16), PatchRingAddress, 0, patchRingBytes);

        var globalTable = new byte[0x100];
        BitConverter.TryWriteBytes(globalTable.AsSpan(0x00), DescriptorBlockAddress);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, merged);
        memory.AddRegion(ConstantsAddress, constants);
        memory.AddRegion(VertexTableAddress, vertexTable);
        memory.AddRegion(LatticeAddress, hasDrawStream ? drawStream : lattice);
        if (!hasDrawStream)
        {
            memory.AddRegion(RingAddress, ring);
        }
        memory.AddRegion(GlobalTableAddress, globalTable);
        memory.AddRegion(DescriptorBlockAddress, descriptorBlock);
        memory.AddRegion(PatchRingAddress, new byte[patchRingBytes]);
        memory.AddRegion(TessFactorAddress, new byte[TessFactorBytes]);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeMergedProgram(
                context, ProgramAddress, out var program, out var error))
        {
            Console.Error.WriteLine($"decode  : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"decode  : OK - {program.Instructions.Count} instructions (local + hull)");

        // s0..s7 are system SGPRs for a merged local+hull wave; the six user
        // SGPRs start at s8. s3 is the merged wave info the prologue turns into
        // EXEC, and s[0:1] is the global table the hull reads its descriptor
        // block through.
        var userData = new uint[14];
        BitConverter.TryWriteBytes(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(0, 2)),
            GlobalTableAddress);
        userData[3] = ControlPoints | (ControlPoints << 8);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(8, 4)),
            ConstantsAddress, 0, constants.Length);
        BitConverter.TryWriteBytes(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(12, 2)),
            VertexTableAddress);

        var state = new Gen5ShaderState(
            program, userData, Metadata: null,
            // s2/s4 are the offchip and factor byte offsets; a single
            // threadgroup leaves both at zero, so no system SGPR is pinned.
            ComputeSystemRegisters: null,
            UserDataScalarRegisterBase: 0);

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            Console.Error.WriteLine($"evaluate: FAILED {error}");
            return 1;
        }

        Console.WriteLine(
            $"evaluate: OK - {evaluation.GlobalMemoryBindings.Count} buffer(s), " +
            $"{evaluation.ImageBindings.Count} image(s)");
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.WriteLine($"          base=0x{binding.BaseAddress:X8} {binding.DataLength,8:N0} bytes" +
                $"{(binding.Writable ? " (writable)" : string.Empty)}");
        }

        if (!Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, PatchesPerGroup * ControlPoints, 1, 1, out var compiled, out error,
                waveLaneCount: uint.TryParse(Environment.GetEnvironmentVariable("WAVE_LANES"), out var wl) ? wl : 32,
                // Apple GPUs cap threadgroup memory at 32 KB where the PS5 gives
                // a workgroup 64 KB. The merged wave's LDS reach here is
                // cpIndex*512 + patchId*32, so 32 KB covers it; an access past
                // the allocation would be reported by ldsAddressOutOfRange.
                ldsDwordCount: 32 * 1024 / sizeof(uint),
                // The local section reads v2 as the vertex index and v3 as the
                // LDS slot; the hull reads v1 as (controlPoint << 8) | patchId,
                // giving it a ring address of patch*512 + point*32 - the
                // 32-byte stride fw_flow_dv reads sixteen control points at.
                mergedWaveSeeding: new Gen5MergedWaveVgprSeeding(
                    VertexIndexVgpr: uint.TryParse(
                        Environment.GetEnvironmentVariable("LS_INDEX_VGPR"), out var lv) ? lv : 2,
                    LdsSlotVgpr: 3,
                    LdsSlotStride: 16,   // patches are 16 control points apart in LDS
                    PackedIdVgpr: 1,
                    PatchId: 0,
                    PatchesPerGroup: PatchesPerGroup,
                    PatchIdFromWorkgroup: PatchesPerGroup == 1,
                    LatticeRowLength: uint.TryParse(
                        Environment.GetEnvironmentVariable("LATTICE_ROW"), out var lr) ? lr : 0,
                    LatticeWrapSpans: uint.TryParse(
                        Environment.GetEnvironmentVariable("LATTICE_WRAP"), out var lw) ? lw : 0,
                    OffchipOffsetSgpr: 2,
                    OffchipBytesPerGroup: PatchesPerGroup * PatchRingStride,
                    FactorOffsetSgpr: 4,
                    FactorBytesPerGroup: PatchesPerGroup * TessFactorStride)))
        {
            Console.Error.WriteLine($"spirv   : FAILED {error}");
            return 1;
        }

        Console.WriteLine($"spirv   : OK - {compiled.Spirv.Length:N0} bytes");
        var spirvOut = Environment.GetEnvironmentVariable("WAVE_SPIRV_OUT");
        if (!string.IsNullOrEmpty(spirvOut))
        {
            File.WriteAllBytes(spirvOut, compiled.Spirv);
        }

        var uploads = new byte[compiled.GlobalMemoryBindings.Count][];
        var tessIndex = -1;
        var ringIndex = -1;
        for (var i = 0; i < uploads.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            var data = new byte[binding.DataLength];
            byte[]? source = binding.BaseAddress switch
            {
                ConstantsAddress => constants,
                VertexTableAddress => vertexTable,
                LatticeAddress => hasDrawStream ? drawStream : lattice,
                RingAddress => ring,
                GlobalTableAddress => globalTable,
                DescriptorBlockAddress => descriptorBlock,
                _ => null,
            };

            if (hasDrawStream && binding.BaseAddress == LatticeAddress + 16)
            {
                drawStream.AsSpan(16, Math.Min(drawStream.Length - 16, data.Length)).CopyTo(data);
                source = null;
            }

            if (binding.BaseAddress == TessFactorAddress)
            {
                tessIndex = i;
            }
            else if (binding.BaseAddress == PatchRingAddress)
            {
                ringIndex = i;
            }

            source?.AsSpan(0, Math.Min(source.Length, data.Length)).CopyTo(data);
            uploads[i] = data;
        }

        var hullPatched = Environment.GetEnvironmentVariable("HULL_CS");
        var hullSpirv = !string.IsNullOrEmpty(hullPatched) && File.Exists(hullPatched)
            ? File.ReadAllBytes(hullPatched)
            : compiled.Spirv;

        byte[][] results;
        using (var runner = new ParticleComputeRunner())
        {
            Console.WriteLine($"device  : {runner.DeviceName}");
            // The last argument bounds total threads, not the group size.
            results = runner.Dispatch(
                hullSpirv, uploads, groupCount, (uint)(patchCount * ControlPoints));
        }

        Console.WriteLine("dispatch: OK");

        if (ringIndex >= 0)
        {
            var ringData = results[ringIndex];
            var ringOut = Environment.GetEnvironmentVariable("RING_OUT");
            if (!string.IsNullOrEmpty(ringOut))
            {
                File.WriteAllBytes(ringOut, ringData);
            }

            var completePatches = 0;
            for (var p = 0; p < patchCount; p++)
            {
                var written = 0;
                for (var c = 0; c < ControlPoints; c++)
                {
                    var o = p * (int)PatchRingStride + c * 32;
                    if (o + 16 > ringData.Length)
                    {
                        break;
                    }

                    if (BitConverter.ToUInt32(ringData, o) != 0
                        || BitConverter.ToUInt32(ringData, o + 4) != 0)
                    {
                        written++;
                    }
                }

                completePatches += written == ControlPoints ? 1 : 0;
                if (p < 4 || written != ControlPoints)
                {
                    Console.WriteLine(
                        $"          patch {p}: {written}/{ControlPoints} control points");
                }
            }

            Console.WriteLine(
                $"          {completePatches:N0}/{patchCount:N0} patches complete");
        }

        if (tessIndex >= 0)
        {
            // The hull materialises 12.0 with v_cvt_f32_i32 from an inline
            // constant and stores six of them: four outer factors at +0x00 and
            // two inner at +0x10. Reading them back is the check that the whole
            // merged wave ran, not just compiled.
            var tess = results[tessIndex];
            var factors = new float[6];
            for (var i = 0; i < 4; i++)
            {
                factors[i] = BitConverter.ToSingle(tess, i * 4);
            }

            factors[4] = BitConverter.ToSingle(tess, 0x10);
            factors[5] = BitConverter.ToSingle(tess, 0x14);
            Console.WriteLine(
                $"tess    : outer=[{string.Join(", ", factors[..4].Select(f => f.ToString("G4")))}] " +
                $"inner=[{factors[4]:G4}, {factors[5]:G4}]");
        }

        if (ringIndex >= 0)
        {
            var ringOut = results[ringIndex];
            var written = 0;
            for (var i = 0; i < ringOut.Length; i++)
            {
                if (ringOut[i] != 0)
                {
                    written++;
                }
            }

            Console.WriteLine($"patches : {written:N0} of {ringOut.Length:N0} bytes written");
            // The hull addresses the ring as patch*512 + controlPoint*32, which
            // is the 32-byte stride fw_flow_dv reads its sixteen control points
            // at, so one patch's points are contiguous.
            var live = 0;
            for (var cp = 0; cp < ControlPoints; cp++)
            {
                var v = new float[4];
                for (var k = 0; k < 4; k++)
                {
                    v[k] = BitConverter.ToSingle(ringOut, (cp * 32) + (k * 4));
                }

                if (v[3] != 0f)
                {
                    live++;
                }

                if (cp < 4)
                {
                    Console.WriteLine(
                        $"          control point {cp}: ({v[0]:G6}, {v[1]:G6}, {v[2]:G6}, {v[3]:G6})");
                }
            }

            Console.WriteLine($"          {live} of {ControlPoints} control points written");
        }

        // The domain stage reads the same descriptor block the hull wrote
        // through - its ring V# is at +0x30 either way - plus the
        // worldViewProjection and world matrices at constant-buffer +0x80 and
        // +0xC0. Its only VGPR inputs are the two tessellation coordinates and
        // the patch id.
        var outPng = Environment.GetEnvironmentVariable("WAVE_OUT_PNG");
        if (string.IsNullOrEmpty(outPng))
        {
            return 0;
        }

        return RenderSurface(
            image, constants, descriptorBlock,
            ringIndex >= 0 ? results[ringIndex] : new byte[patchRingBytes],
            outPng, patchCount);
    }

    private const int DomainOffset = 0x11F5500;
    private const int DomainLength = 0xF68;
    private const int Segments = 12;

    private static int RenderSurface(
        byte[] image, byte[] constants, byte[] descriptorBlock, byte[] patchRing,
        string outPath, int patchCount)
    {
        var width = uint.TryParse(Environment.GetEnvironmentVariable("WAVE_W"), out var ww) ? ww : 1280u;
        var height = uint.TryParse(Environment.GetEnvironmentVariable("WAVE_H"), out var wh) ? wh : 720u;
        constants = (byte[])constants.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(constants.AsSpan(0x190), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(constants.AsSpan(0x194), height);

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, Slice(image, DomainOffset, DomainLength));
        memory.AddRegion(ConstantsAddress, constants);
        memory.AddRegion(DescriptorBlockAddress, descriptorBlock);
        memory.AddRegion(PatchRingAddress, patchRing);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var program, out var error))
        {
            Console.Error.WriteLine($"domain  : decode FAILED {error}");
            return 1;
        }

        Console.WriteLine($"domain  : OK - {program.Instructions.Count} instructions");

        // Segments x Segments quads, two clockwise triangles each.
        const uint vertices = Segments * Segments * 6;
        var userData = new uint[14];
        userData[3] = 0x4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(8, 4)),
            ConstantsAddress, 0, constants.Length);
        BitConverter.TryWriteBytes(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(12, 2)),
            DescriptorBlockAddress);

        var state = new Gen5ShaderState(program, userData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out error))
        {
            Console.Error.WriteLine($"domain  : evaluate FAILED {error}");
            return 1;
        }

        Console.WriteLine($"domain  : {evaluation.GlobalMemoryBindings.Count} buffer(s)");

        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                state, evaluation, out var compiled, out error,
                requiredVertexOutputCount: 5,
                domainSeeding: new Gen5TessellationDomainSeeding(
                    UVgpr: 5, VVgpr: 6, PatchVgpr: 7, Segments: Segments,
                    PatchId: uint.TryParse(
                        Environment.GetEnvironmentVariable("DOMAIN_PATCH_ID"), out var dp) ? dp : 0,
                    PatchIdFromInstance:
                        Environment.GetEnvironmentVariable("DOMAIN_PATCH_ID") is null,
                    OffchipOffsetSgpr: uint.TryParse(
                        Environment.GetEnvironmentVariable("DOMAIN_OFFCHIP_SGPR"), out var ds)
                        ? ds
                        : 6,
                    // The patch VGPR already selects the ring slot; supplying a
                    // byte offset as well double-counts it, the same way the
                    // hull's two indices did. DOMAIN_OFFCHIP_BYTES re-enables it.
                    OffchipBytesPerPatch: uint.TryParse(
                        Environment.GetEnvironmentVariable("DOMAIN_OFFCHIP_BYTES"), out var db)
                        ? db
                        : 0)))
        {
            Console.Error.WriteLine($"domain  : spirv FAILED {error}");
            return 1;
        }

        Console.WriteLine($"domain  : spirv OK - {compiled.Spirv.Length:N0} bytes");
        var domainOut = Environment.GetEnvironmentVariable("DOMAIN_SPIRV_OUT");
        if (!string.IsNullOrEmpty(domainOut))
        {
            File.WriteAllBytes(domainOut, compiled.Spirv);
        }

        var buffers = new byte[compiled.GlobalMemoryBindings.Count][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var binding = compiled.GlobalMemoryBindings[i];
            var data = new byte[binding.DataLength];
            var source = binding.BaseAddress switch
            {
                ConstantsAddress => constants,
                DescriptorBlockAddress => descriptorBlock,
                PatchRingAddress => patchRing,
                _ => null,
            };
            source?.AsSpan(0, Math.Min(source.Length, data.Length)).CopyTo(data);
            buffers[i] = data;
            Console.WriteLine(
                $"domain  : buffer[{i}] base 0x{binding.BaseAddress:X} len {binding.DataLength}");
        }

        // A patched module writes its clip output into one extra scratch buffer
        // appended past the shader's own bindings. Diagnostic only.
        if (Environment.GetEnvironmentVariable("DOMAIN_PROBE") == "1")
        {
            buffers = [.. buffers, new byte[vertices * 8 * sizeof(uint)]];
        }

        // fw_oit_p is the surface's own fragment stage. Its header gives
        // SPI_PS_INPUT_ENA/ADDR = 0x302 and SPI_PS_IN_CONTROL.NUM_INTERP = 5,
        // which is what the domain is asked to export. SURFACE_FS still
        // overrides it, for the placeholder used while the geometry was blind.
        byte[] fragmentSpirv;
        var oitBuffers = Array.Empty<byte[]>();
        var fragmentPath = Environment.GetEnvironmentVariable("SURFACE_FS");
        if (!string.IsNullOrEmpty(fragmentPath) && File.Exists(fragmentPath))
        {
            fragmentSpirv = File.ReadAllBytes(fragmentPath);
        }
        else
        {
            // fw_oit_p resolves three buffers of its own; they follow the
            // domain's in the shared descriptor array.
            const int oitBufferCount = 3;
            if (!TryCompileOit(
                    image, constants, descriptorBlock,
                    width, height, buffers.Length, buffers.Length + oitBufferCount,
                    out fragmentSpirv, out oitBuffers, out var oitError))
            {
                Console.Error.WriteLine($"oit     : FAILED {oitError}");
                return 1;
            }
        }

        var allBuffers = oitBuffers.Length > 0 ? [.. buffers, .. oitBuffers] : buffers;

        // fw_oit_p addresses its OIT arrays from screenDim at +0x190. The copy
        // above makes that host render state match this diagnostic target.
        using var runner = new ParticleComputeRunner();
        Console.WriteLine($"device  : {runner.DeviceName}");
        // DOMAIN_VS swaps in a patched module so the clip output can be read
        // back out of a storage buffer. Diagnostic only.
        var patched = Environment.GetEnvironmentVariable("DOMAIN_VS");
        var vertexSpirv = !string.IsNullOrEmpty(patched) && File.Exists(patched)
            ? File.ReadAllBytes(patched)
            : compiled.Spirv;

        var draw = new ParticleComputeRunner.ParticleDraw(
            vertexSpirv, fragmentSpirv, allBuffers, vertices, null, false,
            InstanceCount: (uint)patchCount);
        var rgba = runner.RenderParticleFrame([draw], width, height, out var after);

        if (oitBuffers.Length > 0 && after.Length > 0)
        {
            for (var i = 0; i < oitBuffers.Length; i++)
            {
                var slot = buffers.Length + i;
                if (slot >= after[0].Length)
                {
                    continue;
                }

                var data = after[0][slot];
                var nonzero = data.Count(value => value != 0);
                var first = data.Length >= sizeof(uint)
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data)
                    : 0;
                ulong dwordSum = 0;
                var nonzeroDwords = 0;
                for (var offset = 0; offset + sizeof(uint) <= data.Length; offset += sizeof(uint))
                {
                    var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(offset));
                    dwordSum += value;
                    nonzeroDwords += value != 0 ? 1 : 0;
                }
                Console.WriteLine(
                    $"oit     : buffer[{i}] {nonzero:N0}/{data.Length:N0} nonzero bytes, " +
                    $"{nonzeroDwords:N0} nonzero dwords, sum {dwordSum:N0}, first {first:N0}");
            }

            var expectedCounterBytes = checked((int)(width * height * sizeof(uint)));
            var expectedNodeBytes = checked(expectedCounterBytes * 8);
            var captured = after[0].Skip(buffers.Length).ToArray();
            var counters = captured.FirstOrDefault(data => data.Length == expectedCounterBytes);
            var nodes = captured.FirstOrDefault(data => data.Length == expectedNodeBytes);
            if (counters is not null && nodes is not null)
            {
                if (!TryBuildOitResolve(
                        image, constants, nodes, counters, out var resolveDraw, out var resolveError))
                {
                    Console.Error.WriteLine($"resolve : FAILED {resolveError}");
                    return 1;
                }

                rgba = runner.RenderParticleFrame([resolveDraw], width, height, out _);
                Console.WriteLine("resolve : fw_comp_oit_p rendered");

                var postPasses = new[]
                {
                    (Offset: BlurHorizontalOffset, Length: BlurLength,
                        Blur: true, Label: "blur-h"),
                    (Offset: BlurVerticalOffset, Length: BlurLength,
                        Blur: true, Label: "blur-v"),
                    (Offset: FxaaOffset, Length: FxaaLength,
                        Blur: false, Label: "fxaa"),
                };
                foreach (var pass in postPasses)
                {
                    if (!TryBuildPostPass(
                            image, constants, rgba, width, height,
                            pass.Offset, pass.Length, pass.Blur, pass.Label,
                            out var postDraw, out var postError))
                    {
                        Console.Error.WriteLine($"{pass.Label,-8}: FAILED {postError}");
                        return 1;
                    }

                    rgba = runner.RenderParticleFrame([postDraw], width, height, out _);
                    Console.WriteLine($"{pass.Label,-8}: rendered");
                }
            }
        }

        var clipOut = Environment.GetEnvironmentVariable("DOMAIN_CLIP_OUT");
        if (!string.IsNullOrEmpty(clipOut) && after.Length > 0)
        {
            var slot = int.TryParse(Environment.GetEnvironmentVariable("DOMAIN_CLIP_BUFFER"), out var cb) ? cb : 0;
            if (slot < after[0].Length)
            {
                File.WriteAllBytes(clipOut, after[0][slot]);
            }
        }

        var lit = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0 || rgba[i + 1] != 0 || rgba[i + 2] != 0)
            {
                lit++;
            }
        }

        Console.WriteLine($"surface : {lit:N0} lit pixels of {width * height:N0}");
        FirstWaveProbe.WritePngPublic(outPath, (int)width, (int)height, rgba);
        Console.WriteLine($"output  : {outPath}");
        return 0;
    }

    private static byte[] Slice(byte[] image, int offset, int length)
    {
        var text = new byte[length];
        Array.Copy(image, offset, text, 0, length);
        return text;
    }
}
