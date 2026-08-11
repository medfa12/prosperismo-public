// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The library app's grid constants, ported rule for rule from the game library
/// bundle (NPXS40071) rather than reconstructed from a screenshot.
///
/// The whole table is self-checking: five columns of
/// <see cref="LibraryTileWidth"/> with <see cref="ShellGridPadding"/>'s
/// horizontal padding between them come to
/// <c>296*5 + 24*4 = 1576</c>, which is the container's own declared width and
/// the same 1576 band the rest of the shell pins to inside its 172 margins. A
/// number that does not reproduce that identity is the wrong number.
///
/// Provenance, in the house locator style (LIB is NPXS40071, mNNN is the haul
/// module id): the tile preset is <c>SIZE.OVERLAY.SQUARE.SMALL</c>
/// (LIB m894, reached through LIB m101 <c>LIBRARY_TILE_SIZE</c>); the column
/// count and visible-row count are LIB m101; the padding table, item margin,
/// segment header and bottom margin are LIB m145; the container is LIB m105;
/// the section tail margin is LIB m927.
/// </summary>
public static class ShellLibraryMetrics
{
    /// <summary>LIBRARY_TILE_SIZE.width. SIZE.OVERLAY.SQUARE.SMALL is a square,
    /// so the height is the same number.</summary>
    public const double LibraryTileWidth = 296.0;

    /// <summary>LIBRARY_TILE_SIZE.height.</summary>
    public const double LibraryTileHeight = 296.0;

    /// <summary>NUM_ROW_TILES: how many tiles a grid row carries.</summary>
    public const int NumRowTiles = 5;

    /// <summary>NUM_VISIBLE_ROWS: how many rows the viewport shows.</summary>
    public const int NumVisibleRows = 3;

    /// <summary>
    /// GRID_ACTIVE_AREA_OFFSET, written in the source as <c>2 * tile height</c>.
    /// The focused row is held this far down from the top of the viewport once
    /// the grid has scrolled, which is what leaves exactly two rows above it and
    /// makes NUM_VISIBLE_ROWS 3 come out right.
    /// </summary>
    public const double GridActiveAreaOffset = 2.0 * LibraryTileHeight;

    /// <summary>GRID_ITEM_MARGIN. Exported beside the padding table; the five
    /// column grid packs on the padding, not on this.</summary>
    public const double GridItemMargin = 20.0;

    /// <summary>DEFAULT_MARGIN_UNDER_BOTTOM_ITEM: air under the last row so the
    /// bottom row can be scrolled clear of the screen edge.</summary>
    public const double DefaultMarginUnderBottomItem = 90.0;

    /// <summary>SEGMENT_HEADER_HEIGHT: the section title strip.</summary>
    public const double SegmentHeaderHeight = 34.0;

    /// <summary>SEGMENT_HEADER_BOTTOM_MARGIN: air under a section title. Note
    /// that 34 + 24 is the 58 the hub bundle quotes as its section header
    /// height, which is the two bundles agreeing.</summary>
    public const double SegmentHeaderBottomMargin = 24.0;

    /// <summary>sectionTailItemMargin: extra air after a section's last row,
    /// before the next section's header.</summary>
    public const double SectionTailItemMargin = 64.0;

    /// <summary>The grid container's own width; equals the packed width of five
    /// tiles and their four gutters.</summary>
    public const double ContainerWidth = 1576.0;

    /// <summary>The container's marginHorizontal, the shell's content inset.</summary>
    public const double ContainerMarginHorizontal = 172.0;

    /// <summary>optionContainer.left. The sort control hangs outside the grid
    /// container, in the left margin, so it is a negative offset.</summary>
    public const double OptionContainerLeft = -120.0;

    /// <summary>optionItem.marginBottom, the pitch of stacked option buttons.</summary>
    public const double OptionItemMarginBottom = 32.0;

    /// <summary>sortIconContainer: the sort/filter control is a 72 square.</summary>
    public const double SortIconSide = 72.0;

    /// <summary>A sort or filter row in the popup panel.</summary>
    public const double SortOptionHeight = 72.0;

    /// <summary>sortOption.paddingLeft: the leading gutter a row's label clears,
    /// which is where the selected row's checkmark sits.</summary>
    public const double SortOptionLeadingGutter = 72.0;

