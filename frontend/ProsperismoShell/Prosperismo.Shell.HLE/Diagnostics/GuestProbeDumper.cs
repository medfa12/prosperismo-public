// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Renders a <see cref="GuestDumpOp"/> against live guest memory.
///
/// <para>Output is one line per operation, in a stable <c>key=value</c> shape so
/// two boots can be diffed directly and a field can be grepped without a parser.
/// Unreadable memory prints as <c>?</c> and never aborts the rest of the dump —
/// a partial answer from one boot beats a clean failure that costs another.</para>
/// </summary>
public static class GuestProbeDumper
{
    /// <summary>Renders one operation. Never throws.</summary>
    public static string Render(GuestDumpOp op, IGuestProbeScope scope)
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(scope);

        var line = new StringBuilder();
        line.Append(op.Label.Length > 0 ? op.Label : op.At).Append('=');

        if (!GuestAddressExpression.TryEvaluate(op.At, scope, out var address, out var error))
        {
            return line.Append("<unresolved: ").Append(error).Append('>').ToString();
        }

        try
        {
            AppendValue(line, op, scope, address);
        }
        catch (Exception ex)
        {
            // A probe must not be able to take down a boot it was meant to explain.
            line.Append("<error: ").Append(ex.GetType().Name).Append('>');
        }

