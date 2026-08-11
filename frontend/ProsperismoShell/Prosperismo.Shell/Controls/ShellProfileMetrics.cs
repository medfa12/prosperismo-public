// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The trophy summary's five layouts, HOME m157
/// (<c>@rnps-ppr/ui-shared-utilities-trophy-summary</c>).
///
/// The package ships one summary drawn five different ways and each carries its
/// own numbers rather than scaling one set: a small inline badge, a per-grade
/// column, a portrait block, a wide block, and a total. The level icon alone is
/// 48, 108 or 128 depending on which, so there is no single "trophy icon size"
/// to factor out.
///
/// Several offsets are negative (<c>text.marginTop: -2</c>,
/// <c>text.marginLeft: -11</c>). They are optical corrections pulling a numeral
/// back against its icon, not slips, and rounding them to zero visibly
/// misaligns the count from the badge it belongs to.
/// </summary>
public static class ShellTrophySummaryMetrics
{
    /// <summary>The inline badge: a 48 icon with the level beside it.</summary>
    public static class Small
    {
        /// <summary><c>levelIcon</c>, 48 square.</summary>
        public const double LevelIconSize = 48.0;

        /// <summary><c>levelNum.marginLeft</c>.</summary>
        public const double LevelNumMarginLeft = 6.0;

        /// <summary><c>levelNum.opacity</c>.</summary>
        public const double LevelNumOpacity = 0.7;
    }

    /// <summary>One grade's column: an icon over its count.</summary>
    public static class Grade
    {
        /// <summary><c>container.width</c>.</summary>
        public const double ContainerWidth = 80.0;

        /// <summary><c>icon</c>, 48 square.</summary>
        public const double IconSize = 48.0;

        /// <summary>
        /// <c>text.marginTop</c>. Negative on purpose: the count is pulled back
        /// up against the icon above it.
        /// </summary>
        public const double TextMarginTop = -2.0;
    }

    /// <summary>The portrait block: a 108 level icon under a 56 gap.</summary>
    public static class Portrait
    {
        /// <summary><c>container.marginTop</c>.</summary>
        public const double ContainerMarginTop = 56.0;

        /// <summary><c>levelIcon</c>, 108 square.</summary>
        public const double LevelIconSize = 108.0;
    }

    /// <summary>The wide block: a 128 icon in a fixed 256 by 128 box.</summary>
    public static class Wide
    {
        /// <summary><c>container.width</c>.</summary>
        public const double ContainerWidth = 256.0;

        /// <summary><c>container.height</c>.</summary>
        public const double ContainerHeight = 128.0;

        /// <summary><c>container.marginBottom</c>.</summary>
        public const double ContainerMarginBottom = 68.0;

        /// <summary><c>icon</c>, 128 square: it fills the box's height.</summary>
        public const double IconSize = 128.0;

        /// <summary>
        /// <c>text.marginLeft</c>. Negative: the numeral is pulled back over
        /// the icon's right edge rather than sitting clear of it.
        /// </summary>
        public const double TextMarginLeft = -11.0;

        /// <summary><c>text.marginBottom</c>, with <c>textAlignVertical: "bottom"</c>.</summary>
        public const double TextMarginBottom = 13.0;
    }

    /// <summary>
    /// The portrait card the summary sits in, HOME m797
    /// (<c>TrophySummary/components/Portrait.js</c>).
    ///
    /// Its 370 by 456 is not a new size: 370 is a blessed content-tile width
    /// and 456 is STACKED.LARGE.FULL's height, so the card drops into the same
    /// grid as everything else on that tier.
    /// </summary>
    public static class PortraitCard
    {
        /// <summary><c>container.width</c>.</summary>
        public const double Width = 370.0;

        /// <summary><c>container.height</c>.</summary>
        public const double Height = 456.0;

        /// <summary><c>rowGrade.marginTop</c>.</summary>
        public const double GradeRowMarginTop = 10.0;

        /// <summary><c>earnedContainer.marginHorizontal</c> and <c>.marginBottom</c>.</summary>
        public const double EarnedMargin = 32.0;

        /// <summary><c>earnedLabel.opacity</c>.</summary>
        public const double EarnedLabelOpacity = 0.7;

        /// <summary><c>errorImage.width</c>: the placeholder when art fails.</summary>
        public const double ErrorImageWidth = 306.0;

        /// <summary><c>errorImage.height</c>.</summary>
        public const double ErrorImageHeight = 236.0;

        /// <summary><c>errorImage.marginTop</c> and <c>.marginHorizontal</c>.</summary>
        public const double ErrorImageMargin = 32.0;
    }

