// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

// Implicit usings pull in System.IO, whose Path collides with the shape.
using Path = Avalonia.Controls.Shapes.Path;

namespace Prosperismo.GUI.Controls;

/// <summary>The three system destinations the home band carries, in the order
/// the bundle lists them (HOME m217 <c>iconNames</c>, HOME m624).</summary>
public enum ShellSystemDestination
{
    /// <summary><c>pssearch:main</c>, iconIndex 3.</summary>
    Search,

    /// <summary><c>pssettings:play?mode=settings</c>, iconIndex 4.</summary>
    Settings,

    /// <summary>The profile popover; no deep link, it opens a modal in place.</summary>
    Profile,
}

/// <summary>Raised when a system icon is pressed.</summary>
public sealed class ShellSystemEventArgs : EventArgs
{
    public ShellSystemEventArgs(ShellSystemDestination destination, string link)
    {
        Destination = destination;
        Link = link;
    }

    public ShellSystemDestination Destination { get; }

    /// <summary>The console's deep link for this destination, kept so the
    /// mapping to our own pages stays visible at the call site.</summary>
    public string Link { get; }
}

/// <summary>Raised when a space label is pressed.</summary>
public sealed class ShellSpaceEventArgs : EventArgs
{
    public ShellSpaceEventArgs(string spaceId, int index)
    {
        SpaceId = spaceId;
        Index = index;
    }

    /// <summary><c>game</c> or <c>media</c> (HOME m56 <c>spaceIds</c>).</summary>
    public string SpaceId { get; }

    public int Index { get; }
}

/// <summary>
/// The home shell's 126 px top navigation band: the space switcher on the left,
/// the three system icons and the clock on the right.
///
/// Ported from the home bundle rather than reimplemented from a description.
/// The pieces and where they come from:
///
/// <list type="bullet">
/// <item><c>System/index.tsx</c> (HOME m217): the <c>home-system</c> focus
/// region, its edge contract, and <c>systemIconsCount = 3</c>.</item>
/// <item><c>SpaceSwitcher/index.tsx</c> (HOME m813) and its stylesheet
/// (HOME m815): the two space labels, the selected/unselected treatment, and
/// the <c>space-switcher</c> region.</item>
/// <item><c>SystemIcon/index.tsx</c> (HOME m624): the deep links behind each
/// icon.</item>
/// <item><c>SystemIcon/iconAndText.tsx</c> (HOME m224) and its stylesheet
/// (HOME m143): the icon box, the glance label strip, and the sizes.</item>
/// <item><c>useTextAnimation</c> (HOME m673): the glance state machine, which
/// lives in <see cref="ShellGlanceState"/>.</item>
/// <item>The System stylesheet (HOME m96): the 126 band height and the clock
/// gutter.</item>
/// </list>
///
/// The band lays out in design units. <c>LibraryPage</c> is pinned to
/// 1920 x 1080 and the surrounding Viewbox does the scaling, so the authored
/// numbers land unscaled here and the focus rect is mapped through
/// <c>TransformToVisual</c> like every other row's.
/// </summary>
public sealed class ShellNavBand : TemplatedControl
{
    // ---- Geometry, all EXACT from the bundle -------------------------------

    /// <summary>Band height (<c>SYSTEM_HEIGHT</c>, HOME m96).</summary>
    public const double SystemHeight = 126.0;

    /// <summary>Icon box side (<c>SYSTEM_ICON_SIZE</c>, HOME m143).</summary>
    public const double SystemIconSize = 56.0;

    /// <summary>
    /// Icon side before the glance grows it (<c>SYSTEM_ICON_SIZE_NO_GLANCE</c>,
    /// HOME m143). The glance interpolates the scale between the ratio of these
    /// two and 1, so an un-glanced icon is drawn at 48 inside its 56 box.
    /// </summary>
    public const double SystemIconSizeNoGlance = 48.0;

    /// <summary>Leading margin on every icon container (HOME m143
    /// <c>iconContainer.marginLeft</c>), which is what makes the pitch 104.</summary>
    public const double IconMarginLeft = 48.0;

    /// <summary>Distance from one icon's left edge to the next: 56 + 48.</summary>
    public const double IconPitch = SystemIconSize + IconMarginLeft;

    /// <summary>Top of the glance label strip, relative to the icon box
    /// (HOME m143 <c>iconTextContainer.top</c> 56 plus <c>marginTop</c> 4).</summary>
    public const double IconTextTop = 60.0;

    /// <summary>Width of the glance label strip (HOME m143). Far wider than the
    /// icon: the strip is centred on the box so a long label spills evenly
    /// either side instead of wrapping.</summary>
    public const double IconTextWidth = 368.0;

