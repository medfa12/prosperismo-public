// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Executes <c>light_p</c>, the background's light-shaft and compositing layer.
///
/// instruction stream, <c>texFloor</c> and <c>texVolume</c> from the GNF blobs
/// <c>createIesTex</c> embeds at <c>0x1006AE0</c> and <c>0x10029E0</c>, the
/// <c>ColorCb</c> record replayed from the seeder at <c>0xEA786</c>, and the
/// pixel-input registers from the shader's own header. See
/// </summary>
internal static class LightLayerProbe
{
    private const ulong ProgramAddress = 0x1000_0000;
    private const ulong VolatileCbAddress = 0x0900_0000;
    private const ulong ColorCbAddress = 0x0A00_0000;
    private const ulong RectConstantsAddress = 0x0B00_0000;
    private const ulong TexFloorAddress = 0x1000_0000_0UL;
    private const ulong TexVolumeAddress = 0x2000_0000_0UL;
    private const ulong TexParticleAddress = 0x3000_0000_0UL;

    private const int LightPsOffset = 0x11F9700;
    private const int LightPsLength = 0x818;
    private const int RectVsOffset = 0x11EEE00;
    private const int RectVsLength = 0xCC;

    // createIesTex's two literal arguments.
    private const int TexFloorBlob = 0x1006AE0 + 0x4000;
    private const int TexVolumeBlob = 0x10029E0 + 0x4000;
    private const int GnfPayloadOffset = 0x100;
    private const int GnfPayloadLength = 0x4000;
    private const int TextureSize = 128;

    internal static int Render(
        string eboot, string outPath, string colorCbPath, byte[]? particleRgba,
        uint width, uint height)
    {
        var image = File.ReadAllBytes(eboot);
        if (!TryBuildDraw(image, colorCbPath, particleRgba, width, height, 0f,
                out var standalone, out var buildError))
        {
            Console.Error.WriteLine(buildError);
            return 1;
        }

        using var soloRunner = new ParticleComputeRunner();
        Console.WriteLine($"device  : {soloRunner.DeviceName}");
        var solo = soloRunner.RenderParticleFrame([standalone], width, height);
        FirstWaveProbe.WritePngPublic(outPath, (int)width, (int)height, solo);
        Console.WriteLine($"output  : {outPath}");
        return 0;
    }