    /// <summary>The total: a label over a count.</summary>
    public static class Total
    {
        /// <summary><c>container.marginBottom</c>.</summary>
        public const double ContainerMarginBottom = 81.0;

        /// <summary><c>label.opacity</c>.</summary>
        public const double LabelOpacity = 0.7;

        /// <summary><c>label.marginBottom</c>.</summary>
        public const double LabelMarginBottom = 4.0;
    }
}

/// <summary>
/// The player tile's sizes, HOME m98 with the square tile's stylesheet m701
/// (<c>@rnps-ppr/ui-shared-utilities-player-tile</c>).
///
/// Two families. The row tile is 130 or 98 tall depending on density; the
/// square tile is 370 wide and 340, 314 or 360 tall - the last only when the
/// font scale is very large, which is why a fixed height cannot stand in for
/// all three.
///
/// The avatar's <c>marginRight: 113</c> is not a centring value and does not
/// become one if you compute it: the avatar is right-aligned inside its row
/// (<c>justifyContent: "flex-end"</c>) and then pushed back off that edge, so
/// the number is the offset from the right, not from the middle.
/// </summary>
public static class ShellPlayerTileMetrics
{
    /// <summary><c>TILE_HEIGHT_L</c>: the roomy row tile.</summary>
    public const double RowHeightLarge = 130.0;

    /// <summary><c>TILE_HEIGHT_S</c>: the dense row tile.</summary>
    public const double RowHeightSmall = 98.0;

    /// <summary><c>TILE_SQUARE_WIDTH</c>.</summary>
    public const double SquareWidth = 370.0;

    /// <summary><c>TILE_SQUARE_HEIGHT_L</c>.</summary>
    public const double SquareHeightLarge = 340.0;

    /// <summary><c>TILE_SQUARE_HEIGHT_S</c>.</summary>
    public const double SquareHeightSmall = 314.0;

    /// <summary>
    /// <c>TILE_SQUARE_HEIGHT_L_VL</c>: the very-large font-scale height. The
    /// tile grows for accessibility rather than clipping its name.
    /// </summary>
    public const double SquareHeightLargeVeryLargeFont = 360.0;

    /// <summary><c>avatar.height</c> and <c>.width</c>.</summary>
    public const double AvatarSize = 144.0;

    /// <summary><c>avatar.marginTop</c>.</summary>
    public const double AvatarMarginTop = 32.0;

    /// <summary>
    /// <c>avatar.marginRight</c>. Measured from the right edge, because the
    /// avatar's row is <c>justifyContent: "flex-end"</c>.
    /// </summary>
    public const double AvatarMarginRight = 113.0;

    /// <summary><c>avatar.marginBottom</c>.</summary>
    public const double AvatarMarginBottom = 24.0;

    /// <summary><c>nameTextStyle.width</c>.</summary>
    public const double NameWidth = 322.0;

    /// <summary><c>tagContainerStyle.marginLeft</c> and <c>.marginTop</c>.</summary>
    public const double TagMargin = 16.0;

    /// <summary>
    /// Square tile height for a font scale. Only the very-large scale changes
    /// it; every smaller scale uses the large height.
    /// </summary>
    public static double SquareHeightFor(bool dense, bool veryLargeFont)
    {
        if (veryLargeFont)
        {
            return SquareHeightLargeVeryLargeFont;
        }

        return dense ? SquareHeightSmall : SquareHeightLarge;
    }
}

/// <summary>
/// The beta build watermark, HOME m842 (<c>BetaVersion</c>).
///
/// <code>
/// betaVersion: { fontSize: Size2XSmall, opacity: .7,
///                position: "absolute", top: 1038, marginLeft: 16 }
/// </code>
///
/// <c>top: 1038</c> is an absolute position on the 1080 canvas, so the label
/// sits 42 px off the bottom edge. It is quoted from the top rather than the
/// bottom, which means a surface that is not exactly 1080 tall has to convert
/// rather than reuse the number.
/// </summary>
public static class ShellBetaWatermarkMetrics
{
    /// <summary><c>betaVersion.top</c>, on the 1080 canvas.</summary>
    public const double Top = 1038.0;

    /// <summary><c>betaVersion.marginLeft</c>.</summary>
    public const double MarginLeft = 16.0;

    /// <summary><c>betaVersion.opacity</c>.</summary>
    public const double Opacity = 0.7;

    /// <summary>Distance from the bottom of the authored canvas.</summary>
    public const double FromBottom = ShellDialog.DesignHeight - Top;
}
