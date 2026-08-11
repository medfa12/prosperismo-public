// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Replays NPXS40087's straight-line WaveColourPreset seeder. The table is
/// runtime-initialised, so reading its file bytes directly cannot recover the
/// <c>light_p</c> ColorCb records.
/// </summary>
public static class Ps5NativeWaveColourPresetMaterializer
{
    public const int HomeScreenPreset = 4;
    public const int BootPreset = 11;
    public const int RecordByteCount = 0x80;
    public const int ColorCbByteCount = 0x7C;

    private const ulong VirtualAddressDelta = 0x4000;
    private const ulong TableAddress = 0x137CFC0;
    private const int PresetCount = 22;
    private const ulong SeederAddress = 0xEA700;
    private const ulong SeederEndAddress = 0xED000;

    private readonly record struct RegisterSource(ulong Address, bool Broadcast);

    public static byte[] MaterializeColorCb(ReadOnlySpan<byte> eboot, int preset)
    {
        if ((uint)preset >= PresetCount)
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        var seederOffset = checked((int)(SeederAddress + VirtualAddressDelta));
        var seederLength = checked((int)(SeederEndAddress - SeederAddress));
        if (eboot.Length < seederOffset + seederLength)
        {
            throw new InvalidDataException("NPXS40087 eboot does not contain the light-colour seeder");
        }

        var flat = new byte[PresetCount * RecordByteCount];
        var covered = new bool[flat.Length];
        var sources = new Dictionary<Register, RegisterSource>();
        var reader = new ByteArrayCodeReader(eboot.Slice(seederOffset, seederLength).ToArray());
        var decoder = Decoder.Create(64, reader);
        decoder.IP = SeederAddress;

        while (decoder.IP < SeederEndAddress)
        {
            decoder.Decode(out var instruction);
            if (instruction.Code == Code.INVALID)
            {
                continue;
            }

            var mnemonic = instruction.Mnemonic;
            if (instruction.Op0Kind == OpKind.Register &&
                instruction.Op1Kind == OpKind.Memory &&
                instruction.IsIPRelativeMemoryOperand)
            {
                if (mnemonic is Mnemonic.Vmovups or Mnemonic.Vmovaps or
                    Mnemonic.Vmovsd or Mnemonic.Vmovlps)
                {
                    sources[instruction.Op0Register] = new RegisterSource(
                        instruction.IPRelativeMemoryAddress,
                        Broadcast: false);
                    continue;
                }

                if (mnemonic == Mnemonic.Vbroadcastss)
                {
                    sources[instruction.Op0Register] = new RegisterSource(
                        instruction.IPRelativeMemoryAddress,
                        Broadcast: true);
                    continue;
                }
            }

            if (instruction.Op0Kind == OpKind.Register &&
                instruction.Op1Kind == OpKind.Register &&
                mnemonic is Mnemonic.Vmovups or Mnemonic.Vmovaps or Mnemonic.Vmovapd &&
                sources.TryGetValue(instruction.Op1Register, out var copiedSource))
            {
                sources[instruction.Op0Register] = copiedSource;
                continue;
            }

            if (instruction.Op0Kind == OpKind.Memory &&
                instruction.Op1Kind == OpKind.Register &&
                instruction.IsIPRelativeMemoryOperand &&
                sources.TryGetValue(instruction.Op1Register, out var source))
            {
                var byteCount = mnemonic switch
                {
                    Mnemonic.Vmovups or Mnemonic.Vmovaps =>
                        IsYmm(instruction.Op1Register) ? 32 : 16,
                    Mnemonic.Vmovsd or Mnemonic.Vmovlps => 8,
                    _ => 0,
                };
                if (byteCount > 0)
                {
                    Store(
                        eboot,
                        flat,
                        covered,
                        instruction.IPRelativeMemoryAddress,
                        source,
                        byteCount);
                }
                continue;
            }

            if (mnemonic == Mnemonic.Mov &&
                instruction.Op0Kind == OpKind.Memory &&
                instruction.IsIPRelativeMemoryOperand &&
                instruction.Op1Kind is OpKind.Immediate32 or OpKind.Immediate32to64)
            {
                Span<byte> immediate = new byte[sizeof(uint)];
                BitConverter.TryWriteBytes(immediate, instruction.Immediate32);
                Store(flat, covered, instruction.IPRelativeMemoryAddress, immediate);
            }
        }

        var recordOffset = preset * RecordByteCount;
        if (!covered.AsSpan(recordOffset, ColorCbByteCount).Contains(false))
        {
            return flat.AsSpan(recordOffset, ColorCbByteCount).ToArray();
        }

        throw new InvalidDataException($"WaveColourPreset {preset} is incomplete in the firmware seeder");
    }

    private static bool IsYmm(Register register) => register is >= Register.YMM0 and <= Register.YMM31;

    private static void Store(
        ReadOnlySpan<byte> eboot,
        Span<byte> flat,
        Span<bool> covered,
        ulong destination,
        RegisterSource source,
        int byteCount)
    {
        var sourceOffset = checked((int)(source.Address + VirtualAddressDelta));
        if (sourceOffset < 0 || sourceOffset > eboot.Length - (source.Broadcast ? 4 : byteCount))
        {
            throw new InvalidDataException("WaveColourPreset seeder references data outside the eboot");
        }

        if (source.Broadcast)
        {
            var scalar = eboot.Slice(sourceOffset, 4);
            var data = new byte[byteCount];
            for (var offset = 0; offset < data.Length; offset += 4)
            {
                scalar.CopyTo(data.AsSpan(offset, 4));
            }
            Store(flat, covered, destination, data);
            return;
        }

        Store(flat, covered, destination, eboot.Slice(sourceOffset, byteCount));
    }

    private static void Store(
        Span<byte> flat,
        Span<bool> covered,
        ulong destination,
        ReadOnlySpan<byte> data)
    {
        var tableOffset = checked((long)destination - (long)TableAddress);
        if (tableOffset < 0 || tableOffset >= flat.Length)
        {
            return;
        }

        var count = Math.Min(data.Length, flat.Length - (int)tableOffset);
        data[..count].CopyTo(flat[(int)tableOffset..]);
        covered.Slice((int)tableOffset, count).Fill(true);
    }
}
