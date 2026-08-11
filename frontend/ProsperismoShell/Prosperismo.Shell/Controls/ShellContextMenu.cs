// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.SystemAssets;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// A monochrome leading mark for an options-menu row.
///
/// The console's rows carry a vector pictogram in the icon gutter, never a
/// colour glyph; a full-colour emoji next to greyscale UI is the single
/// loudest desktop tell in the whole menu. Marks are authored in a 40 x 40 box
/// because that is the mark size the console's own list rows use
/// (EXACT, HOME m259 `checkmarkContainer { marginHorizontal: 16, width: 40 }`,
/// which is 16 + 40 + 16 = the 72 gutter).
/// </summary>
public sealed record ShellMenuIcon
{
    public ShellMenuIcon(string data, bool filled = false, double strokeThickness = 2.4)
    {
        Data = data ?? throw new ArgumentNullException(nameof(data));
        Filled = filled;
        StrokeThickness = strokeThickness;
    }

    /// <summary>
    /// Path data, held as text rather than as a parsed
    /// <see cref="Geometry"/>. Parsing needs a platform render interface, so a
    /// geometry here would make the whole mark set unusable to anything that
    /// is not a live app, tests included.
    /// </summary>
    public string Data { get; }

    /// <summary>Filled marks (the play triangle) rather than stroked outlines.</summary>
    public bool Filled { get; }

    public double StrokeThickness { get; }
}

/// <summary>
/// The monochrome marks the shell menu uses in place of emoji. Authored on a
/// 40 x 40 grid; see <see cref="ShellMenuIcon"/> for why 40.
/// </summary>
public static class ShellMenuIcons
{
    /// <summary>Play triangle, the only filled mark.</summary>
    public static readonly ShellMenuIcon Play =
        new("M 12,7 L 33,20 L 12,33 Z", filled: true);

    /// <summary>Folder outline.</summary>
    public static readonly ShellMenuIcon Folder =
        new("M 5,32 V 9 h 11 l 3.5,4.5 H 35 V 32 Z");

    /// <summary>Gear: a toothed ring around a hub.</summary>
    public static readonly ShellMenuIcon Settings =
        new("M 20,13.5 A 6.5,6.5 0 1 1 19.99,13.5 Z "
            + "M 17.6,4.5 h 4.8 l 0.7,4.0 3.0,1.7 3.5,-2.1 3.4,3.4 -2.1,3.5 1.7,3.0 4.0,0.7 v 4.8 "
            + "l -4.0,0.7 -1.7,3.0 2.1,3.5 -3.4,3.4 -3.5,-2.1 -3.0,1.7 -0.7,4.0 h -4.8 "
            + "l -0.7,-4.0 -3.0,-1.7 -3.5,2.1 -3.4,-3.4 2.1,-3.5 -1.7,-3.0 -4.0,-0.7 v -4.8 "
            + "l 4.0,-0.7 1.7,-3.0 -2.1,-3.5 3.4,-3.4 3.5,2.1 3.0,-1.7 Z");

    /// <summary>Bin, for the delete/remove rows.</summary>
    public static readonly ShellMenuIcon Delete =
        new("M 7,11 H 33 M 15.5,11 V 7 h 9 v 4 M 10,11 V 34 h 20 V 11 "
            + "M 17,16 V 29 M 23,16 V 29");

    /// <summary>Two stacked sheets, for the copy rows.</summary>
    public static readonly ShellMenuIcon Copy =
        new("M 13,6 H 34 V 27 H 13 Z M 27,33 H 6 V 12 h 7");

    /// <summary>Circled "i", the console's information row.</summary>
    public static readonly ShellMenuIcon Information =
        new("M 20,4 A 16,16 0 1 1 19.99,4 Z M 20,11.5 v 0.8 M 20,17.5 V 29");

    /// <summary>Crossed square, for closing a running application.</summary>
    public static readonly ShellMenuIcon CloseApplication =
        new("M 6,6 H 34 V 34 H 6 Z M 14,14 L 26,26 M 26,14 L 14,26");