    /// <summary>sortOption.minWidth.</summary>
    public const double SortOptionMinWidth = 384.0;

    /// <summary>checkmarkContainer: marginHorizontal 16, width 40.</summary>
    public const double SortCheckmarkMargin = 16.0;

    /// <summary>checkmarkContainer.width.</summary>
    public const double SortCheckmarkWidth = 40.0;

    // ---- Tile chrome (SIZE.OVERLAY.SQUARE.SMALL and the OVERLAY template) ----

    /// <summary>PADDING.MEDIUM, the SMALL preset's primary padding: the inset
    /// the tile's overlay content keeps from the art's edge.</summary>
    public const double TilePrimaryPadding = 16.0;

    /// <summary>PADDING.SMALL, the SMALL preset's secondary padding: the gap
    /// between two stacked pieces of overlay metadata.</summary>
    public const double TileSecondaryPadding = 8.0;

    /// <summary>SIZE_ELEMENT.ATTRIBUTE: height of the tag row.</summary>
    public const double TileAttributeHeight = 32.0;

    /// <summary>SIZE_ELEMENT.LABEL_PADDING: added to the font size per label
    /// line when the overlay works out how tall its metadata is.</summary>
    public const double TileLabelPadding = 10.0;

    /// <summary>SIZE_ELEMENT.BOTTOM.</summary>
    public const double TileBottomElement = 26.0;

    /// <summary>media_fallbackIcon for the SMALL preset: the neutral app icon
    /// drawn when a tile has no art is a 64 square, centred.</summary>
    public const double TileFallbackIconSide = 64.0;

    /// <summary>statusIcon: 32 square with marginLeft 8.</summary>
    public const double TileStatusIconSide = 32.0;

    /// <summary>statusIcon.marginLeft.</summary>
    public const double TileStatusIconMargin = 8.0;

    /// <summary>
    /// The library tile has no corner radius. This is an exact absence, not an
    /// oversight: the tile preset carries root, container, media, fallback,
    /// attribute, subLabel and selectionIndicator rules and no borderRadius
    /// anywhere, and the blank slot behind every tile takes borderRadius 0 by
    /// default and is never passed another value. The home switcher's 16-at-106
    /// belongs to a different tier and does not carry over. The art itself is
    /// composited natively, so a small native rounding cannot be ruled out; 0 is
    /// the only value in evidence.
    /// </summary>
    public const double TileCornerRadius = 0.0;

    /// <summary>ANIMATION.OPACITY.GRADIENT.MIN: the overlay gradient never goes
    /// fully away, it rests at one hundredth.</summary>
    public const double GradientOpacityMin = 0.010000001;

    /// <summary>ANIMATION.OPACITY.GRADIENT.MAX.</summary>
    public const double GradientOpacityMax = 1.0;

    /// <summary>ANIMATION.GRADIENT_OFFSET, applied to the gradient's length.</summary>
    public const double GradientOffset = -80.0;

    /// <summary>ANIMATION.TIMING.DEFAULT, the tile's own fade duration in ms.</summary>
    public const double AnimationTimingDefault = 300.0;

    /// <summary>ANIMATION.TIMING.LOADING, the shimmer period in ms.</summary>
    public const double AnimationTimingLoading = 750.0;

    /// <summary>ANIMATION.OPACITY.LOADING_GRID: what a not-yet-loaded grid slot
    /// pulses between.</summary>
    public const double LoadingGridOpacityMin = 0.0;

    /// <summary>ANIMATION.OPACITY.LOADING_GRID max.</summary>
    public const double LoadingGridOpacityMax = 0.03;

    /// <summary>subLabelText.opacity.</summary>
    public const double SubLabelOpacity = 0.7;

    // ---- The empty grid (ErrorView) ----------------------------------------

    /// <summary>ErrorView container: the full content band, 824 tall.</summary>
    public const double EmptyContainerHeight = 824.0;

    /// <summary>ErrorView innerContainer width.</summary>
    public const double EmptyInnerWidth = 1040.0;

    /// <summary>mainTextContainer.marginTop.</summary>
    public const double EmptyMainTextMarginTop = 16.0;

    /// <summary>mainTextContainer.marginBottom.</summary>
    public const double EmptyMainTextMarginBottom = 56.0;

    /// <summary>mainButton.minWidth.</summary>
    public const double EmptyButtonMinWidth = 334.0;

