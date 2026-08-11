// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// One tile of the library grid. The library's own tile carries art, a label
/// that only shows on focus, an optional sub-label, and a short list of
/// attribute tags; the sort keys ride along because the screen owns its sort.
/// </summary>
public sealed record ShellLibraryItem
{
    public ShellLibraryItem(string title, IImage? icon = null, object? tag = null)
    {
        Title = title ?? string.Empty;
        Icon = icon;
        Tag = tag;
    }

    /// <summary>The tile's label. Hidden while the tile is only glanced.</summary>
    public string Title { get; init; }

    /// <summary>Cover art. Missing art draws the neutral fallback mark.</summary>
    public IImage? Icon { get; init; }

    /// <summary>subLabel: the install or download line under the label.</summary>
    public string? SubLabel { get; init; }

    /// <summary>attribute: the tag row above the label.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Sort key for SORTTYPE.FILESIZE.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Sort key for SORTTYPE.INSTALLDATE.</summary>
    public DateTime InstalledAt { get; init; }

    /// <summary>Caller payload round-tripped through the events.</summary>
    public object? Tag { get; init; }
}

/// <summary>Payload for the grid's selection and activation events.</summary>
public sealed class ShellLibraryItemEventArgs : EventArgs
{
    public ShellLibraryItemEventArgs(int index, ShellLibraryItem? item)
    {
        Index = index;
        Item = item;
    }

    /// <summary>Index into the sorted item list, or -1 when the grid is empty.</summary>
    public int Index { get; }

    /// <summary>The item, or null when the grid is empty.</summary>
    public ShellLibraryItem? Item { get; }
}

/// <summary>
/// The All Games surface: the console's game library grid, ported from the
/// library app's own components rather than reimagined.
///
/// What it is a port of, module by module (LIB is NPXS40071):
///
/// <list type="bullet">
/// <item>LIB m950 <c>InstalledScreen</c> — the screen's configuration: the six
/// sort options in their order, the console-storage cluster, and the
/// nothing-installed empty text.</item>
/// <item>LIB m70 <c>ContentScreen</c> — the screen itself: the grid item
/// wrapper sized to the tile, the row and column index each tile is told about,
/// the focus-changed plumbing, and which of the empty states is chosen.</item>
/// <item>LIB m365 <c>GridWrapper</c> — the grid maths:
/// <c>itemWidth * numColumns + paddingHorizontal * (numColumns - 1)</c>, and
/// the option container's offset by the header block.</item>
/// <item>LIB m366 — the sort control hanging in the left margin, and the
/// content it wraps.</item>
/// <item>LIB m927 <c>PaginatedGrid</c> — segmented sections, their headers, and
/// the section tail margin.</item>
/// <item>LIB m142 / m367 — the section header row: title left, sort summary
/// right in a 772 wide right-aligned slot.</item>
/// <item>LIB m931 <c>LibraryGridItem</c> and LIB m35 / m918 — the tile: the
/// OVERLAY template, a label that hides on glance, and the gradient that rises
/// under it on focus.</item>
/// <item>LIB m948 / m257 / m258 <c>LibraryEmptyGridComponent</c> — the empty
/// grid.</item>
/// </list>
///
/// The numbers all live in <see cref="ShellLibraryMetrics"/> and
/// <see cref="ShellLibraryGridGeometry"/> so the layout can be checked without a
/// window. What could not come across: the scroller itself
/// (<c>GridListViewPS</c>) and the tile's gradient are native, so their inner
/// behaviour is reconstructed from the parameters the JS hands them, and the
/// font sizes resolve through the recovered UI3 token ladder in
/// <see cref="Ps5FontScale"/>.
/// </summary>
public sealed class ShellAllGames : TemplatedControl
{
    // ---- Type scale ------------------------------------------------------
    // The library bundle names symbolic FontSizePS tokens. Their pixel values
    // are now recovered from UI3's managed UIFont constants; keep this surface
    // on that shared ladder rather than retaining its pre-recovery guesses.

    /// <summary>FontSizePS.SizeLarge.</summary>
    internal const double SizeLarge = Ps5FontScale.SizeLarge;

    /// <summary>FontSizePS.SizeNormal.</summary>
    internal const double SizeNormal = Ps5FontScale.SizeNormal;

    /// <summary>FontSizePS.SizeXSmall.</summary>
    internal const double SizeXSmall = Ps5FontScale.SizeXSmall;

    /// <summary>FontSizePS.Size2XSmall.</summary>
    internal const double Size2XSmall = Ps5FontScale.Size2XSmall;

    /// <summary>FontSizePS.Size3XSmall.</summary>
    internal const double Size3XSmall = Ps5FontScale.Size3XSmall;

    // ---- Palette ---------------------------------------------------------
    // The tile surface palette, exactly as the tile module declares it. No
    // fill, border or shadow is added on top: the tile stylesheet is exhaustive
    // about what a tile carries and it carries none of those.

