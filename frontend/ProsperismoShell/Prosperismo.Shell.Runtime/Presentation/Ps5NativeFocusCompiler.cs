// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

public enum Ps5NativeFocusShaderKind
{
    Area,
    Line,
}

public sealed record Ps5NativeFocusProgram(
    Ps5NativeFocusShaderKind Kind,
    ReadOnlyMemory<byte> FragmentSpirv,
    int FocusConstantBytes,
    uint HostSubgroupSize)
{
    public bool IsValid =>
        !FragmentSpirv.IsEmpty && FocusConstantBytes is 128 or 160 &&
        HostSubgroupSize is 32 or 64;
}

public sealed record Ps5NativeFocusVertexProgram(
    Ps5NativeFocusShaderKind Kind,
    ReadOnlyMemory<byte> VertexSpirv,
    int ConstantBytes,
    uint HostSubgroupSize)
{
    public bool IsValid =>
        !VertexSpirv.IsEmpty &&
        ConstantBytes == (Kind == Ps5NativeFocusShaderKind.Area ? 112 : 116) &&
        HostSubgroupSize is 32 or 64;
}

/// <summary>
/// Loads and translates libScePsm's original AreaFocus/LineFocus programs.
/// Callers may supply either the historical full library image or one exact
/// embedded ELF slice from the release asset package.
/// </summary>
public static class Ps5NativeFocusCompiler
{
    public const long AreaVertexElfOffset = 0x004B_7070;
    public const int AreaVertexElfLength = 0x0AF0;
    public const long LineVertexElfOffset = 0x004B_7B60;
    public const int LineVertexElfLength = 0x0B80;
    public const long AreaElfOffset = 0x004F_5AE0;
    public const int AreaElfLength = 0x1560;
    public const long LineElfOffset = 0x004F_7040;
    public const int LineElfLength = 0x16D0;
    public const uint ProgramResource1 = 0x022C_0142;
    public const uint PixelInputEnable = 0x0000_0002;
    public const uint PixelInputAddress = 0x0000_0002;

    private const ulong ProgramAddress = 0x0010_0000;
    private const ulong FocusConstantsAddress = 0x0520_0000;
    private const ulong DisplayConstantsAddress = 0x0530_0000;
    private const ulong VertexConstantsAddress = 0x0540_0000;
    private const ulong VertexDescriptorsAddress = 0x0550_0000;
    private const ulong VertexDataAddress = 0x0560_0000;

