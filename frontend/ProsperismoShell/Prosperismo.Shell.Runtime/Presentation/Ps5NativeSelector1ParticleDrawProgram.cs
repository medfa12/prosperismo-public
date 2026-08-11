// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// The compiled NPXS40087 12.40 selector-1 small-particle draw program.
///
/// <para>This is the runtime port of <c>ParticleDrawProbe.BuildDraw</c>:
/// <c>particle_vv</c> and <c>particle_p</c> are translated once, their
/// rebuilds the guest buffer views over the live property/resource state.</para>
/// </summary>
public sealed class Ps5NativeSelector1ParticleDrawProgram
{
    private const ulong ProgramAddress = 0x1000_0000;
    private const ulong PropertyAddress = 0x0400_0000;
    private const ulong IdAddress = 0x0500_0000;
    private const ulong SrtVsPsAddress = 0x0600_0000;
    private const ulong ResourcesVsPsAddress = 0x0700_0000;
    private const ulong LargeImage0Address = 0x1_0000_0000;
    private const ulong LargeImage1Address = 0x2_0000_0000;

    private const int RecordStride = 0x44;
    private const int RecordCount = 0x1770;
    private const int ParticleVsOffset = 0x1201D00;
    private const int ParticleVsLength = 0x700;
    private const int ParticlePsOffset = 0x1201500;
    private const int ParticlePsLength = 0x800;
    private const int LargeParticleVsOffset = 0x1202C00;
    private const int LargeParticleVsLength = 0x600;
    private const int LargeParticlePsOffset = 0x1202400;
    private const int LargeParticlePsLength = 0x600;

    private readonly byte[] _vertexText;
    private readonly byte[] _pixelText;
    private readonly byte[] _vertexSpirv;
    private readonly byte[] _pixelSpirv;
    private readonly Gen5GlobalMemoryBinding[] _vertexBindings;
    private readonly Gen5GlobalMemoryBinding[] _pixelBindings;
    private readonly bool _isLarge;
    private readonly byte[]? _largeImageDescriptor0;
    private readonly byte[]? _largeImageDescriptor1;
    private readonly Gen5NggPrimitiveConnectivity _nggPrimitiveConnectivity;

    private Ps5NativeSelector1ParticleDrawProgram(
        byte[] vertexText,
        byte[] pixelText,
        byte[] vertexSpirv,
        byte[] pixelSpirv,
        IReadOnlyList<Gen5GlobalMemoryBinding> vertexBindings,
        IReadOnlyList<Gen5GlobalMemoryBinding> pixelBindings,
        Gen5NggPrimitiveConnectivity nggPrimitiveConnectivity,
        bool isLarge = false,
        byte[]? largeImageDescriptor0 = null,
        byte[]? largeImageDescriptor1 = null)
    {
        _vertexText = vertexText;
        _pixelText = pixelText;
        _vertexSpirv = vertexSpirv;
        _pixelSpirv = pixelSpirv;
        _vertexBindings = vertexBindings.ToArray();
        _pixelBindings = pixelBindings.ToArray();
        _nggPrimitiveConnectivity = nggPrimitiveConnectivity;
        _isLarge = isLarge;
        _largeImageDescriptor0 = largeImageDescriptor0;
        _largeImageDescriptor1 = largeImageDescriptor1;
    }

    public ReadOnlyMemory<byte> VertexSpirv => _vertexSpirv;

    public ReadOnlyMemory<byte> FragmentSpirv => _pixelSpirv;

    /// <summary>The host launch which realizes this shader's target-20 export.</summary>
    public Gen5NggPrimitiveConnectivity NggPrimitiveConnectivity =>
        _nggPrimitiveConnectivity;

