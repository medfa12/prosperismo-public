// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The Search screen, recovered from the search bundle (NPXS40015,
/// <c>rnps-search</c>) module 44's tokens and module 102's stylesheet.
///
/// Its grid is the strongest cross-check we have on the content-tile
/// catalogue: four 370 tiles on 32 gaps comes to exactly 1576, which is the
/// same strand width the home bundle uses and 370 is one of the catalogue's
/// blessed widths. Two independently authored applications agreeing on the
/// arithmetic is better evidence than either one alone.
/// </summary>
public static class ShellSearchMetrics
{
    // ---- The grid ----------------------------------------------------------

    /// <summary><c>GRID_ROW_ITEMS</c> / <c>ITEMS_PER_ROW</c>.</summary>
    public const int Columns = 4;

    /// <summary><c>GRID_ITEM_WIDTH</c>.</summary>
    public const double TileWidth = 370.0;

    /// <summary><c>GRID_ITEM_HEIGHT</c>. Square, like the catalogue's 370.</summary>
    public const double TileHeight = 370.0;

    /// <summary><c>GRID_ITEM_HORIZONTAL_MARGIN</c>.</summary>
    public const double TileGapX = 32.0;

    /// <summary><c>GRID_ITEM_VERTICAL_MARGIN</c>.</summary>
    public const double TileGapY = 32.0;

    /// <summary>
    /// <c>GRID_ROW_WIDTH</c> is declared as 1736, but the width the grid
    /// actually lays out at is computed as
    /// <c>itemWidth*columns + gap*(columns-1)</c> = 1576, and every container
    /// in the screen is 1576. The declared 1736 is carried here because it is
    /// in the bundle, not because anything measures by it.
    /// </summary>
    public const double DeclaredRowWidth = 1736.0;

    /// <summary><c>GRID_ROW_HEIGHT</c>: a tile plus its caption band.</summary>
    public const double RowHeight = 434.0;

    /// <summary>The laid-out grid width: 370*4 + 32*3.</summary>
    public const double GridWidth =
        (TileWidth * Columns) + (TileGapX * (Columns - 1));

    /// <summary><c>ITEMS_PER_STRAND</c>.</summary>
    public const int ItemsPerStrand = 8;

    // ---- The screen frame --------------------------------------------------

    /// <summary><c>container.width</c>, matching <see cref="GridWidth"/>.</summary>
    public const double ContentWidth = 1576.0;

    /// <summary><c>PAGE_MARGIN_TOP</c>, also the container's marginTop.</summary>
    public const double PageMarginTop = 30.0;

    /// <summary><c>SCENE_CONTAINER_HEIGHT</c>.</summary>
    public const double SceneContainerHeight = 892.0;

    /// <summary><c>SCENE_BOTTOM_MARGIN</c>.</summary>
    public const double SceneBottomMargin = 30.0;

    // ---- The input row -----------------------------------------------------

    /// <summary><c>TEXT_INPUT_HEIGHT</c>.</summary>
    public const double InputHeight = 72.0;

    /// <summary><c>TEXT_INPUT_MARGIN_BOTTOM</c>.</summary>
    public const double InputMarginBottom = 32.0;

    /// <summary>The input's <c>maxLength</c>.</summary>
    public const int InputMaxLength = 128;

    /// <summary>
    /// How long the screen waits before acting on a keystroke. Leading-edge, so
    /// the first character searches at once and the rest coalesce.
    /// </summary>
    public const double InputDebounceMs = 500.0;

    /// <summary><c>IME_POS_X</c>: the keyboard is pinned, not anchored to the
    /// field.</summary>
    public const double ImeX = 172.0;

    /// <summary><c>IME_POS_Y</c>.</summary>
    public const double ImeY = 198.0;

    // ---- Motion ------------------------------------------------------------

    /// <summary>
    /// How far the results pane travels up when the on-screen keyboard opens.
    /// Keyed by input method: the software keyboard needs the most room, voice
    /// far less, and a physical keyboard none at all.
    /// </summary>
    public const double ResultsTravelOsk = 440.0;

    /// <summary>Travel for voice input.</summary>
    public const double ResultsTravelVoice = 90.0;

    /// <summary>Travel for a Bluetooth keyboard: none.</summary>
    public const double ResultsTravelBluetooth = 0.0;

    /// <summary>The error and loading panes come down from this instead.</summary>
    public const double PaneTravelError = -220.0;

    /// <summary>Spring the keyboard transition runs on.</summary>
    public const double SpringStiffness = 100.0;

    /// <summary>Damping of that spring.</summary>
    public const double SpringDamping = 100.0;

    /// <summary>Mass of that spring.</summary>
    public const double SpringMass = 0.2;

    // ---- Zero state and overflow ------------------------------------------

    /// <summary>
    /// Top-results width when a search history column is present: three tiles
    /// rather than four.
    /// </summary>
    public const double TopResultsWidthWithHistory =
        (TileWidth * 3) + (TileGapX * 3);

    /// <summary>Top-results width with no history column.</summary>
    public const double TopResultsWidthWithoutHistory =
        (TileWidth * 4) + (TileGapX * 4);

    /// <summary><c>OVERFLOW_TILE_PADDING</c>, on the "view all" tile.</summary>
    public const double OverflowTilePadding = 32.0;

    /// <summary>The "view all" tile's fill.</summary>
    public const string ViewAllTileBackground = "#020408";

    /// <summary>
    /// The height a two-line result caption is given. Fixed, so a one-line and
    /// a two-line title occupy the same band and the grid stays regular.
    /// </summary>
    public const double CaptionHeight = 60.0;

    /// <summary>How many lines a result caption may wrap to.</summary>
    public const int CaptionLines = 2;

    /// <summary>
    /// Which travel the results pane uses for a given input method.
    /// </summary>
    public static double ResultsTravelFor(ShellSearchInput input) => input switch
    {
        ShellSearchInput.OnScreenKeyboard => ResultsTravelOsk,
        ShellSearchInput.Voice => ResultsTravelVoice,
        ShellSearchInput.Bluetooth => ResultsTravelBluetooth,
        _ => ResultsTravelOsk,
    };

    /// <summary>
    /// Where a result sits in the grid, in authored pixels, relative to the
    /// grid's own origin.
    /// </summary>
    public static (double X, double Y) TileOrigin(int index)
    {
        int column = index % Columns;
        int row = index / Columns;
        return (column * (TileWidth + TileGapX), row * (TileHeight + TileGapY));
    }
}

/// <summary>How the user is typing, which decides how far the results move.</summary>
public enum ShellSearchInput
{
    /// <summary>The on-screen keyboard: the largest travel.</summary>
    OnScreenKeyboard,

    /// <summary>Voice input.</summary>
    Voice,

    /// <summary>A physical keyboard: nothing moves.</summary>
    Bluetooth,
}