    /// <summary>COLOR.BLANK: the empty grid slot behind every tile.</summary>
    private static readonly IBrush BlankBrush = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255));

    /// <summary>COLOR.DARK_GREY: the mat behind a system asset or a missing
    /// cover.</summary>
    private static readonly IBrush DarkGreyBrush = new SolidColorBrush(Color.FromRgb(53, 53, 53));

    /// <summary>COLOR.GREY.</summary>
    private static readonly IBrush GreyBrush = new SolidColorBrush(Color.FromRgb(41, 41, 41));

    private static readonly IBrush TextBrush = new SolidColorBrush(Colors.White);

    /// <summary>
    /// The overlay gradient. The real one is a native basemat
    /// (<c>baseMatType: "overlay-gradient-tile"</c>) whose ramp is not in the
    /// JS, so the stops are ours; what is exact is that it exists only on the
    /// OVERLAY template, that it rises from the bottom under the metadata, and
    /// that it rests at one hundredth opacity and animates to full on focus.
    /// </summary>
    private static readonly IBrush OverlayGradientBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(230, 0, 0, 0), 0.0),
            new GradientStop(Color.FromArgb(140, 0, 0, 0), 0.28),
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.62),
        },
    };

    /// <summary>ANIMATION.TIMING.DEFAULT.</summary>
    private static readonly TimeSpan DefaultTiming =
        TimeSpan.FromMilliseconds(ShellLibraryMetrics.AnimationTimingDefault);

    /// <summary>
    /// The title band above the grid. The console's library carries its name in
    /// the hub header, which is a different component from the grid, so there is
    /// no recovered number for this; the band is ours and it collapses to
    /// nothing when no title is set, which is the bundle-exact configuration.
    /// </summary>
    private const double TitleBandHeight = 104.0;

    // ---- Styled properties -----------------------------------------------

    public static readonly StyledProperty<IEnumerable<ShellLibraryItem>?> ItemsProperty =
        AvaloniaProperty.Register<ShellAllGames, IEnumerable<ShellLibraryItem>?>(nameof(Items));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellAllGames, int>(nameof(SelectedIndex), defaultValue: -1);

    /// <summary>
    /// The cluster title, before its count is appended. The installed screen's
    /// internal-storage cluster is <c>msgid_console_storage_num</c>, a format
    /// with a <c>num</c> slot, which is why the count is not part of this.
    /// </summary>
    public static readonly StyledProperty<string> SectionTitleProperty =
        AvaloniaProperty.Register<ShellAllGames, string>(nameof(SectionTitle), "Console Storage");

    /// <summary>Optional screen title. Null keeps the surface exactly what the
    /// grid component draws.</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ShellAllGames, string?>(nameof(Title));

    public static readonly StyledProperty<bool> IsRegionFocusedProperty =
        AvaloniaProperty.Register<ShellAllGames, bool>(nameof(IsRegionFocused), defaultValue: true);

    /// <summary>Empty state header, <c>msgid_nothing_installed</c>.</summary>
    public static readonly StyledProperty<string> EmptyHeaderTextProperty =
        AvaloniaProperty.Register<ShellAllGames, string>(nameof(EmptyHeaderText), "Nothing installed");

    /// <summary>Empty state body, <c>msgid_installed_games_apps_appear</c>.</summary>
    public static readonly StyledProperty<string> EmptyMainTextProperty =
        AvaloniaProperty.Register<ShellAllGames, string>(
            nameof(EmptyMainText),
            "Installed games and apps appear here.");

    /// <summary>Empty state button. The console sends you to the store; a shell
    /// with no store sends you to the folder picker, which the host names.</summary>
    public static readonly StyledProperty<string> EmptyButtonTextProperty =
        AvaloniaProperty.Register<ShellAllGames, string>(nameof(EmptyButtonText), "Add game folder");

    // ---- Backing state ---------------------------------------------------

    private readonly List<ShellLibraryItem> _source = new();
    private readonly List<ShellLibraryItem> _sorted = new();
    private readonly Dictionary<int, TileVisual> _realised = new();
    private readonly List<ShellLibrarySection> _sections = new();

    private ShellLibrarySortOption _sort = ShellLibrarySort.InstallDateNewToOld;
    private Canvas? _content;
    private Border? _clip;
    private Canvas? _root;
    private TextBlock? _screenTitle;
    private TextBlock? _sectionTitle;
    private TextBlock? _sortHeader;
    private Border? _sortIcon;
    private Border? _sortPanel;
    private StackPanel? _sortPanelRows;
    private Border? _emptyHost;
    private TextBlock? _emptyHeader;
    private TextBlock? _emptyMain;
    private Button? _emptyButton;
    private double _scroll;
    private int _sortPanelIndex;
    private bool _focusPushQueued;

    public ShellAllGames()
    {
        Focusable = true;
        ClipToBounds = true;
        Template = BuildTemplate();
        GotFocus += (_, _) => SchedulePushFocusRect();
    }

    /// <summary>Raised whenever the focused tile changes.</summary>
    public event EventHandler<ShellLibraryItemEventArgs>? SelectionChanged;

    /// <summary>Raised on Enter or a double click over the focused tile.</summary>
    public event EventHandler<ShellLibraryItemEventArgs>? ItemActivated;

    /// <summary>Raised when the options key is pressed over the focused tile;
    /// the host owns the menu, exactly as the library app hands its item list to
    /// a native <c>OptionsMenuPS</c> and draws nothing itself.</summary>
    public event EventHandler<ShellLibraryItemEventArgs>? OptionsRequested;

    /// <summary>Raised on Escape, the surface's back edge.</summary>
    public event EventHandler? Closed;

    /// <summary>Raised when a new sort is chosen from the panel.</summary>
    public event EventHandler? SortChanged;

    /// <summary>Raised when the empty state's button is pressed.</summary>
    public event EventHandler? EmptyActionInvoked;

    public IEnumerable<ShellLibraryItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Index into the sorted list. -1 when the grid is empty.</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public string SectionTitle
    {
        get => GetValue(SectionTitleProperty);
        set => SetValue(SectionTitleProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsRegionFocused
    {
        get => GetValue(IsRegionFocusedProperty);
        set => SetValue(IsRegionFocusedProperty, value);
    }

    public string EmptyHeaderText
    {
        get => GetValue(EmptyHeaderTextProperty);
        set => SetValue(EmptyHeaderTextProperty, value);
    }

    public string EmptyMainText
    {
        get => GetValue(EmptyMainTextProperty);
        set => SetValue(EmptyMainTextProperty, value);
    }

    public string EmptyButtonText
    {
        get => GetValue(EmptyButtonTextProperty);
        set => SetValue(EmptyButtonTextProperty, value);
    }

    /// <summary>How many tiles the grid holds.</summary>
    public int Count => _sorted.Count;

    /// <summary>The tiles in the order the grid draws them.</summary>
    public IReadOnlyList<ShellLibraryItem> SortedItems => _sorted;

    /// <summary>The focused tile, or null.</summary>
    public ShellLibraryItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < _sorted.Count ? _sorted[SelectedIndex] : null;

    /// <summary>The sort the grid is ordered on.</summary>
    public ShellLibrarySortOption Sort
    {
        get => _sort;
        set
        {
            if (ReferenceEquals(_sort, value) || value is null)
            {
                return;
            }

            _sort = value;
            ApplySort(keepSelection: true);
        }
    }

    /// <summary>getSortFilterHeader for the current selection: the string the
    /// section header shows on its right.</summary>
    public string SortHeaderText => ShellLibrarySort.GetSortFilterHeader(_sort);

    /// <summary>The section header's left text, the cluster title with its
    /// count folded into the <c>num</c> slot.</summary>
    public string SectionHeaderText => $"{SectionTitle} ({_sorted.Count})";

    /// <summary>True while the sort panel is open.</summary>
    public bool IsSortPanelOpen => _sortPanel?.IsVisible == true;

    /// <summary>Grid scroll, in content pixels.</summary>
    public double ScrollOffset => _scroll;

    /// <summary>The grid's declared sections. One cluster, the way an installed
    /// screen with no external storage attached reports one.</summary>
    public IReadOnlyList<ShellLibrarySection> Sections => _sections;

    /// <summary>The layout the surface runs on.</summary>
    public ShellLibraryGridGeometry Geometry => ShellLibraryGridGeometry.Library;

    /// <summary>
    /// The focused tile's rect in the surface's own coordinates, computed from
    /// the grid maths. This is what the focus ring frames.
    /// </summary>
    public Rect? FocusHighlightRect
    {
        get
        {
            if (SelectedIndex < 0 || SelectedIndex >= _sorted.Count)
            {
                return null;
            }

            var geometry = Geometry;
            return new Rect(
                ShellLibraryMetrics.ContainerMarginHorizontal + geometry.ItemLeft(SelectedIndex),
                GridTop + geometry.ItemTop(_sections, 0, SelectedIndex) - _scroll,
                geometry.ItemWidth,
                geometry.ItemHeight);
        }
    }

    /// <summary>Height available to the grid below the title band.</summary>
    public double ViewportHeight => Math.Max(0.0, Bounds.Height - GridTop);

    /// <summary>Y the grid container starts at inside the surface.</summary>
    public double GridTop => string.IsNullOrEmpty(Title) ? 0.0 : TitleBandHeight;

    /// <summary>
    /// Moves focus one step. Left and right walk the row and roll into the
    /// neighbouring row at its ends, up and down move a whole row; the grid
    /// clamps at both ends rather than wrapping, which is the shell's rule
    /// everywhere.
    /// </summary>
    public bool MoveFocus(ShellFocusDirection direction)
    {
        if (_sorted.Count == 0)
        {
            return false;
        }

        int columns = Geometry.NumColumns;
        int index = SelectedIndex < 0 ? 0 : SelectedIndex;
        int next = direction switch
        {
            ShellFocusDirection.Left => index - 1,
            ShellFocusDirection.Right => index + 1,
            ShellFocusDirection.Up => index - columns,
            _ => index + columns,
        };

        // Down off the last partial row lands on the last tile rather than
        // nowhere, so the bottom row is reachable from any column.
        if (direction == ShellFocusDirection.Down && next >= _sorted.Count && index < _sorted.Count - 1)
        {
            next = _sorted.Count - 1;
        }

        if (next < 0 || next >= _sorted.Count)
        {
            return false;
        }

        return SetSelectedIndex(next);
    }

    /// <summary>Focuses a tile, clamped into range. Returns true when the
    /// selection actually moved.</summary>
    public bool SetSelectedIndex(int index)
    {
        if (_sorted.Count == 0)
        {
            if (SelectedIndex == -1)
            {
                return false;
            }

            SetCurrentValue(SelectedIndexProperty, -1);
            return true;
        }

        int clamped = Math.Clamp(index, 0, _sorted.Count - 1);
        if (clamped == SelectedIndex)
        {
            return false;
        }

        SetCurrentValue(SelectedIndexProperty, clamped);
        ShellUiSounds.Play(UiSoundEvent.FocusMove);
        return true;
    }

    /// <summary>Fires <see cref="ItemActivated"/> for the focused tile.</summary>
    public void ActivateSelected()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);
        ItemActivated?.Invoke(this, new ShellLibraryItemEventArgs(SelectedIndex, item));
    }

    /// <summary>Opens or closes the sort panel.</summary>
    public void SetSortPanelOpen(bool open)
    {
        if (_sortPanel is null || _sortPanel.IsVisible == open)
        {
            return;
        }

        _sortPanel.IsVisible = open;
        if (open)
        {
            _sortPanelIndex = Math.Max(
                0,
                ShellLibrarySort.InstalledScreenOptions.ToList().FindIndex(o => ReferenceEquals(o, _sort)));
            UpdateSortPanelRows();
        }

        SchedulePushFocusRect();
    }

    /// <summary>Re-reads <see cref="Items"/> and re-sorts. The host calls this
    /// after a rescan when the collection instance did not change.</summary>
    public void Refresh()
    {
        RebuildItems();
    }

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _root = e.NameScope.Find<Canvas>("PART_Root");
        _clip = e.NameScope.Find<Border>("PART_Clip");
        _content = e.NameScope.Find<Canvas>("PART_Content");
        _screenTitle = e.NameScope.Find<TextBlock>("PART_ScreenTitle");
        _sectionTitle = e.NameScope.Find<TextBlock>("PART_SectionTitle");
        _sortHeader = e.NameScope.Find<TextBlock>("PART_SortHeader");
        _sortIcon = e.NameScope.Find<Border>("PART_SortIcon");
        _sortPanel = e.NameScope.Find<Border>("PART_SortPanel");
        _sortPanelRows = e.NameScope.Find<StackPanel>("PART_SortPanelRows");
        _emptyHost = e.NameScope.Find<Border>("PART_Empty");
        _emptyHeader = e.NameScope.Find<TextBlock>("PART_EmptyHeader");
        _emptyMain = e.NameScope.Find<TextBlock>("PART_EmptyMain");
        _emptyButton = e.NameScope.Find<Button>("PART_EmptyButton");

        if (_sortIcon is not null)
        {
            _sortIcon.PointerPressed += (_, _) => SetSortPanelOpen(!IsSortPanelOpen);
        }

        if (_emptyButton is not null)
        {
            _emptyButton.Click += (_, _) => EmptyActionInvoked?.Invoke(this, EventArgs.Empty);
        }

        BuildSortPanelRows();
        RebuildItems();
        UpdateLayoutMetrics();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsProperty)
        {
            RebuildItems();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            OnSelectionChanged();
        }
        else if (change.Property == SectionTitleProperty)
        {
            UpdateHeaderText();
        }
        else if (change.Property == TitleProperty)
        {
            UpdateLayoutMetrics();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateLayoutMetrics();
        }
        else if (change.Property == IsRegionFocusedProperty)
        {
            SchedulePushFocusRect();
        }
        else if (change.Property == EmptyHeaderTextProperty ||
                 change.Property == EmptyMainTextProperty ||
                 change.Property == EmptyButtonTextProperty)
        {
            UpdateEmptyState();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsSortPanelOpen)
        {
            if (HandleSortPanelKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            return;
        }

        int columns = Geometry.NumColumns;
        int page = columns * ShellLibraryMetrics.NumVisibleRows;

        switch (e.Key)
        {
            case Key.Left:
                e.Handled = MoveFocus(ShellFocusDirection.Left);
                break;
            case Key.Right:
                e.Handled = MoveFocus(ShellFocusDirection.Right);
                break;
            case Key.Up:
                e.Handled = MoveFocus(ShellFocusDirection.Up);
                break;
            case Key.Down:
                e.Handled = MoveFocus(ShellFocusDirection.Down);
                break;
            case Key.PageUp:
                e.Handled = SetSelectedIndex(SelectedIndex - page);
                break;
            case Key.PageDown:
                e.Handled = SetSelectedIndex(SelectedIndex + page);
                break;
            case Key.Home:
                e.Handled = SetSelectedIndex(0);
                break;
            case Key.End:
                e.Handled = SetSelectedIndex(_sorted.Count - 1);
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Apps:
                if (SelectedItem is { } item)
                {
                    OptionsRequested?.Invoke(this, new ShellLibraryItemEventArgs(SelectedIndex, item));
                    e.Handled = true;
                }

                break;
            case Key.Escape:
            case Key.Back:
                Closed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_sorted.Count == 0)
        {
            return;
        }

        // A wheel notch is a row, which is how a grid this size reads under a
        // pointer; the selection follows so the ring never leaves the screen.
        int delta = e.Delta.Y > 0 ? -Geometry.NumColumns : Geometry.NumColumns;
        if (SetSelectedIndex((SelectedIndex < 0 ? 0 : SelectedIndex) + delta))
        {
            e.Handled = true;
        }
    }

    // ---- Items -----------------------------------------------------------

    private void RebuildItems()
    {
        _source.Clear();
        if (Items is { } source)
        {
            _source.AddRange(source);
        }

        ApplySort(keepSelection: false);
    }

    private void ApplySort(bool keepSelection)
    {
        var previous = keepSelection ? SelectedItem : null;

        _sorted.Clear();
        _sorted.AddRange(SortItems(_source, _sort));

        _sections.Clear();
        _sections.Add(new ShellLibrarySection(SectionTitle, _sorted.Count));

        int index = -1;
        if (_sorted.Count > 0)
        {
            index = previous is null ? 0 : Math.Max(0, _sorted.IndexOf(previous));
        }

        // Setting the property fires OnSelectionChanged, which rebuilds the
        // visuals; when the index does not change we still have to.
        if (SelectedIndex == index)
        {
            OnSelectionChanged();
        }
        else
        {
            SetCurrentValue(SelectedIndexProperty, index);
        }

        UpdateHeaderText();
        UpdateEmptyState();
    }

    /// <summary>The screen's own ordering, on the sort's field and direction.</summary>
    internal static IEnumerable<ShellLibraryItem> SortItems(
        IReadOnlyList<ShellLibraryItem> items,
        ShellLibrarySortOption sort)
    {
        IEnumerable<ShellLibraryItem> ordered = sort.Field switch
        {
            ShellLibrarySortField.Alpha => items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            ShellLibrarySortField.FileSize => items.OrderBy(i => i.SizeBytes),
            _ => items.OrderBy(i => i.InstalledAt),
        };

        return sort.Direction == ShellLibrarySortDirection.Descending ? ordered.Reverse() : ordered;
    }

    private void OnSelectionChanged()
    {
        UpdateScroll();
        RealiseVisibleTiles();
        UpdateTileStates();
        SchedulePushFocusRect();
        SelectionChanged?.Invoke(this, new ShellLibraryItemEventArgs(SelectedIndex, SelectedItem));
    }

    // ---- Layout ----------------------------------------------------------

    private void UpdateLayoutMetrics()
    {
        if (_root is null || _clip is null)
        {
            return;
        }

        double gridTop = GridTop;

        if (_screenTitle is not null)
        {
            _screenTitle.Text = Title ?? string.Empty;
            _screenTitle.IsVisible = !string.IsNullOrEmpty(Title);
            Canvas.SetLeft(_screenTitle, ShellLibraryMetrics.ContainerMarginHorizontal);
            Canvas.SetTop(_screenTitle, TitleBandHeight / 2.0 - SizeLarge);
        }

        Canvas.SetLeft(_clip, ShellLibraryMetrics.ContainerMarginHorizontal);
        Canvas.SetTop(_clip, gridTop);
        _clip.Width = ShellLibraryMetrics.ContainerWidth;
        _clip.Height = ViewportHeight;

        if (_sortIcon is not null)
        {
            // optionContainer sits at left -120 relative to the 1576 container,
            // and its top is overridden by the grid to clear the header block:
            // paddingVertical + segment header + its bottom margin.
            Canvas.SetLeft(
                _sortIcon,
                ShellLibraryMetrics.ContainerMarginHorizontal + ShellLibraryMetrics.OptionContainerLeft);
            Canvas.SetTop(_sortIcon, gridTop + Geometry.FirstRowTop);
        }

        if (_sortPanel is not null)
        {
            Canvas.SetLeft(
                _sortPanel,
                ShellLibraryMetrics.ContainerMarginHorizontal + ShellLibraryMetrics.OptionContainerLeft);
            Canvas.SetTop(_sortPanel, gridTop + Geometry.FirstRowTop + ShellLibraryMetrics.SortIconSide);
        }

        if (_emptyHost is not null)
        {
            _emptyHost.Height = Math.Min(ViewportHeight, ShellLibraryMetrics.EmptyContainerHeight);
            Canvas.SetLeft(_emptyHost, ShellLibraryMetrics.ContainerMarginHorizontal);
            Canvas.SetTop(_emptyHost, gridTop);
        }

        UpdateScroll();
        RealiseVisibleTiles();
        UpdateTileStates();
        SchedulePushFocusRect();
    }

    private void UpdateScroll()
    {
        if (_sorted.Count == 0 || SelectedIndex < 0)
        {
            _scroll = 0.0;
        }
        else
        {
            _scroll = Geometry.ScrollFor(
                _sections,
                0,
                SelectedIndex,
                ViewportHeight,
                ShellLibraryMetrics.GridActiveAreaOffset);
        }

        PositionChrome();
    }

    /// <summary>Places the section header, which scrolls with the content.</summary>
    private void PositionChrome()
    {
        if (_sectionTitle is not null)
        {
            Canvas.SetLeft(_sectionTitle, 0);
            Canvas.SetTop(_sectionTitle, Geometry.PaddingVertical - _scroll);
        }

        if (_sortHeader is not null)
        {
            // sortFilterTitle: a 772 wide right-aligned slot pushed to the end
            // of the 1576 header row by marginLeft auto.
            Canvas.SetLeft(_sortHeader, ShellLibraryMetrics.ContainerWidth - 772.0);
            Canvas.SetTop(_sortHeader, Geometry.PaddingVertical - _scroll);
        }
    }

    private void UpdateHeaderText()
    {
        if (_sectionTitle is not null)
        {
            _sectionTitle.Text = SectionHeaderText;
            _sectionTitle.IsVisible = _sorted.Count > 0;
        }

        if (_sortHeader is not null)
        {
            _sortHeader.Text = SortHeaderText;
            _sortHeader.IsVisible = _sorted.Count > 0;
        }
    }

    private void UpdateEmptyState()
    {
        bool empty = _sorted.Count == 0;

        if (_emptyHost is not null)
        {
            _emptyHost.IsVisible = empty;
        }

        if (_clip is not null)
        {
            _clip.IsVisible = !empty;
        }

        if (_sortIcon is not null)
        {
            // showUtilities: the sort control is hidden when the grid is empty
            // and nothing has been filtered away.
            _sortIcon.IsVisible = !empty;
        }

        if (_emptyHeader is not null)
        {
            _emptyHeader.Text = EmptyHeaderText;
        }

        if (_emptyMain is not null)
        {
            _emptyMain.Text = EmptyMainText;
        }

        if (_emptyButton is not null)
        {
            _emptyButton.Content = EmptyButtonText;
        }
    }

    // ---- Tiles -----------------------------------------------------------

    /// <summary>
    /// Realises the tiles whose rows touch the viewport and drops the rest. The
    /// library app does the same thing for the same reason: it pages its data in
    /// around the focused row and keeps a fixed working set, which is what makes
    /// a two hundred title grid cost the same as a twelve title one.
    /// </summary>
    private void RealiseVisibleTiles()
    {
        if (_content is null)
        {
            return;
        }

        var geometry = Geometry;
        double firstRowTop = geometry.FirstRowTop;
        double rowPitch = geometry.RowPitch;

        int firstRow = Math.Max(0, (int)Math.Floor((_scroll - firstRowTop) / rowPitch) - 1);
        int lastRow = (int)Math.Ceiling((_scroll + ViewportHeight - firstRowTop) / rowPitch) + 1;

        int first = firstRow * geometry.NumColumns;
        int last = Math.Min(_sorted.Count - 1, ((lastRow + 1) * geometry.NumColumns) - 1);

        foreach (int index in _realised.Keys.ToList())
        {
            if (index < first || index > last || index >= _sorted.Count)
            {
                _content.Children.Remove(_realised[index].Root);
                _realised.Remove(index);
            }
        }

        for (int i = Math.Max(0, first); i <= last; i++)
        {
            if (!_realised.TryGetValue(i, out var tile))
            {
                tile = CreateTile(_sorted[i], i);
                _realised[i] = tile;
                _content.Children.Add(tile.Root);
            }

            Canvas.SetLeft(tile.Root, geometry.ItemLeft(i));
            Canvas.SetTop(tile.Root, geometry.ItemTop(_sections, 0, i) - _scroll);
        }
    }

    private void UpdateTileStates()
    {
        foreach (var (index, tile) in _realised)
        {
            bool focused = index == SelectedIndex && IsRegionFocused;

            // The overlay gradient rests at ANIMATION.OPACITY.GRADIENT.MIN and
            // runs to MAX on focus; the label hides on glance, which is what
            // keeps a resting grid pure cover art.
            tile.Gradient.Opacity = focused
                ? ShellLibraryMetrics.GradientOpacityMax
                : ShellLibraryMetrics.GradientOpacityMin;
            tile.Label.IsVisible = focused;
            tile.Attributes.IsVisible = focused && tile.Attributes.Children.Count > 0;
            tile.SubLabel.IsVisible = focused && !string.IsNullOrEmpty(tile.SubLabel.Text);
        }
    }

    private TileVisual CreateTile(ShellLibraryItem item, int index)
    {
        double side = ShellLibraryMetrics.LibraryTileWidth;

        Control media = item.Icon is { } icon
            ? new Image { Source = icon, Stretch = Stretch.UniformToFill }
            : BuildFallbackMedia();

        var gradient = new Border
        {
            Background = OverlayGradientBrush,
            Opacity = ShellLibraryMetrics.GradientOpacityMin,
            Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = DefaultTiming,
                },
            },
        };

        var attributes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = ShellLibraryMetrics.TileAttributeHeight,
            IsVisible = false,
        };
        foreach (var attribute in item.Attributes)
        {
            attributes.Children.Add(new TextBlock
            {
                Text = attribute,
                FontSize = Size3XSmall,
                Foreground = TextBrush,
                Opacity = ShellLibraryMetrics.SubLabelOpacity,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, ShellLibraryMetrics.TileStatusIconMargin, 0),
            });
        }

        var label = new TextBlock
        {
            Text = item.Title,
            FontSize = Size2XSmall,
            Foreground = TextBrush,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsVisible = false,
        };

        var subLabel = new TextBlock
        {
            Text = item.SubLabel ?? string.Empty,
            FontSize = Size3XSmall,
            Foreground = TextBrush,
            Opacity = ShellLibraryMetrics.SubLabelOpacity,
            Margin = new Thickness(0, ShellLibraryMetrics.TileSecondaryPadding, 0, 0),
            IsVisible = false,
        };

        // overlayContainer: the metadata column is pinned to the bottom of the
        // art (justifyContent space-between with overlayMeta's marginTop auto)
        // and inset by the preset's primary padding.
        var overlayMeta = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(ShellLibraryMetrics.TilePrimaryPadding),
            Children = { attributes, label, subLabel },
        };

        var root = new Border
        {
            Width = side,
            Height = side,
            CornerRadius = new CornerRadius(ShellLibraryMetrics.TileCornerRadius),
            ClipToBounds = true,

            // The blank grid slot the tile is drawn into. No fill, border or
            // shadow beyond this: the tile stylesheet declares none.
            Background = BlankBrush,
            Child = new Panel { Children = { media, gradient, overlayMeta } },
        };

        int captured = index;
        root.PointerEntered += (_, _) => SetSelectedIndex(captured);
        root.PointerPressed += (_, args) =>
        {
            Focus();
            SetSelectedIndex(captured);
            SetRingPressed(true);
            if (args.ClickCount >= 2)
            {
                ActivateSelected();
            }
        };
        root.PointerReleased += (_, _) => SetRingPressed(false);
        root.PointerExited += (_, _) => SetRingPressed(false);

        return new TileVisual(root, gradient, label, subLabel, attributes);
    }

    /// <summary>
    /// media_fallbackIcon: a neutral mark at the preset's 64 square on the
    /// tile palette's dark grey. Deliberately not a coloured placeholder — the
    /// console's missing-art tile is neutral and carries no initials.
    /// </summary>
    private static Control BuildFallbackMedia()
    {
        double icon = ShellLibraryMetrics.TileFallbackIconSide;
        var mark = new Panel
        {
            Width = icon,
            Height = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Rectangle
                {
                    Width = icon,
                    Height = icon * 0.72,
                    RadiusX = 6,
                    RadiusY = 6,
                    Stroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    StrokeThickness = 3,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        return new Panel
        {
            Background = DarkGreyBrush,
            Children = { mark },
        };
    }

    // ---- Sort panel ------------------------------------------------------

    private void BuildSortPanelRows()
    {
        if (_sortPanelRows is null)
        {
            return;
        }

        _sortPanelRows.Children.Clear();
        for (int i = 0; i < ShellLibrarySort.InstalledScreenOptions.Count; i++)
        {
            var option = ShellLibrarySort.InstalledScreenOptions[i];
            int captured = i;

            var checkmark = new TextBlock
            {
                Text = "✓",
                FontSize = SizeXSmall,
                Foreground = TextBrush,
                Width = ShellLibraryMetrics.SortCheckmarkWidth,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ShellLibraryMetrics.SortCheckmarkMargin, 0),
                Opacity = 0,
            };

            var row = new Border
            {
                Height = ShellLibraryMetrics.SortOptionHeight,
                MinWidth = ShellLibraryMetrics.SortOptionMinWidth,
                Background = Brushes.Transparent,
                Child = new DockPanel
                {
                    Children =
                    {
                        checkmark,
                        new TextBlock
                        {
                            Text = option.Label,
                            FontSize = SizeXSmall,
                            Foreground = TextBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Tag = checkmark,
            };

            row.PointerEntered += (_, _) =>
            {
                _sortPanelIndex = captured;
                UpdateSortPanelRows();
            };
            row.PointerPressed += (_, _) => ChooseSort(captured);
            _sortPanelRows.Children.Add(row);
        }
    }

    private void UpdateSortPanelRows()
    {
        if (_sortPanelRows is null)
        {
            return;
        }

        for (int i = 0; i < _sortPanelRows.Children.Count; i++)
        {
            if (_sortPanelRows.Children[i] is not Border row)
            {
                continue;
            }

            var option = ShellLibrarySort.InstalledScreenOptions[i];
            if (row.Tag is TextBlock checkmark)
            {
                checkmark.Opacity = ReferenceEquals(option, _sort) ? 1 : 0;
            }

            // Focus in the panel is the same travelling ring as everywhere
            // else, so a hovered row gets no fill of its own.
            row.BorderThickness = new Thickness(0);
        }

        SchedulePushFocusRect();
    }

    private bool HandleSortPanelKey(Key key)
    {
        int count = ShellLibrarySort.InstalledScreenOptions.Count;
        switch (key)
        {
            case Key.Up:
                _sortPanelIndex = Math.Max(0, _sortPanelIndex - 1);
                UpdateSortPanelRows();
                return true;
            case Key.Down:
                _sortPanelIndex = Math.Min(count - 1, _sortPanelIndex + 1);
                UpdateSortPanelRows();
                return true;
            case Key.Enter:
            case Key.Space:
                ChooseSort(_sortPanelIndex);
                return true;
            case Key.Escape:
            case Key.Back:
            case Key.Left:
                SetSortPanelOpen(false);
                return true;
            default:
                return false;
        }
    }

    private void ChooseSort(int index)
    {
        if (index < 0 || index >= ShellLibrarySort.InstalledScreenOptions.Count)
        {
            return;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);
        Sort = ShellLibrarySort.InstalledScreenOptions[index];
        UpdateHeaderText();
        SetSortPanelOpen(false);
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Focus ring ------------------------------------------------------

    private void SchedulePushFocusRect()
    {
        if (_focusPushQueued)
        {
            return;
        }

        _focusPushQueued = true;
        try
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    _focusPushQueued = false;
                    PushFocusRect();
                },
                DispatcherPriority.Render);
        }
        catch
        {
            // No dispatcher (a pure logic host): the ring is simply not driven.
            _focusPushQueued = false;
        }
    }

    /// <summary>Retargets the scene's single focus ring onto the focused tile,
    /// or onto the open sort panel's row.</summary>
    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (!IsRegionFocused || !IsEffectivelyVisible)
            {
                ring.Release(this);
                return;
            }

            Rect? local = null;
            if (IsSortPanelOpen && _sortPanelRows is not null &&
                _sortPanelIndex >= 0 && _sortPanelIndex < _sortPanelRows.Children.Count)
            {
                if (_sortPanelRows.Children[_sortPanelIndex] is Control row &&
                    row.TranslatePoint(default, this) is { } origin)
                {
                    local = new Rect(origin, row.Bounds.Size);
                }
            }
            else if (SelectedIndex >= 0 && SelectedIndex < _sorted.Count)
            {
                // Straight off the grid maths rather than off the tile's own
                // arranged bounds: a tile realised during a scroll has not been
                // through a layout pass yet, so its visual position is a frame
                // behind and the ring would frame where the tile used to be.
                local = FocusHighlightRect;
            }

            if (local is not { } rect || this.TransformToVisual(ring) is not { } transform)
            {
                ring.Release(this);
                return;
            }

            ring.Radius = ShellLibraryMetrics.TileCornerRadius;
            ring.Claim(this, rect.TransformToAABB(transform));
        }
        catch
        {
            // A detached or half-built tree just leaves the ring where it is.
        }
    }

    private void SetRingPressed(bool pressed)
    {
        try
        {
            if (IsRegionFocused && ShellFocusRing.For(this) is { } ring && ReferenceEquals(ring.Owner, this))
            {
                ring.SetPressed(pressed);
            }
        }
        catch
        {
            // Decoration only.
        }
    }

    // ---- Template --------------------------------------------------------

    private static FuncControlTemplate BuildTemplate() => new((_, scope) =>
    {
        var content = new Canvas { Name = "PART_Content" }.RegisterInNameScope(scope);

        var sectionTitle = new TextBlock
        {
            Name = "PART_SectionTitle",
            FontSize = SizeXSmall,
            Height = ShellLibraryMetrics.SegmentHeaderHeight,
            Foreground = TextBrush,
        }.RegisterInNameScope(scope);

        var sortHeader = new TextBlock
        {
            Name = "PART_SortHeader",
            FontSize = SizeXSmall,
            Height = ShellLibraryMetrics.SegmentHeaderHeight,
            Width = 772.0,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = TextBrush,
        }.RegisterInNameScope(scope);

        content.Children.Add(sectionTitle);
        content.Children.Add(sortHeader);

        var clip = new Border
        {
            Name = "PART_Clip",
            ClipToBounds = true,
            Background = Brushes.Transparent,
            Child = content,
        }.RegisterInNameScope(scope);

        var screenTitle = new TextBlock
        {
            Name = "PART_ScreenTitle",
            FontSize = SizeLarge,
            Foreground = TextBrush,
            IsVisible = false,
        }.RegisterInNameScope(scope);

        // The sort control: a 72 square in the left margin, level with the
        // first row of tiles. The mark is the console's own sort glyph in kind,
        // a stack of shortening rules, drawn rather than borrowed because the
        // dump ships it vector-only.
        var sortIcon = new Border
        {
            Name = "PART_SortIcon",
            Width = ShellLibraryMetrics.SortIconSide,
            Height = ShellLibraryMetrics.SortIconSide,
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 7,
                Children =
                {
                    new Rectangle { Width = 30, Height = 3, Fill = TextBrush },
                    new Rectangle { Width = 22, Height = 3, Fill = TextBrush, HorizontalAlignment = HorizontalAlignment.Center },
                    new Rectangle { Width = 14, Height = 3, Fill = TextBrush, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        }.RegisterInNameScope(scope);

        var sortRows = new StackPanel { Name = "PART_SortPanelRows" }.RegisterInNameScope(scope);

        var sortPanel = new Border
        {
            Name = "PART_SortPanel",
            IsVisible = false,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(8, 10, 15)),
            Child = new StackPanel
            {
                Children =
                {
                    // SectionHeaderPS with showSeparator false: the panel names
                    // itself and draws no rule under the name.
                    new TextBlock
                    {
                        Text = "Sort by",
                        FontSize = SizeXSmall,
                        Foreground = TextBrush,
                        Opacity = ShellLibraryMetrics.SubLabelOpacity,
                        Margin = new Thickness(
                            ShellLibraryMetrics.SortOptionLeadingGutter,
                            ShellLibraryMetrics.TilePrimaryPadding,
                            ShellLibraryMetrics.TilePrimaryPadding,
                            ShellLibraryMetrics.TileSecondaryPadding),
                    },
                    sortRows,
                },
            },
        }.RegisterInNameScope(scope);

        var emptyHeader = new TextBlock
        {
            Name = "PART_EmptyHeader",
            FontSize = SizeLarge,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        }.RegisterInNameScope(scope);

        var emptyMain = new TextBlock
        {
            Name = "PART_EmptyMain",
            FontSize = SizeNormal,
            Foreground = TextBrush,
            Opacity = ShellLibraryMetrics.SubLabelOpacity,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(
                0,
                ShellLibraryMetrics.EmptyMainTextMarginTop,
                0,
                ShellLibraryMetrics.EmptyMainTextMarginBottom),
        }.RegisterInNameScope(scope);

        var emptyButton = new Button
        {
            Name = "PART_EmptyButton",
            MinWidth = ShellLibraryMetrics.EmptyButtonMinWidth,
            MaxWidth = ShellLibraryMetrics.EmptyButtonMaxWidth,
            Height = 72.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = SizeXSmall,
        }.RegisterInNameScope(scope);
        emptyButton.Classes.Add("ps5EmptyAction");

        // ErrorView: an inner column centred in the whole content band, both
        // ways. The band is 1576 by 824 with the shell's own margins.
        var empty = new Border
        {
            Name = "PART_Empty",
            IsVisible = false,
            Width = ShellLibraryMetrics.ContainerWidth,
            Child = new StackPanel
            {
                Width = ShellLibraryMetrics.EmptyInnerWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { emptyHeader, emptyMain, emptyButton },
            },
        }.RegisterInNameScope(scope);

        var root = new Canvas { Name = "PART_Root" }.RegisterInNameScope(scope);
        root.Children.Add(screenTitle);
        root.Children.Add(clip);
        root.Children.Add(empty);
        root.Children.Add(sortIcon);
        root.Children.Add(sortPanel);
        return root;
    });

    /// <summary>The mutable parts of one realised tile.</summary>
    private sealed record TileVisual(
        Border Root,
        Border Gradient,
        TextBlock Label,
        TextBlock SubLabel,
        StackPanel Attributes);
}
