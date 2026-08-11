// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// What a probe can see when it fires: guest memory, plus the named values of
/// the site it fired at (registers at a CPU trap, arguments at an HLE export).
/// </summary>
public interface IGuestProbeScope
{
    /// <summary>Guest memory, read-only and fault-tolerant.</summary>
    GuestProbeMemory Memory { get; }

    /// <summary>
    /// Resolves a bare name in an address expression. Implementations should
    /// accept their site anchors first, then register names, case-insensitively.
    /// </summary>
    bool TryResolveName(string name, out ulong value);
}

/// <summary>
/// A probe scope backed by an explicit name table. Used by HLE export sites,
/// by the CPU backend (which fills the table from the trap's register file),
/// and by tests.
/// </summary>
public sealed class GuestProbeScope : IGuestProbeScope
{
    private readonly Dictionary<string, ulong> _names =
        new(StringComparer.OrdinalIgnoreCase);

    public GuestProbeScope(GuestProbeMemory memory)
    {
        Memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public GuestProbeMemory Memory { get; }

    /// <summary>Publishes a name usable in probe expressions, e.g. "r14" or "sound".</summary>
    public GuestProbeScope Define(string name, ulong value)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _names[name] = value;
        }

        return this;
    }

    public bool TryResolveName(string name, out ulong value) =>
        _names.TryGetValue(name, out value);
}