    /// <summary>Clock gutter (HOME m96 <c>clockWrapper.marginLeft</c>).</summary>
    public const double ClockMarginLeft = 88.0;

    /// <summary>Gap after each space label (HOME m815 <c>spaceSwitcherItem</c>).</summary>
    public const double SpaceItemMarginRight = 64.0;

    /// <summary>Padding inside a space label (HOME m815).</summary>
    public const double SpaceItemPadding = 8.0;

    /// <summary>Opacity of the space that is not selected (HOME m815
    /// <c>spaceSwitcherItemBlur</c>). One of the few opacity rules in the shell
    /// that is a real literal rather than an inference.</summary>
    public const double UnselectedSpaceOpacity = 0.6;

    /// <summary>Content inset either side of the band.</summary>
    public const double ContentInset = 84.0;

    /// <summary>How many system icons the band carries (HOME m217
    /// <c>systemIconsCount</c>).</summary>
    public const int SystemIconsCount = 3;

    // ---- Focus region names, EXACT -----------------------------------------

    /// <summary>The space switcher's region name (HOME m813).</summary>
    public const string SpaceSwitcherRegion = "space-switcher";

    /// <summary>The system cluster's region name (HOME m217).</summary>
    public const string SystemRegion = "home-system";

    /// <summary>
    /// The console's own spaces, in order (HOME m56 <c>spaceIds</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> ConsoleSpaceIds = new[] { "game", "media" };

    /// <summary>
    /// desktop presentation is a separate shell and must not silently rewrite
    /// the console scene.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<string>> SpaceIdsProperty =
        AvaloniaProperty.Register<ShellNavBand, IReadOnlyList<string>>(
            nameof(SpaceIds), ConsoleSpaceIds);

    /// <summary>Deep links behind the icons (HOME m624).</summary>
    public const string SearchLink = "pssearch:main";

    /// <summary>Deep link behind the settings icon (HOME m624).</summary>
    public const string SettingsLink = "pssettings:play?mode=settings";

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

    // The glance runs at 60 Hz like the focus ring, and off the same kind of
    // frame clock, so the two never disagree about how far a transition got.
    private static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(166_667);

    private readonly List<SpaceVisual> _spaces = new();
    private readonly List<IconVisual> _icons = new();
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _timer;

    private StackPanel? _spaceHost;
    private StackPanel? _systemHost;
    private TextBlock? _clock;
    private bool _focusPushQueued;

    public ShellNavBand()
    {
        Focusable = true;
        // PUI draws one scene-level focus plane. Avalonia's theme adorner would
        // add a second rectangle around this control's full 1920x126 bounds.
        FocusAdorner = null;
        Height = SystemHeight;
        Template = BuildTemplate();
        GotFocus += (_, _) => SchedulePushFocusRect();
    }

    /// <summary>Raised when a space label is activated.</summary>
    public event EventHandler<ShellSpaceEventArgs>? SpaceActivated;

    /// <summary>Raised when a system icon is activated.</summary>
    public event EventHandler<ShellSystemEventArgs>? SystemActivated;

    /// <summary>Raised when the cursor runs off one end of the focused band
    /// region, so the page can hand focus to whatever the graph names there.</summary>
    public event EventHandler<ShellFocusDirection>? EdgeReached;

    // ---- Properties --------------------------------------------------------

    public static readonly StyledProperty<int> SelectedSpaceIndexProperty =
        AvaloniaProperty.Register<ShellNavBand, int>(nameof(SelectedSpaceIndex));

    public static readonly StyledProperty<int> SelectedSystemIndexProperty =
        AvaloniaProperty.Register<ShellNavBand, int>(nameof(SelectedSystemIndex));

    public static readonly StyledProperty<string?> FocusedRegionProperty =
        AvaloniaProperty.Register<ShellNavBand, string?>(nameof(FocusedRegion));

    /// <summary>Which space is currently showing. This is the selection, not the
    /// cursor: the console keeps the two apart so you can glance the other space
    /// without switching to it.</summary>
    public int SelectedSpaceIndex
    {
        get => GetValue(SelectedSpaceIndexProperty);
        set => SetValue(SelectedSpaceIndexProperty, value);
    }

    /// <summary>Which system icon the cursor is on inside
    /// <see cref="SystemRegion"/>.</summary>
    public int SelectedSystemIndex
    {
        get => GetValue(SelectedSystemIndexProperty);
        set => SetValue(SelectedSystemIndexProperty, value);
    }

