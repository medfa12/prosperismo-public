// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// One place to ask "may this boot afford it?".
///
/// <para>Bring-up work and presentation quality want opposite things. A boot run
/// to answer a question about guest state wants the shortest path to that
/// answer; a boot run to look at the game wants HDR and full resolution. This
/// type holds that choice so the two are a switch apart rather than a code edit
/// apart, and so every subsystem reads the same answer.</para>
///
/// <para>The controls are deliberately reversible and default to full quality —
/// nothing here changes behaviour unless a variable is set.</para>
///
/// <list type="table">
///   <listheader><term>Variable</term><description>Effect</description></listheader>
///   <item>
///     <term><c>PROSPERISMO_FAST_BOOT=1</c></term>
///     <description>Master switch: SDR, 1080p cap. Individual variables below
///     still win, so a fast boot can keep one expensive feature.</description>
///   </item>
///   <item>
///     <term><c>PROSPERISMO_HDR=0|1</c></term>
///     <description>Whether the title is told the display is HDR-capable.
///     Reporting SDR makes the title itself choose the cheaper path rather than
///     rendering HDR that we then discard.</description>
///   </item>
///   <item>
///     <term><c>PROSPERISMO_MAX_WIDTH</c>, <c>PROSPERISMO_MAX_HEIGHT</c></term>
///     <description>Caps the display buffer the title may allocate. A 4K title
///     clamped to 1080p renders a quarter of the pixels.</description>
///   </item>
/// </list>
/// </summary>
public static class EmulationCostProfile
{
    private const uint FastBootMaxWidth = 1920;
    private const uint FastBootMaxHeight = 1080;

    private static readonly Lock Gate = new();
    private static bool _loaded;
    private static bool _fastBoot;
    private static bool _hdrEnabled;
    private static uint _maxWidth;
    private static uint _maxHeight;

    /// <summary>True when this boot is optimised for reaching an answer quickly.</summary>
    public static bool FastBoot
    {
        get
        {
            EnsureLoaded();
            return _fastBoot;
        }
    }

    /// <summary>
    /// Whether the guest should be told the display supports HDR. False makes
    /// <c>sceSystemServiceGetHdrToneMapLuminance</c> report SDR reference
    /// luminance, which is the signal a title uses to pick its tone-mapping path.
    /// </summary>
    public static bool HdrEnabled
    {
        get
        {
            EnsureLoaded();
            return _hdrEnabled;
        }
    }

    /// <summary>Largest display width the guest may configure. 0 means unlimited.</summary>
    public static uint MaxWidth
    {
        get
        {
            EnsureLoaded();
            return _maxWidth;
        }
    }

    /// <summary>Largest display height the guest may configure. 0 means unlimited.</summary>
    public static uint MaxHeight
    {
        get
        {
            EnsureLoaded();
            return _maxHeight;
        }
    }

    /// <summary>
    /// Clamps a requested display size to the configured cap, preserving aspect
    /// ratio. Returns true when the size was reduced, so the caller can say so
    /// once rather than leaving a silently different resolution to be discovered
    /// later in a screenshot.
    /// </summary>
    public static bool TryClampDisplaySize(ref uint width, ref uint height)
    {
        EnsureLoaded();

        if (width == 0 || height == 0)
        {
            return false;
        }

        var maxWidth = _maxWidth == 0 ? width : Math.Min(_maxWidth, width);
        var maxHeight = _maxHeight == 0 ? height : Math.Min(_maxHeight, height);

        if (maxWidth == width && maxHeight == height)
        {
            return false;
        }

        // Scale by the tighter of the two constraints so the result keeps the
        // aspect ratio the title asked for; a stretched frame would look like a
        // rendering bug and cost time to rule out.
        var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
        var clampedWidth = (uint)Math.Max(1, Math.Round(width * scale));
        var clampedHeight = (uint)Math.Max(1, Math.Round(height * scale));

        // Display pipelines dislike odd dimensions; round down to even.
        width = clampedWidth & ~1u;
        height = clampedHeight & ~1u;
        return true;
    }

    /// <summary>Overrides the profile directly. For tests and for hosts with their own settings UI.</summary>
    public static void Override(bool fastBoot, bool hdrEnabled, uint maxWidth, uint maxHeight)
    {
        lock (Gate)
        {
            _fastBoot = fastBoot;
            _hdrEnabled = hdrEnabled;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _loaded = true;
        }
    }

    /// <summary>Forgets the resolved profile so the next read re-reads the environment.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _loaded = false;
        }
    }

    /// <summary>A one-line summary for the boot banner, so a log always says which profile produced it.</summary>
    public static string Describe()
    {
        EnsureLoaded();

        var resolution = _maxWidth == 0 && _maxHeight == 0
            ? "uncapped"
            : $"{(_maxWidth == 0 ? "*" : _maxWidth.ToString(CultureInfo.InvariantCulture))}x" +
              $"{(_maxHeight == 0 ? "*" : _maxHeight.ToString(CultureInfo.InvariantCulture))}";

        return $"fastBoot={(_fastBoot ? "on" : "off")} hdr={(_hdrEnabled ? "on" : "off")} maxRes={resolution}";
    }

    private static void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded))
        {
            return;
        }

        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            _fastBoot = ReadFlag("PROSPERISMO_FAST_BOOT") ?? false;

            // A fast boot turns these down, but an explicit variable always wins:
            // "fast boot, but I still need HDR" has to be expressible.
            _hdrEnabled = ReadFlag("PROSPERISMO_HDR") ?? !_fastBoot;
            _maxWidth = ReadSize("PROSPERISMO_MAX_WIDTH") ?? (_fastBoot ? FastBootMaxWidth : 0);
            _maxHeight = ReadSize("PROSPERISMO_MAX_HEIGHT") ?? (_fastBoot ? FastBootMaxHeight : 0);

            _loaded = true;
        }
    }

    private static bool? ReadFlag(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => null,
        };
    }

    private static uint? ReadSize(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return uint.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
               value > 0
            ? value
            : null;
    }
}
