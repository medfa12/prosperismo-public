// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The shell's shared padding scale, HOME m721 <c>PADDING</c>.
///
/// Every content tile picks its paddings from this scale rather than carrying
/// its own numbers, which is why 24 and 16 and 8 recur across surfaces that
/// otherwise share nothing.
/// </summary>
public static class ShellTilePadding
{
    /// <summary><c>PADDING.LARGE</c>.</summary>
    public const double Large = 24.0;

    /// <summary><c>PADDING.MEDIUM</c>.</summary>
    public const double Medium = 16.0;

    /// <summary><c>PADDING.SMALL</c>.</summary>
    public const double Small = 8.0;

    /// <summary><c>PADDING.SEARCH</c>.</summary>
    public const double Search = 30.0;

    /// <summary><c>PADDING.SEARCH_SOURCE</c>.</summary>
    public const double SearchSource = 25.0;

    /// <summary><c>PADDING.PROFILE.LARGE.TOP</c>.</summary>
    public const double ProfileLargeTop = 40.0;

    /// <summary><c>PADDING.PROFILE.LARGE.BOTTOM</c>.</summary>
    public const double ProfileLargeBottom = 40.0;

    /// <summary><c>PADDING.PROFILE.SMALL.TOP</c>.</summary>
    public const double ProfileSmallTop = 32.0;

    /// <summary><c>PADDING.PROFILE.SMALL.BOTTOM</c>.</summary>
    public const double ProfileSmallBottom = 16.0;
}

/// <summary>Element metrics shared by every content tile, HOME m721 <c>ELEMENT</c>.</summary>
public static class ShellTileElement
{
    /// <summary><c>ELEMENT.ATTRIBUTE</c>.</summary>
    public const double Attribute = 32.0;

    /// <summary><c>ELEMENT.LABEL_PADDING</c>.</summary>
    public const double LabelPadding = 10.0;

    /// <summary><c>ELEMENT.BOTTOM</c>.</summary>
    public const double Bottom = 26.0;
}

/// <summary>
/// The three fallback icon sizes a content tile can draw when it has no art.
/// The bundle keeps them as bare locals (<c>o = 92, r = 72, g = 64</c>) and
/// picks one per tile size.
/// </summary>
public static class ShellFallbackIcon
{
    /// <summary>92, for the 504-wide family.</summary>
    public const double Large = 92.0;

    /// <summary>72, for the 370-wide family.</summary>
    public const double Medium = 72.0;

    /// <summary>64, for the 236 and 296 families.</summary>
    public const double Small = 64.0;
}

/// <summary>
/// One entry from the shell's content-tile catalogue, HOME m721.
///
/// A tile is not free-form: the shell chooses from a fixed set of shapes, and
/// each carries its own media box, paddings, label line count and fallback icon
/// size. <see cref="MetaHeight"/> is set only for the stacked family, where the
/// art sits above a meta block rather than filling the tile.
/// </summary>
/// <param name="Name">The catalogue path, e.g. <c>PLAIN.SQUARE.LARGE</c>.</param>
/// <param name="Width">Overall tile width.</param>
/// <param name="Height">Overall tile height.</param>
/// <param name="MediaWidth">Art box width.</param>
/// <param name="MediaHeight">Art box height.</param>
/// <param name="PrimaryPadding">The tile's own padding.</param>
/// <param name="SecondaryPadding">Gap above a sub-label.</param>
/// <param name="FallbackIcon">Side of the placeholder icon.</param>
/// <param name="LabelLines">How many lines the label may wrap to.</param>
/// <param name="MetaHeight">Height of the meta block, stacked family only.</param>
public sealed record ShellTileSpec(
    string Name,
    double Width,
    double Height,
    double MediaWidth,
    double MediaHeight,
    double PrimaryPadding,
    double SecondaryPadding,
    double FallbackIcon,
    int LabelLines,
    double? MetaHeight = null)
{
    /// <summary>True where the art fills the tile rather than sitting above a
    /// meta block.</summary>
    public bool IsPlain => MetaHeight is null;
}