    /// <summary>mainButton.maxWidth.</summary>
    public const double EmptyButtonMaxWidth = 638.0;
}

/// <summary>
/// GridPadding, the per-column-count padding table. The grid does not derive a
/// gutter from its column count, it looks the count up, exactly like the
/// horizontal strand packing table does for widths. Only 3, 4 and 5 exist.
/// </summary>
public static class ShellGridPadding
{
    /// <summary>The padding pair a column count is entitled to.</summary>
    /// <param name="PaddingVertical">Air between two rows.</param>
    /// <param name="PaddingHorizontal">Gutter between two columns.</param>
    public readonly record struct Entry(double PaddingVertical, double PaddingHorizontal);

    private static readonly Dictionary<int, Entry> Table = new()
    {
        [3] = new Entry(32.0, 32.0),
        [4] = new Entry(32.0, 32.0),
        [5] = new Entry(24.0, 24.0),
    };

    /// <summary>Looks a column count up. Returns false for a count the table
    /// does not bless, which is the caller's cue that it is off the grid.</summary>
    public static bool TryGet(int numColumns, out Entry entry) => Table.TryGetValue(numColumns, out entry);

    /// <summary>The padding for a column count, falling back to the five column
    /// entry the library itself uses.</summary>
    public static Entry For(int numColumns) =>
        Table.TryGetValue(numColumns, out var entry) ? entry : Table[ShellLibraryMetrics.NumRowTiles];
}

/// <summary>One titled run of tiles in the grid, the bundle's "cluster".</summary>
/// <param name="Title">The section header's left hand text.</param>
/// <param name="Count">How many tiles the section holds.</param>
public readonly record struct ShellLibrarySection(string Title, int Count);

