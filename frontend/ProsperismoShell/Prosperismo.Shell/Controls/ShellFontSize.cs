// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The shell's type tokens, by name.
///
/// <para>These used to be placeholders, with a comment saying the real values
/// "are recoverable from nothing we hold". That was true of the JS bundles and
/// false of the dump: the numbers are <c>public static readonly int</c> fields
/// on <c>Sce.PlayStation.PUI.UI3.UIFont</c>, and they have now been read
/// (<c>docs/ps5-shell-recovery-audit.md</c> §2.1). Every value below is a
/// measurement.</para>
///
/// <para>This type is kept as the name every existing control already binds to;
/// it forwards to <see cref="Ps5FontScale"/>, which carries the full ten-rung
/// ladder and the line-height rule. New code should use that directly.</para>
/// </summary>
public static class ShellFontSize
{
    /// <summary><c>UIFont.Size3XLarge</c>.</summary>
    public const double XXXLarge = Ps5FontScale.Size3XLarge;

    /// <summary><c>UIFont.Size2XLarge</c>, aliased <c>SizeXXLarge</c> in JS.</summary>
    public const double XXLarge = Ps5FontScale.Size2XLarge;

    /// <summary><c>UIFont.SizeXLarge</c>. Confirm-screen body copy in the dialog app.</summary>
    public const double XLarge = Ps5FontScale.SizeXLarge;

    /// <summary><c>UIFont.SizeLarge</c>. Nav clock and space labels.</summary>
    public const double Large = Ps5FontScale.SizeLarge;

    /// <summary><c>UIFont.SizeNormal</c>. Dialog bodies and the switcher title.</summary>
    public const double Normal = Ps5FontScale.SizeNormal;

    /// <summary><c>UIFont.SizeSmall</c>. Options-panel section headers.</summary>
    public const double Small = Ps5FontScale.SizeSmall;

    /// <summary><c>UIFont.SizeXSmall</c>. Dialog error codes and nav icon labels.</summary>
    public const double XSmall = Ps5FontScale.SizeXSmall;

    /// <summary><c>UIFont.Size2XSmall</c>, aliased <c>SizeXXSmall</c> in JS.</summary>
    public const double XXSmall = Ps5FontScale.Size2XSmall;

    /// <summary><c>UIFont.Size3XSmall</c>. Tile sub-labels.</summary>
    public const double XXXSmall = Ps5FontScale.Size3XSmall;

    /// <summary><c>UIFont.Size4XSmall</c>. The home tile's primary title id.</summary>
    public const double XXXXSmall = Ps5FontScale.Size4XSmall;
}
