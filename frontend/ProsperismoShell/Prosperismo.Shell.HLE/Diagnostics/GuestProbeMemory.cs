// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Read-only guest memory access for diagnostics, with the plausibility checks
/// a probe needs. Every accessor returns false instead of throwing: probes run
/// from fault handlers and mid-unwind, where a second fault is unrecoverable.
/// </summary>
public sealed class GuestProbeMemory(ICpuMemory memory)
{
    private readonly ICpuMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    /// <summary>
    /// Guest pointers live well above the null page and below the 64 GiB mark in
    /// every mapping this emulator produces. Used to decide whether a value is
    /// worth chasing, so a probe reports "0x7 (not a pointer)" instead of
    /// spending a read and printing garbage.
    /// </summary>
    public static bool LooksLikePointer(ulong value) =>
        value >= 0x10000UL && value < 0x0000_0010_0000_0000UL;

    public bool TryReadBytes(ulong address, Span<byte> destination) =>
        _memory.TryRead(address, destination);

    public bool TryReadByte(ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!_memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    public bool TryReadUInt16(ulong address, out ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        if (!_memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!_memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    public bool TryReadUInt64(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!_memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    public bool TryReadSingle(ulong address, out float value)
    {
        if (!TryReadUInt32(address, out var bits))
        {
            value = 0;
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return true;
    }

    public bool TryReadDouble(ulong address, out double value)
    {
        if (!TryReadUInt64(address, out var bits))
        {
            value = 0;
            return false;
        }

        value = BitConverter.UInt64BitsToDouble(bits);
        return true;
    }

    /// <summary>
    /// Reads a NUL-terminated UTF-8 string, stopping at <paramref name="maxLength"/>.
    /// Returns false only when the first byte is unreadable, so a truncated or
    /// unterminated string still yields what was there.
    /// </summary>
    public bool TryReadCString(ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (maxLength <= 0 || !LooksLikePointer(address))
        {
            return false;
        }

        var bytes = new List<byte>(Math.Min(maxLength, 64));
        for (var offset = 0; offset < maxLength; offset++)
        {
            if (!TryReadByte(address + (ulong)offset, out var b))
            {
                break;
            }

            if (b == 0)
            {
                value = Encoding.UTF8.GetString(CollectionsMarshalSpan(bytes));
                return true;
            }

            bytes.Add(b);
        }

        if (bytes.Count == 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(CollectionsMarshalSpan(bytes));
        return true;
    }

    /// <summary>
    /// Reads a libc++ <c>std::string</c>. The layout is a union: when the low bit
    /// of the first byte is clear the string is short and stored inline, with
    /// <c>size = byte >> 1</c> and the characters starting at +1. Otherwise it is
    /// a heap string with capacity at +0, size at +8 and the data pointer at +16.
    ///
    /// <para>Getting this right matters: an inline string read as a heap string
    /// yields a nonsense pointer, which is how a short-string capacity marker
    /// gets mistaken for a data field.</para>
    /// </summary>
    public bool TryReadStdString(ulong address, out string value, out bool isShort)
    {
        value = string.Empty;
        isShort = false;

        if (!TryReadByte(address, out var first))
        {
            return false;
        }

        if ((first & 1) == 0)
        {
            isShort = true;
            var length = first >> 1;
            if (length == 0)
            {
                return true;
            }

            // libc++ caps the inline buffer at 22 bytes for a 24-byte string.
            if (length > 22)
            {
                return false;
            }

            Span<byte> inline = stackalloc byte[length];
            if (!_memory.TryRead(address + 1, inline))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(inline);
            return true;
        }

        if (!TryReadUInt64(address + 8, out var size) ||
            !TryReadUInt64(address + 16, out var data))
        {
            return false;
        }

        if (size > 4096 || !LooksLikePointer(data))
        {
            return false;
        }

        Span<byte> heap = size == 0 ? [] : new byte[size];
        if (size != 0 && !_memory.TryRead(data, heap))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(heap);
        return true;
    }

    private static ReadOnlySpan<byte> CollectionsMarshalSpan(List<byte> list) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
}