/// <summary>
/// The library grid's layout, as arithmetic. Pure and allocation-free so the
/// whole contract can be checked without a render surface, the same way the
/// strand's geometry is.
///
/// The shape is the bundle's own: the grid packs
/// <c>itemWidth * numColumns + paddingHorizontal * (numColumns - 1)</c> wide,
/// every section opens with a <see cref="SectionItemHeight"/> header and its
/// bottom margin, rows sit on a pitch of tile plus vertical padding, sections
/// are parted by <see cref="ShellLibraryMetrics.SectionTailItemMargin"/>, and
/// the last row keeps
/// <see cref="ShellLibraryMetrics.DefaultMarginUnderBottomItem"/> under it.
/// </summary>
public readonly struct ShellLibraryGridGeometry
{
    public ShellLibraryGridGeometry(
        double itemWidth,
        double itemHeight,
        int numColumns,
        double paddingVertical,
        double paddingHorizontal,
        double sectionItemHeight)
    {
        ItemWidth = itemWidth;
        ItemHeight = itemHeight;
        NumColumns = Math.Max(1, numColumns);
        PaddingVertical = paddingVertical;
        PaddingHorizontal = paddingHorizontal;
        SectionItemHeight = sectionItemHeight;
    }

    /// <summary>The library's own grid: five 296 squares, 24 padding both ways,
    /// 34 tall section headers.</summary>
    public static ShellLibraryGridGeometry Library => ForColumns(ShellLibraryMetrics.NumRowTiles);

    /// <summary>The grid for a blessed column count, taking the padding the
    /// table gives that count.</summary>
    public static ShellLibraryGridGeometry ForColumns(int numColumns)
    {
        var padding = ShellGridPadding.For(numColumns);
        return new ShellLibraryGridGeometry(
            ShellLibraryMetrics.LibraryTileWidth,
            ShellLibraryMetrics.LibraryTileHeight,
            numColumns,
            padding.PaddingVertical,
            padding.PaddingHorizontal,
            ShellLibraryMetrics.SegmentHeaderHeight);
    }

    public double ItemWidth { get; }

    public double ItemHeight { get; }

    public int NumColumns { get; }

    /// <summary>Air between two rows, and the inset the content opens on.</summary>
    public double PaddingVertical { get; }

    /// <summary>Gutter between two columns.</summary>
    public double PaddingHorizontal { get; }

    /// <summary>sectionItemHeight: the height of a section header row.</summary>
    public double SectionItemHeight { get; }

    /// <summary>The packed width of the grid. For the library this is 1576,
    /// which is the container's declared width, and that identity is the whole
    /// reason to trust the 296.</summary>
    public double ContentWidth => (ItemWidth * NumColumns) + (PaddingHorizontal * (NumColumns - 1));

    /// <summary>Distance between two columns' left edges.</summary>
    public double ColumnPitch => ItemWidth + PaddingHorizontal;

    /// <summary>Distance between two rows' top edges.</summary>
    public double RowPitch => ItemHeight + PaddingVertical;

    /// <summary>
    /// Where a section's first row of tiles begins, measured from the section's
    /// own top. This is the source's own sum: the vertical padding, the header,
    /// and the header's bottom margin.
    /// </summary>
    public double FirstRowTop =>
        PaddingVertical + SectionItemHeight + ShellLibraryMetrics.SegmentHeaderBottomMargin;

    /// <summary>Left edge of a column, in grid coordinates.</summary>
    public double ColumnLeft(int column) => column * ColumnPitch;

    /// <summary>Rows a section of <paramref name="count"/> tiles occupies.</summary>
    public int RowsFor(int count) => count <= 0 ? 0 : ((count - 1) / NumColumns) + 1;

    /// <summary>The column an index within a section falls in.</summary>
    public int ColumnOf(int indexInSection) => indexInSection % NumColumns;

    /// <summary>The row an index within a section falls in.</summary>
    public int RowOf(int indexInSection) => indexInSection / NumColumns;

    /// <summary>
    /// Top of a section's header, in content coordinates, given the tile counts
    /// of every section before it. Sections with no tiles still take their
    /// header when they are declared, which is how the console keeps an empty
    /// storage cluster visible while it counts.
    /// </summary>
    public double SectionTop(IReadOnlyList<ShellLibrarySection> sections, int sectionIndex)
    {
        double top = 0.0;
        for (int i = 0; i < sectionIndex && i < sections.Count; i++)
        {
            top += SectionHeight(sections[i].Count) + ShellLibraryMetrics.SectionTailItemMargin;
        }

        return top;
    }

    /// <summary>Height of one section: its opening padding, header and margin,
    /// then its rows on the row pitch.</summary>
    public double SectionHeight(int count)
    {
        int rows = RowsFor(count);
        return rows == 0
            ? FirstRowTop
            : FirstRowTop + (rows * RowPitch) - PaddingVertical;
    }

    /// <summary>Top of a tile, in content coordinates.</summary>
    public double ItemTop(IReadOnlyList<ShellLibrarySection> sections, int sectionIndex, int indexInSection) =>
        SectionTop(sections, sectionIndex) + FirstRowTop + (RowOf(indexInSection) * RowPitch);

    /// <summary>Left of a tile, in grid coordinates.</summary>
    public double ItemLeft(int indexInSection) => ColumnLeft(ColumnOf(indexInSection));

    /// <summary>
    /// The scrollable content height: every section, the margins between them,
    /// and the bottom margin under the very last row.
    /// </summary>
    public double ContentHeight(IReadOnlyList<ShellLibrarySection> sections)
    {
        if (sections.Count == 0)
        {
            return 0.0;
        }

        double height = 0.0;
        for (int i = 0; i < sections.Count; i++)
        {
            height += SectionHeight(sections[i].Count);
            if (i < sections.Count - 1)
            {
                height += ShellLibraryMetrics.SectionTailItemMargin;
            }
        }

        return height + ShellLibraryMetrics.DefaultMarginUnderBottomItem;
    }

    /// <summary>
    /// Where the grid scrolls to so that the focused row sits on the active
    /// area offset. The focused row is pinned
    /// <see cref="ShellLibraryMetrics.GridActiveAreaOffset"/> down from the top
    /// of the viewport, clamped so the grid neither scrolls above its first row
    /// nor past its last.
    /// </summary>
    public double ScrollFor(
        IReadOnlyList<ShellLibrarySection> sections,
        int sectionIndex,
        int indexInSection,
        double viewportHeight,
        double activeAreaOffset)
    {
        double maxScroll = Math.Max(0.0, ContentHeight(sections) - viewportHeight);
        double wanted = ItemTop(sections, sectionIndex, indexInSection) - activeAreaOffset;
        return Math.Clamp(wanted, 0.0, maxScroll);
    }
}

/// <summary>Which field a library sort orders on.</summary>
public enum ShellLibrarySortField
{
    /// <summary>SORTTYPE.ALPHA.</summary>
    Alpha,

