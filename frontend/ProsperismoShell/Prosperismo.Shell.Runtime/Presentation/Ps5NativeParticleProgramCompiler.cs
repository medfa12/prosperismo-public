// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>Translates NPXS40087's original particle programs in process.</summary>
public static class Ps5NativeParticleProgramCompiler
{
    public const int ParticleComputeFileOffset = 0x11FA100;
    public const int ParticleComputeByteLength = 0x71A4;

    private const ulong ProgramAddress = 0x0100_0000;
    private const ulong SrtAddress = 0x0200_0000;
    private const ulong ResourcesAddress = 0x0300_0000;
    private const ulong PropertiesAddress = 0x0400_0000;
    private const ulong ParticleIdsAddress = 0x0500_0000;

    /// <summary>
    /// Compiles <c>particle_c</c> directly from the decrypted 12.40 eboot.
    /// The temporary guest address map exists only for scalar resource
    /// discovery; per-frame authored values are supplied later by the live
    /// pattern materializer.
    /// </summary>
    public static byte[] CompileSmallParticleCompute(ReadOnlySpan<byte> eboot)
    {
        if (eboot.Length < ParticleComputeFileOffset + ParticleComputeByteLength)
        {
            throw new InvalidDataException("NPXS40087 eboot is too short for particle_c");
        }

        var text = eboot.Slice(ParticleComputeFileOffset, ParticleComputeByteLength).ToArray();
        var srt = new byte[Ps5NativeParticleComputeBackend.SrtByteCount];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesAddress);
        var resources = new byte[Ps5NativeParticleComputeRequest.ResourceByteCount];
        WriteBufferDescriptor(
            resources.AsSpan(0x00, 16),
            ParticleIdsAddress,
            sizeof(uint),
            6000);
        WriteBufferDescriptor(
            resources.AsSpan(0x10, 16),
            PropertiesAddress,
            0x44,
            6000);

        var memory = new FlatMemory();
        memory.AddRegion(ProgramAddress, text);
        memory.AddRegion(SrtAddress, srt);
        memory.AddRegion(ResourcesAddress, resources);
        memory.AddRegion(
            PropertiesAddress,
            new byte[Ps5NativeParticleComputeRequest.ParticlePropertyByteCount]);
        memory.AddRegion(
            ParticleIdsAddress,
            new byte[Ps5NativeParticleComputeRequest.ParticleIdByteCount]);

        var context = new CpuContext(memory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                context, ProgramAddress, out var decoded, out var error))
        {
            throw new InvalidDataException($"particle_c decode failed: {error}");
        }

        var userData = new uint[4];
        WriteBufferDescriptor(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(userData.AsSpan()),
            SrtAddress,
            0,
            srt.Length);
        var state = new Gen5ShaderState(
            decoded,
            userData,
            Metadata: null,
            ComputeSystemRegisters: new Gen5ComputeSystemRegisters(4, null, null, null),
            UserDataScalarRegisterBase: 0,
            ProgramResource1: 0x0000_0090);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                context, state, out var evaluation, out error))
        {
            throw new InvalidDataException($"particle_c resource evaluation failed: {error}");
        }

        if (!Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                64,
                1,
                1,
                out var compiled,
                out error,
                waveLaneCount: 64))
        {
            throw new InvalidDataException($"particle_c SPIR-V translation failed: {error}");
        }

        return compiled.Spirv;
    }

    /// <summary>
    /// Installs the two runtime allocation descriptors that are deliberately
    /// absent from serialized pattern blocks. particle_c reads their stride
    /// and capacity fields as data even after scalar resource discovery has
    /// mapped the allocations to Vulkan descriptors.
    /// </summary>
    public static byte[] CreateSmallParticleComputeResources(ReadOnlySpan<byte> authored)
    {
        if (authored.Length != Ps5NativeParticleComputeRequest.ResourceByteCount)
        {
            throw new ArgumentException("particle_c resource block has the wrong size", nameof(authored));
        }

        var resources = authored.ToArray();
        WriteBufferDescriptor(
            resources.AsSpan(0x00, 16),
            ParticleIdsAddress,
            sizeof(uint),
            6000);
        WriteBufferDescriptor(
            resources.AsSpan(0x10, 16),
            PropertiesAddress,
            0x44,
            6000);
        return resources;
    }

    /// <summary>
    /// Returns the exact 12.40 compute SRT block used by <c>particle_c</c>.
    /// The descriptor itself is supplied by the persistent compute backend.
    /// </summary>
    public static byte[] CreateSmallParticleComputeSrt(
        float time = 0.0f,
        bool preSimulation = true,
        uint transitionPatternFlag = 0,
        float timeStep = 1.0f / 60.0f,
        float timeRateForLifeCountdown = 1.0f)
    {
        if (!float.IsFinite(time) || time < 0.0f ||
            !float.IsFinite(timeStep) || timeStep <= 0.0f ||
            !float.IsFinite(timeRateForLifeCountdown) || timeRateForLifeCountdown < 0.0f ||
            transitionPatternFlag > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException();
        }

        var srt = new byte[Ps5NativeParticleComputeBackend.SrtByteCount];
        BitConverter.TryWriteBytes(srt.AsSpan(0x00), ResourcesAddress);
        BitConverter.TryWriteBytes(srt.AsSpan(0x08), time);
        BitConverter.TryWriteBytes(srt.AsSpan(0x0C), timeStep);
        BitConverter.TryWriteBytes(srt.AsSpan(0x10), timeRateForLifeCountdown);
        BitConverter.TryWriteBytes(srt.AsSpan(0x14), preSimulation ? 1u : 0u);
        BitConverter.TryWriteBytes(srt.AsSpan(0x18), transitionPatternFlag);
        return srt;
    }

    internal static void WriteBufferDescriptor(
        Span<byte> destination,
        ulong address,
        int stride,
        int records)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..],
            (uint)(address >> 32) | (((uint)stride & 0x3FFF) << 16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)records);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
    }

    private sealed class FlatMemory : ICpuMemory
    {
        private readonly List<(ulong Base, byte[] Data)> _regions = [];

        internal void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));

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