    /// <summary>Downward arrow into a tray, for the update check.</summary>
    public static readonly ShellMenuIcon CheckForUpdate =
        new("M 20,5 V 25 M 12,18 L 20,26 L 28,18 M 6,31 H 34");
}

/// <summary>One row of a <see cref="ShellContextMenu"/>.</summary>
public sealed record ShellMenuEntry
{
    public ShellMenuEntry(string header, Action? onPress = null)
    {
        Header = header ?? string.Empty;
        OnPress = onPress;
    }

    /// <summary>The visible label.</summary>
    public string Header { get; init; }

    /// <summary>
    /// The console's own menu id where the row has a counterpart, e.g.
    /// <c>MENU_ID_APPLICATION_CLOSE</c> (EXACT list, HOME m525:37923). Carried
    /// so the taxonomy stays traceable; nothing renders it.
    /// </summary>
    public string? MenuId { get; init; }

    /// <summary>The leading mark, or null for a row with an empty gutter.</summary>
    public ShellMenuIcon? Icon { get; init; }

    /// <summary>
    /// A control put in the icon gutter instead of <see cref="Icon"/>, for the
    /// </summary>
    public Control? IconContent { get; init; }

    /// <summary>
    /// Whether this native menu model reserves the 72 px icon column. The
    /// App Switcher's PopupMenuPS uses label-only Button rows; HOME title
    /// options keep the gutter.
    /// </summary>
    public bool ShowIconGutter { get; init; } = true;

    /// <summary>
    /// Section index. The console breaks a menu into sections with a single
    /// 2 px line, never a rule between every row (EXACT, HOME m679:48457,
    /// HOME m259 `separatorContainer`/`separator`); a change of section index
    /// is what draws that one line.
    /// </summary>
    public int Section { get; init; }

    /// <summary>
    /// A grey sub-label introducing a section, drawn as the console's own
    /// options-panel header rather than as a menu row (EXACT, HOME m259
    /// `headerContainer`/`header`). Only read on the first entry of a section.
    /// </summary>
    public string? SectionHeader { get; init; }

    public bool IsEnabled { get; init; } = true;

    public Action? OnPress { get; init; }
}

/// <summary>
/// The per-tile options menu, in the console's grammar.
///
/// The real menu is a native component: the home bundle hands
/// <c>OptionsMenuPS</c> an item list and an anchor and draws nothing itself
/// (EXACT, HOME m840 `TileOptionsMenu/index.tsx`, m514), so the panel's own
/// geometry is UNRESOLVED and the numbers below are the nearest real analogues,
/// labelled as such. What is not an analogue is the grammar, and the grammar is
/// what was wrong:
///
/// * A leading icon gutter of exactly 72, with a 40 px mark centred in it
///   (EXACT, HOME m259:20683 `sortOption { paddingLeft: 72 }`,
///   `sortIconContainer { height: 72, width: 72 }`,
///   `checkmarkContainer { marginHorizontal: 16, width: 40 }`).
/// * One 2 px `rgba(255,255,255,0.1)` line between sections, inset 16 from
///   either edge, and no line at all between ordinary rows (EXACT, HOME
///   m679:48457 `lineSeperator`/`lineContainer`, HOME m259 `separator`).
/// * A section is introduced by a grey SizeSmall label in a 40 tall band at a
///   24 px inset, not by a styled menu row (EXACT, HOME m259 `headerContainer`,
///   `header { color: COLOR_MAP.GRAY, fontSize: SizeSmall }`).
/// * Every row is white. The console has no destructive-red row; the weight of
///   a destructive action lands on its confirm dialog instead, which is why
///   <c>MENU_ID_APPLICATION_DELETE</c> is an ordinary entry in the same flat
///   list as everything else (EXACT, HOME m525:37923).
/// * Focus is the same travelling rectangle every other surface uses
///   (<c>focusStyle: "rectangle"</c>, RN-BASE:8618), never a row fill.
///
/// Row height 98 and the 652 panel width are analogues, not measurements: 98 is
/// the shell's own list-row height (EXACT, DIALOG m914 `item { height: 98 }`)
/// and 652 is the function-control popover's width (EXACT, HOME m143:10683).
/// The console's own sort/filter rows are 72 tall; the menu's are not mined.
/// </summary>
public sealed class ShellContextMenu : ContextMenu
{
    /// <summary>Width of the leading icon gutter (EXACT, HOME m259).</summary>
    public const double IconGutter = 72;

