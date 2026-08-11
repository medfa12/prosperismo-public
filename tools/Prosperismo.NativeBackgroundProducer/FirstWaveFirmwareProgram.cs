// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prosperismo.NativeBackgroundProducer;

/// <summary>
/// The exact FirstWave graphics program shipped in NPXS40087 on firmware 12.40.
/// No Sony program bytes are stored in Prosperismo: they are read from, and
/// fingerprinted against, the user's own decrypted eboot.bin.
/// </summary>
internal sealed record FirstWaveFirmwareStage(
    string Name,
    long DescriptorFileOffset,
    long HeaderFileOffset,
    long CodeFileOffset,
    int CodeBytes,
    byte ShaderType,
    string Sha256,
    ReadOnlyMemory<byte> Header,
    ReadOnlyMemory<byte> Code);

internal sealed record FirstWaveFirmwareProgram(
    string EbootPath,
    IReadOnlyList<FirstWaveFirmwareStage> Stages)
{
    private const uint ShaderFileMagic = 0x3433_3231;
    private const uint ShaderFileVersion = 0x18;
    private const int DescriptorBytes = 0x60;
    private const int MinimumHeaderBytes = 0x60;

    // File offsets come from the 12.40 NPXS40087 ELF PT_LOAD mappings.  Code
    // lengths are the values in each AGC shader header at +0x44, rather than
    // the zero-padded distance to the next shader.
    private static readonly StageContract[] Contracts =
    [
        new("fw_basic_vv",      0x013C_7810, 0x011D_B3C0, 0x011F_4600, 0x0150, 2, "5ef2ecfbbaf728128a75b7939cdaac4bffc7321ca0ccb9088688b52fa7fa15cc"),
        new("fw_blurh_p",       0x013C_7870, 0x011D_B530, 0x011F_4800, 0x04A0, 1, "8c0d6f18644dd76e6bc895236a1e70ae13b580cf5b4d725f3be4f9f35b96852e"),
        new("fw_blurv_p",       0x013C_78D0, 0x011D_B6A8, 0x011F_4D00, 0x04A0, 1, "de529dd44cbd90bd0527e0c123c1d3fe4c078bca8957652c6ed60099288731ec"),
        new("fw_blur_vv",       0x013C_7930, 0x011D_B820, 0x011F_5200, 0x0220, 2, "f2c1b14c7dd2ec3141e14fd7d82c48fb551b0b382f6a787e550baae6bde1f30c"),
        new("fw_flow_dv",       0x013C_7990, 0x011D_B998, 0x011F_5500, 0x10A0, 2, "4388eacb9ae9e3899f92890369ca4fed470990f157a11ae6bd759d7f07db2879"),
        new("fw_flow_h",        0x013C_79F0, 0x011D_BB18, 0x011F_6600, 0x0260, 7, "e5e35e79f2a3637c7885373a57259e80e46b1dca396198d33393d6221caba09c"),
        new("fw_flow_vl",       0x013C_7A50, 0x011D_BC68, 0x011F_6900, 0x0850, 5, "ae6f0a7f9f72412ee4b80583e996b52bb8d4040c0ad4d4606f481ca616a717d5"),
        new("fw_oit_p",         0x013C_7AB0, 0x011D_BD80, 0x011F_7200, 0x0E20, 1, "c26f9b2ce672bb4c9fd97e9d20e657e7319e44efa94c8413eb18bba39e5cbd6e"),
        new("fw_comp_oit_p",    0x013C_7B10, 0x011D_BF00, 0x011F_8100, 0x0560, 1, "3a432a5043c26d13e1fa14e8ce4150b89253c05c44873066e777df13f9b6f0ff"),
        new("fw_fxaa_p",        0x013C_7B70, 0x011D_C070, 0x011F_8700, 0x0C00, 1, "3ff4118d04aa920c1f28e9ababa4da21bf508f50a7c013cb40be6958415d6995"),
        new("fw_background_p",  0x013C_7BD0, 0x011D_C1E0, 0x011F_9300, 0x0350, 1, "267665c934d94663a75649a6c67ccc500f2ca8444392f92e6dc47f15c29c2e5e"),
    ];

    internal static IReadOnlyList<string> StageNames =>
        Contracts.Select(static contract => contract.Name).ToArray();

    public static FirstWaveFirmwareProgram Load(string ebootPath)
    {
        var fullPath = Path.GetFullPath(ebootPath);
        using var stream = File.OpenRead(fullPath);
        var stages = new List<FirstWaveFirmwareStage>(Contracts.Length);
        foreach (var contract in Contracts)
        {
            ValidateBounds(stream, contract.DescriptorFileOffset, DescriptorBytes, contract.Name, "descriptor");
            ValidateBounds(stream, contract.HeaderFileOffset, MinimumHeaderBytes, contract.Name, "header");
            ValidateBounds(stream, contract.CodeFileOffset, contract.CodeBytes, contract.Name, "code");

            var descriptor = ReadExactly(stream, contract.DescriptorFileOffset, DescriptorBytes);
            var headerLength = checked((int)ReadUInt32(stream, contract.HeaderFileOffset + 0x40));
            if (headerLength < MinimumHeaderBytes || headerLength > 0x10000)
            {
                throw new InvalidDataException($"{contract.Name}: invalid AGC header length 0x{headerLength:X}");
            }
            ValidateBounds(stream, contract.HeaderFileOffset, headerLength, contract.Name, "full header");
            var header = ReadExactly(stream, contract.HeaderFileOffset, headerLength);
            var code = ReadExactly(stream, contract.CodeFileOffset, contract.CodeBytes);

            ValidateDescriptorName(stream, descriptor, contract.Name);
            ValidateHeader(header, contract);
            var digest = Convert.ToHexString(SHA256.HashData(code)).ToLowerInvariant();
            if (!string.Equals(digest, contract.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{contract.Name}: 12.40 code fingerprint mismatch; expected {contract.Sha256}, got {digest}");
            }

            stages.Add(new FirstWaveFirmwareStage(
                contract.Name,
                contract.DescriptorFileOffset,
                contract.HeaderFileOffset,
                contract.CodeFileOffset,
                contract.CodeBytes,
                contract.ShaderType,
                contract.Sha256,
                header,
                code));
        }

        return new FirstWaveFirmwareProgram(fullPath, stages);
    }

    internal static void ValidateContractTable()
    {
        if (Contracts.Length != 11 ||
            Contracts.Select(static contract => contract.Name).Distinct(StringComparer.Ordinal).Count() != Contracts.Length ||
            Contracts.Any(static contract => contract.CodeBytes <= 0 || contract.Sha256.Length != 64) ||
            !Contracts.Select(static contract => contract.DescriptorFileOffset)
                .SequenceEqual(Contracts.Select(static contract => contract.DescriptorFileOffset).Order()))
        {
            throw new InvalidOperationException("FirstWave 12.40 stage contract table is malformed");
        }

        var required = new[]
        {
            "fw_flow_vl", "fw_flow_h", "fw_flow_dv", "fw_oit_p",
            "fw_comp_oit_p", "fw_blurh_p", "fw_blurv_p", "fw_fxaa_p",
            "fw_background_p",
        };
        if (required.Except(StageNames, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("FirstWave stage contract omits a required rendering pass");
        }
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header, StageContract contract)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var codeBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var shaderType = header[0x5A];
        if (magic != ShaderFileMagic || version != ShaderFileVersion ||
            codeBytes != contract.CodeBytes || shaderType != contract.ShaderType)
        {
            throw new InvalidDataException(
                $"{contract.Name}: AGC header mismatch " +
                $"magic=0x{magic:X8} version=0x{version:X} code=0x{codeBytes:X} type={shaderType}");
        }
    }

    private static void ValidateDescriptorName(Stream stream, ReadOnlySpan<byte> descriptor, string expected)
    {
        var nameVirtualAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
        var nameFileOffset = VirtualAddressToFileOffset(nameVirtualAddress);
        ValidateBounds(stream, nameFileOffset, expected.Length + 1, expected, "name");
        var bytes = ReadExactly(stream, nameFileOffset, expected.Length + 1);
        if (bytes[^1] != 0 || !Encoding.ASCII.GetString(bytes.AsSpan(0, bytes.Length - 1)).Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{expected}: descriptor does not point to the expected shader name");
        }
    }

    private static long VirtualAddressToFileOffset(ulong address)
    {
        // The names live in NPXS40087's read-only PT_LOAD segment:
        // p_offset 0xD10000, p_vaddr 0xD0C000.
        const ulong segmentAddress = 0x00D0_C000;
        const ulong segmentBytes = 0x0042_9580;
        const ulong segmentFileOffset = 0x00D1_0000;
        if (address < segmentAddress || address >= segmentAddress + segmentBytes)
        {
            throw new InvalidDataException($"FirstWave descriptor name VA 0x{address:X} is outside the 12.40 string segment");
        }
        return checked((long)(segmentFileOffset + address - segmentAddress));
    }

    private static uint ReadUInt32(Stream stream, long offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(stream, offset, sizeof(uint)));

    private static byte[] ReadExactly(Stream stream, long offset, int bytes)
    {
        var result = new byte[bytes];
        stream.Position = offset;
        stream.ReadExactly(result);
        return result;
    }

    private static void ValidateBounds(Stream stream, long offset, int bytes, string name, string region)
    {
        if (offset < 0 || bytes <= 0 || offset > stream.Length - bytes)
        {
            throw new InvalidDataException($"{name}: {region} lies outside NPXS40087 eboot.bin");
        }
    }

    private sealed record StageContract(
        string Name,
        long DescriptorFileOffset,
        long HeaderFileOffset,
        long CodeFileOffset,
        int CodeBytes,
        byte ShaderType,
        string Sha256);
}
