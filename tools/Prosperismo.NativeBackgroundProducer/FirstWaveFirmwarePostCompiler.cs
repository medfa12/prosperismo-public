// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;

namespace Prosperismo.NativeBackgroundProducer;

internal sealed record FirstWavePostProgram(string Name, ReadOnlyMemory<byte> Spirv);

/// <summary>
/// Translates the pixel-program portion of FirstWave from Sony's original GCN
/// instructions. Tessellation is intentionally excluded: the current backend
/// has no local/hull/domain SPIR-V execution model and must not mislabel those
/// stages as ordinary vertex shaders.
/// </summary>
internal static class FirstWaveFirmwarePostCompiler
{
    private const ulong ProgramAddress = 0x0010_0000;
    private const ulong Buffer0Address = 0x0500_0000;
    private const ulong Buffer1Address = 0x0600_0000;
    private const ulong ConstantsAddress = 0x0700_0000;
    private const ulong TextureAddress = 0x0800_0000;
    private const int ConstantsBytes = 0x1A0;
    private const int ScratchBytes = 0x20_0000;

    private static readonly HashSet<string> PixelStages = new(StringComparer.Ordinal)
    {
        "fw_blurh_p",
        "fw_blurv_p",
        "fw_oit_p",
        "fw_comp_oit_p",
        "fw_fxaa_p",
        "fw_background_p",
    };

    public static IReadOnlyList<FirstWavePostProgram> Compile(FirstWaveFirmwareProgram firmware)
    {
        var result = new List<FirstWavePostProgram>(PixelStages.Count);
        foreach (var stage in firmware.Stages.Where(stage => PixelStages.Contains(stage.Name)))
        {
            var memory = new FirstWaveMemory();
            memory.AddRegion(ProgramAddress, stage.Code.ToArray());
            memory.AddRegion(Buffer0Address, new byte[ScratchBytes]);
            memory.AddRegion(Buffer1Address, new byte[ScratchBytes]);
            memory.AddRegion(ConstantsAddress, new byte[ConstantsBytes]);
            memory.AddRegion(TextureAddress, new byte[4 * 4 * 4]);
            var context = new CpuContext(memory, Generation.Gen5);
            if (!Gen5ShaderTranslator.TryDecodeProgram(
                    context,
                    ProgramAddress,
                    out var decoded,
                    out var error))
            {
                throw new InvalidDataException($"{stage.Name}: decode failed: {error}");
            }

            var userData = CreateUserData(stage.Name);
            var (programResource1, pixelInputEnable, pixelInputAddress) = ReadGraphicsRegisters(stage);
            var state = new Gen5ShaderState(
                decoded,
                userData,
                Metadata: null,
                UserDataScalarRegisterBase: 0,
                ProgramResource1: programResource1);
            if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                    context,
                    state,
                    out var evaluation,
                    out error))
            {
                throw new InvalidDataException($"{stage.Name}: scalar evaluation failed: {error}");
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
            if (!Gen5SpirvTranslator.TryCompilePixelShader(
                    state,
                    evaluation,
                    [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
                    out var translated,
                    out error,
                    pixelInputEnable: pixelInputEnable,
                    pixelInputAddress: pixelInputAddress))
            {
                throw new InvalidDataException($"{stage.Name}: SPIR-V translation failed: {error}");
            }

            result.Add(new FirstWavePostProgram(stage.Name, translated.Spirv));
        }

        if (result.Count != PixelStages.Count)
        {
            throw new InvalidDataException("validated firmware omitted a required FirstWave pixel pass");
        }
        return result;
    }

    private static uint[] CreateUserData(string stageName)
    {
        var userData = new uint[32];
        var bytes = MemoryMarshal.AsBytes(userData.AsSpan());
        if (stageName is "fw_blurh_p" or "fw_blurv_p" or "fw_fxaa_p")
        {
            WriteTextureDescriptor(bytes.Slice(0, 32), TextureAddress, 4, 4);
            ReadOnlySpan<uint> sampler = [0x0000_0092u, 0, 0x0250_0000u, 0];
            sampler.CopyTo(userData.AsSpan(8, 4));
            WriteBufferDescriptor(bytes.Slice(12 * 4, 16), ConstantsAddress, ConstantsBytes);
        }
        else if (stageName == "fw_background_p")
        {
            WriteBufferDescriptor(bytes.Slice(0, 16), ConstantsAddress, ConstantsBytes);
        }
        else
        {
            WriteBufferDescriptor(bytes.Slice(0, 16), Buffer0Address, ScratchBytes);
            WriteBufferDescriptor(bytes.Slice(4 * 4, 16), Buffer1Address, ScratchBytes);
            WriteBufferDescriptor(bytes.Slice(8 * 4, 16), ConstantsAddress, ConstantsBytes);
        }
        return userData;
    }

    private static (uint ProgramResource1, uint PixelInputEnable, uint PixelInputAddress)
        ReadGraphicsRegisters(FirstWaveFirmwareStage stage)
    {
        var header = stage.Header.Span;
        var shOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(header[0x20..]));
        var cxOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(header[0x18..]));
        var shCount = header[0x5C];
        var cxCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x4C..]);
        var programResource1 = FindRegister(header, shOffset, shCount, 0x0A);
        var inputEnable = FindRegister(header, cxOffset, cxCount, 0x1B3, required: false);
        var inputAddress = FindRegister(header, cxOffset, cxCount, 0x1B4, required: false);
        return (programResource1, inputEnable, inputAddress);
    }

    private static uint FindRegister(
        ReadOnlySpan<byte> header,
        int offset,
        uint count,
        uint target,
        bool required = true)
    {
        if (offset < 0 || count > 256 || offset > header.Length - checked((int)count * 8))
        {
            throw new InvalidDataException("FirstWave AGC register table lies outside its header");
        }
        for (var index = 0u; index < count; index++)
        {
            var entry = header.Slice(offset + checked((int)index * 8), 8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) == target)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            }
        }
        // AGC v24 keeps PGM_LO/HI in the relocated SH list, but places some
        // immutable shader-state pairs (including PS_RSRC1) in the adjacent
        // shader-special block. Search only aligned register/value pairs in
        // the validated header after the authoritative table misses.
        for (var candidate = MinimumRegisterPairOffset; candidate <= header.Length - 8; candidate += 8)
        {
            var entry = header.Slice(candidate, 8);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) == target)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            }
        }
        if (required)
        {
            throw new InvalidDataException($"FirstWave AGC header omits required register 0x{target:X}");
        }
        return 0;
    }

    private const int MinimumRegisterPairOffset = 0x60;

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
        words[1] = (uint)(address >> 40) | (rgba8Unorm << 20) | ((width - 1) << 30);
        words[2] = ((width - 1) >> 2) | ((height - 1) << 14);
        words[3] = (imageType2D << 28) | 0xFACu;
    }

    private sealed class FirstWaveMemory : ICpuMemory
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
                    data.AsSpan(checked((int)(virtualAddress - address)), destination.Length)
                        .CopyTo(destination);
                    return true;
                }
            }
            return false;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
