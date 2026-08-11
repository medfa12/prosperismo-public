// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Host program translated from NPXS40087's embedded <c>ripple_p</c> shader.
/// </summary>
public sealed record Ps5NativeRippleProgram(ReadOnlyMemory<byte> FragmentSpirv)
{
    public bool IsValid => !FragmentSpirv.IsEmpty;
}

/// <summary>
/// Loads and translates the Transition ripple pixel shader with its real
/// two-texture/two-cbuffer user-SGPR contract. The input may be the historical
/// full executable or the exact packaged embedded-ELF slice.
/// </summary>
public static class Ps5NativeRippleCompiler
{
    public const long FirmwareElfOffset = 0x00D8_A070;
    public const int FirmwareElfLength = 0x1BB8;

    private const ulong ProgramAddress = 0x0010_0000;
    private const ulong RippleConstantsAddress = 0x0500_0000;
    private const ulong GradationConstantsAddress = 0x0510_0000;

    public static bool TryCompile(
        string ebootPath,
        out Ps5NativeRippleProgram program,
        out string error) =>
        TryCompile(ebootPath, FirmwareElfOffset, FirmwareElfLength, out program, out error);

    /// <summary>
    /// </summary>
    public static bool TryCompile(
        string ebootPath,
        long elfOffset,
        int elfLength,
        out Ps5NativeRippleProgram program,
        out string error)
    {
        program = default!;
        error = string.Empty;
        try
        {
            var shaderText = ReadShaderText(ebootPath, elfOffset, elfLength);
            var memory = new RippleMemory();
            memory.AddRegion(ProgramAddress, shaderText);
            var c0 = new byte[40];
            var c1 = new byte[160];
            memory.AddRegion(RippleConstantsAddress, c0);
            memory.AddRegion(GradationConstantsAddress, c1);
            var context = new CpuContext(memory, Generation.Gen5);
            if (!Gen5ShaderTranslator.TryDecodeProgram(
                    context,
                    ProgramAddress,
                    out var decoded,
                    out error))
            {
                return false;
            }

            var userData = CreateUserData();
            var state = new Gen5ShaderState(
                decoded,
                userData,
                Metadata: null,
                UserDataScalarRegisterBase: 0,
                ProgramResource1: 0x022C_0148);
            if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                    context,
                    state,
                    out var evaluation,
                    out error))
            {
                return false;
            }

            evaluation = evaluation with
            {
                ImageBindings = evaluation.ImageBindings
                    .GroupBy(static binding =>
                        (binding.Control.ScalarResource, binding.Control.ScalarSampler))
                    .Select(static group => group.First())
                    .OrderBy(static binding => binding.Control.ScalarResource)
                    .ToArray(),
            };
            if (evaluation.GlobalMemoryBindings.Count != 2 ||
                evaluation.GlobalMemoryBindings[0].DataLength != 40 ||
                evaluation.GlobalMemoryBindings[1].DataLength != 160 ||
                evaluation.ImageBindings.Count != 2 ||
                evaluation.ImageBindings[0].Control.ScalarResource != 0 ||
                evaluation.ImageBindings[1].Control.ScalarResource != 8)
            {
                error =
                    "ripple ABI mismatch: expected cbuffer[40,160] and texture SGPRs [s0,s8]";
                return false;
            }