    /// <summary>The band region that owns focus, or null when focus is
    /// elsewhere on the page. One of <see cref="SpaceSwitcherRegion"/> or
    /// <see cref="SystemRegion"/>.</summary>
    public string? FocusedRegion
    {
        get => GetValue(FocusedRegionProperty);
        set => SetValue(FocusedRegionProperty, value);
    }

    /// <summary>The spaces this band shows. See <see cref="SpaceIdsProperty"/>.</summary>
    public IReadOnlyList<string> SpaceIds
    {
        get => GetValue(SpaceIdsProperty);
        set => SetValue(SpaceIdsProperty, value);
    }

    /// <summary>The selected space id, <c>game</c> by default.</summary>
    public string SelectedSpaceId =>
        SpaceIds.Count == 0
            ? "game"
            : SpaceIds[Math.Clamp(SelectedSpaceIndex, 0, SpaceIds.Count - 1)];

    /// <summary>
    /// Resolves one L1/R1 space step. HOME's space model is the exact
    /// <c>["game", "media"]</c> pair and its shoulder navigation clamps rather
    /// than wrapping (HOME ExperienceDataStore / useSpace).
    /// </summary>
    public static int AdjacentSpaceIndex(int selectedSpaceIndex, int delta)
    {
        if (delta == 0)
        {
            return Math.Clamp(selectedSpaceIndex, 0, ConsoleSpaceIds.Count - 1);
        }

        return Math.Clamp(
            selectedSpaceIndex + Math.Sign(delta),
            0,
            ConsoleSpaceIds.Count - 1);
    }

    /// <summary>The cursor position inside whichever band region owns focus.</summary>
    private int ActiveIndex => FocusedRegion == SystemRegion
        ? Math.Clamp(SelectedSystemIndex, 0, Math.Max(0, _icons.Count - 1))
        : Math.Clamp(SelectedSpaceIndex, 0, Math.Max(0, _spaces.Count - 1));

    // ---- Focus graph -------------------------------------------------------

    /// <summary>
    /// Joins the band's two regions to <paramref name="graph"/> with the exact
    /// edge contract the bundle declares:
    ///
    /// <list type="bullet">
    /// <item><c>space-switcher</c>: <c>canMoveLeft: false</c>,
    /// <c>rightCandidate: "home-system"</c> (HOME m813).</item>
    /// <item><c>home-system</c>: <c>leftCandidate: "space-switcher"</c>,
    /// <c>canMoveRight: false</c>, <c>downCandidate: "experience-switcher-*"</c>
    /// (HOME m217).</item>
    /// </list>
    ///
    /// It also re-registers <paramref name="experienceRegion"/> carrying
    /// <c>upCandidate: "space-switcher-*"</c>, which is what the switcher tile
    /// itself declares (HOME m540:39074). Worth stating plainly because it is
    /// easy to assume otherwise: up from the game row lands on the <b>space
    /// labels</b>, and the system icons are reached by moving right from there.
    /// There is no edge straight from the row to the icons.
    ///
    /// The one edge we add that the bundle does not declare is down from the
    /// space switcher. The console leaves it unspecified; leaving it clamped
    /// here would strand the cursor on the labels, since our band is reachable
    /// by pointer as well as by stick.
    /// </summary>
    /// <param name="spaceCount">
    /// How many spaces the band is showing. Passed rather than read off
    /// <see cref="SpaceIds"/> because the graph is registered before any band
    /// exists, and because a host that shows one space must not leave the
    /// switcher claiming two focusable items.
    /// </param>
    public static void RegisterRegions(
        ShellFocusGraph graph,
        string experienceRegion,
        int spaceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(experienceRegion);

        graph.Add(new ShellFocusRegion(SpaceSwitcherRegion)
        {
            CanMoveRight = true,
            RightCandidate = SystemRegion,
            CanMoveDown = true,
            DownCandidate = experienceRegion,
        });
        graph.SetItemCount(SpaceSwitcherRegion, Math.Max(1, spaceCount));

        graph.Add(new ShellFocusRegion(SystemRegion)
        {
            CanMoveLeft = true,
            LeftCandidate = SpaceSwitcherRegion,
            CanMoveDown = true,
            DownCandidate = experienceRegion,
        });
        graph.SetItemCount(SystemRegion, SystemIconsCount);

        // The experience row already exists with its own down edge; give it the
        // up edge without disturbing what it remembers.
        if (graph.Find(experienceRegion) is { } experience)
        {
            var replacement = new ShellFocusRegion(experienceRegion)
            {
                CanMoveLeft = experience.CanMoveLeft,
                LeftCandidate = experience.LeftCandidate,
                CanMoveRight = experience.CanMoveRight,
                RightCandidate = experience.RightCandidate,
                CanMoveDown = experience.CanMoveDown,
                DownCandidate = experience.DownCandidate,
                CanMoveUp = true,
                UpCandidate = SpaceSwitcherRegion,
                ItemCount = experience.ItemCount,
                LastFocusedItem = experience.LastFocusedItem,
            };
            graph.Add(replacement);
        }
    }

