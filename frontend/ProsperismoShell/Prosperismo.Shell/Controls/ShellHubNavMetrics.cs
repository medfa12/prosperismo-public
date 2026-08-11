// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The hub's navigation strip, HOME m389 (<c>HubNav</c>).
///
/// A hub lays its nav out one of two ways and each has a wrapper that undoes
/// its insets:
///
/// <code>
/// horizontalNav:     { marginRight: 172, marginLeft: 148 }
/// horizontalWrapper: { flex: 1, paddingTop: 40, marginRight: -172, marginLeft: -148 }
/// verticalNav:       { width: 2152, marginTop: 86, marginLeft: 40 }
/// verticalWrapper:   { flex: 1, marginLeft: -12 }
/// </code>
///
/// The negative margins are not a mistake to be tidied away. The nav is inset
/// so its own content lines up with the surface, and its wrapper cancels those
/// insets exactly so the scrolling content underneath bleeds to the full width
/// and is clipped by the screen instead of by the nav's box. Removing them
/// leaves a strip that stops short of both edges.
///
/// The vertical variant's 2152 is wider than the 1920 canvas on purpose: it is
/// a scrolling track, not a visible box.
/// </summary>
public static class ShellHubNavMetrics
{
    /// <summary><c>horizontalNav.marginLeft</c>.</summary>
    public const double HorizontalMarginLeft = 148.0;

    /// <summary><c>horizontalNav.marginRight</c>.</summary>
    public const double HorizontalMarginRight = 172.0;

    /// <summary><c>horizontalWrapper.paddingTop</c>.</summary>
    public const double HorizontalWrapperPaddingTop = 40.0;

    /// <summary>
    /// <c>horizontalWrapper.marginLeft</c>: the exact negative of
    /// <see cref="HorizontalMarginLeft"/>.
    /// </summary>
    public const double HorizontalWrapperMarginLeft = -HorizontalMarginLeft;

    /// <summary>
    /// <c>horizontalWrapper.marginRight</c>: the exact negative of
    /// <see cref="HorizontalMarginRight"/>.
    /// </summary>
    public const double HorizontalWrapperMarginRight = -HorizontalMarginRight;

    /// <summary><c>verticalNav.width</c>, a track wider than the screen.</summary>
    public const double VerticalTrackWidth = 2152.0;

    /// <summary><c>verticalNav.marginTop</c>.</summary>
    public const double VerticalMarginTop = 86.0;

    /// <summary><c>verticalNav.marginLeft</c>.</summary>
    public const double VerticalMarginLeft = 40.0;

    /// <summary><c>verticalWrapper.marginLeft</c>.</summary>
    public const double VerticalWrapperMarginLeft = -12.0;

    /// <summary>
    /// Width the horizontal nav's own content occupies on the 1920 canvas,
    /// once both insets are taken off.
    /// </summary>
    public const double HorizontalContentWidth =
        ShellDialog.DesignWidth - HorizontalMarginLeft - HorizontalMarginRight;

    /// <summary>
    /// True when a wrapper cancels its nav's inset exactly, which is the
    /// property that makes the content bleed rather than inset twice.
    /// </summary>
    public static bool WrapperCancelsInset(double navMargin, double wrapperMargin) =>
        navMargin + wrapperMargin == 0.0;
}

/// <summary>
/// The hub's scene list, HOME m399 (<c>SceneList</c>).
///
/// <code>
/// container: { flex: 1, marginTop: -40 }
/// list:      { flex: 1 }
/// </code>
///
/// The −40 is VERTICAL_HEIGHT_CHANGE, the same number the hub rides up by when
/// it takes focus from the row. The list pulls itself up by exactly that, so
/// the scenes sit flush against the row above rather than a band below it.
/// </summary>
public static class ShellSceneListMetrics
{
    /// <summary><c>container.marginTop</c>, the negative of VERTICAL_HEIGHT_CHANGE.</summary>
    public const double ContainerMarginTop = -ShellTileRow.VerticalHeightChange;
}