    /// <summary>Side of the mark inside the gutter (EXACT, HOME m259).</summary>
    public const double IconMark = 40;

    /// <summary>Row height. An analogue, not a measurement (DIALOG m914).</summary>
    public const double RowHeight = 98;

    /// <summary>Separator thickness (EXACT, HOME m679, m259).</summary>
    public const double SeparatorHeight = 2;

    /// <summary>Separator inset from either edge (EXACT, HOME m679
    /// `lineContainer`, HOME m259 `separatorContainer`).</summary>
    public const double SeparatorInset = 16;

    /// <summary>Air above a separator (EXACT, HOME m259
    /// `separatorContainer.marginTop`).</summary>
    public const double SeparatorTopMargin = 16;

    /// <summary>Section header band height (EXACT, HOME m259
    /// `headerContainer`).</summary>
    public const double SectionHeaderHeight = 40;

    /// <summary>Section header left inset (EXACT, HOME m259).</summary>
    public const double SectionHeaderInset = 24;

    /// <summary>Panel width. An analogue: the function-control popover
    /// (EXACT for that surface, HOME m143:10683), not a measured options
    /// menu.</summary>
    public const double AnalogueMinWidth = 652;

    public const double AnalogueMaxWidth = 784;

    // COLOR_MAP.GRAY is a token the native theme resolves (EXACT name, HOME
    // m75); its value is UNRESOLVED, so the shell's own muted white stands in.
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#B3FFFFFF"));
    private static readonly IBrush MarkBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush MenuFallbackBrush = new SolidColorBrush(Color.Parse("#F211141A"));

    /// <summary>Authoritative UI3 texture used behind the composed item rows.</summary>
    public const Ps5Ui3ChromeAsset ChromeAsset = Ps5Ui3ChromeAsset.MenuBase;

    /// <summary>UI3's menu texture is neutral; this keeps it a subtle surface grain.</summary>
    public const double ChromeOpacity = 0.28;

    /// <summary>Menu plates use the same native 16px radius family as popups.</summary>
    public const double ChromeRadius = 16;

    private readonly List<MenuItem> _rows = new();
    private readonly List<Action?> _controllerActions = new();
    private int _controllerSelectedIndex = -1;

    public ShellContextMenu()
    {
        // The shell anchors the menu to the focused tile and opens it to the
        // right, with a flip/fit collision rather than a pointer position.
        Placement = PlacementMode.Right;
        PlacementConstraintAdjustment =
            Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipX
            | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.FlipY
            | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideX
            | Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.SlideY;

        MinWidth = AnalogueMinWidth;
        MaxWidth = AnalogueMaxWidth;
        Background = Brushes.Transparent;
        ShellMotion.SetMenuMotion(this, true);

        Closed += (_, _) => ShellFocusRing.For(this)?.Release(this);
        Opening += (_, _) => Dispatcher.UIThread.Post(
            FocusControllerSelection,
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Places UI3's <c>image_menu_base</c> beneath the existing model-built
    /// rows. This affects chrome only: entries still arrive exclusively from
    /// the title-options composer supplied by the host.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            context.DrawRectangle(MenuFallbackBrush, null, bounds, ChromeRadius, ChromeRadius);
            if (Ps5Ui3Chrome.TryGet(ChromeAsset) is { } bitmap)
            {
                using (context.PushOpacity(ChromeOpacity))
                {
                    Ps5Ui3ChromePlate.DrawNinePatch(context, bitmap, bounds, ChromeRadius);
                }
            }
        }

        base.Render(context);
    }