/// <summary>
/// The shell's content-tile catalogue, ported verbatim from HOME m721.
///
/// This is the tier the library, the hubs and the search results draw on. It is
/// emphatically <b>not</b> the experience switcher: that row is 106 growing to
/// 168 and has its own constants in <see cref="ShellTileRow"/>. Mixing the two
/// is what produces a home screen with library-sized game icons.
///
/// The sizes are a closed set. A surface picks an entry; it does not invent a
/// width. Where the catalogue offers 236, 296, 370, 504 and 772, those are the
/// only widths the console draws at this tier.
/// </summary>
public static class ShellTileCatalogue
{
    // ---- PLAIN: the art fills the tile ------------------------------------

    /// <summary><c>PLAIN.SQUARE.LARGE</c>.</summary>
    public static readonly ShellTileSpec SquareLarge = new(
        "PLAIN.SQUARE.LARGE", 504, 504, 504, 504,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 1);

    /// <summary><c>PLAIN.SQUARE.MEDIUM</c>.</summary>
    public static readonly ShellTileSpec SquareMedium = new(
        "PLAIN.SQUARE.MEDIUM", 370, 370, 370, 370,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Medium, 1);

    /// <summary><c>PLAIN.SQUARE.SMALL</c>. The library grid's tile.</summary>
    public static readonly ShellTileSpec SquareSmall = new(
        "PLAIN.SQUARE.SMALL", 296, 296, 296, 296,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Small, 1);

    /// <summary><c>PLAIN.SQUARE.XSMALL</c>.</summary>
    public static readonly ShellTileSpec SquareXSmall = new(
        "PLAIN.SQUARE.XSMALL", 236, 236, 236, 236,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Small, 1);

    /// <summary><c>PLAIN.WIDE.LARGE</c>.</summary>
    public static readonly ShellTileSpec WideLarge = new(
        "PLAIN.WIDE.LARGE", 504, 284, 504, 284,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 1);

    /// <summary><c>PLAIN.WIDE.MEDIUM</c>.</summary>
    public static readonly ShellTileSpec WideMedium = new(
        "PLAIN.WIDE.MEDIUM", 370, 208, 370, 208,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 1);

    /// <summary><c>PLAIN.WIDE.SMALL</c>.</summary>
    public static readonly ShellTileSpec WideSmall = new(
        "PLAIN.WIDE.SMALL", 236, 133, 236, 133,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Small, 1);

    /// <summary><c>PLAIN.TALL.MEDIUM</c>, the only tall shape.</summary>
    public static readonly ShellTileSpec TallMedium = new(
        "PLAIN.TALL.MEDIUM", 370, 555, 370, 555,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 1);

    /// <summary><c>PLAIN.FULL.LARGE</c>, the widest shape the tier draws.</summary>
    public static readonly ShellTileSpec FullLarge = new(
        "PLAIN.FULL.LARGE", 772, 579, 772, 579,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Medium, 1);

    // ---- STACKED: art above a meta block ----------------------------------

    /// <summary><c>STACKED.LARGE.FULL</c>.</summary>
    public static readonly ShellTileSpec StackedLargeFull = new(
        "STACKED.LARGE.FULL", 504, 456, 504, 284,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 1, 172);

    /// <summary><c>STACKED.LARGE.DESCRIPTION</c>.</summary>
    public static readonly ShellTileSpec StackedLargeDescription = new(
        "STACKED.LARGE.DESCRIPTION", 504, 448, 504, 284,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 2, 164);

    /// <summary><c>STACKED.LARGE.DUAL_LABEL</c>.</summary>
    public static readonly ShellTileSpec StackedLargeDualLabel = new(
        "STACKED.LARGE.DUAL_LABEL", 504, 442, 504, 284,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 2, 158);

