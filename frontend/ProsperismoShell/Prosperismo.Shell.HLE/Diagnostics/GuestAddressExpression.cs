// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Evaluates the small address language used by probe specs, so a new probe is
/// a text edit instead of a rebuild.
///
/// <para>Grammar (whitespace insignificant):</para>
/// <code>
///   expr    := term (('+' | '-') term)*
///   term    := '[' expr ']'      // dereference: read 8 bytes at expr
///            | hex               // 0x1234 or 1234h
///            | decimal
///            | name              // anchor or register, e.g. "sound", "r14", "rsp"
/// </code>
///
/// <para>Examples:</para>
/// <code>
///   sound+0x2660          // anchor plus offset
///   [sound]               // the object's vtable pointer
///   [sound+0x2770]+0x10   // first list node, then its id field
///   [[rsp+8]]             // double indirection off a stack slot
/// </code>
/// </summary>
public static class GuestAddressExpression
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> against the supplied anchors and
    /// memory. Returns false (with a reason) rather than throwing, because a
    /// probe running inside a fault handler must never introduce a second fault.
    /// </summary>
    public static bool TryEvaluate(
        string? expression,
        IGuestProbeScope scope,
        out ulong value,
        out string? error)
    {
        value = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "empty expression";
            return false;
        }

        var position = 0;
        if (!TryParseSum(expression, scope, ref position, out value, out error))
        {
            return false;
        }

        SkipWhitespace(expression, ref position);
        if (position != expression.Length)
        {
            error = $"unexpected '{expression[position]}' at {position}";
            return false;
        }

        return true;
    }

    private static bool TryParseSum(
        string text,
        IGuestProbeScope scope,
        ref int position,
        out ulong value,
        out string? error)
    {
        if (!TryParseTerm(text, scope, ref position, out value, out error))
        {
            return false;
        }

        while (true)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length)
            {
                return true;
            }

            var op = text[position];
            if (op != '+' && op != '-')
            {
                return true;
            }

            position++;
            if (!TryParseTerm(text, scope, ref position, out var operand, out error))
            {
                return false;
            }

            // Unchecked so a negative offset expressed as a subtraction wraps the
            // same way the guest's own pointer arithmetic would.
            value = op == '+' ? unchecked(value + operand) : unchecked(value - operand);
        }
    }

    private static bool TryParseTerm(
        string text,
        IGuestProbeScope scope,
        ref int position,
        out ulong value,
        out string? error)
    {
        value = 0;
        error = null;
        SkipWhitespace(text, ref position);

        if (position >= text.Length)
        {
            error = "expression ends after an operator";
            return false;
        }

        if (text[position] == '[')
        {
            position++;
            if (!TryParseSum(text, scope, ref position, out var pointer, out error))
            {
                return false;
            }

            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != ']')
            {
                error = "unbalanced '['";
                return false;
            }

            position++;

            if (!scope.Memory.TryReadUInt64(pointer, out value))
            {
                error = $"unreadable pointer at 0x{pointer:X}";
                return false;
            }

            return true;
        }

        var start = position;
        while (position < text.Length && IsSymbolChar(text[position]))
        {
            position++;
        }

        if (position == start)
        {
            error = $"unexpected '{text[position]}' at {position}";
            return false;
        }

        var token = text[start..position];
        return TryResolveToken(token, scope, out value, out error);
    }

    private static bool TryResolveToken(
        string token,
        IGuestProbeScope scope,
        out ulong value,
        out string? error)
    {
        error = null;

        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            error = $"malformed hex literal '{token}'";
            return false;
        }

        if (ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // A bare name is an anchor (site-supplied, e.g. "sound") or a register.
        if (scope.TryResolveName(token, out value))
        {
            return true;
        }

        error = $"unknown name '{token}'";
        value = 0;
        return false;
    }

    private static bool IsSymbolChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ':' || c == '$';

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