    // ---- Navigation --------------------------------------------------------

    /// <summary>
    /// Moves the cursor within the focused band region. Unlike the other rows
    /// the band does not simply clamp: its two regions sit side by side and the
    /// bundle joins them horizontally, so walking off the right of the space
    /// switcher is what enters the system cluster and walking off its left is
    /// what comes back. Running off an edge raises <see cref="EdgeReached"/>
    /// and leaves the cursor alone; the page turns that into the region move,
    /// because only the page owns the focus graph.
    /// </summary>
    public void MoveFocus(int delta)
    {
        if (delta == 0 || FocusedRegion is null)
        {
            return;
        }

        bool system = FocusedRegion == SystemRegion;
        int count = system ? SystemIconsCount : SpaceIds.Count;
        int current = system ? SelectedSystemIndex : _spaceCursor;
        int next = current + delta;

        if (next < 0 || next >= count)
        {
            EdgeReached?.Invoke(
                this,
                delta < 0 ? ShellFocusDirection.Left : ShellFocusDirection.Right);
            return;
        }

        if (system)
        {
            SetSelectedSystemIndex(next);
        }
        else
        {
            SetSpaceCursor(next);
        }
    }

    // The space switcher tracks a cursor separately from the selection: glancing
    // "Media" must not switch spaces, only pressing it does (HOME m813, where
    // onPress is what calls focusSpaceDefaultPosition).
    private int _spaceCursor;

    /// <summary>Where the cursor sits in the space switcher.</summary>
    public int SpaceCursor => _spaceCursor;

    public void SetSpaceCursor(int index)
    {
        int clamped = Math.Clamp(index, 0, SpaceIds.Count - 1);
        if (clamped == _spaceCursor)
        {
            return;
        }

        _spaceCursor = clamped;
        UpdateVisuals();
        SchedulePushFocusRect();
    }

    public void SetSelectedSystemIndex(int index)
    {
        int clamped = Math.Clamp(index, 0, Math.Max(0, SystemIconsCount - 1));
        if (clamped == SelectedSystemIndex)
        {
            return;
        }

        SelectedSystemIndex = clamped;
    }

    /// <summary>Activates whatever the cursor is on in the focused region.</summary>
    public void ActivateSelected()
    {
        if (FocusedRegion == SystemRegion)
        {
            ActivateSystem(SelectedSystemIndex);
        }
        else if (FocusedRegion == SpaceSwitcherRegion)
        {
            ActivateSpace(_spaceCursor);
        }
    }

    /// <summary>Selects a space and raises <see cref="SpaceActivated"/>.</summary>
    public void ActivateSpace(int index)
    {
        int clamped = Math.Clamp(index, 0, SpaceIds.Count - 1);
        SelectedSpaceIndex = clamped;
        _spaceCursor = clamped;
        UpdateVisuals();
        SpaceActivated?.Invoke(this, new ShellSpaceEventArgs(SpaceIds[clamped], clamped));
    }

    /// <summary>Raises <see cref="SystemActivated"/> for one destination.</summary>
    public void ActivateSystem(int index)
    {
        if (index < 0 || index >= SystemIconsCount)
        {
            return;
        }

        var destination = (ShellSystemDestination)index;
        SystemActivated?.Invoke(this, new ShellSystemEventArgs(destination, LinkFor(destination)));
    }

    /// <summary>The console's deep link for a destination (HOME m624).</summary>
    public static string LinkFor(ShellSystemDestination destination) => destination switch
    {
        ShellSystemDestination.Search => SearchLink,
        ShellSystemDestination.Settings => SettingsLink,
        _ => string.Empty,
    };

    /// <summary>The label under an icon on glance. These are the bundle's own
    /// message ids resolved to English (HOME m624 <c>msgid_search</c>,
    /// <c>msgid_settings</c>; the profile icon speaks its account name on the
    /// console, so it carries a plain word here).</summary>
    public static string LabelFor(ShellSystemDestination destination) => destination switch
    {
        ShellSystemDestination.Search => "Search",
        ShellSystemDestination.Settings => "Settings",
        _ => "Profile",
    };

    /// <summary>The two space labels (HOME m814 <c>textBySpaceId</c>).</summary>
    public static string LabelForSpace(string spaceId) =>
        string.Equals(spaceId, "media", StringComparison.Ordinal) ? "Media" : "Games";