    /// <summary>The rows currently built, in order. Separators and section
    /// headers are not rows and do not appear here.</summary>
    public IReadOnlyList<MenuItem> Rows => _rows;

    public int ControllerSelectedIndex => _controllerSelectedIndex;

    /// <summary>Controller route for popup menus hosted above manual shell input.</summary>
    public bool MoveControllerFocus(int delta)
    {
        if (_rows.Count == 0 || delta == 0)
        {
            return false;
        }

        var start = _controllerSelectedIndex < 0 ? 0 : _controllerSelectedIndex;
        for (int step = 1; step <= _rows.Count; step++)
        {
            var candidate = Math.Clamp(start + (Math.Sign(delta) * step), 0, _rows.Count - 1);
            if (_rows[candidate].IsEnabled)
            {
                if (candidate == _controllerSelectedIndex)
                {
                    return false;
                }
                _controllerSelectedIndex = candidate;
                FocusControllerSelection();
                return true;
            }
            if (candidate is 0 || candidate == _rows.Count - 1)
            {
                break;
            }
        }
        return false;
    }

    public bool ActivateFromController()
    {
        if (_controllerSelectedIndex < 0 ||
            _controllerSelectedIndex >= _rows.Count ||
            !_rows[_controllerSelectedIndex].IsEnabled)
        {
            return false;
        }

        var action = _controllerActions[_controllerSelectedIndex];
        if (action is null)
        {
            return false;
        }

        Close();
        action();
        return true;
    }

    /// <summary>The label of a row this menu built, or null for anything else.</summary>
    public static TextBlock? LabelOf(MenuItem? row) =>
        row?.Header is Grid grid ? grid.Children.OfType<TextBlock>().FirstOrDefault() : null;

    /// <summary>The width of a row's leading gutter, or null for anything this
    /// menu did not build.</summary>
    public static double? GutterOf(MenuItem? row) =>
        row?.Header is Grid { ColumnDefinitions.Count: > 0 } grid
            ? grid.ColumnDefinitions[0].Width.Value
            : null;

    /// <summary>
    /// Rebuilds the menu from <paramref name="entries"/>. A separator is
    /// emitted only where the section index changes, and a section header only
    /// where one is supplied.
    /// </summary>
    public void SetEntries(IEnumerable<ShellMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.ToList();
        var items = new List<Control>();
        _rows.Clear();
        _controllerActions.Clear();

        int? section = null;
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            var startsSection = section is null || entry.Section != section;

            if (startsSection && section is not null)
            {
                items.Add(BuildSeparator());
            }

            if (startsSection && !string.IsNullOrEmpty(entry.SectionHeader))
            {
                items.Add(BuildSectionHeader(entry.SectionHeader!));
            }

            section = entry.Section;

            var row = BuildRow(entry);
            _rows.Add(row);
            _controllerActions.Add(entry.OnPress);
            items.Add(row);
        }