        return line.ToString();
    }

    private static void AppendValue(
        StringBuilder line,
        GuestDumpOp op,
        IGuestProbeScope scope,
        ulong address)
    {
        var memory = scope.Memory;

        switch (op.As)
        {
            case GuestDumpKind.Vector:
                AppendVector(line, op, memory, address);
                return;

            case GuestDumpKind.List:
                AppendList(line, op, memory, address);
                return;

            case GuestDumpKind.Array:
                AppendArray(line, op, memory, address);
                return;

            case GuestDumpKind.Struct:
                line.Append("0x").Append(address.ToString("X")).Append('{');
                AppendFields(line, op.Fields, memory, address);
                line.Append('}');
                return;

            case GuestDumpKind.Value:
                line.Append("0x").Append(address.ToString("X"));
                if (address <= int.MaxValue)
                {
                    // Handles, indices and error codes read better in decimal.
                    line.Append('(').Append(address).Append(')');
                }

                return;

            case GuestDumpKind.Hex:
                line.Append("0x").Append(address.ToString("X")).Append(' ');
                AppendHex(line, memory, address, op.Length);
                return;

            default:
                AppendScalar(line, op.As, memory, address, op.Length);
                return;
        }
    }

    /// <summary>
    /// A libc++ <c>std::vector</c> is three pointers: begin, end, capacity-end.
    /// Element count is <c>(end - begin) / stride</c>; reporting begin and end
    /// raw as well makes an empty-versus-unmapped vector unambiguous.
    /// </summary>
    private static void AppendVector(
        StringBuilder line,
        GuestDumpOp op,
        GuestProbeMemory memory,
        ulong address)
    {
        line.Append("vector@0x").Append(address.ToString("X"));

        if (!memory.TryReadUInt64(address, out var begin) ||
            !memory.TryReadUInt64(address + 8, out var end) ||
            !memory.TryReadUInt64(address + 16, out var capacity))
        {
            line.Append(" <unreadable>");
            return;
        }

        line.Append(" begin=0x").Append(begin.ToString("X"))
            .Append(" end=0x").Append(end.ToString("X"))
            .Append(" cap=0x").Append(capacity.ToString("X"));

        var stride = op.Stride == 0 ? 8 : op.Stride;
        if (begin == 0 && end == 0)
        {
            line.Append(" count=0 EMPTY");
            return;
        }

        if (end < begin)
        {
            line.Append(" count=? INVERTED");
            return;
        }

        var bytes = end - begin;
        var count = bytes / stride;
        line.Append(" bytes=0x").Append(bytes.ToString("X"))
            .Append(" stride=0x").Append(stride.ToString("X"))
            .Append(" count=").Append(count);

        if (bytes % stride != 0)
        {
            line.Append(" MISALIGNED");
        }

        var limit = (int)Math.Min(count, (ulong)Math.Max(0, op.Max));
        for (var index = 0; index < limit; index++)
        {
            var element = begin + ((ulong)index * stride);
            line.Append(" [").Append(index).Append("]@0x").Append(element.ToString("X"));
            if (op.Fields.Count > 0)
            {
                line.Append('{');
                AppendFields(line, op.Fields, memory, element);
                line.Append('}');
            }
        }

        if (count > (ulong)limit)
        {
            line.Append(" ...+").Append(count - (ulong)limit);
        }
    }

    /// <summary>
    /// Walks an intrusive list through <see cref="GuestDumpOp.Next"/>, stopping at
    /// the head (circular), at null (linear), at an unreadable node, or at
    /// <see cref="GuestDumpOp.Max"/> — whichever comes first, so a corrupt list
    /// bounds the dump instead of hanging the guest.
    /// </summary>
    private static void AppendList(
        StringBuilder line,
        GuestDumpOp op,
        GuestProbeMemory memory,
        ulong address)
    {
        line.Append("list@0x").Append(address.ToString("X"));

        var head = address;
        var node = head;
        var visited = new HashSet<ulong>();
        var count = 0;
        var max = Math.Max(1, op.Max);

        while (count < max)
        {
            if (!GuestProbeMemory.LooksLikePointer(node))
            {
                line.Append(count == 0 ? " EMPTY" : " end=0x").Append(count == 0 ? string.Empty : node.ToString("X"));
                break;
            }

            if (!visited.Add(node))
            {
                line.Append(" CIRCULAR-AFTER=").Append(count);
                break;
            }

            line.Append(' ').Append('[').Append(count).Append("]@0x").Append(node.ToString("X"));
            if (op.Fields.Count > 0)
            {
                line.Append('{');
                AppendFields(line, op.Fields, memory, node);
                line.Append('}');
            }

            count++;

            if (!memory.TryReadUInt64(node + op.Next, out var next))
            {
                line.Append(" <next unreadable>");
                break;
            }

            if (next == head)
            {
                line.Append(" CIRCULAR");
                break;
            }

            node = next;
        }

        line.Append(" count=").Append(count);
        if (count >= max)
        {
            line.Append(" TRUNCATED");
        }
    }

    private static void AppendArray(
        StringBuilder line,
        GuestDumpOp op,
        GuestProbeMemory memory,
        ulong address)
    {
        var stride = op.Stride == 0 ? 8 : op.Stride;
        var count = Math.Clamp(op.Count, 0, Math.Max(1, op.Max));
        line.Append("array@0x").Append(address.ToString("X"))
            .Append(" count=").Append(count)
            .Append(" stride=0x").Append(stride.ToString("X"));

        for (var index = 0; index < count; index++)
        {
            var element = address + ((ulong)index * stride);
            line.Append(" [").Append(index).Append("]=");

            if (op.Fields.Count > 0)
            {
                line.Append('{');
                AppendFields(line, op.Fields, memory, element);
                line.Append('}');
            }
            else if (memory.TryReadUInt64(element, out var value))
            {
                line.Append("0x").Append(value.ToString("X"));
            }
            else
            {
                line.Append('?');
            }
        }
    }

    private static void AppendFields(
        StringBuilder line,
        IReadOnlyList<GuestDumpField> fields,
        GuestProbeMemory memory,
        ulong elementBase)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (index > 0)
            {
                line.Append(' ');
            }

            line.Append(field.Name).Append('=');

            // Field offsets resolve against the element, so "+0x10" reads inside
            // the element rather than at an absolute address.
            var scope = new GuestProbeScope(memory).Define("this", elementBase);
            var expression = field.At.StartsWith('+') || field.At.StartsWith('-')
                ? "this" + field.At
                : field.At;

            if (!GuestAddressExpression.TryEvaluate(expression, scope, out var address, out _))
            {
                line.Append('?');
                continue;
            }

            AppendScalar(line, field.As, memory, address, field.Length);
        }
    }

    private static void AppendScalar(
        StringBuilder line,
        GuestDumpKind kind,
        GuestProbeMemory memory,
        ulong address,
        int length)
    {
        switch (kind)
        {
            case GuestDumpKind.Value:
                line.Append("0x").Append(address.ToString("X"));
                return;

            case GuestDumpKind.UInt8:
                line.Append(memory.TryReadByte(address, out var u8) ? "0x" + u8.ToString("X2") : "?");
                return;

            case GuestDumpKind.UInt16:
                line.Append(memory.TryReadUInt16(address, out var u16) ? "0x" + u16.ToString("X4") : "?");
                return;

            case GuestDumpKind.UInt32:
                line.Append(memory.TryReadUInt32(address, out var u32) ? "0x" + u32.ToString("X") : "?");
                return;

            case GuestDumpKind.Int32:
                line.Append(memory.TryReadUInt32(address, out var i32) ? ((int)i32).ToString() : "?");
                return;

            case GuestDumpKind.Int64:
                line.Append(memory.TryReadUInt64(address, out var i64) ? ((long)i64).ToString() : "?");
                return;

            case GuestDumpKind.Single:
                line.Append(memory.TryReadSingle(address, out var f32) ? f32.ToString("G9") : "?");
                return;

            case GuestDumpKind.Double:
                line.Append(memory.TryReadDouble(address, out var f64) ? f64.ToString("G17") : "?");
                return;

            case GuestDumpKind.Pointer:
                if (!memory.TryReadUInt64(address, out var pointer))
                {
                    line.Append('?');
                    return;
                }

                line.Append("0x").Append(pointer.ToString("X"));
                if (pointer != 0 && !GuestProbeMemory.LooksLikePointer(pointer))
                {
                    // Calling this out stops a small integer in a pointer slot
                    // from being chased as an address.
                    line.Append("(not-a-pointer)");
                }

                return;

            case GuestDumpKind.CString:
                line.Append(memory.TryReadCString(address, Math.Max(1, length), out var cstr)
                    ? '"' + Escape(cstr) + '"'
                    : "?");
                return;

            case GuestDumpKind.StdString:
                if (!memory.TryReadStdString(address, out var text, out var isShort))
                {
                    line.Append('?');
                    return;
                }

                line.Append('"').Append(Escape(text)).Append('"').Append(isShort ? "(sso)" : "(heap)");
                return;

            case GuestDumpKind.Hex:
                AppendHex(line, memory, address, length);
                return;

            default:
                line.Append(memory.TryReadUInt64(address, out var u64) ? "0x" + u64.ToString("X") : "?");
                return;
        }
    }

    private static void AppendHex(StringBuilder line, GuestProbeMemory memory, ulong address, int length)
    {
        var count = Math.Clamp(length, 1, 4096);
        var buffer = new byte[count];

        if (!memory.TryReadBytes(address, buffer))
        {
            // Fall back to per-byte reads so a dump that straddles the end of a
            // mapping still reports the readable prefix.
            var readable = 0;
            for (; readable < count; readable++)
            {
                if (!memory.TryReadByte(address + (ulong)readable, out var b))
                {
                    break;
                }

                buffer[readable] = b;
            }

            if (readable == 0)
            {
                line.Append("<unreadable>");
                return;
            }

            count = readable;
            line.Append("(partial ").Append(count).Append(") ");
        }

        for (var index = 0; index < count; index++)
        {
            if (index > 0 && index % 16 == 0)
            {
                line.Append('|');
            }
            else if (index > 0)
            {
                line.Append(' ');
            }

            line.Append(buffer[index].ToString("X2"));
        }
    }

    private static string Escape(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString(),
            });
        }

        return builder.ToString();
    }
}
