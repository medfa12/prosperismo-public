// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The focused title's strip, from HOME m565 (<c>TitleContainer</c>) and its
/// stylesheet m214.
///
/// The strip is one row, absolutely placed at
/// <c>(TITLE_X, TITLE_Y) = (356, 106)</c> and
/// <c>SCALED_EXP_SIZE - EXPERIENCE_SIZE = 62</c> tall, holding the experience
/// name and then, only where the metadata calls for it, a separator, a platform
/// tag, a package tag and up to two metadata icons.
///
/// The width the name gets is not a constant. The source computes it per title:
///
/// <code>
/// function (platformTag, packageTag, metadata) {
///     return 1132
///         - (metadata?.entitlementIconId ? 54 : 0)
///         - (metadata?.storageIconId     ? 54 : 0)
///         - (platformTag ? 76  : 0)
///         - (packageTag  ? 260 : 0);
/// }
/// </code>
///
/// and it decides whether each tag shows at all by excluding the values that
/// mean "nothing worth saying":
///
/// <code>
/// showPlatform = !["", STRING_ID.PPR].includes(platformType)
/// showPackage  = !["", STRING_ID.FULL, STRING_ID.DEMO].includes(packageType)
/// </code>
///
/// So a title with no metadata renders as the name alone across the full 1132,
/// which is exactly what a library of local titles produces. That is the same
/// code path the console takes, not a simplification of it.
/// </summary>
public static class ShellTitleMetrics
{
    /// <summary>TITLE_MARGIN_TOP.</summary>
    public const double TitleMarginTop = 10.0;

    /// <summary>TITLE_MARGIN_LEFT.</summary>
    public const double TitleMarginLeft = 16.0;

    /// <summary>
    /// TITLE_X, <c>SCALED_EXP_MARGIN_LEFT + SCALED_EXP_SIZE + 16</c>: 16 px
    /// past the focused tile's right edge.
    /// </summary>
    public const double TitleX =
        ShellTileRow.ScaledExpMarginLeft + ShellTileRow.ScaledExperienceSize + TitleMarginLeft;

    /// <summary>TITLE_Y, which is EXPERIENCE_SIZE: bottom aligned with the tile.</summary>
    public const double TitleY = ShellTileRow.ExperienceSize;

    /// <summary><c>itemContainer.height</c>, <c>SCALED_EXP_SIZE - EXPERIENCE_SIZE</c>.</summary>
    public const double StripHeight =
        ShellTileRow.ScaledExperienceSize - ShellTileRow.ExperienceSize;

    /// <summary>MINIMIZED_TITLE_MARGIN_LEFT, for the running-title header.</summary>
    public const double MinimizedTitleMarginLeft = 44.0;

    /// <summary>MINIMIZED_TITLE_MARGIN_TOP.</summary>
    public const double MinimizedTitleMarginTop = 9.0;

    /// <summary>The name's width before anything else claims part of the row.</summary>
    public const double BaseTitleWidth = 1132.0;

    /// <summary>Width surrendered to each metadata icon that is present.</summary>
    public const double MetadataIconAllowance = 54.0;

    /// <summary>Width surrendered to a platform tag.</summary>
    public const double PlatformTagAllowance = 76.0;

    /// <summary>Width surrendered to a package tag.</summary>
    public const double PackageTagAllowance = 260.0;

    /// <summary><c>entitlementIconId</c> and <c>storageIconId</c> are both square.</summary>
    public const double MetadataIconSize = 42.0;

    /// <summary><c>matadataIconContainer.marginLeft</c>, the source's own spelling.</summary>
    public const double MetadataIconContainerMarginLeft = 12.0;

    /// <summary><c>separatorText</c> width: a 2 px rule between name and tag.</summary>
    public const double SeparatorWidth = 2.0;

    /// <summary><c>separatorText.top</c> and <c>.bottom</c>, inset from the row.</summary>
    public const double SeparatorInset = 6.0;

    /// <summary><c>separatorText.left</c>.</summary>
    public const double SeparatorLeft = 12.0;

    /// <summary><c>tagText.marginLeft</c>.</summary>
    public const double TagTextMarginLeft = 26.0;

    /// <summary><c>separatorText.backgroundColor</c>, rgba(255,255,255,0.25).</summary>
    public const double SeparatorOpacity = 0.25;

    /// <summary><c>tagText.color</c>, rgba(255,255,255,0.7).</summary>
    public const double TagTextOpacity = 0.7;

    /// <summary>
    /// The name's available width for one title. Mirrors the source's own
    /// deduction chain rather than measuring, so the answer is the same one the
    /// console would reach for the same metadata.
    /// </summary>
    public static double NameWidth(
        bool hasEntitlementIcon,
        bool hasStorageIcon,
        bool showPlatformTag,
        bool showPackageTag)
    {
        double width = BaseTitleWidth;
        if (hasEntitlementIcon)
        {
            width -= MetadataIconAllowance;
        }

        if (hasStorageIcon)
        {
            width -= MetadataIconAllowance;
        }

        if (showPlatformTag)
        {
            width -= PlatformTagAllowance;
        }

        if (showPackageTag)
        {
            width -= PackageTagAllowance;
        }

        return width;
    }

    /// <summary>
    /// Whether a platform tag is worth showing. The source hides it for an
    /// unset value and for its own PPR platform, because "this is a PS5 title"
    /// on a PS5 says nothing.
    /// </summary>
    public static bool ShowPlatformTag(string? platformType) =>
        !string.IsNullOrEmpty(platformType)
        && !string.Equals(platformType, "PPR", StringComparison.Ordinal);

    /// <summary>
    /// Whether a package tag is worth showing. Hidden for an unset value and
    /// for the two package types that are not worth calling out.
    /// </summary>
    public static bool ShowPackageTag(string? packageType) =>
        !string.IsNullOrEmpty(packageType)
        && !string.Equals(packageType, "FULL", StringComparison.Ordinal)
        && !string.Equals(packageType, "DEMO", StringComparison.Ordinal);
}
