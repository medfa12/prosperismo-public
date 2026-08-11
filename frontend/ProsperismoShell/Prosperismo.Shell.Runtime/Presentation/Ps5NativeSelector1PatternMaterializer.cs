// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Prosperismo.Libs.Presentation;

/// <summary>One of selector 1's eight small-particle resource pairs.</summary>
public sealed record Ps5NativeSelector1ResourceBank(
    int Index,
    ReadOnlyMemory<byte> ResourcesCs,
    ReadOnlyMemory<byte> ResourcesVsPs);

/// <summary>The complete selector-1 small-bank state at one authored time.</summary>
public sealed record Ps5NativeSelector1ResourceFrame(
    double ElapsedSeconds,
    IReadOnlyList<Ps5NativeSelector1ResourceBank> Banks)
{
    public const int BankCount = Ps5NativeParticleComputeRequest.SmallParticleBankCount;

    public Ps5NativeSelector1ResourceBank GetBank(int index)
    {
        if ((uint)index >= BankCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Banks[index];
    }
}

/// <summary>One authored particle resource pair from any NPXS40087 pattern.</summary>
public sealed record Ps5NativePatternResourceBank(
    int Index,
    ReadOnlyMemory<byte> ResourcesCs,
    ReadOnlyMemory<byte> ResourcesVsPs);

/// <summary>
/// Complete small- and large-particle resource state for one authored pattern
/// at one local time. The fixed bank counts are the native BGLayer allocation:
/// eight small banks and two large banks.
/// </summary>
public sealed record Ps5NativePatternResourceFrame(
    int Selector,
    string EmbeddedName,
    double ElapsedSeconds,
    IReadOnlyList<Ps5NativePatternResourceBank> SmallBanks,
    IReadOnlyList<Ps5NativePatternResourceBank> LargeBanks)
{
    public const int SmallBankCount = Ps5NativeParticleComputeRequest.SmallParticleBankCount;
    public const int LargeBankCount = 2;
}

/// <summary>
/// Exact 12.40 materializer for the NPXS40087 selector-1
/// <c>spread_expanded</c> pattern.
///
/// <para>The constructor reads the same length/blob offsets as
/// <c>tools/export_particle_frames.py</c>. It decodes the serialized fields
/// once, then replays the authored direct and interpolated writes for each
/// requested time. No frame table, finite loop, or host-authored particle
/// value is involved.</para>
/// </summary>
public sealed class Ps5NativeSelector1PatternMaterializer
{
    public const int Selector = 1;
    public const int BlobLengthsVirtualAddress = 0xFF18A0;
    public const int BlobVirtualAddress = 0xFF3890;
    public const int VirtualAddressToFileOffset = 0x4000;
    public const int SmallResourcesVsPsByteCount = 0x140;

    private const int PatternCount = 7;
    private const int VectorCount = 25;
    private const int SmallResourcesCsByteCount = Ps5NativeParticleComputeRequest.ResourceByteCount;

    private const int LargeResourcesCsByteCount = Ps5NativeParticleComputeRequest.ResourceByteCount;
    private const int LargeResourcesVsPsByteCount = 0xEC;

    private static readonly int[] BlobVirtualAddresses =
    [
        0xFF18E0,
        0xFF3890,
        0xFF5690,
        0xFF7E00,
        0xFFA660,
        0xFFCD70,
        0xFFF6D0,
    ];

    private readonly IReadOnlyList<DecodedField> _fields;

    private Ps5NativeSelector1PatternMaterializer(
        int selector,
        string embeddedName,
        int blobFileOffset,
        int blobByteLength,
        IReadOnlyList<DecodedField> fields)
    {
        PatternSelector = selector;
        EmbeddedName = embeddedName;
        BlobFileOffset = blobFileOffset;
        BlobByteLength = blobByteLength;
        _fields = fields;
    }

    /// <summary>Native pattern selector (0 is coldboot, 1 is spread_expanded).</summary>
    public int PatternSelector { get; }

    public string EmbeddedName { get; }

    /// <summary>File offset of the audited selector-1 blob.</summary>
    public int BlobFileOffset { get; }

    /// <summary>Serialized selector-1 blob length read from the eboot table.</summary>
    public int BlobByteLength { get; }

    public static Ps5NativeSelector1PatternMaterializer FromEboot(string ebootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ebootPath);
        return FromEboot(File.ReadAllBytes(ebootPath));
    }

    public static Ps5NativeSelector1PatternMaterializer FromEboot(
        string ebootPath,
        int selector)
    {
        ArgumentException.ThrowIfNullOrEmpty(ebootPath);
        return FromEboot(File.ReadAllBytes(ebootPath), selector);
    }

    public static Ps5NativeSelector1PatternMaterializer FromEboot(ReadOnlyMemory<byte> eboot)
        => FromEboot(eboot, Selector);

    /// <summary>
    /// Opens one of the seven serialized 12.40 particle patterns. The legacy
    /// parameterless selector overload remains pinned to selector 1.
    /// </summary>
    public static Ps5NativeSelector1PatternMaterializer FromEboot(
        ReadOnlyMemory<byte> eboot,
        int selector)
    {
        if ((uint)selector >= PatternCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selector));
        }

        var data = eboot.Span;
        var lengthsOffset = checked(BlobLengthsVirtualAddress + VirtualAddressToFileOffset);
        var lengths = new ulong[PatternCount];
        for (var index = 0; index < PatternCount; index++)
        {
            lengths[index] = ReadUInt64(data, lengthsOffset + index * sizeof(ulong));
        }

        var blobOffset = checked(BlobVirtualAddresses[selector] + VirtualAddressToFileOffset);
        var blobLength = checked((int)lengths[selector]);
        if (blobLength <= 0 || blobOffset < 0 || blobOffset + blobLength > data.Length)
        {
            throw new InvalidDataException($"selector-{selector} blob is outside the NPXS40087 eboot");
        }

        var blob = data.Slice(blobOffset, blobLength);
        var decoded = DecodeBlob(blob);
        if (selector == Selector &&
            !string.Equals(decoded.EmbeddedName, "spread_expanded", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"selector 1 embedded name is '{decoded.EmbeddedName}', expected spread_expanded");
        }

        return new Ps5NativeSelector1PatternMaterializer(
            selector,
            decoded.EmbeddedName,
            blobOffset,
            blobLength,
            decoded.Fields);
    }

    /// <summary>
    /// Materializes all eight small ResourcesCs and ResourcesVsPs blocks at an
    /// arbitrary non-negative elapsed time. Values after an authored event
    /// retain the event's terminal state, matching the Python exporter.
    /// </summary>
    public Ps5NativeSelector1ResourceFrame Materialize(double elapsedSeconds)
    {
        var frame = MaterializeResources(elapsedSeconds);
        var banks = frame.SmallBanks.Select(static bank => new Ps5NativeSelector1ResourceBank(
            bank.Index,
            bank.ResourcesCs,
            bank.ResourcesVsPs)).ToArray();
        return new Ps5NativeSelector1ResourceFrame(elapsedSeconds, banks);
    }

    /// <summary>
    /// Materializes all four native resource families at an arbitrary local
    /// event records for the requested time.
    /// </summary>
    public Ps5NativePatternResourceFrame MaterializeResources(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        var state = new Dictionary<ResourceKey, byte[]>(ResourceKeyComparer.Instance);
        var operations = new List<Operation>();
        var sequence = 0;
        foreach (var field in _fields)
        {
            if (!IsResource(field.Family))
            {
                continue;
            }

            foreach (var direct in field.DirectEvents)
            {
                if (direct.Time <= elapsedSeconds)
                {
                    operations.Add(new Operation(
                        direct.Time,
                        Kind: 0,
                        field.Field,
                        sequence++,
                        field.Family,
                        direct.DestinationIndices,
                        direct.Assignments,
                        InterpolatedAssignments: [],
                        Amount: 0.0));
                }
            }

            foreach (var interpolated in field.InterpolatedEvents)
            {
                if (elapsedSeconds < interpolated.StartTime)
                {
                    continue;
                }

                var span = interpolated.EndTime - interpolated.StartTime;
                var amount = span <= 0.0
                    ? 1.0
                    : Math.Min((elapsedSeconds - interpolated.StartTime) / span, 1.0);
                operations.Add(new Operation(
                    Math.Min(elapsedSeconds, interpolated.EndTime),
                    Kind: 1,
                    field.Field,
                    sequence++,
                    field.Family,
                    interpolated.DestinationIndices,
                    DirectAssignments: [],
                    InterpolatedAssignments: interpolated.Assignments,
                    Amount: amount));
            }
        }

        // Python's list.sort is stable and its key is (time, kind, field).
        // Sequence preserves that stability when two records share all three
        // key values.
        operations.Sort(static (left, right) =>
        {
            var comparison = left.EffectiveTime.CompareTo(right.EffectiveTime);
            if (comparison != 0) return comparison;
            comparison = left.Kind.CompareTo(right.Kind);
            if (comparison != 0) return comparison;
            comparison = left.Field.CompareTo(right.Field);
            return comparison != 0 ? comparison : left.Sequence.CompareTo(right.Sequence);
        });

        foreach (var operation in operations)
        {
            foreach (var index in operation.DestinationIndices)
            {
                var target = GetBlock(state, operation.Family, index);
                if (operation.Kind == 0)
                {
                    foreach (var assignment in operation.DirectAssignments)
                    {
                        ApplyDirect(target, assignment);
                    }
                }
                else
                {
                    foreach (var assignment in operation.InterpolatedAssignments)
                    {
                        ApplyInterpolated(target, assignment, operation.Amount);
                    }
                }
            }
        }

        var smallBanks = new Ps5NativePatternResourceBank[Ps5NativePatternResourceFrame.SmallBankCount];
        for (var index = 0; index < smallBanks.Length; index++)
        {
            smallBanks[index] = new Ps5NativePatternResourceBank(
                index,
                GetBlock(state, ResourceFamily.SmallCompute, index),
                GetBlock(state, ResourceFamily.SmallDraw, index));
        }

        var largeBanks = new Ps5NativePatternResourceBank[Ps5NativePatternResourceFrame.LargeBankCount];
        for (var index = 0; index < largeBanks.Length; index++)
        {
            largeBanks[index] = new Ps5NativePatternResourceBank(
                index,
                GetBlock(state, ResourceFamily.LargeCompute, index),
                GetBlock(state, ResourceFamily.LargeDraw, index));
        }

        return new Ps5NativePatternResourceFrame(
            PatternSelector,
            EmbeddedName,
            elapsedSeconds,
            smallBanks,
            largeBanks);
    }

    private static byte[] GetBlock(
        Dictionary<ResourceKey, byte[]> state,
        ResourceFamily family,
        int index)
    {
        var bankCount = family is ResourceFamily.LargeCompute or ResourceFamily.LargeDraw
            ? Ps5NativePatternResourceFrame.LargeBankCount
            : Ps5NativePatternResourceFrame.SmallBankCount;
        if ((uint)index >= bankCount)
        {
            throw new InvalidDataException(
                $"selector destination index {index} is outside {family}'s {bankCount} banks");
        }

        var key = new ResourceKey(family, index);
        if (!state.TryGetValue(key, out var block))
        {
            block = new byte[family switch
            {
                ResourceFamily.SmallCompute => SmallResourcesCsByteCount,
                ResourceFamily.SmallDraw => SmallResourcesVsPsByteCount,
                ResourceFamily.LargeCompute => LargeResourcesCsByteCount,
                ResourceFamily.LargeDraw => LargeResourcesVsPsByteCount,
                _ => throw new InvalidOperationException($"{family} has no resource block"),
            }];
            state.Add(key, block);
        }

        return block;
    }

    private static void ApplyDirect(byte[] target, DirectAssignment assignment)
    {
        var offset = assignment.DestinationOffset;
        var raw = assignment.Payload;
        switch (assignment.Opcode)
        {
            case 1:
                WriteBytes(target, offset, raw, 8);
                break;
            case 2:
                WriteBytes(target, offset, raw, 4);
                break;
            case 3:
                WriteBytes(target, offset, raw, 2);
                break;
            case 4:
                WriteBytes(target, offset, raw, 8);
                break;
            case 5:
                WriteBytes(target, offset, raw, 4);
                break;
            case 6:
                WriteBytes(target, offset, raw, 2);
                break;
            case 7:
                WriteBytes(target, offset, raw, 8);
                break;
            case 8:
                WriteBytes(target, offset, raw, 4);
                break;
            case 9:
                return;
            case 10:
            {
                var value = unchecked((uint)raw);
                var mask = unchecked((uint)(raw >> 32));
                var current = BinaryPrimitives.ReadUInt32LittleEndian(target.AsSpan(offset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    target.AsSpan(offset, 4),
                    (current & ~mask) | value);
                break;
            }
            default:
                throw new InvalidDataException(
                    $"selector-1 routed direct opcode {assignment.Opcode} is unsupported");
        }
    }

    private static void ApplyInterpolated(
        byte[] target,
        InterpolatedAssignment assignment,
        double amount)
    {
        var offset = assignment.DestinationOffset;
        if (assignment.Opcode == 8)
        {
            // Python unpacks f32 values to binary64 Python floats and performs
            // the expression before packing one f32 result. Keep that order.
            var interpolatedValue = (double)assignment.StartF32 +
                (((double)assignment.EndF32 - assignment.StartF32) * amount);
            WriteUInt32(target, offset, BitConverter.SingleToUInt32Bits((float)interpolatedValue));
            return;
        }

        if (assignment.Opcode == 7)
        {
            var start = BitConverter.Int64BitsToDouble(unchecked((long)assignment.Start));
            var end = BitConverter.Int64BitsToDouble(unchecked((long)assignment.End));
            WriteUInt64(target, offset, BitConverter.DoubleToUInt64Bits(start + ((end - start) * amount)));
            return;
        }

        var value = assignment.Opcode switch
        {
            1 => (long)(DecodeUnsigned(assignment.Start, 64) +
                ((DecodeUnsigned(assignment.End, 64) - DecodeUnsigned(assignment.Start, 64)) * amount)),
            2 => (long)(DecodeUnsigned(assignment.Start, 32) +
                ((DecodeUnsigned(assignment.End, 32) - DecodeUnsigned(assignment.Start, 32)) * amount)),
            3 => (long)(DecodeUnsigned(assignment.Start, 16) +
                ((DecodeUnsigned(assignment.End, 16) - DecodeUnsigned(assignment.Start, 16)) * amount)),
            4 => (long)(DecodeSigned(assignment.Start, 64) +
                ((DecodeSigned(assignment.End, 64) - DecodeSigned(assignment.Start, 64)) * amount)),
            5 => (long)(DecodeSigned(assignment.Start, 32) +
                ((DecodeSigned(assignment.End, 32) - DecodeSigned(assignment.Start, 32)) * amount)),
            6 => (long)(DecodeSigned(assignment.Start, 16) +
                ((DecodeSigned(assignment.End, 16) - DecodeSigned(assignment.Start, 16)) * amount)),
            _ => throw new InvalidDataException(
                $"selector-1 routed interpolated opcode {assignment.Opcode} is unsupported"),
        };

        switch (assignment.Opcode)
        {
            case 1:
                WriteUInt64(target, offset, unchecked((ulong)value));
                break;
            case 2:
                WriteUInt32(target, offset, unchecked((uint)value));
                break;
            case 3:
                WriteUInt16(target, offset, unchecked((ushort)value));
                break;
            case 4:
                WriteUInt64(target, offset, unchecked((ulong)value));
                break;
            case 5:
                WriteUInt32(target, offset, unchecked((uint)value));
                break;
            case 6:
                WriteUInt16(target, offset, unchecked((ushort)value));
                break;
        }
    }

    private static double DecodeUnsigned(ulong value, int bits) => bits switch
    {
        16 => value & ushort.MaxValue,
        32 => value & uint.MaxValue,
        64 => value,
        _ => throw new ArgumentOutOfRangeException(nameof(bits)),
    };

    private static double DecodeSigned(ulong value, int bits)
    {
        return bits switch
        {
            16 => (short)(value & ushort.MaxValue),
            32 => (int)(value & uint.MaxValue),
            64 => unchecked((long)value),
            _ => throw new ArgumentOutOfRangeException(nameof(bits)),
        };
    }

    private static void WriteBytes(byte[] target, int offset, ulong value, int width)
    {
        Span<byte> raw = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, value);
        raw[..width].CopyTo(target.AsSpan(offset, width));
    }

    private static void WriteUInt16(byte[] target, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, sizeof(ushort)), value);

    private static void WriteUInt32(byte[] target, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, sizeof(uint)), value);

    private static void WriteUInt64(byte[] target, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(offset, sizeof(ulong)), value);

    private static DecodedBlob DecodeBlob(ReadOnlySpan<byte> blob)
    {
        var cursor = 0;
        var nameLength = checked((int)ReadUInt32(blob, ref cursor));
        if (nameLength < 2 || nameLength > 256 || cursor + nameLength + sizeof(uint) > blob.Length)
        {
            throw new InvalidDataException("selector-1 pattern name length is invalid");
        }

        var rawName = ReadBytes(blob, ref cursor, nameLength);
        if (rawName[^1] != 0 || rawName[..^1].Contains((byte)0))
        {
            throw new InvalidDataException("selector-1 pattern name is malformed");
        }

        var embeddedName = Encoding.ASCII.GetString(rawName[..^1]);
        _ = ReadUInt32(blob, ref cursor); // serialized pattern version
        var counts = new int[VectorCount];
        for (var field = 0; field < VectorCount; field++)
        {
            counts[field] = checked((int)ReadUInt32(blob, ref cursor));
        }

        var rawPrefixes = new byte[VectorCount][][];
        for (var field = 8; field <= 17; field++)
        {
            rawPrefixes[field] = new byte[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                rawPrefixes[field][record] = ReadBytes(blob, ref cursor, 12);
            }
        }
        for (var field = 18; field <= 23; field++)
        {
            rawPrefixes[field] = new byte[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                rawPrefixes[field][record] = ReadBytes(blob, ref cursor, 16);
            }
        }
        rawPrefixes[24] = new byte[counts[24]][];
        for (var record = 0; record < counts[24]; record++)
        {
            rawPrefixes[24][record] = ReadBytes(blob, ref cursor, 36);
        }

        var destinationIndices = new int[VectorCount][][];
        for (var field = 8; field <= 17; field++)
        {
            destinationIndices[field] = new int[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                var count = checked((int)ReadUInt32(rawPrefixes[field][record], 4));
                destinationIndices[field][record] = ReadInt32Array(blob, ref cursor, count);
            }
        }
        for (var field = 18; field <= 22; field++)
        {
            destinationIndices[field] = new int[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                var count = checked((int)ReadUInt32(rawPrefixes[field][record], 8));
                destinationIndices[field][record] = ReadInt32Array(blob, ref cursor, count);
            }
        }
        for (var record = 0; record < counts[23]; record++)
        {
            var count = checked((int)ReadUInt32(rawPrefixes[23][record], 12));
            _ = ReadBytes(blob, ref cursor, checked(count * sizeof(uint)));
        }

        _ = ReadBytes(blob, ref cursor, 5 * sizeof(float));

        var directAssignments = new DirectAssignment[VectorCount][][];
        for (var field = 8; field <= 17; field++)
        {
            directAssignments[field] = new DirectAssignment[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                var count = checked((int)ReadUInt32(rawPrefixes[field][record], 8));
                var assignments = new DirectAssignment[count];
                for (var index = 0; index < count; index++)
                {
                    var opcode = checked((int)ReadUInt32(blob, ref cursor));
                    _ = ReadUInt32(blob, ref cursor); // <IIQQ>: reserved u32
                    var payload = ReadUInt64(blob, ref cursor);
                    var destination = checked((int)ReadUInt64(blob, ref cursor));
                    assignments[index] = new DirectAssignment(opcode, payload, destination);
                }
                directAssignments[field][record] = assignments;
            }
        }

        var interpolatedAssignments = new InterpolatedAssignment[VectorCount][][];
        for (var field = 18; field <= 22; field++)
        {
            interpolatedAssignments[field] = new InterpolatedAssignment[counts[field]][];
            for (var record = 0; record < counts[field]; record++)
            {
                var count = checked((int)ReadUInt32(rawPrefixes[field][record], 12));
                var assignments = new InterpolatedAssignment[count];
                for (var index = 0; index < count; index++)
                {
                    // <IIQQQ>: opcode, one reserved u32, start u64, end u64,
                    // destination u64. There is only one reserved word.
                    var opcode = checked((int)ReadUInt32(blob, ref cursor));
                    _ = ReadUInt32(blob, ref cursor); // reserved u32
                    var start = ReadUInt64(blob, ref cursor);
                    var end = ReadUInt64(blob, ref cursor);
                    var destination = checked((int)ReadUInt64(blob, ref cursor));
                    assignments[index] = new InterpolatedAssignment(
                        opcode,
                        start,
                        end,
                        BitConverter.UInt32BitsToSingle(unchecked((uint)start)),
                        BitConverter.UInt32BitsToSingle(unchecked((uint)end)),
                        destination);
                }
                interpolatedAssignments[field][record] = assignments;
            }
        }

        for (var record = 0; record < counts[23]; record++)
        {
            _ = ReadBytes(blob, ref cursor, 3 * sizeof(float));
        }

        for (var record = 0; record < counts[24]; record++)
        {
            var prefix = rawPrefixes[24][record];
            var firstLength = checked((int)ReadUInt32(prefix, 28));
            var secondLength = checked((int)ReadUInt32(prefix, 32));
            _ = ReadBytes(blob, ref cursor, firstLength);
            _ = ReadBytes(blob, ref cursor, secondLength);
        }

        if (cursor != blob.Length)
        {
            throw new InvalidDataException(
                $"selector-1 payload ended at 0x{cursor:X}, blob ends at 0x{blob.Length:X}");
        }

        var fields = new List<DecodedField>(15);
        for (var field = 8; field <= 17; field++)
        {
            var direct = new DirectEvent[counts[field]];
            for (var record = 0; record < counts[field]; record++)
            {
                direct[record] = new DirectEvent(
                    ReadSingle(rawPrefixes[field][record], 0),
                    destinationIndices[field][record],
                    directAssignments[field][record]);
            }
            fields.Add(new DecodedField(field, FamilyFor(field), direct, []));
        }
        for (var field = 18; field <= 22; field++)
        {
            var interpolated = new InterpolatedEvent[counts[field]];
            for (var record = 0; record < counts[field]; record++)
            {
                interpolated[record] = new InterpolatedEvent(
                    ReadSingle(rawPrefixes[field][record], 0),
                    ReadSingle(rawPrefixes[field][record], 4),
                    destinationIndices[field][record],
                    interpolatedAssignments[field][record]);
            }
            fields.Add(new DecodedField(field, FamilyFor(field), [], interpolated));
        }

        return new DecodedBlob(embeddedName, fields);
    }

    private static ResourceFamily FamilyFor(int field) => field switch
    {
        8 or 13 or 18 => ResourceFamily.SmallCompute,
        9 or 14 or 19 => ResourceFamily.SmallDraw,
        10 or 15 or 20 => ResourceFamily.LargeCompute,
        11 or 16 or 21 => ResourceFamily.LargeDraw,
        _ => ResourceFamily.Local,
    };

    private static int[] ReadInt32Array(ReadOnlySpan<byte> data, ref int cursor, int count)
    {
        var result = new int[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = unchecked((int)ReadUInt32(data, ref cursor));
        }
        return result;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int cursor, int count)
    {
        if (count < 0 || cursor < 0 || count > data.Length - cursor)
        {
            throw new InvalidDataException("selector-1 blob ends inside a serialized record");
        }

        var result = data.Slice(cursor, count).ToArray();
        cursor += count;
        return result;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset > (uint)(data.Length - sizeof(uint)))
        {
            throw new InvalidDataException("selector-1 read is outside the serialized record");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor)
    {
        var value = ReadUInt32(data, cursor);
        cursor += sizeof(uint);
        return value;
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset > (uint)(data.Length - sizeof(ulong)))
        {
            throw new InvalidDataException("selector-1 read is outside the eboot");
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int cursor)
    {
        var value = ReadUInt64(data, cursor);
        cursor += sizeof(ulong);
        return value;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset) =>
        BitConverter.UInt32BitsToSingle(ReadUInt32(data, offset));

    private enum ResourceFamily
    {
        Local,
        SmallCompute,
        SmallDraw,
        LargeCompute,
        LargeDraw,
    }

    private static bool IsResource(ResourceFamily family) => family != ResourceFamily.Local;

    private readonly record struct ResourceKey(ResourceFamily Family, int Index);

    private sealed class ResourceKeyComparer : IEqualityComparer<ResourceKey>
    {
        public static readonly ResourceKeyComparer Instance = new();

        public bool Equals(ResourceKey x, ResourceKey y) => x == y;

        public int GetHashCode(ResourceKey obj) => HashCode.Combine(obj.Family, obj.Index);
    }

    private sealed record DecodedBlob(string EmbeddedName, IReadOnlyList<DecodedField> Fields);

    private sealed record DecodedField(
        int Field,
        ResourceFamily Family,
        IReadOnlyList<DirectEvent> DirectEvents,
        IReadOnlyList<InterpolatedEvent> InterpolatedEvents);

    private sealed record DirectEvent(
        double Time,
        int[] DestinationIndices,
        DirectAssignment[] Assignments);

    private sealed record InterpolatedEvent(
        double StartTime,
        double EndTime,
        int[] DestinationIndices,
        InterpolatedAssignment[] Assignments);

    private readonly record struct DirectAssignment(int Opcode, ulong Payload, int DestinationOffset);

    private readonly record struct InterpolatedAssignment(
        int Opcode,
        ulong Start,
        ulong End,
        float StartF32,
        float EndF32,
        int DestinationOffset);

    private sealed record Operation(
        double EffectiveTime,
        int Kind,
        int Field,
        int Sequence,
        ResourceFamily Family,
        int[] DestinationIndices,
        DirectAssignment[] DirectAssignments,
        InterpolatedAssignment[] InterpolatedAssignments,
        double Amount);
}