    /// <summary>
    /// <c>STACKED.LARGE.LABEL</c>. Note the entry's own <c>height</c> is 400
    /// while its stylesheet's <c>root.height</c> is 408; the stylesheet is what
    /// lays the tile out, so that is what is carried here.
    /// </summary>
    public static readonly ShellTileSpec StackedLargeLabel = new(
        "STACKED.LARGE.LABEL", 504, 408, 504, 284,
        ShellTilePadding.Large, ShellTilePadding.Medium, ShellFallbackIcon.Large, 2, 116);

    /// <summary><c>STACKED.MEDIUM.FULL</c>.</summary>
    public static readonly ShellTileSpec StackedMediumFull = new(
        "STACKED.MEDIUM.FULL", 370, 344, 370, 208,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 1, 136);

    /// <summary><c>STACKED.MEDIUM.DESCRIPTION</c>.</summary>
    public static readonly ShellTileSpec StackedMediumDescription = new(
        "STACKED.MEDIUM.DESCRIPTION", 370, 340, 370, 208,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 2, 136);

    /// <summary><c>STACKED.MEDIUM.DUAL_LABEL</c>.</summary>
    public static readonly ShellTileSpec StackedMediumDualLabel = new(
        "STACKED.MEDIUM.DUAL_LABEL", 370, 334, 370, 208,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 2, 126);

    /// <summary><c>STACKED.MEDIUM.LABEL</c>.</summary>
    public static readonly ShellTileSpec StackedMediumLabel = new(
        "STACKED.MEDIUM.LABEL", 370, 300, 370, 208,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 2, 92);

    /// <summary><c>STACKED.MEDIUM.SQUARE_DUAL_LABEL</c>: square art, stacked meta.</summary>
    public static readonly ShellTileSpec StackedMediumSquareDualLabel = new(
        "STACKED.MEDIUM.SQUARE_DUAL_LABEL", 370, 498, 370, 370,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Medium, 2, 128);

    /// <summary><c>STACKED.SMALL.LABEL</c>.</summary>
    public static readonly ShellTileSpec StackedSmallLabel = new(
        "STACKED.SMALL.LABEL", 236, 201, 236, 133,
        ShellTilePadding.Medium, ShellTilePadding.Small, ShellFallbackIcon.Small, 2, 68);

    // ---- SLIM: a full-width row, art on the left --------------------------

    /// <summary><c>SLIM.SQUARE</c>. Width is <c>"100%"</c> in the source.</summary>
    public static readonly ShellTileSpec SlimSquare = new(
        "SLIM.SQUARE", double.NaN, 192, 144, 144,
        ShellTilePadding.Large, ShellTilePadding.Small, ShellFallbackIcon.Small, 2);

    /// <summary><c>SLIM.WIDE</c>, a 16:9 art box on the same 192 row.</summary>
    public static readonly ShellTileSpec SlimWide = new(
        "SLIM.WIDE", double.NaN, 192, 144, 81,
        ShellTilePadding.Large, ShellTilePadding.Small, ShellFallbackIcon.Small, 2);

    /// <summary>Every catalogued shape, in the order the source declares them.</summary>
    public static IReadOnlyList<ShellTileSpec> All { get; } = new[]
    {
        SquareLarge, SquareMedium, SquareSmall, SquareXSmall,
        WideLarge, WideMedium, WideSmall,
        TallMedium, FullLarge,
        StackedLargeFull, StackedLargeDescription, StackedLargeDualLabel, StackedLargeLabel,
        StackedMediumFull, StackedMediumDescription, StackedMediumDualLabel, StackedMediumLabel,
        StackedMediumSquareDualLabel, StackedSmallLabel,
        SlimSquare, SlimWide,
    };

    /// <summary>
    /// The widths this tier is allowed to draw at. A surface that needs a
    /// different one is on the wrong tier, not in need of a new size.
    /// </summary>
    public static IReadOnlyList<double> BlessedWidths { get; } =
        new[] { 236.0, 296.0, 370.0, 504.0, 772.0 };
}