    /// <summary>
    /// textures, the replayed <c>ColorCb</c> record, and the particle frame as
    /// <c>texP</c>.
    /// </summary>
    internal static bool TryBuildDraw(
        byte[] image, string colorCbPath, byte[]? particleRgba,
        uint width, uint height, float time,
        out ParticleComputeRunner.ParticleDraw draw, out string error)
    {
        draw = default;
        error = string.Empty;

        // ColorCb comes from tools/dump_wave_colour_presets.py, which replays
        // the seeder rather than reading the (runtime-initialised) table.
        var colorCb = new byte[0x100];
        if (!File.Exists(colorCbPath))
        {
            error = $"missing ColorCb record: {colorCbPath}";
            return false;
        }

        var record = File.ReadAllBytes(colorCbPath);
        record.AsSpan(0, Math.Min(record.Length, colorCb.Length)).CopyTo(colorCb);

        var volatileCb = new byte[0x100];
        BitConverter.TryWriteBytes(volatileCb.AsSpan(0x00, 4), time);
        // intensity = ease(obj+0xD0) * obj+0xD4. The constructor seeds
        // (+0xD0, +0xD4) = (1, 0) and the light-start path at 0xB80D7 sets
        // +0xD4 to 1.0, so a lit frame is ease(1) * 1 = 1. EA650 and the owner
        // update at B77E0 prove that opacity is the live owner fade and that
        // particleAlpha is owner opacity multiplied by the particle-owner
        // weight. A standalone fully visible probe uses 1 for both; environment
        // variables permit a caller to replay another exact owner
        // state without changing shader inputs.
        BitConverter.TryWriteBytes(
            volatileCb.AsSpan(0x04, 4),
            float.TryParse(Environment.GetEnvironmentVariable("LIGHT_OPACITY"), out var o) ? o : 1f);
        BitConverter.TryWriteBytes(
            volatileCb.AsSpan(0x08, 4),
            float.TryParse(Environment.GetEnvironmentVariable("LIGHT_INTENSITY"), out var n) ? n : 1f);
        BitConverter.TryWriteBytes(
            volatileCb.AsSpan(0x0C, 4),
            particleRgba is null
                ? 0f
                : float.TryParse(
                    Environment.GetEnvironmentVariable("LIGHT_PARTICLE_ALPHA"), out var a)
                    ? a
                    : 1f);

        var floor = ReadGnf(image, TexFloorBlob);
        var volume = ReadGnf(image, TexVolumeBlob);
        var particles = particleRgba ?? new byte[width * height * 4];

        var memory = new FirstWaveProbe.FlatMemory();
        memory.AddRegion(ProgramAddress, Slice(image, LightPsOffset, LightPsLength));
        memory.AddRegion(VolatileCbAddress, volatileCb);
        memory.AddRegion(ColorCbAddress, colorCb);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var program, out var stageError))
        {
            error = $"light decode: {stageError}";
            return false;
        }

        Console.WriteLine($"decode  : OK - {program.Instructions.Count} instructions");

        // light_p takes its resources straight from user SGPRs, no SRT
        // indirection: three T#s, then the two constant buffers.
        var userData = new uint[36];
        WriteImageDescriptor(userData.AsSpan(0, 8), image, TexFloorBlob, TexFloorAddress);
        WriteImageDescriptor(userData.AsSpan(8, 8), image, TexVolumeBlob, TexVolumeAddress);
        WriteImageDescriptor(userData.AsSpan(16, 8), image, TexVolumeBlob, TexParticleAddress);
        // texP is the particle target, so it is RGBA at the frame's own size.
        userData[16 + 1] = (userData[16 + 1] & 0xFFFF_FF00u) | 3u;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(24, 4)),
            VolatileCbAddress, 0, volatileCb.Length);
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan(28, 4)),
            ColorCbAddress, 0, colorCb.Length);

        var state = new Gen5ShaderState(program, userData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(context, state, out var evaluation, out stageError))
        {
            error = $"light evaluate: {stageError}";
            return false;
        }

        Console.WriteLine(
            $"evaluate: OK - {evaluation.GlobalMemoryBindings.Count} buffer(s), " +
            $"{evaluation.ImageBindings.Count} image(s)");

        // buffer contains the rectangle bounds and corresponding UV bounds.
        // The four sequential vertex ids select the four corners from their low
        var rectConstants = new byte[0x30];
        var rectValues = new[] { -1f, -1f, 1f, 1f, 0f, 0f, 1f, 1f };
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(rectValues.AsSpan())
            .CopyTo(rectConstants.AsSpan(0x10));

        var vertexMemory = new FirstWaveProbe.FlatMemory();
        vertexMemory.AddRegion(ProgramAddress, Slice(image, RectVsOffset, RectVsLength));
        vertexMemory.AddRegion(RectConstantsAddress, rectConstants);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out stageError))
        {
            error = $"rect vertex decode: {stageError}";
            return false;
        }

        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        FirstWaveProbe.WriteBufferDescriptorPublic(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            RectConstantsAddress, 0, rectConstants.Length);
        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out stageError))
        {
            error = $"rect vertex evaluate: {stageError}";
            return false;
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = evaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelBufferCount;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState, vertexEvaluation, out var vertexShader, out stageError,
                globalBufferBase: 0, totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 1))
        {
            error = $"rect vertex spirv: {stageError}";
            return false;
        }

        // programs for light_p, read from its shader header.
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                state, evaluation, Gen5PixelOutputKind.Float, out var pixelShader, out stageError,
                globalBufferBase: vertexBufferCount, totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x2, pixelInputAddress: 0x2))
        {
            error = $"light spirv: {stageError}";
            return false;
        }

        Console.WriteLine(
            $"spirv   : OK - rect {vertexShader.Spirv.Length:N0} + light {pixelShader.Spirv.Length:N0} bytes");

        var buffers = new byte[totalBufferCount][];
        for (var i = 0; i < buffers.Length; i++)
        {
            var fromVertex = i < vertexBufferCount;
            var binding = fromVertex
                ? vertexShader.GlobalMemoryBindings[i]
                : pixelShader.GlobalMemoryBindings[i - vertexBufferCount];
            var data = new byte[binding.DataLength];
            var source = binding.BaseAddress switch
            {
                RectConstantsAddress => rectConstants,
                VolatileCbAddress => volatileCb,
                ColorCbAddress => colorCb,
                _ => null,
            };
            source?.AsSpan(0, Math.Min(source.Length, data.Length)).CopyTo(data);
            buffers[i] = data;
        }

        // The three T#s are tagged in the low byte of word1, which is where a
        // real descriptor keeps base_address[47:40]: 1 = texFloor,
        // 2 = texVolume, 3 = texP.
        var images = new List<ParticleComputeRunner.GuestImage>();
        foreach (var binding in pixelShader.ImageBindings)
        {
            images.Add((binding.ResourceDescriptor[1] & 0xFF) switch
            {
                3 => new ParticleComputeRunner.GuestImage(
                    particles, width, height, Format.R8G8B8A8Unorm),
                2 => new ParticleComputeRunner.GuestImage(
                    volume, TextureSize, TextureSize, Format.R8Unorm),
                _ => new ParticleComputeRunner.GuestImage(
                    floor, TextureSize, TextureSize, Format.R8Unorm),
            });
        }

        Console.WriteLine($"images  : {images.Count} bound");
        for (var i = 0; i < pixelShader.ImageBindings.Count; i++)
        {
            var b = pixelShader.ImageBindings[i];
            var addr = ((ulong)b.ResourceDescriptor[1] << 32) | b.ResourceDescriptor[0];
            Console.WriteLine(
                $"          [{i}] pc=0x{b.Pc:X} {b.Opcode} base=0x{addr:X} " +
                $"{images[i].Width}x{images[i].Height} {images[i].Format}");
        }

        var spirvOut = Environment.GetEnvironmentVariable("LIGHT_SPIRV_OUT");
        if (!string.IsNullOrEmpty(spirvOut))
        {
            File.WriteAllBytes($"{spirvOut}.vs.spv", vertexShader.Spirv);
            File.WriteAllBytes($"{spirvOut}.ps.spv", pixelShader.Spirv);
        }

        // NPXS40087 12.40 EA290 calls setPrimitiveType(6) then drawIndexAuto(3):
        // triangle strip, three generated vertices. The earlier four-vertex
        draw = new ParticleComputeRunner.ParticleDraw(
            vertexShader.Spirv, pixelShader.Spirv, buffers, 3, null, false, images,
            Topology: PrimitiveTopology.TriangleStrip);
        return true;
    }

    /// <summary>
    /// Reads a GNF blob's base level and untiles it.
    ///
    /// <para>The blob is <c>0x4100</c> bytes with a <c>0xF8</c> header, and the
    /// payload starts at the next 256-byte boundary — <c>0x100</c> — which is
    /// exactly <c>0x4000</c>, the 128×128 single-channel base level the
    /// descriptor declares. Reading from <c>0xF8</c> instead scrambles every
    /// untiling attempt, which is what made this look unsolvable.</para>
    ///
    /// <para>The descriptor's <c>sw_mode</c> is 5: Gen5 Standard4KB. For a
    /// The bit mapping below is the repository Gen5 tiler verbatim
    /// (<c>Gen5Standard4KBOffsetInBlock</c>); the four blocks in this 128×128
    /// image are stored in row-major block order.</para>
    /// </summary>
    private static byte[] ReadGnf(byte[] image, int blob)
    {
        var payload = new byte[GnfPayloadLength];
        Array.Copy(image, blob + GnfPayloadOffset, payload, 0, GnfPayloadLength);

        const int blockWidth = 64;
        const int blockHeight = 64;
        const int blockBytes = 4096;
        var blocksPerRow = TextureSize / blockWidth;
        var linear = new byte[GnfPayloadLength];
        for (var y = 0; y < TextureSize; y++)
        {
            for (var x = 0; x < TextureSize; x++)
            {
                var localX = x & (blockWidth - 1);
                var localY = y & (blockHeight - 1);
                var offset = 0;
                offset ^= (localY << 4) & 0x1F0;
                offset ^= (localY << 5) & 0x400;
                offset ^= localX & 0x00F;
                offset ^= (localX << 5) & 0x200;
                offset ^= (localX << 6) & 0x800;

                var blockIndex = ((y / blockHeight) * blocksPerRow) + (x / blockWidth);
                linear[(y * TextureSize) + x] = payload[(blockIndex * blockBytes) + offset];
            }
        }

        return linear;
    }

    private static void WriteImageDescriptor(
        Span<uint> destination, byte[] image, int blob, ulong address)
    {
        for (var i = 0; i < 8; i++)
        {
            destination[i] = BitConverter.ToUInt32(image, blob + 0x10 + (i * 4));
        }

        destination[0] = (uint)address;
        destination[1] = (destination[1] & 0xFFFF_FF00u) | (uint)(address >> 32);
    }

    private static byte[] Slice(byte[] image, int offset, int length)
    {
        var text = new byte[length];
        Array.Copy(image, offset, text, 0, length);
        return text;
    }
}