            if (!Gen5SpirvTranslator.TryCompilePixelShader(
                    state,
                    evaluation,
                    [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
                    out var translated,
                    out error,
                    pixelInputEnable: 0x0000_0002,
                    pixelInputAddress: 0x0000_0002))
            {
                return false;
            }

            program = new Ps5NativeRippleProgram(translated.Spirv);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return false;
        }
    }

    private static uint[] CreateUserData()
    {
        var userData = new uint[32];
        var bytes = MemoryMarshal.AsBytes(userData.AsSpan());
        WriteTextureDescriptor(bytes.Slice(0, 32), 0x0600_0000, 1920, 1080);
        WriteTextureDescriptor(bytes.Slice(32, 32), 0x0700_0000, 1920, 1080);
        ReadOnlySpan<uint> sampler =
            [0x0000_0092u, 0x0000_0000u, 0x0250_0000u, 0x0000_0000u];
        sampler.CopyTo(userData.AsSpan(16, 4));
        sampler.CopyTo(userData.AsSpan(20, 4));
        WriteBufferDescriptor(bytes.Slice(24 * 4, 16), RippleConstantsAddress, 40);
        WriteBufferDescriptor(bytes.Slice(28 * 4, 16), GradationConstantsAddress, 160);
        return userData;
    }

    private static void WriteBufferDescriptor(Span<byte> destination, ulong address, int bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)(address >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], 0);
    }

    private static void WriteTextureDescriptor(
        Span<byte> destination,
        ulong address,
        uint width,
        uint height)
    {
        const uint rgba8Unorm = 56;
        const uint imageType2D = 9;
        var words = MemoryMarshal.Cast<byte, uint>(destination);
        words.Clear();
        words[0] = (uint)(address >> 8);
        words[1] = (uint)(address >> 40) |
            (rgba8Unorm << 20) |
            ((width - 1) << 30);
        words[2] = ((width - 1) >> 2) | ((height - 1) << 14);
        words[3] = (imageType2D << 28) | 0xFACu;
    }

    private static byte[] ReadShaderText(string path, long elfOffset, int elfLength)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        if (elfOffset < 0 || elfLength <= 0 || elfOffset + elfLength > stream.Length)
        {
            Span<byte> magic = stackalloc byte[4];
            stream.ReadExactly(magic);
            if (stream.Length > 0x10000 || !magic.SequenceEqual("\u007FELF"u8))
            {
                throw new InvalidDataException("ripple ELF bounds are outside NPXS40087 eboot.bin");
            }

            elfOffset = 0;
            elfLength = checked((int)stream.Length);
        }

        stream.Position = elfOffset;
        var elf = new byte[elfLength];
        stream.ReadExactly(elf);
        // Use a fixed-width escape: C#'s \x escape consumes a variable number
        // of hex digits, so "\x7FELF" does not encode the ELF magic.
        if (!elf.AsSpan(0, 4).SequenceEqual("\u007FELF"u8))
        {
            throw new InvalidDataException("ripple offset does not contain an ELF image");
        }

        var sectionOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(0x28)));
        var sectionEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3A));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3C));
        var stringIndex = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3E));
        var stringHeader = sectionOffset + stringIndex * sectionEntrySize;
        var stringOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
            elf.AsSpan(stringHeader + 0x18)));
        for (var index = 0; index < sectionCount; index++)
        {
            var header = sectionOffset + index * sectionEntrySize;
            var nameIndex = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(header)));
            var nameStart = stringOffset + nameIndex;
            var nameEnd = Array.IndexOf(elf, (byte)0, nameStart);
            if (nameEnd < 0 ||
                System.Text.Encoding.ASCII.GetString(elf, nameStart, nameEnd - nameStart) !=
                    ".shader_text")
            {
                continue;
            }

            var dataOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
                elf.AsSpan(header + 0x18)));
            var dataLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
                elf.AsSpan(header + 0x20)));
            return elf.AsSpan(dataOffset, dataLength).ToArray();
        }

        throw new InvalidDataException("ripple ELF has no .shader_text section");
    }

    private sealed class RippleMemory : ICpuMemory
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

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}

public static class Ps5NativeRippleRenderer
{
    public static IReadOnlyList<Ps5NativeParticleFrame> RenderOpaqueFrames(
        Ps5NativeRippleProgram program,
        int width,
        int height,
        ReadOnlyMemory<byte> sourceRgba,
        ReadOnlyMemory<byte> targetRgba,
        IReadOnlyList<ReadOnlyMemory<byte>> rippleConstantFrames)
    {
        if (!program.IsValid || width <= 0 || height <= 0 ||
            sourceRgba.Length != (long)width * height * 4 ||
            targetRgba.Length != (long)width * height * 4 ||
            rippleConstantFrames.Count == 0 ||
            rippleConstantFrames.Any(static frame => frame.Length != 40))
        {
            throw new ArgumentException("invalid native ripple frame inputs");
        }

        var resources = new Ps5NativeParticleResources(
            SpirvFixedShaders.CreateFullscreenVertex(1),
            program.FragmentSpirv,
            new Ps5NativeParticleTexture(width, height, sourceRgba),
            new Ps5NativeParticleTexture(width, height, targetRgba));
        var c1 = new byte[160];
        var draws = rippleConstantFrames.Select(c0 =>
            new Ps5NativeParticleDraw(
                width,
                height,
                1,
                new ReadOnlyMemory<byte>[]
                {
                    c0,
                    c1,
                    new byte[4],
                    new byte[4],
                    new byte[4],
                })).ToArray();
        var atlas = Ps5ParticleDrawProbe.RenderSequence(
            resources,
            draws,
            verticesPerDrawUnit: 3,
            additiveBlend: false,
            separateFrames: true);
        var frameByteCount = checked(width * height * 4);
        return Enumerable.Range(0, draws.Length)
            .Select(index => new Ps5NativeParticleFrame(
                width,
                height,
                atlas.Rgba.Slice(index * frameByteCount, frameByteCount).ToArray()))
            .ToArray();
    }
}