    /// <summary>
    /// resource layout. The values supplied here are compile-time discovery
    /// inputs only; the returned program accepts fresh buffers thereafter.
    /// </summary>
    public static Ps5NativeSelector1ParticleDrawProgram Compile(
        ReadOnlySpan<byte> eboot,
        Ps5NativeSelector1ResourceFrame compileFrame,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids)
    {
        if (eboot.Length < ParticlePsOffset + ParticlePsLength ||
            properties.Length != Ps5NativeParticleComputeRequest.ParticlePropertyByteCount ||
            ids.Length != Ps5NativeParticleComputeRequest.ParticleIdByteCount ||
            compileFrame.Banks.Count != Ps5NativeSelector1ResourceFrame.BankCount)
        {
            throw new ArgumentException("invalid selector-1 draw compiler inputs");
        }

        var resources = CreateResources(compileFrame.GetBank(0).ResourcesVsPs.Span);
        var srt = CreateSrt(instance: 0, currentInstance: 0);
        var vertexText = Slice(eboot, ParticleVsOffset, ParticleVsLength);
        var pixelText = Slice(eboot, ParticlePsOffset, ParticlePsLength);

        var vertexMemory = new DrawMemory();
        vertexMemory.AddRegion(ProgramAddress, vertexText);
        AddGuestRegions(vertexMemory, srt, resources, properties, ids);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out var error))
        {
            throw new InvalidDataException($"particle_vv decode failed: {error}");
        }

        // s[3] is the NGG merged-wave info and s[8:11] is the only real user
        // data. These are the exact values used by ParticleDrawProbe.BuildDraw.
        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            SrtVsPsAddress,
            0,
            srt.Length);
        var vertexState = new Gen5ShaderState(
            vertexProgram,
            vertexUserData,
            Metadata: null,
            UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            throw new InvalidDataException($"particle_vv resource evaluation failed: {error}");
        }

        var pixelMemory = new DrawMemory();
        pixelMemory.AddRegion(ProgramAddress, pixelText);
        AddGuestRegions(pixelMemory, srt, resources, properties, ids);
        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            throw new InvalidDataException($"particle_p decode failed: {error}");
        }

        var pixelUserData = new uint[4];
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(pixelUserData.AsSpan()),
            SrtVsPsAddress,
            0,
            srt.Length);
        var pixelState = new Gen5ShaderState(
            pixelProgram,
            pixelUserData,
            Metadata: null,
            UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            throw new InvalidDataException($"particle_p resource evaluation failed: {error}");
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var pixelBufferCount = pixelEvaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelBufferCount;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState,
                vertexEvaluation,
                out var vertexShader,
                out error,
                globalBufferBase: 0,
                totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 6,
                nggPrimitiveConnectivity: Npxs40087GpuContract.CreateConnectivity(
                    Npxs40087ShellContract.SmallParticle)))
        {
            throw new InvalidDataException($"particle_vv SPIR-V translation failed: {error}");
        }

        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState,
                pixelEvaluation,
                Gen5PixelOutputKind.Float,
                out var pixelShader,
                out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x2,
                pixelInputAddress: 0x2))
        {
            throw new InvalidDataException($"particle_p SPIR-V translation failed: {error}");
        }

        var connectivity = vertexShader.NggPrimitiveConnectivity
            ?? throw new InvalidDataException(
                "particle_vv emitted no host-verified NGG primitive connectivity");
        return new Ps5NativeSelector1ParticleDrawProgram(
            vertexText,
            pixelText,
            vertexShader.Spirv,
            pixelShader.Spirv,
            vertexEvaluation.GlobalMemoryBindings,
            pixelEvaluation.GlobalMemoryBindings,
            connectivity);
    }

    /// <summary>
    /// pair. The two native T# descriptor templates are supplied separately
    /// from the host-portable PNG pixels owned by the renderer.
    /// </summary>
    public static Ps5NativeSelector1ParticleDrawProgram CompileLarge(
        ReadOnlySpan<byte> eboot,
        Ps5NativePatternResourceBank compileBank,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids,
        ReadOnlySpan<byte> particle0Descriptor,
        ReadOnlySpan<byte> particle1Descriptor)
    {
        if (eboot.Length < LargeParticleVsOffset + LargeParticleVsLength ||
            eboot.Length < LargeParticlePsOffset + LargeParticlePsLength ||
            compileBank.ResourcesVsPs.Length != 0xEC ||
            properties.Length != Ps5NativeParticleComputeRequest.ParticlePropertyByteCount ||
            ids.Length != Ps5NativeParticleComputeRequest.ParticleIdByteCount ||
            particle0Descriptor.Length < 32 || particle1Descriptor.Length < 32)
        {
            throw new ArgumentException("invalid coldboot large-particle compiler inputs");
        }

        var resources = CreateLargeResources(
            compileBank.ResourcesVsPs.Span,
            particle0Descriptor,
            particle1Descriptor,
            1920,
            1080);
        var srt = CreateSrt(instance: 0, currentInstance: 0, time: 0.0f, timeStep: 1.0f / 60.0f);
        var vertexText = Slice(eboot, LargeParticleVsOffset, LargeParticleVsLength);
        var pixelText = Slice(eboot, LargeParticlePsOffset, LargeParticlePsLength);

        var vertexMemory = new DrawMemory();
        vertexMemory.AddRegion(ProgramAddress, vertexText);
        AddGuestRegions(vertexMemory, srt, resources, properties, ids);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext, ProgramAddress, out var vertexProgram, out var error))
        {
            throw new InvalidDataException($"large_particle_vv decode failed: {error}");
        }

        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            SrtVsPsAddress,
            0,
            srt.Length);
        var vertexState = new Gen5ShaderState(
            vertexProgram, vertexUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext, vertexState, out var vertexEvaluation, out error))
        {
            throw new InvalidDataException($"large_particle_vv resource evaluation failed: {error}");
        }

        var pixelMemory = new DrawMemory();
        pixelMemory.AddRegion(ProgramAddress, pixelText);
        AddGuestRegions(pixelMemory, srt, resources, properties, ids);
        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext, ProgramAddress, out var pixelProgram, out error))
        {
            throw new InvalidDataException($"large_particle_p decode failed: {error}");
        }

        var pixelUserData = new uint[4];
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(pixelUserData.AsSpan()), SrtVsPsAddress, 0, srt.Length);
        var pixelState = new Gen5ShaderState(
            pixelProgram, pixelUserData, Metadata: null, UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext, pixelState, out var pixelEvaluation, out error))
        {
            throw new InvalidDataException($"large_particle_p resource evaluation failed: {error}");
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelEvaluation.GlobalMemoryBindings.Count;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState, vertexEvaluation, out var vertexShader, out error,
                globalBufferBase: 0,
                totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 5,
                nggPrimitiveConnectivity: Npxs40087GpuContract.CreateConnectivity(
                    Npxs40087ShellContract.LargeParticle)))
        {
            throw new InvalidDataException($"large_particle_vv SPIR-V translation failed: {error}");
        }
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState, pixelEvaluation, Gen5PixelOutputKind.Float,
                out var pixelShader, out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x2,
                pixelInputAddress: 0x2))
        {
            throw new InvalidDataException($"large_particle_p SPIR-V translation failed: {error}");
        }

        var connectivity = vertexShader.NggPrimitiveConnectivity
            ?? throw new InvalidDataException(
                "large_particle_vv emitted no host-verified NGG primitive connectivity");
        return new Ps5NativeSelector1ParticleDrawProgram(
            vertexText,
            pixelText,
            vertexShader.Spirv,
            pixelShader.Spirv,
            vertexEvaluation.GlobalMemoryBindings,
            pixelEvaluation.GlobalMemoryBindings,
            connectivity,
            isLarge: true,
            particle0Descriptor[..32].ToArray(),
            particle1Descriptor[..32].ToArray());
    }

    /// <summary>Builds one live bank draw at the requested target size.</summary>
    public Ps5NativeParticleDraw BuildDraw(
        Ps5NativeSelector1ResourceBank bank,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids,
        int width,
        int height,
        float time = 0.0f,
        float timeStep = 1.0f / 60.0f,
        int instance = 0,
        int currentInstance = 0)
    {
        return BuildDrawCore(
            bank.Index,
            bank.ResourcesVsPs,
            properties,
            ids,
            width,
            height,
            time,
            timeStep,
            instance,
            currentInstance);
    }

    /// <summary>Builds one live coldboot large-bank draw.</summary>
    public Ps5NativeParticleDraw BuildLargeDraw(
        Ps5NativePatternResourceBank bank,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids,
        int width,
        int height,
        float time,
        float timeStep,
        int instance = 0,
        int currentInstance = 0) => BuildDrawCore(
            bank.Index,
            bank.ResourcesVsPs,
            properties,
            ids,
            width,
            height,
            time,
            timeStep,
            instance,
            currentInstance);

    private Ps5NativeParticleDraw BuildDrawCore(
        int bankIndex,
        ReadOnlyMemory<byte> drawBlock,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids,
        int width,
        int height,
        float time,
        float timeStep,
        int instance,
        int currentInstance)
    {
        var expectedDrawLength = _isLarge
            ? 0xEC
            : Ps5NativeSelector1PatternMaterializer.SmallResourcesVsPsByteCount;
        if (properties.Length != Ps5NativeParticleComputeRequest.ParticlePropertyByteCount ||
            ids.Length != Ps5NativeParticleComputeRequest.ParticleIdByteCount ||
            drawBlock.Length != expectedDrawLength || width <= 0 || height <= 0 ||
            !float.IsFinite(time) || !float.IsFinite(timeStep))
        {
            throw new ArgumentException("invalid live particle draw inputs");
        }

        var resources = _isLarge
            ? CreateLargeResources(
                drawBlock.Span,
                _largeImageDescriptor0 ?? throw new InvalidOperationException("missing Particle0 descriptor"),
                _largeImageDescriptor1 ?? throw new InvalidOperationException("missing Particle1 descriptor"),
                width,
                height)
            : CreateResources(drawBlock.Span);
        var srt = CreateSrt(instance, currentInstance, time, timeStep);
        var buffers = new byte[_vertexBindings.Length + _pixelBindings.Length][];
        var aliases = new int[buffers.Length];
        Array.Fill(aliases, -1);
        var byAddress = new Dictionary<ulong, int>();

        for (var index = 0; index < buffers.Length; index++)
        {
            var fromVertex = index < _vertexBindings.Length;
            var binding = fromVertex
                ? _vertexBindings[index]
                : _pixelBindings[index - _vertexBindings.Length];
            var data = new byte[binding.DataLength];
            var text = fromVertex ? _vertexText : _pixelText;
            var source = ResolveSource(
                binding.BaseAddress,
                text,
                srt,
                resources,
                properties,
                ids,
                out var offset);
            if (source is null || offset < 0 || offset > source.Length)
            {
                throw new InvalidDataException(
                    $"unmapped particle draw binding base 0x{binding.BaseAddress:X}");
            }

            source.AsSpan(offset, Math.Min(source.Length - offset, data.Length)).CopyTo(data);
            buffers[index] = data;
            if (byAddress.TryGetValue(binding.BaseAddress, out var firstSlot) &&
                buffers[firstSlot].Length == data.Length)
            {
                aliases[index] = firstSlot;
            }
            else
            {
                byAddress[binding.BaseAddress] = index;
            }
        }

        var particleCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            drawBlock.Span[(_isLarge ? 0xAC : 0x20)..]);
        if (particleCount == 0)
        {
            throw new InvalidDataException($"particle bank {bankIndex} has no particles");
        }

        // The renderer owns the buffer storage; aliases document shared guest
        // allocations to its persistent descriptor setup.
        return new Ps5NativeParticleDraw(
            width,
            height,
            particleCount,
            buffers.Select(static buffer => (ReadOnlyMemory<byte>)buffer).ToArray(),
            BufferAliases: aliases);
    }

    /// <summary>Creates the 12.40 selector-1 particle-id permutation.</summary>
    public static byte[] BuildParticleIds(int count = 6000)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var ids = new uint[count];
        var s0 = 0x1122_10F4_7DE9_8115UL;
        var s1 = 0x7BUL;
        for (var index = 0; index < count; index++)
        {
            var a = (s0 << 23) ^ s0;
            var b = ((s1 >> 26) ^ s1) ^ a;
            var next = (a >> 17) ^ b;
            var draw = (uint)(s1 + next);
            s0 = s1;
            s1 = next;

            var slot = draw % (uint)(index + 1);
            if (slot != index)
            {
                ids[index] = ids[slot];
            }

            ids[slot] = (uint)index;
        }

        var bytes = new byte[count * sizeof(uint)];
        Buffer.BlockCopy(ids, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] CreateResources(ReadOnlySpan<byte> drawBlock)
    {
        var resources = new byte[0x1000];
        drawBlock[..Math.Min(drawBlock.Length, resources.Length)].CopyTo(resources);
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            resources.AsSpan(0x00, 16),
            PropertyAddress,
            RecordStride,
            RecordCount);
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            resources.AsSpan(0x10, 16),
            IdAddress,
            sizeof(uint),
            RecordCount);
        return resources;
    }

    private static byte[] CreateLargeResources(
        ReadOnlySpan<byte> drawBlock,
        ReadOnlySpan<byte> descriptor0,
        ReadOnlySpan<byte> descriptor1,
        int width,
        int height)
    {
        var resources = CreateResources(drawBlock);
        WriteImageDescriptor(resources.AsSpan(0x20, 32), descriptor0, LargeImage0Address);
        WriteImageDescriptor(resources.AsSpan(0x40, 32), descriptor1, LargeImage1Address);
        BitConverter.TryWriteBytes(
            resources.AsSpan(0x78, sizeof(float)),
            ResolveNativeThreeDimensionalAspect(width, height));
        return resources;
    }

    /// <summary>
    /// <c>large_particle_vv</c> 3-D projection. Particle positions remain in
    /// the native simulation; they are not normalized, wrapped, or tiled by
    /// the host.
    /// </summary>
    public static float ResolveNativeThreeDimensionalAspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        return width / (float)height;
    }

    private static void WriteImageDescriptor(
        Span<byte> destination,
        ReadOnlySpan<byte> descriptor,
        ulong address)
    {
        descriptor[..32].CopyTo(destination);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        var high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(destination[4..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..],
            (high & 0xFFFF_FF00u) | (uint)(address >> 32));
    }

    private static byte[] CreateSrt(
        int instance,
        int currentInstance,
        float time = 0.0f,
        float timeStep = 1.0f / 60.0f)
    {
        var srt = new byte[0x1000];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesVsPsAddress);
        BitConverter.TryWriteBytes(srt.AsSpan(0x08, 4), time);
        BitConverter.TryWriteBytes(srt.AsSpan(0x0C, 4), timeStep);
        BitConverter.TryWriteBytes(
            srt.AsSpan(0x10, 4),
            (uint)(instance | (currentInstance << 4)));
        BitConverter.TryWriteBytes(srt.AsSpan(0x14, 4), 0u);
        return srt;
    }

    private static void AddGuestRegions(
        DrawMemory memory,
        byte[] srt,
        byte[] resources,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids)
    {
        memory.AddRegion(SrtVsPsAddress, srt);
        memory.AddRegion(ResourcesVsPsAddress, resources);
        memory.AddRegion(PropertyAddress, properties.ToArray());
        memory.AddRegion(IdAddress, ids.ToArray());
    }

    private static byte[]? ResolveSource(
        ulong address,
        byte[] programText,
        byte[] srt,
        byte[] resources,
        ReadOnlySpan<byte> properties,
        ReadOnlySpan<byte> ids,
        out int offset)
    {
        offset = 0;
        if (address >= ProgramAddress &&
            address < ProgramAddress + (ulong)programText.Length)
        {
            offset = checked((int)(address - ProgramAddress));
            return programText;
        }

        return address switch
        {
            SrtVsPsAddress => srt,
            ResourcesVsPsAddress => resources,
            PropertyAddress => properties.ToArray(),
            IdAddress => ids.ToArray(),
            _ => null,
        };
    }

    private static byte[] Slice(ReadOnlySpan<byte> image, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > image.Length - length)
        {
            throw new InvalidDataException(
                $"NPXS40087 draw program slice 0x{offset:X}+0x{length:X} is outside eboot");
        }

        return image.Slice(offset, length).ToArray();
    }

    private sealed class DrawMemory : ICpuMemory
    {
        private readonly List<(ulong Address, byte[] Data)> _regions = [];

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

        internal void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));
    }
}
