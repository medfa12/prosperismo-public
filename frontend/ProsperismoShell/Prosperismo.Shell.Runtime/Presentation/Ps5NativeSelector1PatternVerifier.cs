// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Byte-level verifier for the PFRM files emitted by
/// <c>tools/export_particle_frames.py</c>.
/// </summary>
public static class Ps5NativeSelector1PatternVerifier
{
    /// <summary>
    /// Compares every small and large resource pair in one exporter frame.
    /// This is the selector-agnostic verification boundary used by cold boot.
    /// </summary>
    public static void VerifyAllResourcesAgainstPythonExporterFrame(
        Ps5NativeSelector1PatternMaterializer materializer,
        double elapsedSeconds,
        string exporterFramePath)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        ArgumentException.ThrowIfNullOrEmpty(exporterFramePath);

        var expected = ReadAllPythonResources(File.ReadAllBytes(exporterFramePath), elapsedSeconds);
        var actual = materializer.MaterializeResources(elapsedSeconds);
        CompareFamily(elapsedSeconds, "small", expected.Small, actual.SmallBanks);
        CompareFamily(elapsedSeconds, "large", expected.Large, actual.LargeBanks);
    }

    /// <summary>
    /// Compares all eight small compute and draw blocks in one Python-exporter
    /// frame against the C# materializer at the same authored time.
    /// </summary>
    public static void VerifyAgainstPythonExporterFrame(
        Ps5NativeSelector1PatternMaterializer materializer,
        double elapsedSeconds,
        string exporterFramePath)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        ArgumentException.ThrowIfNullOrEmpty(exporterFramePath);

        var expected = ReadPythonFrame(File.ReadAllBytes(exporterFramePath), elapsedSeconds);
        var actual = materializer.Materialize(elapsedSeconds);
        if (actual.Banks.Count != Ps5NativeSelector1ResourceFrame.BankCount)
        {
            throw new InvalidDataException(
                $"C# materializer returned {actual.Banks.Count} banks, expected {Ps5NativeSelector1ResourceFrame.BankCount}");
        }

        for (var index = 0; index < Ps5NativeSelector1ResourceFrame.BankCount; index++)
        {
            var actualBank = actual.Banks[index];
            var expectedBank = expected[index];
            CompareBytes(elapsedSeconds, index, "ResourcesCs", expectedBank.Compute, actualBank.ResourcesCs.Span);
            CompareBytes(elapsedSeconds, index, "ResourcesVsPs", expectedBank.Draw, actualBank.ResourcesVsPs.Span);
        }
    }

    private static IReadOnlyList<ExpectedBank> ReadPythonFrame(
        ReadOnlySpan<byte> frame,
        double expectedTime)
    {
        if (frame.Length < 16 || !frame[..4].SequenceEqual("PFRM"u8))
        {
            throw new InvalidDataException("Python exporter frame is not a PFRM file");
        }

        var groupCount = checked((int)ReadUInt32(frame, 4));
        var encodedTime = ReadUInt32(frame, 8);
        if (encodedTime != BitConverter.SingleToUInt32Bits((float)expectedTime))
        {
            throw new InvalidDataException(
                $"Python exporter frame time is {BitConverter.UInt32BitsToSingle(encodedTime):G9}, expected {expectedTime:G9}");
        }

        var banks = new ExpectedBank?[Ps5NativeSelector1ResourceFrame.BankCount];
        var cursor = 16;
        for (var group = 0; group < groupCount; group++)
        {
            if (cursor > frame.Length - 16)
            {
                throw new InvalidDataException("Python exporter frame ends inside a group header");
            }

            var kind = ReadUInt32(frame, cursor);
            var index = checked((int)ReadUInt32(frame, cursor + 4));
            var computeLength = checked((int)ReadUInt32(frame, cursor + 8));
            var drawLength = checked((int)ReadUInt32(frame, cursor + 12));
            cursor += 16;
            var payloadLength = checked(computeLength + drawLength);
            if (payloadLength < 0 || payloadLength > frame.Length - cursor)
            {
                throw new InvalidDataException("Python exporter frame ends inside a group payload");
            }

            var isSmall = (kind & 0xFFu) == 0;
            if (isSmall && (uint)index < Ps5NativeSelector1ResourceFrame.BankCount)
            {
                if (computeLength != Ps5NativeParticleComputeRequest.ResourceByteCount ||
                    drawLength != Ps5NativeSelector1PatternMaterializer.SmallResourcesVsPsByteCount)
                {
                    throw new InvalidDataException(
                        $"Python exporter small bank {index} has lengths {computeLength}/{drawLength}");
                }

                if (banks[index] is not null)
                {
                    throw new InvalidDataException($"Python exporter duplicated small bank {index}");
                }

                banks[index] = new ExpectedBank(
                    frame.Slice(cursor, computeLength).ToArray(),
                    frame.Slice(cursor + computeLength, drawLength).ToArray());
            }

            cursor += payloadLength;
        }

        if (cursor != frame.Length)
        {
            throw new InvalidDataException(
                $"Python exporter frame has {frame.Length - cursor} trailing bytes");
        }

        if (banks.Any(static bank => bank is null))
        {
            throw new InvalidDataException("Python exporter frame did not contain all eight small banks");
        }

        return banks.Select(static bank => bank!).ToArray();
    }

    private static ExpectedResources ReadAllPythonResources(
        ReadOnlySpan<byte> frame,
        double expectedTime)
    {
        if (frame.Length < 16 || !frame[..4].SequenceEqual("PFRM"u8))
        {
            throw new InvalidDataException("Python exporter frame is not a PFRM file");
        }

        var groupCount = checked((int)ReadUInt32(frame, 4));
        var encodedTime = ReadUInt32(frame, 8);
        if (encodedTime != BitConverter.SingleToUInt32Bits((float)expectedTime))
        {
            throw new InvalidDataException(
                $"Python exporter frame time is {BitConverter.UInt32BitsToSingle(encodedTime):G9}, expected {expectedTime:G9}");
        }

        var small = new ExpectedBank?[Ps5NativePatternResourceFrame.SmallBankCount];
        var large = new ExpectedBank?[Ps5NativePatternResourceFrame.LargeBankCount];
        var cursor = 16;
        for (var group = 0; group < groupCount; group++)
        {
            if (cursor > frame.Length - 16)
            {
                throw new InvalidDataException("Python exporter frame ends inside a group header");
            }

            var kind = ReadUInt32(frame, cursor);
            var index = checked((int)ReadUInt32(frame, cursor + 4));
            var computeLength = checked((int)ReadUInt32(frame, cursor + 8));
            var drawLength = checked((int)ReadUInt32(frame, cursor + 12));
            cursor += 16;
            var payloadLength = checked(computeLength + drawLength);
            if (payloadLength < 0 || payloadLength > frame.Length - cursor)
            {
                throw new InvalidDataException("Python exporter frame ends inside a group payload");
            }

            var family = kind & 0xFFu;
            var target = family switch
            {
                0 => small,
                1 => large,
                _ => throw new InvalidDataException($"Python exporter group has unknown kind {family}"),
            };
            var expectedDrawLength = family == 0
                ? Ps5NativeSelector1PatternMaterializer.SmallResourcesVsPsByteCount
                : 0xEC;
            if ((uint)index >= target.Length ||
                computeLength != Ps5NativeParticleComputeRequest.ResourceByteCount ||
                drawLength != expectedDrawLength)
            {
                throw new InvalidDataException(
                    $"Python exporter kind {family} bank {index} has invalid lengths {computeLength}/{drawLength}");
            }
            if (target[index] is not null)
            {
                throw new InvalidDataException($"Python exporter duplicated kind {family} bank {index}");
            }

            target[index] = new ExpectedBank(
                frame.Slice(cursor, computeLength).ToArray(),
                frame.Slice(cursor + computeLength, drawLength).ToArray());
            cursor += payloadLength;
        }

        if (cursor != frame.Length || small.Any(static bank => bank is null) ||
            large.Any(static bank => bank is null))
        {
            throw new InvalidDataException("Python exporter frame is incomplete or has trailing bytes");
        }

        return new ExpectedResources(
            small.Select(static bank => bank!).ToArray(),
            large.Select(static bank => bank!).ToArray());
    }

    private static void CompareFamily(
        double elapsedSeconds,
        string family,
        IReadOnlyList<ExpectedBank> expected,
        IReadOnlyList<Ps5NativePatternResourceBank> actual)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"C# materializer returned {actual.Count} {family} banks, expected {expected.Count}");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            CompareBytes(elapsedSeconds, index, $"{family}.ResourcesCs",
                expected[index].Compute, actual[index].ResourcesCs.Span);
            CompareBytes(elapsedSeconds, index, $"{family}.ResourcesVsPs",
                expected[index].Draw, actual[index].ResourcesVsPs.Span);
        }
    }

    private static void CompareBytes(
        double elapsedSeconds,
        int bankIndex,
        string family,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual)
    {
        if (expected.Length != actual.Length)
        {
            throw new InvalidDataException(
                $"t={elapsedSeconds:G9} bank={bankIndex} {family} length {actual.Length}, expected {expected.Length}");
        }

        for (var offset = 0; offset < expected.Length; offset++)
        {
            if (expected[offset] != actual[offset])
            {
                throw new InvalidDataException(
                    $"t={elapsedSeconds:G9} bank={bankIndex} {family} mismatch at 0x{offset:X}: " +
                    $"actual=0x{actual[offset]:X2}, expected=0x{expected[offset]:X2}");
            }
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(uint))
        {
            throw new InvalidDataException("Python exporter frame read is outside the file");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private sealed record ExpectedBank(byte[] Compute, byte[] Draw);

    private sealed record ExpectedResources(
        IReadOnlyList<ExpectedBank> Small,
        IReadOnlyList<ExpectedBank> Large);
}