        // Populated through Items rather than ItemsSource: these are containers,
        // not data, and a menu given controls as an items source wraps and
        // remeasures them.
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        _controllerSelectedIndex = _rows.FindIndex(static row => row.IsEnabled);
    }

    /// <summary>
    /// The row content, built here rather than handed to the menu theme's icon
    /// slot. The console's row is a flex row of a 72 wide icon container and a
    /// label, and owning that grid outright is the only way to guarantee the
    /// gutter: the theme's own icon presenter sizes itself and cannot be told
    /// to be 72.
    /// </summary>
    private MenuItem BuildRow(ShellMenuEntry entry)
    {
        var label = new TextBlock
        {
            Text = entry.Header,
            FontSize = ShellFontSize.Normal,
            Foreground = MarkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(entry.ShowIconGutter
                ? $"{IconGutter},*"
                : "0,*"),
        };
        if (entry.ShowIconGutter)
        {
            var gutter = entry.IconContent
                ?? (entry.Icon is { } mark ? BuildMark(mark) : new Border { Width = IconGutter });
            content.Children.Add(gutter);
        }
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var row = new MenuItem
        {
            Header = content,
            MinHeight = RowHeight,
            IsEnabled = entry.IsEnabled,
            Tag = entry.MenuId,
        };

        if (entry.OnPress is { } press)
        {
            row.Click += (_, _) => press();
        }

        // Focus is the travelling ring, so every path that changes the focused
        // row publishes a rect and nothing paints itself.
        row.PointerEntered += (_, _) => PushFocusRect(row);
        row.GotFocus += (_, _) => PushFocusRect(row);

        return row;
    }

    private void FocusControllerSelection()
    {
        if (_controllerSelectedIndex < 0 || _controllerSelectedIndex >= _rows.Count)
        {
            return;
        }

        _rows[_controllerSelectedIndex].Focus();
        PushFocusRect(_rows[_controllerSelectedIndex]);
    }

    /// <summary>
    /// Builds the 40 px mark that sits in the 72 px gutter. Path parsing needs
    /// a platform render interface, so a tree built without one falls back to a
    /// blank gutter box of the right size rather than taking the menu down.
    /// </summary>
    private static Control BuildMark(ShellMenuIcon mark)
    {
        var box = new Border
        {
            Width = IconGutter,
            Height = IconGutter,
            Padding = new Thickness((IconGutter - IconMark) / 2),
        };

        try
        {
            var path = new ShapePath
            {
                Data = Geometry.Parse(mark.Data),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            if (mark.Filled)
            {
                path.Fill = MarkBrush;
            }
            else
            {
                path.Stroke = MarkBrush;
                path.StrokeThickness = mark.StrokeThickness;
                path.StrokeJoin = PenLineJoin.Round;
                path.StrokeLineCap = PenLineCap.Round;
            }

            box.Child = path;
        }
        catch
        {
            // No render interface: the gutter keeps its size and stays empty.
        }

        return box;
    }

    // The section line, rgba(255,255,255,0.1) (EXACT, HOME m679:48457). Set on
    // the separator itself rather than left to a style: a menu theme's own
    // separator brush is much brighter and wins wherever the style does not
    // reach.
    private static readonly IBrush SeparatorBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF"));

    private static Control BuildSeparator() => new Separator
    {
        Height = SeparatorHeight,
        Background = SeparatorBrush,
        Margin = new Thickness(SeparatorInset, SeparatorTopMargin, SeparatorInset, 0),
    };

    /// <summary>
    /// The section label. Built as a menu item wearing the `sectionHeader`
    /// class rather than as a loose TextBlock, because a menu wraps any child
    /// that is not already a menu item in one, and a wrapped label would pick
    /// up the 98 px row height and become focusable. Never enabled: a header is
    /// not a row and focus must skip it.
    /// </summary>
    private static Control BuildSectionHeader(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = ShellFontSize.Small,
            Foreground = HeaderBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(SectionHeaderInset, 0, 0, 0),
        };

        var header = new MenuItem
        {
            Header = label,
            MinHeight = SectionHeaderHeight,
            Padding = new Thickness(0),
            Margin = new Thickness(0, SeparatorTopMargin, 0, 5),
            IsEnabled = false,
            IsHitTestVisible = false,
        };

        // Named so nothing else styles it; the shell's own header rules live on
        // the App.axaml `sectionHeader` class, which the markup menu still uses.
        header.Classes.Add("shellSectionHeader");
        return header;
    }

    private void PushFocusRect(MenuItem row)
    {
        try
        {
            if (ShellFocusRing.For(row) is not { } ring)
            {
                return;
            }

            if (row.Bounds.Width <= 0 || row.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            ring.Radius = ShellDialog.BorderRadius;
            ring.Claim(this, new Rect(row.Bounds.Size).TransformToAABB(transform));
        }
        catch
        {
            // A menu still opening simply leaves the ring where it is.
        }
    }
}