    /// <summary>SORTTYPE.INSTALLDATE.</summary>
    InstallDate,

    /// <summary>SORTTYPE.FILESIZE.</summary>
    FileSize,
}

/// <summary>SORTDIR.</summary>
public enum ShellLibrarySortDirection
{
    Ascending,
    Descending,
}

/// <summary>
/// One entry of the library's sort menu. <paramref name="DisplayNameId"/> is the
/// bundle's own message id, kept so a future string dump lands in one pass;
/// <paramref name="Label"/> is our rendering of it, because the bundles resolve
/// every string through a native module and carry no text at all.
/// </summary>
public sealed record ShellLibrarySortOption(
    string DisplayNameId,
    ShellLibrarySortField Field,
    ShellLibrarySortDirection Direction,
    string Label);

/// <summary>
/// The installed-games screen's sort configuration, ported in the bundle's own
/// order. The library offers no other sorts on this screen and offers them in
/// exactly this sequence, install date first.
/// </summary>
public static class ShellLibrarySort
{
    /// <summary>STANDARD_SORT_INSTALLDATE_NEW_TO_OLD.</summary>
    public static readonly ShellLibrarySortOption InstallDateNewToOld = new(
        "msgid_installed_date_new_old",
        ShellLibrarySortField.InstallDate,
        ShellLibrarySortDirection.Descending,
        "Installed Date (Newest First)");

    /// <summary>STANDARD_SORT_INSTALLDATE_OLD_TO_NEW.</summary>
    public static readonly ShellLibrarySortOption InstallDateOldToNew = new(
        "msgid_installed_date_old_new",
        ShellLibrarySortField.InstallDate,
        ShellLibrarySortDirection.Ascending,
        "Installed Date (Oldest First)");

    /// <summary>STANDARD_SORT_NAME_A_TO_Z.</summary>
    public static readonly ShellLibrarySortOption NameAToZ = new(
        "msgid_name_a_z",
        ShellLibrarySortField.Alpha,
        ShellLibrarySortDirection.Ascending,
        "Name (A - Z)");

    /// <summary>STANDARD_SORT_NAME_Z_TO_A.</summary>
    public static readonly ShellLibrarySortOption NameZToA = new(
        "msgid_name_z_a",
        ShellLibrarySortField.Alpha,
        ShellLibrarySortDirection.Descending,
        "Name (Z - A)");

    /// <summary>STANDARD_SORT_FILESIZE_LARGE_TO_SMALL.</summary>
    public static readonly ShellLibrarySortOption SizeLargeToSmall = new(
        "msgid_size_large_small",
        ShellLibrarySortField.FileSize,
        ShellLibrarySortDirection.Descending,
        "Size (Largest First)");

    /// <summary>STANDARD_SORT_FILESIZE_SMALL_TO_LARGE.</summary>
    public static readonly ShellLibrarySortOption SizeSmallToLarge = new(
        "msgid_size_small_large",
        ShellLibrarySortField.FileSize,
        ShellLibrarySortDirection.Ascending,
        "Size (Smallest First)");

    /// <summary>The installed screen's sortOptions, in order.</summary>
    public static readonly IReadOnlyList<ShellLibrarySortOption> InstalledScreenOptions =
    [
        InstallDateNewToOld,
        InstallDateOldToNew,
        NameAToZ,
        NameZToA,
        SizeLargeToSmall,
        SizeSmallToLarge,
    ];

    /// <summary>
    /// getSortString: the header text for a chosen sort. The bundle's format is
    /// <c>msgid_sort_by_variable</c> with an <c>option</c> slot; the community
    /// trace of a real console reads "Sort by: Name (A - Z)", which is the same
    /// shape, so that is what the slot is filled into.
    /// </summary>
    public static string GetSortString(ShellLibrarySortOption? option) =>
        option is null ? string.Empty : $"Sort by: {option.Label}";

    /// <summary>
    /// getSortFilterHeader with no filters selected. The bundle joins a sort and
    /// a filter summary with <c>%sort% | %filters%</c> and drops whichever half
    /// is absent; our library has no filter axis, so only the sort half is ever
    /// produced.
    /// </summary>
    public static string GetSortFilterHeader(ShellLibrarySortOption? selectedSort) =>
        GetSortString(selectedSort);
}