    // ---- Clock -------------------------------------------------------------

    /// <summary>
    /// Writes the clock text. The console's clock carries
    /// <c>fontVariant: ["tabular-nums"]</c> (HOME m623), which the template
    /// applies as Avalonia's equivalent font feature so the digits keep a
    /// constant advance and the cluster does not shuffle on the minute.
    /// </summary>
    public void SetClockText(string text)
    {
        if (_clock is { } clock)
        {
            clock.Text = text;
        }
    }

    // ---- Template ----------------------------------------------------------

    private static FuncControlTemplate BuildTemplate()
    {
        return new FuncControlTemplate((_, ns) =>
        {
            var spaceHost = new StackPanel
            {
                Name = "PART_Spaces",
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(ContentInset, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            spaceHost.RegisterInNameScope(ns);

            var systemHost = new StackPanel
            {
                Name = "PART_System",
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, ContentInset, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            systemHost.RegisterInNameScope(ns);

            var clock = new TextBlock
            {
                Name = "PART_Clock",
                Text = "00:00",
                Margin = new Thickness(ClockMarginLeft, 0, 0, 0),
                FontSize = ShellFontSize.Large,
                FontWeight = FontWeight.Light,
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Ps5FontLibrary.TryGet(Ps5FontFace.Light) is { } clockFont)
            {
                clock.FontFamily = clockFont;
            }

            // The console's tabular-nums, expressed as the OpenType feature it
            // resolves to. Without it a proportional font re-measures the string
            // every minute and walks the whole cluster a pixel sideways.
            clock.SetValue(
                TextBlock.FontFeaturesProperty,
                new FontFeatureCollection { new FontFeature { Tag = "tnum", Value = 1 } });
            clock.RegisterInNameScope(ns);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Height = SystemHeight,
                ClipToBounds = false,
            };
            Grid.SetColumn(spaceHost, 0);
            Grid.SetColumn(systemHost, 1);
            grid.Children.Add(spaceHost);
            grid.Children.Add(systemHost);
            systemHost.Children.Add(clock);

            return grid;
        });
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _spaceHost = e.NameScope.Find<StackPanel>("PART_Spaces");
        _systemHost = e.NameScope.Find<StackPanel>("PART_System");
        _clock = e.NameScope.Find<TextBlock>("PART_Clock");
        BuildSpaces();
        BuildIcons();
        UpdateVisuals();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedSpaceIndexProperty)
        {
            _spaceCursor = Math.Clamp(SelectedSpaceIndex, 0, SpaceIds.Count - 1);
            UpdateVisuals();
            SchedulePushFocusRect();
        }
        else if (change.Property == SelectedSystemIndexProperty)
        {
            UpdateVisuals();
            SchedulePushFocusRect();
        }
        else if (change.Property == FocusedRegionProperty)
        {
            UpdateVisuals();
            SchedulePushFocusRect();
        }
    }

    // ---- Build -------------------------------------------------------------

    private void BuildSpaces()
    {
        if (_spaceHost is not { } host)
        {
            return;
        }

        host.Children.Clear();
        _spaces.Clear();

        for (int i = 0; i < SpaceIds.Count; i++)
        {
            int captured = i;
            var label = new TextBlock
            {
                Text = LabelForSpace(SpaceIds[i]),
                FontSize = ShellFontSize.Large,
                FontWeight = FontWeight.Bold,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (Ps5FontLibrary.TryGet(Ps5FontFace.Bold) is { } bold)
            {
                label.FontFamily = bold;
            }

            // marginRight 64 and padding 8, straight from the stylesheet. The
            // padding matters because it is what the focus rect wraps.
            var root = new Border
            {
                Margin = new Thickness(0, 0, SpaceItemMarginRight, 0),
                Padding = new Thickness(SpaceItemPadding),
                Background = Brushes.Transparent,
                Child = label,
            };

            root.PointerEntered += (_, _) =>
            {
                FocusedRegion = SpaceSwitcherRegion;
                SetSpaceCursor(captured);
                Focus();
            };
            root.PointerPressed += (_, _) =>
            {
                Focus();
                FocusedRegion = SpaceSwitcherRegion;
                SetSpaceCursor(captured);
                SetRingPressed(true);
                ActivateSpace(captured);
            };
            root.PointerReleased += (_, _) => SetRingPressed(false);
            root.PointerExited += (_, _) => SetRingPressed(false);

            host.Children.Add(root);
            _spaces.Add(new SpaceVisual(root, label));
        }
    }

    private void BuildIcons()
    {
        if (_systemHost is not { } host)
        {
            return;
        }

        // The clock is the last child and stays; rebuild only the icons in front
        // of it.
        for (int i = host.Children.Count - 1; i >= 0; i--)
        {
            if (host.Children[i] is not TextBlock)
            {
                host.Children.RemoveAt(i);
            }
        }

        _icons.Clear();

        for (int i = 0; i < SystemIconsCount; i++)
        {
            var destination = (ShellSystemDestination)i;
            int captured = i;

            var mark = BuildMark(destination);
            var markHost = new Panel
            {
                Width = SystemIconSize,
                Height = SystemIconSize,
                RenderTransformOrigin = RelativePoint.Center,
                Children = { mark },
            };
            var focusBackground = new Border
            {
                Width = SystemIconSize,
                Height = SystemIconSize,
                CornerRadius = new CornerRadius(SystemIconSize / 2.0),
                Background = Brushes.White,
                Opacity = 0,
                IsHitTestVisible = false,
            };

            // The glance strip: 368 wide and centred on the 56 box, so it is the
            // icon's own label rather than a column of its own. It starts at
            // zero opacity because an un-glanced icon has no label at all.
            var label = new TextBlock
            {
                Text = LabelFor(destination),
                Width = IconTextWidth,
                FontSize = 15,
                FontWeight = FontWeight.Light,
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            if (Ps5FontLibrary.TryGet(Ps5FontFace.Light) is { } labelFont)
            {
                label.FontFamily = labelFont;
            }

            // A Canvas, not a Panel, because the strip is `position: absolute`
            // on the console and hangs below a box only 56 tall. A flow parent
            // clamps a child whose top margin exceeds its height, which silently
            // collapses the label to nothing; a Canvas arranges children at
            // their desired size wherever they are put.
            var root = new Canvas
            {
                Width = SystemIconSize,
                Height = SystemIconSize,
                Margin = new Thickness(IconMarginLeft, 0, 0, 0),
                Background = Brushes.Transparent,
                ClipToBounds = false,
            };

            Canvas.SetLeft(markHost, 0);
            Canvas.SetTop(markHost, 0);

            // alignItems: center on a 56 box centres the 368 strip on the icon,
            // so it starts 156 px to the left of it.
            Canvas.SetLeft(label, (SystemIconSize - IconTextWidth) / 2.0);
            Canvas.SetTop(label, IconTextTop);

            root.Children.Add(focusBackground);
            root.Children.Add(markHost);
            root.Children.Add(label);

            root.PointerEntered += (_, _) =>
            {
                FocusedRegion = SystemRegion;
                SetSelectedSystemIndex(captured);
                Focus();
            };
            root.PointerPressed += (_, _) =>
            {
                Focus();
                FocusedRegion = SystemRegion;
                SetSelectedSystemIndex(captured);
                SetRingPressed(true);
                ActivateSystem(captured);
            };
            root.PointerReleased += (_, _) => SetRingPressed(false);
            root.PointerExited += (_, _) => SetRingPressed(false);

            // Insert ahead of the clock so the cluster order stays
            // search, settings, profile, clock.
            host.Children.Insert(_icons.Count, root);
            _icons.Add(new IconVisual(
                root,
                markHost,
                label,
                focusBackground,
                mark as Ps5IconPresenter));
        }
    }

    /// <summary>
    /// The icon id each system destination draws, exactly as the bundle passes
    /// it to <c>SystemIcon</c>: <c>iconId: "search", iconIndex: 3</c> and
    /// <c>iconId: "settings", iconIndex: 4</c> (HOME m624).
    ///
    /// <para>The console normally mounts the signed-in account's <c>Avatar</c>
    /// in the profile slot rather than an icon id. Prosperismo has no PSN avatar
    /// <c>person</c> pictogram; this remains visibly distinct from pretending a
    /// host image is the user's console account picture.</para>
    /// </summary>
    /// <param name="destination">System destination to name.</param>
    internal static string? IconIdFor(ShellSystemDestination destination) => destination switch
    {
        ShellSystemDestination.Search => "search",
        ShellSystemDestination.Settings => "settings",
        ShellSystemDestination.Profile => "person",
        _ => null,
    };

    /// <summary>
    /// The mark inside one icon box: the shell's own SVG, rendered as a vector
    /// at <c>SYSTEM_ICON_SIZE_NO_GLANCE</c> inside the 56 box.
    ///
    /// <para>This used to be a hand-cut line drawing per destination, plus a
    /// bitmap for settings when the dump supplied one. Both are gone. The
    /// pictograms are in <c>Sce.PlayStation.PUI_UI3.rco</c> as SVG and
    /// <see cref="Ps5IconPresenter"/> draws them at whatever size the layout
    /// asks for. The bounded packaged set keeps a standalone release complete;
    /// shipping set.</para>
    /// </summary>
    private static Control BuildMark(ShellSystemDestination destination)
    {
        return new Ps5IconPresenter
        {
            IconId = IconIdFor(destination),
            Tint = Ps5HomeMetrics.IconNormal,
            // These compact system icons sit inside ButtonBase's white
            // visible-on-focus disc. Their authored SVG fill is white, so the
            // glance animation must be allowed to tint that declared fill from
            // white toward #292929 as the disc appears.
            OverrideDeclaredFill = true,
            Width = SystemIconSizeNoGlance,
            Height = SystemIconSizeNoGlance,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ---- Visual state ------------------------------------------------------

    /// <summary>
    /// Applies the selected/unselected space treatment and drives every icon's
    /// glance. Exactly one icon can be glanced, and only while the system
    /// region owns focus, which is what keeps the label strip from showing two
    /// labels over each other.
    /// </summary>
    private void UpdateVisuals()
    {
        for (int i = 0; i < _spaces.Count; i++)
        {
            // The shell composes a bold selected label with a light resting
            // label at reduced opacity; both are concrete bundled Fira cuts.
            bool selected = i == SelectedSpaceIndex;
            _spaces[i].Label.FontWeight = selected ? FontWeight.Bold : FontWeight.Light;
            if (Ps5FontLibrary.TryGet(selected ? Ps5FontFace.Bold : Ps5FontFace.Light) is { } family)
            {
                _spaces[i].Label.FontFamily = family;
            }
            _spaces[i].Label.Opacity = selected ? 1.0 : UnselectedSpaceOpacity;
        }

        int glancedIndex = GlancedIconIndex(FocusedRegion, SelectedSystemIndex);
        for (int i = 0; i < _icons.Count; i++)
        {
            if (i == glancedIndex)
            {
                _icons[i].Glance.Glance();
            }
            else
            {
                _icons[i].Glance.Blur();
            }
        }

        ApplyGlance();
        WakeGlance();
    }

    /// <summary>
    /// Which system icon is glanced, or -1 for none. Pointer hover and stick
    /// movement both land here: entering an icon sets the focused region and the
    /// cursor, and this is what turns that pair into the one glanced icon. Kept
    /// pure so the hover rule is checkable without a pointer, which matters
    /// because synthetic pointer input does not reach the shell here.
    /// </summary>
    public static int GlancedIconIndex(string? focusedRegion, int selectedSystemIndex)
    {
        if (!string.Equals(focusedRegion, SystemRegion, StringComparison.Ordinal))
        {
            return -1;
        }

        return selectedSystemIndex >= 0 && selectedSystemIndex < SystemIconsCount
            ? selectedSystemIndex
            : -1;
    }

    /// <summary>
    /// ButtonBase's <c>visibleOnFocus</c> alpha, driven by the icon's 48 to 56
    /// focus scale: <c>lerp(.08*t, 1*t, t)</c>.
    /// </summary>
    public static double FocusBackgroundOpacityForScale(double scale)
    {
        double rest = SystemIconSizeNoGlance / SystemIconSize;
        double t = Math.Clamp((scale - rest) / (1.0 - rest), 0.0, 1.0);
        return (0.08 * t) + (0.92 * t * t);
    }

    /// <summary>Stock icon inversion: white to ButtonBase's #292929.</summary>
    public static byte FocusedIconChannelForScale(double scale)
    {
        double rest = SystemIconSizeNoGlance / SystemIconSize;
        double t = Math.Clamp((scale - rest) / (1.0 - rest), 0.0, 1.0);
        return (byte)Math.Round(255.0 + ((41.0 - 255.0) * t));
    }

    /// <summary>Pushes the current spring values onto the visuals.</summary>
    private void ApplyGlance()
    {
        foreach (var icon in _icons)
        {
            double scale = icon.Glance.IconScale;
            icon.MarkHost.RenderTransform =
                TransformOperations.Parse($"scale({scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            icon.Label.Opacity = icon.Glance.LabelOpacity;

            // IconButton is not a card. HOME sets backgroundVisibility to
            // visibleOnFocus; ButtonBase animates its white fill from alpha
            // .08 to 1 and swaps the icon toward the stock inverted colour
            // #292929. The 48 -> 56 glance spring supplies the same focus
            // progress here, keeping fill, glyph and scale together.
            icon.FocusBackground.Opacity = FocusBackgroundOpacityForScale(scale);
            if (icon.Mark is { } mark)
            {
                byte channel = FocusedIconChannelForScale(scale);
                mark.Tint = Color.FromRgb(channel, channel, channel);
            }
        }
    }

    private bool NeedsGlanceTick()
    {
        foreach (var icon in _icons)
        {
            if (icon.Glance.IsAnimating)
            {
                return true;
            }
        }

        return false;
    }

    private void WakeGlance()
    {
        if (!NeedsGlanceTick())
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                _timer.Tick += OnGlanceTick;
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            // Without a dispatcher the glance simply lands on its target.
        }
    }

    private void OnGlanceTick(object? sender, EventArgs e)
    {
        var delta = _stopwatch.Elapsed;
        _stopwatch.Restart();
        AdvanceGlance(delta);

        if (!NeedsGlanceTick())
        {
            try
            {
                _timer?.Stop();
                _stopwatch.Reset();
            }
            catch
            {
                // Nothing to unwind.
            }
        }
    }

    /// <summary>Advances every icon's glance springs and repaints. The internal
    /// timer calls this; a headless test calls it directly.</summary>
    public void AdvanceGlance(TimeSpan delta)
    {
        foreach (var icon in _icons)
        {
            icon.Glance.Advance(delta);
        }

        ApplyGlance();
    }

    // ---- Focus ring --------------------------------------------------------

    /// <summary>The rect the focus highlight sits on, in this band's own
    /// coordinates, or null when the band does not own focus.</summary>
    internal Rect? FocusHighlightRect
    {
        get
        {
            if (FocusedRegion == SystemRegion)
            {
                if (ActiveIndex >= _icons.Count)
                {
                    return null;
                }

                return RectOf(_icons[ActiveIndex].Root);
            }

            if (FocusedRegion == SpaceSwitcherRegion)
            {
                if (ActiveIndex >= _spaces.Count)
                {
                    return null;
                }

                return RectOf(_spaces[ActiveIndex].Root);
            }

            return null;
        }
    }

    private Rect? RectOf(Visual child)
    {
        try
        {
            if (child.TransformToVisual(this) is not { } transform)
            {
                return null;
            }

            var local = new Rect(child.Bounds.Size);
            return local.Width > 0 && local.Height > 0 ? local.TransformToAABB(transform) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Re-publishes the focus rect after the surface has been
    /// rescaled.</summary>
    public void RefreshFocusRect() => SchedulePushFocusRect();

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
            _focusPushQueued = false;
        }
    }

    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (FocusedRegion is null || !IsEffectivelyVisible)
            {
                ring.Release(this);
                return;
            }

            if (FocusHighlightRect is not { } local)
            {
                ring.Release(this);
                return;
            }

            if (this.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            // IconButton's native FocusCustomSettings fixes its radius to half
            // the 56-pixel button height. Space labels remain rectangles.
            ring.Radius = FocusedRegion == SystemRegion
                ? SystemIconSize / 2.0
                : local.Height * ShellTileRow.CornerRadiusRatio;

            // PUI exposes one 3 px UI3 line; no compact-icon thickness override
            // has been recovered.
            ring.LineScale = 1.0;
            ring.Claim(this, local.TransformToAABB(transform));
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
            if (FocusedRegion is not null &&
                ShellFocusRing.For(this) is { } ring &&
                ReferenceEquals(ring.Owner, this))
            {
                ring.SetPressed(pressed);
            }
        }
        catch
        {
            // Decoration only.
        }
    }

    // ---- Input -------------------------------------------------------------

    /// <summary>
    /// Horizontal movement inside the focused band region. Left and right are
    /// the band's own, the same split the other rows use: the page handles up
    /// and down because those cross regions, and the row that has focus handles
    /// the axis it lays out along.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Up:
                EdgeReached?.Invoke(this, ShellFocusDirection.Up);
                e.Handled = true;
                break;
            case Key.Down:
                EdgeReached?.Invoke(this, ShellFocusDirection.Down);
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    private sealed record SpaceVisual(Border Root, TextBlock Label);

    private sealed class IconVisual
    {
        public IconVisual(
            Panel root,
            Panel markHost,
            TextBlock label,
            Border focusBackground,
            Ps5IconPresenter? mark)
        {
            Root = root;
            MarkHost = markHost;
            Label = label;
            FocusBackground = focusBackground;
            Mark = mark;
        }

        public Panel Root { get; }

        public Panel MarkHost { get; }

        public TextBlock Label { get; }

        public Border FocusBackground { get; }

        public Ps5IconPresenter? Mark { get; }

        public ShellGlanceState Glance { get; } = new();
    }
}
