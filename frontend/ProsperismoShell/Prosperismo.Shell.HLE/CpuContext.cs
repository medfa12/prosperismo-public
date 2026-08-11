// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Prosperismo.HLE;

public sealed class CpuContext(ICpuMemory memory, Generation generation)
{
    /// <summary>Bit set in <see cref="StateWriteMask"/> when RAX is written.</summary>
    public const ulong RaxWriteBit = 1UL << (int)CpuRegister.Rax;

    /// <summary>Bit set when <see cref="Rip"/> is written.</summary>
    public const ulong RipWriteBit = 1UL << 16;

    /// <summary>Bit set when <see cref="Rflags"/> is written.</summary>
    public const ulong RflagsWriteBit = 1UL << 17;

    /// <summary>Bit set when <see cref="FsBase"/> is written.</summary>
    public const ulong FsBaseWriteBit = 1UL << 18;

    /// <summary>Bit set when <see cref="GsBase"/> is written.</summary>
    public const ulong GsBaseWriteBit = 1UL << 19;

    /// <summary>Bit set when <see cref="FpuControlWord"/> is written.</summary>
    public const ulong FpuControlWriteBit = 1UL << 20;

    /// <summary>Bit set when <see cref="Mxcsr"/> is written.</summary>
    public const ulong MxcsrWriteBit = 1UL << 21;

    /// <summary>Bit 0 of the vector-register half of <see cref="StateWriteMask"/>.</summary>
    public const int VectorWriteBitBase = 32;

    private readonly ulong[] _registers = new ulong[16];
    private readonly ulong[] _xmmRegisters = new ulong[32];
    private readonly ulong[] _ymmUpperRegisters = new ulong[32];

    // One bitmask stands in for the old "was RAX written" bool. It costs the same
    // single OR per register write that the bool cost, and it answers the question
    // the effect census needs — "did this call write any guest-visible context at
    // all, or only its return value" — which a RAX-only flag cannot.
    private ulong _stateWrites;

    private ulong _rip;
    private ulong _rflags;
    private ulong _fsBase;
    private ulong _gsBase;
    private ushort _fpuControlWord = 0x037F;
    private uint _mxcsr = 0x1F80;

    public ICpuMemory Memory { get; } = memory ?? throw new ArgumentNullException(nameof(memory));

    public Generation TargetGeneration { get; } = generation;

    public ulong Rip
    {
        get => _rip;
        set
        {
            _rip = value;
            _stateWrites |= RipWriteBit;
        }
    }

    public ulong Rflags
    {
        get => _rflags;
        set
        {
            _rflags = value;
            _stateWrites |= RflagsWriteBit;
        }
    }

    public ulong FsBase
    {
        get => _fsBase;
        set
        {
            _fsBase = value;
            _stateWrites |= FsBaseWriteBit;
        }
    }

    public ulong GsBase
    {
        get => _gsBase;
        set
        {
            _gsBase = value;
            _stateWrites |= GsBaseWriteBit;
        }
    }

    /// <summary>x87 control word observed at the current guest boundary.</summary>
    public ushort FpuControlWord
    {
        get => _fpuControlWord;
        set
        {
            _fpuControlWord = value;
            _stateWrites |= FpuControlWriteBit;
        }
    }

    /// <summary>MXCSR observed at the current guest boundary.</summary>
    public uint Mxcsr
    {
        get => _mxcsr;
        set
        {
            _mxcsr = value;
            _stateWrites |= MxcsrWriteBit;
        }
    }

    public ulong this[CpuRegister register]
    {
        get => _registers[(int)register];
        set
        {
            _registers[(int)register] = value;
            _stateWrites |= 1UL << (int)register;
        }
    }

    /// <summary>
    /// Which guest-visible context slots have been written since the mask was last
    /// cleared: bits 0-15 are the general-purpose registers in
    /// <see cref="CpuRegister"/> order, bits 16-21 the named state above, and bits
    /// 32-47 the vector registers.
    /// </summary>
    public ulong StateWriteMask => _stateWrites;

    /// <summary>
    /// Replaces <see cref="StateWriteMask"/> and returns what it was. An observer
    /// that wants to attribute writes to one nested call brackets that call with
    /// this and ORs the old mask back in afterwards, so its measurement cannot
    /// erase the caller's.
    /// </summary>
    public ulong ExchangeStateWriteMask(ulong mask)
    {
        var previous = _stateWrites;
        _stateWrites = mask;
        return previous;
    }

    public void ClearRaxWriteFlag()
    {
        _stateWrites &= ~RaxWriteBit;
    }

    public bool WasRaxWritten => (_stateWrites & RaxWriteBit) != 0;

    public void GetXmmRegister(int registerIndex, out ulong low, out ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        low = _xmmRegisters[offset];
        high = _xmmRegisters[offset + 1];
    }

    public void SetXmmRegister(int registerIndex, ulong low, ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        _xmmRegisters[offset] = low;
        _xmmRegisters[offset + 1] = high;
        _stateWrites |= 1UL << (VectorWriteBitBase + registerIndex);
    }

