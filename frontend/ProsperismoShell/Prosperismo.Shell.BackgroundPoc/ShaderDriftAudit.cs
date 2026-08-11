// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;

namespace Prosperismo.Shell.BackgroundPoc;

/// <summary>
/// Locates named NPXS40087 AGC programs through ELF relocations and compares
/// between releases.
/// </summary>
internal static class ShaderDriftAudit
{
    private const ulong DecodeAddress = 0x0010_0000;

    private static readonly HashSet<string> BackgroundPrograms = new(
    [
        "fw_background_p",
        "fw_flow_vl",
        "fw_flow_h",
        "fw_flow_dv",
        "fw_oit_p",
        "fw_comp_oit_p",
        "fw_blur_vv",
        "fw_blurh_p",
        "fw_blurv_p",
        "fw_fxaa_p",
        "rect_uv_vv",
        "light_p",
        "particle_c",
        "particle_p",
        "particle_vv",
        "large_particle_p",
        "large_particle_vv",
    ], StringComparer.Ordinal);

    internal static int Run(string baselinePath, string comparisonPath)
    {
        try
        {
            var baseline = Snapshot.Load(baselinePath);
            var comparison = Snapshot.Load(comparisonPath);
            var names = baseline.Programs.Keys
                .Union(comparison.Programs.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Console.WriteLine($"baseline   : {Path.GetFullPath(baselinePath)}");
            Console.WriteLine($"comparison : {Path.GetFullPath(comparisonPath)}");
            Console.WriteLine();
            Console.WriteLine(
                $"{"program",-22} {"bytes",13} {"instructions",13} {"decoded contract"}");

            var missing = 0;
            var contractDrift = 0;
            var compilerDrift = 0;
            var identical = 0;
            foreach (var name in names)
            {
                baseline.Programs.TryGetValue(name, out var left);
                comparison.Programs.TryGetValue(name, out var right);
                if (left is null || right is null)
                {
                    missing++;
                    Console.WriteLine(
                        $"{name,-22} {DescribePresence(left),13} {DescribePresence(right),13} MISSING");
                    continue;
                }

                var rawEqual = left.RawHash == right.RawHash;
                var orderedEqual = left.OrderedHash == right.OrderedHash;
                var contractEqual = left.ContractHash == right.ContractHash;
                string result;
                if (rawEqual)
                {
                    identical++;
                    result = "IDENTICAL";
                }
                else if (contractEqual)
                {
                    compilerDrift++;
                    result = orderedEqual
                        ? "PADDING/BYTE-LAYOUT DRIFT"
                        : "COMPILER SCHEDULING DRIFT";
                }
                else
                {
                    contractDrift++;
                    result = "SEMANTIC/ABI DRIFT";
                }

                Console.WriteLine(
                    $"{name,-22} {left.DeclaredBytes,6:X}/{right.DeclaredBytes,-6:X} " +
                    $"{left.InstructionCount,6}/{right.InstructionCount,-6} {result}");
                if (!contractEqual)
                {
                    Console.WriteLine(
                        $"  contract {left.ContractHash[..12]} -> {right.ContractHash[..12]}; " +
                        $"ordered {left.OrderedHash[..12]} -> {right.OrderedHash[..12]}");
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                $"summary: identical={identical} compiler-only={compilerDrift} " +
                $"semantic-or-ABI={contractDrift} missing={missing}");
            return missing == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"shader drift audit failed: {exception.Message}");
            return 1;
        }

        static string DescribePresence(ProgramContract? contract) =>
            contract is null ? "absent" : $"0x{contract.DeclaredBytes:X}";
    }

    private sealed record Snapshot(IReadOnlyDictionary<string, ProgramContract> Programs)
    {
        internal static Snapshot Load(string path)
        {
            var elf = ElfImage.Load(path);
            var programs = new Dictionary<string, ProgramContract>(StringComparer.Ordinal);
            foreach (var slot in elf.Relocations.Keys.Order())
            {
                if (!elf.Relocations.TryGetValue(slot, out var nameAddress) ||
                    !elf.Relocations.TryGetValue(slot + 8, out var headerAddress) ||
                    !elf.Relocations.TryGetValue(slot + 16, out var codeAddress) ||
                    !elf.TryReadCString(nameAddress, out var name) ||
                    !BackgroundPrograms.Contains(name) ||
                    !elf.TryReadUInt32(headerAddress + 0x44, out var declaredBytes) ||
                    declaredBytes is 0 or > 1024 * 1024 ||
                    !elf.TryReadBytes(codeAddress, checked((int)declaredBytes), out var code))
                {
                    continue;
                }

                programs.TryAdd(name, ProgramContract.Decode(name, code, declaredBytes));
            }

            if (programs.Count == 0)
            {
                throw new InvalidDataException("no named background AGC descriptors were found");
            }

            return new Snapshot(programs);
        }
    }

    private sealed record ProgramContract(
        int DeclaredBytes,
        int InstructionCount,
        string RawHash,
        string OrderedHash,
        string ContractHash)
    {
        internal static ProgramContract Decode(string name, byte[] code, uint declaredBytes)
        {
            var memory = new FirstWaveProbe.FlatMemory();
            memory.AddRegion(DecodeAddress, code);
            var context = new CpuContext(memory, Generation.Gen5);
            var decode = Gen5ShaderTranslator.TryDecodeProgram(
                context, DecodeAddress, out var program, out var error);
            if (!decode)
            {
                throw new InvalidDataException($"{name} decode failed: {error}");
            }

            var ordered = new StringBuilder();
            var contract = new List<string>();
            foreach (var instruction in program.Instructions)
            {
                ordered.Append(instruction.Encoding).Append('|')
                    .Append(instruction.Opcode).Append('|')
                    .AppendJoin(',', instruction.Destinations).Append('|')
                    .AppendJoin(',', instruction.Sources).Append('|')
                    .Append(instruction.Control).AppendLine();

                contract.Add($"op:{instruction.Encoding}:{instruction.Opcode}");
                if (instruction.Control is not null)
                {
                    contract.Add($"ctrl:{instruction.Control}");
                }
                foreach (var source in instruction.Sources)
                {
                    if (source.Kind == Gen5OperandKind.LiteralConstant)
                    {
                        contract.Add($"literal:{source.Value:X8}");
                    }
                }
            }

            contract.Sort(StringComparer.Ordinal);
            contract.Add($"pixel-exports:{program.PixelColorExportMasks:X8}");
            return new ProgramContract(
                checked((int)declaredBytes),
                program.Instructions.Count,
                Hash(code),
                Hash(Encoding.UTF8.GetBytes(ordered.ToString())),
                Hash(Encoding.UTF8.GetBytes(string.Join('\n', contract))));
        }

        private static string Hash(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private sealed class ElfImage
    {
        private readonly byte[] _data;
        private readonly IReadOnlyList<LoadSegment> _segments;

        private ElfImage(
            byte[] data,
            IReadOnlyList<LoadSegment> segments,
            IReadOnlyDictionary<ulong, ulong> relocations)
        {
            _data = data;
            _segments = segments;
            Relocations = relocations;
        }

        internal IReadOnlyDictionary<ulong, ulong> Relocations { get; }

        internal static ElfImage Load(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 0x40 ||
                data[0] != 0x7F || data[1] != (byte)'E' ||
                data[2] != (byte)'L' || data[3] != (byte)'F')
            {
                throw new InvalidDataException($"{path} is not ELF64");
            }

            var programHeaderOffset = ReadUInt64(data, 0x20);
            var entrySize = ReadUInt16(data, 0x36);
            var entryCount = ReadUInt16(data, 0x38);
            var segments = new List<LoadSegment>();
            (ulong Offset, ulong Size)? dynamic = null;
            for (var index = 0; index < entryCount; index++)
            {
                var offset = checked((int)(programHeaderOffset + (ulong)(index * entrySize)));
                var type = ReadUInt32(data, offset);
                var fileOffset = ReadUInt64(data, offset + 8);
                var virtualAddress = ReadUInt64(data, offset + 16);
                var fileSize = ReadUInt64(data, offset + 32);
                if (type == 1 && fileSize > 0)
                {
                    segments.Add(new LoadSegment(fileOffset, virtualAddress, fileSize));
                }
                else if (type == 2)
                {
                    dynamic = (fileOffset, fileSize);
                }
            }

            if (dynamic is null)
            {
                throw new InvalidDataException("ELF has no PT_DYNAMIC segment");
            }

            var tags = new Dictionary<ulong, ulong>();
            for (ulong cursor = 0; cursor + 16 <= dynamic.Value.Size; cursor += 16)
            {
                var offset = checked((int)(dynamic.Value.Offset + cursor));
                var tag = ReadUInt64(data, offset);
                var value = ReadUInt64(data, offset + 8);
                if (tag == 0)
                {
                    break;
                }
                tags.TryAdd(tag, value);
            }

            if (!tags.TryGetValue(7, out var relaAddress) ||
                !tags.TryGetValue(8, out var relaBytes) ||
                !tags.TryGetValue(9, out var relaEntryBytes) ||
                relaEntryBytes < 24)
            {
                throw new InvalidDataException("ELF has no complete RELA table");
            }

            var relaOffset = VirtualToFile(segments, relaAddress);
            var relocations = new Dictionary<ulong, ulong>();
            for (ulong cursor = 0; cursor + relaEntryBytes <= relaBytes; cursor += relaEntryBytes)
            {
                var offset = checked((int)(relaOffset + cursor));
                var target = ReadUInt64(data, offset);
                var info = ReadUInt64(data, offset + 8);
                var addend = unchecked((ulong)BinaryPrimitives.ReadInt64LittleEndian(
                    data.AsSpan(offset + 16, 8)));
                if ((uint)info == 8)
                {
                    relocations[target] = addend;
                }
            }

            return new ElfImage(data, segments, relocations);
        }

        internal bool TryReadCString(ulong address, out string value)
        {
            value = string.Empty;
            if (!TryVirtualToFile(address, out var fileOffset))
            {
                return false;
            }

            var end = fileOffset;
            var limit = Math.Min(_data.Length, fileOffset + 128);
            while (end < limit && _data[end] != 0)
            {
                var character = _data[end];
                if (character is < 0x20 or > 0x7E)
                {
                    return false;
                }
                end++;
            }

            if (end == limit)
            {
                return false;
            }

            value = Encoding.ASCII.GetString(_data, fileOffset, end - fileOffset);
            return true;
        }

        internal bool TryReadUInt32(ulong address, out uint value)
        {
            value = 0;
            if (!TryVirtualToFile(address, out var fileOffset) ||
                fileOffset > _data.Length - sizeof(uint))
            {
                return false;
            }
            value = ReadUInt32(_data, fileOffset);
            return true;
        }

        internal bool TryReadBytes(ulong address, int count, out byte[] value)
        {
            value = [];
            if (!TryVirtualToFile(address, out var fileOffset) || count < 0 ||
                fileOffset > _data.Length - count)
            {
                return false;
            }
            value = _data.AsSpan(fileOffset, count).ToArray();
            return true;
        }

        private bool TryVirtualToFile(ulong address, out int fileOffset)
        {
            foreach (var segment in _segments)
            {
                if (address >= segment.VirtualAddress &&
                    address < segment.VirtualAddress + segment.FileSize)
                {
                    fileOffset = checked((int)(segment.FileOffset + address - segment.VirtualAddress));
                    return true;
                }
            }
            fileOffset = 0;
            return false;
        }

        private static ulong VirtualToFile(IReadOnlyList<LoadSegment> segments, ulong address)
        {
            foreach (var segment in segments)
            {
                if (address >= segment.VirtualAddress &&
                    address < segment.VirtualAddress + segment.FileSize)
                {
                    return segment.FileOffset + address - segment.VirtualAddress;
                }
            }
            throw new InvalidDataException($"ELF address 0x{address:X} is outside PT_LOAD");
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        private static ulong ReadUInt64(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

        private sealed record LoadSegment(ulong FileOffset, ulong VirtualAddress, ulong FileSize);
    }
}
