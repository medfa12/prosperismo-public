// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.Ps5Home;

/// <summary>The UI3 type tokens, by name (<c>NativeModules.FontSize.fontSize.*</c>).</summary>
public enum Ps5FontToken
{
    /// <summary>72 px.</summary>
    Size3XLarge,

    /// <summary>54 px. Aliased <c>SizeXXLarge</c> in the JS constant bag.</summary>
    Size2XLarge,

    /// <summary>45 px.</summary>
    SizeXLarge,

    /// <summary>36 px.</summary>
    SizeLarge,

    /// <summary>30 px. The shell's body size.</summary>
    SizeNormal,

    /// <summary>27 px.</summary>
    SizeSmall,

    /// <summary>24 px.</summary>
    SizeXSmall,

    /// <summary>21 px. Aliased <c>SizeXXSmall</c>.</summary>
    Size2XSmall,

    /// <summary>18 px.</summary>
    Size3XSmall,

    /// <summary>15 px.</summary>
    Size4XSmall,

    /// <summary>Not supported on UI3; <c>INVALID_FONT_SIZE</c> = -1.</summary>
    Size5XSmall,
}

/// <summary>
/// The UI3 font scale, read out of <c>Sce.PlayStation.PUI.UI3.UIFont</c>
/// (<c>public static readonly int</c> fields) and
/// <c>ReactNative.Modules.UI3.Font.FontSizeModule..ctor</c>.
/// See <c>docs/ps5-shell-recovery-audit.md</c> §2.1.
///
/// The earlier layout spec listed these as UNRECOVERED and warned "do not guess
/// them". They are no longer guesses; every number below is an IL constant.
/// Because of that history: if you find yourself wanting to nudge one of these
/// to make a screenshot look right, the bug is somewhere else.
/// </summary>
public static class Ps5FontScale
{
    /// <summary><c>UIFont.Size3XLarge</c>.</summary>
    public const double Size3XLarge = 72;

    /// <summary><c>UIFont.Size2XLarge</c>.</summary>
    public const double Size2XLarge = 54;

    /// <summary><c>UIFont.SizeXLarge</c>.</summary>
    public const double SizeXLarge = 45;

    /// <summary><c>UIFont.SizeLarge</c>.</summary>
    public const double SizeLarge = 36;

    /// <summary><c>UIFont.SizeNormal</c>. BASE:982's <c>33 !== R.SizeNormal</c> guard holds.</summary>
    public const double SizeNormal = 30;

    /// <summary><c>UIFont.SizeSmall</c>.</summary>
    public const double SizeSmall = 27;

    /// <summary><c>UIFont.SizeXSmall</c>.</summary>
    public const double SizeXSmall = 24;

    /// <summary><c>UIFont.Size2XSmall</c>.</summary>
    public const double Size2XSmall = 21;

    /// <summary><c>UIFont.Size3XSmall</c>.</summary>
    public const double Size3XSmall = 18;

    /// <summary><c>UIFont.Size4XSmall</c>.</summary>
    public const double Size4XSmall = 15;

    /// <summary><c>FontSizeModule</c>'s <c>INVALID_FONT_SIZE</c>.</summary>
    public const double Invalid = -1;

    /// <summary>Lowest size <c>UIFont</c>'s private ctor accepts.</summary>
    public const int MinimumSize = 1;

    /// <summary>Highest size <c>UIFont</c>'s private ctor accepts.</summary>
    public const int MaximumSize = 1024;

    /// <summary>
    /// The legacy UI2 scale, from the BASE:982 <c>_isUI2SizeOnUI3</c> guard.
    /// Present so a UI2 size can be recognised, never to be rendered with.
    /// </summary>
    public static ReadOnlySpan<int> LegacyUi2Sizes => [40, 33, 26, 22, 16, 12];

    /// <summary>Pixel size for a token, or <see cref="Invalid"/> for <see cref="Ps5FontToken.Size5XSmall"/>.</summary>
    /// <param name="token">A UI3 type token.</param>
    public static double SizeOf(Ps5FontToken token) => token switch
    {
        Ps5FontToken.Size3XLarge => Size3XLarge,
        Ps5FontToken.Size2XLarge => Size2XLarge,
        Ps5FontToken.SizeXLarge => SizeXLarge,
        Ps5FontToken.SizeLarge => SizeLarge,
        Ps5FontToken.SizeNormal => SizeNormal,
        Ps5FontToken.SizeSmall => SizeSmall,
        Ps5FontToken.SizeXSmall => SizeXSmall,
        Ps5FontToken.Size2XSmall => Size2XSmall,
        Ps5FontToken.Size3XSmall => Size3XSmall,
        Ps5FontToken.Size4XSmall => Size4XSmall,
        Ps5FontToken.Size5XSmall => Invalid,
        _ => Invalid,
    };

    /// <summary>
    /// <c>UIFont.DefaultLineHeight</c>, verbatim:
    /// <code>
    /// float num = FMath.Round((float)Size * 1.4f);
    /// return (int)(num + num % 2f);   // snapped up to an even number
    /// </code>
    /// So <c>SizeNormal</c> (30) gives 42. Computed in float32 deliberately —
    /// doing it in double disagrees on the rounding boundaries.
    /// </summary>
    /// <param name="size">Font size in pixels.</param>
    public static int LineHeight(int size)
    {
        var num = MathF.Round(size * 1.4f, MidpointRounding.AwayFromZero);
        return (int)(num + num % 2f);
    }

    /// <summary>Line height for a token.</summary>
    /// <param name="token">A UI3 type token.</param>
    public static int LineHeight(Ps5FontToken token) => LineHeight((int)SizeOf(token));

    /// <summary>
    /// <c>FontSizeModule</c>'s exported <c>lineSpacing[X]</c>:
    /// <c>floor(X / (LineSpacing(SizeNormal) / SizeNormal))</c> = <c>floor(X / 1.4f)</c>.
    /// Note this is <em>not</em> <see cref="LineHeight(int)"/>; the JS side gets
    /// the un-snapped value. Float32 on purpose: <c>Size2XSmall</c> sits on the
    /// boundary (21 / 1.3999999761581421 = 15.00000026).
    /// </summary>
    /// <param name="size">Font size in pixels.</param>
    public static int LineSpacing(int size) => (int)MathF.Floor(size / 1.4f);

    /// <summary>Line spacing for a token.</summary>
    /// <param name="token">A UI3 type token.</param>
    public static int LineSpacing(Ps5FontToken token) => LineSpacing((int)SizeOf(token));
}