    public void GetYmmUpper(int registerIndex, out ulong low, out ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        low = _ymmUpperRegisters[offset];
        high = _ymmUpperRegisters[offset + 1];
    }

    public void SetYmmUpper(int registerIndex, ulong low, ulong high)
    {
        if ((uint)registerIndex >= 16)
        {
            throw new ArgumentOutOfRangeException(nameof(registerIndex));
        }

        var offset = registerIndex * 2;
        _ymmUpperRegisters[offset] = low;
        _ymmUpperRegisters[offset + 1] = high;
        _stateWrites |= 1UL << (VectorWriteBitBase + registerIndex);
    }

    public void ClearYmmUpper(int registerIndex)
    {
        SetYmmUpper(registerIndex, 0, 0);
    }

    public void ClearAllYmmUpper()
    {
        Array.Clear(_ymmUpperRegisters);
        _stateWrites |= 0xFFFFUL << VectorWriteBitBase;
    }

    public void GetYmmRegister(
        int registerIndex,
        out ulong lowLow,
        out ulong lowHigh,
        out ulong highLow,
        out ulong highHigh)
    {
        GetXmmRegister(registerIndex, out lowLow, out lowHigh);
        GetYmmUpper(registerIndex, out highLow, out highHigh);
    }

    public void SetYmmRegister(
        int registerIndex,
        ulong lowLow,
        ulong lowHigh,
        ulong highLow,
        ulong highHigh)
    {
        SetXmmRegister(registerIndex, lowLow, lowHigh);
        SetYmmUpper(registerIndex, highLow, highHigh);
    }

    public bool TryReadByte(ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    public bool TryReadUInt16(ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        if (!Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    public bool TryWriteUInt16(ulong address, ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryReadInt32(ulong address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    public bool TryWriteInt32(ulong address, int value, bool checkNil = false)
    {
        if (checkNil && address == 0)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Memory.TryWrite(address, bytes);
    }

    public bool TryReadUInt32(ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    public bool TryWriteUInt32(ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryWriteInt64(ulong address, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryReadUInt64(ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    public bool TryWriteUInt64(ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return Memory.TryWrite(address, buffer);
    }

    public bool TryReadNullTerminatedUtf8(ulong address, int capacity, out string value)
    {
        value = string.Empty;
        if (address == 0 || capacity <= 0)
        {
            return false;
        }

        const int StackBufferLength = 512;
        const int ReadChunkLength = 128;
        var rented = capacity > StackBufferLength ? ArrayPool<byte>.Shared.Rent(capacity) : null;
        Span<byte> bytes = rented is null ? stackalloc byte[StackBufferLength] : rented;
        try
        {
            var length = 0;
            while (length < capacity)
            {
                // Bulk-read in bounded chunks rather than the full capacity: the string
                // may end just before unmapped memory, and overreading past the
                // terminator by more than a chunk could fault where the old
                // byte-by-byte loop succeeded.
                var chunk = Math.Min(ReadChunkLength, capacity - length);
                var span = bytes.Slice(length, chunk);
                if (Memory.TryRead(address + (ulong)length, span))
                {
                    var terminator = span.IndexOf((byte)0);
                    if (terminator >= 0)
                    {
                        value = Encoding.UTF8.GetString(bytes[..(length + terminator)]);
                        return true;
                    }

                    length += chunk;
                    continue;
                }

                // The chunk touches an unreadable range; fall back to per-byte reads so a
                // terminator sitting before the bad byte still yields the string.
                for (var i = 0; i < chunk; i++)
                {
                    if (!Memory.TryRead(address + (ulong)(length + i), bytes.Slice(length + i, 1)))
                    {
                        return false;
                    }

                    if (bytes[length + i] == 0)
                    {
                        value = Encoding.UTF8.GetString(bytes[..(length + i)]);
                        return true;
                    }
                }

                length += chunk;
            }

            value = Encoding.UTF8.GetString(bytes[..capacity]);
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public bool PushUInt64(ulong value)
    {
        var rsp = this[CpuRegister.Rsp];
        rsp -= sizeof(ulong);
        this[CpuRegister.Rsp] = rsp;
        return TryWriteUInt64(rsp, value);
    }

    public bool PopUInt64(out ulong value)
    {
        var rsp = this[CpuRegister.Rsp];
        if (!TryReadUInt64(rsp, out value))
        {
            return false;
        }

        this[CpuRegister.Rsp] = rsp + sizeof(ulong);
        return true;
    }

    public int SetReturn(int result, Type? cast = null)
    {
        var value = cast switch
        {
            null => (ulong)result,
            _ when cast == typeof(long) => (ulong)(long)result,
            _ => throw new NotSupportedException(),
        };

        this[CpuRegister.Rax] = unchecked(value);
        return result;
    }

    public int SetReturn(OrbisGen2Result result)
    {
        this[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