    public static bool TryCompileVertex(
        string libScePsmPath,
        Ps5NativeFocusShaderKind kind,
        uint hostSubgroupSize,
        out Ps5NativeFocusVertexProgram program,
        out string error)
    {
        program = default!;
        error = string.Empty;
        try
        {
            if (hostSubgroupSize is not (32 or 64))
            {
                error = "focus translation requires a 32- or 64-lane Vulkan host subgroup";
                return false;
            }

            var (offset, length, constantBytes) = kind switch
            {
                Ps5NativeFocusShaderKind.Area => (AreaVertexElfOffset, AreaVertexElfLength, 112),
                Ps5NativeFocusShaderKind.Line => (LineVertexElfOffset, LineVertexElfLength, 116),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var shaderText = ReadShaderText(libScePsmPath, offset, length);
            var memory = new FocusMemory();
            memory.AddRegion(ProgramAddress, shaderText);
            memory.AddRegion(VertexConstantsAddress, new byte[constantBytes]);
            memory.AddRegion(VertexDescriptorsAddress, CreateVertexDescriptors());
            memory.AddRegion(VertexDataAddress, CreateVertexData());
            var context = new CpuContext(memory, Generation.Gen5);
            if (!Gen5ShaderTranslator.TryDecodeProgram(
                    context,
                    ProgramAddress,
                    out var decoded,
                    out error))
            {
                return false;
            }

            var userData = new uint[6];
            WriteBufferDescriptor(
                MemoryMarshal.AsBytes(userData.AsSpan(0, 4)),
                VertexConstantsAddress,
                constantBytes);
            userData[4] = (uint)VertexDescriptorsAddress;
            userData[5] = (uint)(VertexDescriptorsAddress >> 32);
            var state = new Gen5ShaderState(
                decoded,
                userData,
                Metadata: null,
                UserDataScalarRegisterBase: 8);
            if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                    context,
                    state,
                    out var evaluation,
                    out error,
                    resolveVertexInputs: true,
                    requiredVertexRecordCount: 6))
            {
                return false;
            }

            // The scalar evaluator also snapshots the descriptor table used to
            // discover the three host vertex attributes. Those S_LOADs are
            // compile-time only; the translated shader needs just the actual
            // 112-byte vertex constant block at descriptor-array slot 5.
            evaluation = evaluation with
            {
                GlobalMemoryBindings = evaluation.GlobalMemoryBindings
                    .Where(static binding => binding.BaseAddress == VertexConstantsAddress)
                    .ToArray(),
            };

            if (evaluation.GlobalMemoryBindings.Count != 1 ||
                evaluation.GlobalMemoryBindings[0].DataLength != constantBytes ||
                evaluation.VertexInputs is not { Count: 3 })
            {
                error = $"{kind} focus vertex ABI mismatch: expected cbuffer[{constantBytes}] and three vertex inputs; " +
                    $"got buffers=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(static binding => binding.DataLength))}] " +
                    $"inputs={evaluation.VertexInputs?.Count ?? 0}";
                return false;
            }

            if (!Gen5SpirvTranslator.TryCompileVertexShader(
                    state,
                    evaluation,
                    out var translated,
                    out error,
                    globalBufferBase: 5,
                    totalGlobalBufferCount: 6,
                    requiredVertexOutputCount: 3,
                    hostSubgroupSize: hostSubgroupSize))
            {
                return false;
            }

            program = new Ps5NativeFocusVertexProgram(
                kind,
                translated.Spirv,
                constantBytes,
                hostSubgroupSize);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return false;
        }
    }

    public static bool TryCompile(
        string libScePsmPath,
        Ps5NativeFocusShaderKind kind,
        uint hostSubgroupSize,
        out Ps5NativeFocusProgram program,
        out string error)
    {
        program = default!;
        error = string.Empty;
        try
        {
            if (hostSubgroupSize is not (32 or 64))
            {
                error = "focus translation requires a 32- or 64-lane Vulkan host subgroup";
                return false;
            }

            var (offset, length, c0Bytes) = kind switch
            {
                Ps5NativeFocusShaderKind.Area => (AreaElfOffset, AreaElfLength, 128),
                Ps5NativeFocusShaderKind.Line => (LineElfOffset, LineElfLength, 160),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            var shaderText = ReadShaderText(libScePsmPath, offset, length);
            var memory = new FocusMemory();
            memory.AddRegion(ProgramAddress, shaderText);
            memory.AddRegion(FocusConstantsAddress, new byte[c0Bytes]);
            memory.AddRegion(DisplayConstantsAddress, new byte[8]);
            var context = new CpuContext(memory, Generation.Gen5);
            if (!Gen5ShaderTranslator.TryDecodeProgram(
                    context,
                    ProgramAddress,
                    out var decoded,
                    out error))
            {
                return false;
            }

            var userData = CreateUserData(c0Bytes);
            var state = new Gen5ShaderState(
                decoded,
                userData,
                Metadata: null,
                UserDataScalarRegisterBase: 0,
                ProgramResource1: ProgramResource1);
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
                evaluation.GlobalMemoryBindings[0].DataLength != c0Bytes ||
                evaluation.GlobalMemoryBindings[1].DataLength != 8 ||
                evaluation.ImageBindings.Count != 2 ||
                evaluation.ImageBindings[0].Control.ScalarResource != 0 ||
                evaluation.ImageBindings[1].Control.ScalarResource != 8)
            {
                error =
                    $"{kind} focus ABI mismatch: expected cbuffer[{c0Bytes},8] and textures [s0,s8]";
                return false;
            }

            if (!Gen5SpirvTranslator.TryCompilePixelShader(
                    state,
                    evaluation,
                    [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
                    out var translated,
                    out error,
                    pixelInputEnable: PixelInputEnable,
                    pixelInputAddress: PixelInputAddress,
                    hostSubgroupSize: hostSubgroupSize))
            {
                return false;
            }

            program = new Ps5NativeFocusProgram(
                kind,
                translated.Spirv,
                c0Bytes,
                hostSubgroupSize);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return false;
        }
    }

    private static uint[] CreateUserData(int focusConstantBytes)
    {
        var userData = new uint[32];
        var bytes = MemoryMarshal.AsBytes(userData.AsSpan());
        WriteTextureDescriptor(bytes.Slice(0, 32), 0x0800_0000, 7, 1);
        WriteTextureDescriptor(bytes.Slice(32, 32), 0x0810_0000, 64, 64);
        ReadOnlySpan<uint> sampler =
            [0x0000_0092u, 0x0000_0000u, 0x0250_0000u, 0x0000_0000u];
        sampler.CopyTo(userData.AsSpan(16, 4));
        sampler.CopyTo(userData.AsSpan(20, 4));
        WriteBufferDescriptor(bytes.Slice(24 * 4, 16), FocusConstantsAddress, focusConstantBytes);
        WriteBufferDescriptor(bytes.Slice(28 * 4, 16), DisplayConstantsAddress, 8);
        return userData;
    }

    private static void WriteBufferDescriptor(Span<byte> destination, ulong address, int length)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)(address >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)length);
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

    private static byte[] CreateVertexDescriptors()
    {
        var result = new byte[3 * 16];
        WriteVertexDescriptor(result.AsSpan(0, 16), VertexDataAddress, 36, 6, 74);
        WriteVertexDescriptor(result.AsSpan(16, 16), VertexDataAddress + 12, 36, 6, 77);
        WriteVertexDescriptor(result.AsSpan(32, 16), VertexDataAddress + 28, 36, 6, 64);
        return result;
    }

    private static void WriteVertexDescriptor(
        Span<byte> destination,
        ulong address,
        uint stride,
        uint records,
        uint unifiedFormat)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..],
            (uint)(address >> 32) | (stride << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], records);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], unifiedFormat << 12);
    }

    private static byte[] CreateVertexData()
    {
        // Two triangles in PUI's 3f position, 4f colour, 2f UV layout.
        ReadOnlySpan<float> vertices =
        [
            0, 0, 0, 1, 1, 1, 1, 0, 0,
            1, 0, 0, 1, 1, 1, 1, 1, 0,
            0, 1, 0, 1, 1, 1, 1, 0, 1,
            0, 1, 0, 1, 1, 1, 1, 0, 1,
            1, 0, 0, 1, 1, 1, 1, 1, 0,
            1, 1, 0, 1, 1, 1, 1, 1, 1,
        ];
        return MemoryMarshal.AsBytes(vertices).ToArray();
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
                throw new InvalidDataException("focus ELF bounds are outside libScePsm.sprx");
            }

            elfOffset = 0;
            elfLength = checked((int)stream.Length);
        }

        stream.Position = elfOffset;
        var elf = new byte[elfLength];
        stream.ReadExactly(elf);
        if (!elf.AsSpan(0, 4).SequenceEqual("\u007FELF"u8))
        {
            throw new InvalidDataException("focus offset does not contain an ELF image");
        }

        var sectionOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(0x28)));
        var sectionEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3A));
        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3C));
        var stringIndex = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x3E));
        var stringHeader = checked(sectionOffset + stringIndex * sectionEntrySize);
        var strings = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(stringHeader + 0x18)));
        for (var index = 0; index < sectionCount; index++)
        {
            var header = checked(sectionOffset + index * sectionEntrySize);
            var nameOffset = checked(strings + (int)BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(header)));
            var end = Array.IndexOf(elf, (byte)0, nameOffset);
            if (end < 0)
            {
                throw new InvalidDataException("focus ELF section name is unterminated");
            }

            if (System.Text.Encoding.ASCII.GetString(elf, nameOffset, end - nameOffset) != ".shader_text")
            {
                continue;
            }

            var payloadOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(header + 0x18)));
            var payloadLength = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(header + 0x20)));
            return elf.AsSpan(payloadOffset, payloadLength).ToArray();
        }

        throw new InvalidDataException("focus ELF has no .shader_text section");
    }

    private sealed class FocusMemory : ICpuMemory
    {
        private readonly List<(ulong Address, byte[] Data)> _regions = [];

        public void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));

        public bool TryRead(ulong address, Span<byte> destination)
        {
            foreach (var (baseAddress, data) in _regions)
            {
                if (address < baseAddress || address + (ulong)destination.Length > baseAddress + (ulong)data.Length)
                {
                    continue;
                }

                data.AsSpan(checked((int)(address - baseAddress)), destination.Length).CopyTo(destination);
                return true;
            }

            return false;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
