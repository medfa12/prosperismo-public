// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Geometry of the function-control panel, the flyout a nav-band icon opens.
///
/// Recovered from three stylesheets: the panel box and its anchor from HOME
/// m143, the header from m156, and the list rows from m155.
///
/// <code>
/// FCFocusLayer: { marginTop: 126, marginLeft: 1188 }
/// FCContainer:  { position: "absolute", width: 652,
///                 minHeight: 216, maxHeight: 810, borderRadius: 16 }
/// header:       { height: 80, flexDirection: "row", padding: 24, opacity: .7 }
/// headerIcon:   { width: 48, height: 48, borderRadius: 8 }
/// listItem:     { flexDirection: "row", minHeight: 98, alignItems: "center" }
/// rightIcon:    { width: 48, height: 48 }   marginHorizontal 16
/// </code>
///
/// The anchor is absolute, not relative to whichever icon opened the panel: the
/// console drops every function control at the same x, immediately below the
/// 126 px nav band.
/// </summary>
public static class ShellFunctionPanelMetrics
{
    /// <summary><c>FCFocusLayer.marginLeft</c>.</summary>
    public const double AnchorX = 1188.0;

    /// <summary><c>FCFocusLayer.marginTop</c>, which is SYSTEM_HEIGHT.</summary>
    public const double AnchorY = 126.0;

    /// <summary><c>FCContainer.width</c>.</summary>
    public const double Width = 652.0;

    /// <summary><c>FCContainer.minHeight</c>.</summary>
    public const double MinHeight = 216.0;

    /// <summary><c>FCContainer.maxHeight</c>.</summary>
    public const double MaxHeight = 810.0;

    /// <summary><c>FCContainer.borderRadius</c>.</summary>
    public const double CornerRadius = 16.0;

    /// <summary><c>header.height</c>.</summary>
    public const double HeaderHeight = 80.0;

    /// <summary><c>header.padding</c>.</summary>
    public const double HeaderPadding = 24.0;

    /// <summary><c>header.opacity</c>. The header is deliberately quieter than
    /// the rows under it.</summary>
    public const double HeaderOpacity = 0.7;

    /// <summary><c>headerIcon</c> and <c>rightIcon</c> are both 48 square.</summary>
    public const double IconSize = 48.0;

    /// <summary><c>LEFT_ICON_SIZE</c> for ordinary function rows.</summary>
    public const double LeftIconSize = 56.0;

    /// <summary><c>LEFT_ICON_MARGIN_LEFT</c>.</summary>
    public const double LeftIconMarginLeft = 16.0;

    /// <summary><c>LEFT_ICON_MARGIN_RIGHT</c>.</summary>
    public const double LeftIconMarginRight = 20.0;

    /// <summary>List body horizontal inset.</summary>
    public const double ListBodyMarginHorizontal = 8.0;

    /// <summary>
    /// Direct-tour line band is two authored pixels; the shared native line is
    /// three, so this is its presentation scale for function-list rows.
    /// </summary>
    public const double FocusLineScale = 2.0 / 3.0;

    /// <summary><c>headerIcon.borderRadius</c>.</summary>
    public const double HeaderIconRadius = 8.0;

    /// <summary><c>headerIconContainer.marginRight</c>.</summary>
    public const double HeaderIconMarginRight = 16.0;

    /// <summary><c>listItem.minHeight</c>. A row is at least this tall and
    /// grows with its content.</summary>
    public const double ListItemMinHeight = 98.0;

    /// <summary><c>menuListBody.marginBottom</c>.</summary>
    public const double ListBodyMarginBottom = 16.0;

    /// <summary>Normal-font section header line height.</summary>
    public const double SectionHeaderHeight = 34.0;

    public const double SectionHeaderBottomMargin = 8.0;

    public const double SectionHeaderTopMargin = 24.0;

    public const double SectionHeaderHorizontalMargin = 16.0;

    /// <summary><c>rightIconContainer.marginHorizontal</c>.</summary>
    public const double RightIconMarginHorizontal = 16.0;

    /// <summary><c>leftIcon.marginTop</c>, with <c>alignSelf: "flex-start"</c>
    /// so a left icon hangs from the top of its row rather than centring.</summary>
    public const double LeftIconMarginTop = 21.0;

    /// <summary><c>menuListItemButtonProfileContainer.height</c>.</summary>
    public const double ProfileRowHeight = 90.0;

    /// <summary><c>menuListItemButtonProfileContainer.marginBottom</c>.</summary>
    public const double ProfileRowMarginBottom = 2.0;

    /// <summary>
    /// Height the panel settles at for <paramref name="rowCount"/> rows, held
    /// between the source's own minimum and maximum.
    /// </summary>
    public static double HeightFor(int rowCount)
    {
        double content = HeaderHeight +
            (Math.Max(0, rowCount) * ListItemMinHeight) +
            ListBodyMarginBottom;
        return Math.Clamp(content, MinHeight, MaxHeight);
    }

    /// <summary>Settled height for rows with template-specific minimums.</summary>
    public static double HeightFor(IReadOnlyList<ShellFunctionPanelItem>? items)
    {
        double content = HeaderHeight + ListBodyMarginBottom;
        if (items is not null)
        {
            bool hasSection = false;
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.SectionHeader))
                {
                    content += SectionHeaderHeight + SectionHeaderBottomMargin;
                    if (hasSection)
                    {
                        content += SectionHeaderTopMargin;
                    }
                    hasSection = true;
                }
                content += (item.ExactHeight ?? Math.Max(ListItemMinHeight, item.MinHeight)) +
                    item.BottomMargin;
            }
        }
        return Math.Clamp(content, MinHeight, MaxHeight);
    }

    /// <summary>True once the rows overflow the panel and it has to scroll.</summary>
    public static bool Scrolls(int rowCount) =>
        HeaderHeight +
        (Math.Max(0, rowCount) * ListItemMinHeight) +
        ListBodyMarginBottom > MaxHeight;
}

/// <summary>One row in a <see cref="ShellFunctionPanel"/>.</summary>
public sealed record ShellFunctionPanelItem
{
    public ShellFunctionPanelItem(string title, string? glyph = null, object? tag = null)
    {
        Title = title ?? string.Empty;
        Glyph = glyph;
        Tag = tag;
    }

    /// <summary>The row's label.</summary>
    public string Title { get; init; }

    /// <summary>Mark drawn in the row's trailing 48 px icon box, if any.</summary>
    public string? Glyph { get; init; }

    /// <summary>Caller payload round-tripped through the panel's events.</summary>
    public object? Tag { get; init; }

    /// <summary>Optional second line used by embedded notification-list rows.</summary>
    public string? SecondaryText { get; init; }

    /// <summary>
    /// Optional NPXS40003 section label drawn immediately before this row.
    /// The first section has no top margin; later sections add 24 authored px.
    /// </summary>
    public string? SectionHeader { get; init; }

    /// <summary>Optional localized timestamp used by ToastHeaderForList.</summary>
    public string? TimestampText { get; init; }

    /// <summary>Whether ToastHeaderForList should show its 24 px new marker.</summary>
    public bool IsNew { get; init; }

    /// <summary>Optional 64 px notification artwork.</summary>
    public IImage? LeadingImage { get; init; }

    public string? LeadingIconId { get; init; }

    /// <summary>Optional state text at the trailing edge, e.g. Online Status.</summary>
    public string? TrailingText { get; init; }

    /// <summary>Optional lamp drawn after <see cref="TrailingText"/>.</summary>
    public Color? TrailingIndicatorColor { get; init; }

    /// <summary>
    /// Optional UI3 switch rendered at the trailing edge. The panel row owns
    /// activation, so the embedded control is deliberately non-interactive.
    /// </summary>
    public bool? ToggleValue { get; init; }

    /// <summary>
    /// Previous switch value used for one explicit state-change transition.
    /// Null makes rebuilt rows snap to <see cref="ToggleValue"/>, which keeps
    /// focus navigation and backend list refreshes from replaying animation.
    /// </summary>
    public bool? ToggleAnimationStartValue { get; init; }

    /// <summary>Template-specific row minimum; ordinary function rows remain 98.</summary>
    public double MinHeight { get; init; } = ShellFunctionPanelMetrics.ListItemMinHeight;

    /// <summary>Exact template height for native ButtonBasic rows.</summary>
    public double? ExactHeight { get; init; }

    /// <summary>Authored air after this row.</summary>
    public double BottomMargin { get; init; }

    /// <summary>Whether the row can be chosen.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Centers text for ButtonBasic and confirmation surfaces.</summary>
    public bool ContentCentered { get; init; }

    /// <summary>Width of the focusable surface relative to the panel.</summary>
    public double FocusWidthRatio { get; init; } = 1.0;

    /// <summary>Whether the list's 2-unit separator is drawn below this row.</summary>
    public bool ShowSeparator { get; init; } = true;

    /// <summary>Informational rows can be disabled without being visually muted.</summary>
    public bool DimWhenDisabled { get; init; } = true;

    public bool IsBold { get; init; }

    public double? SecondaryLineHeight { get; init; }

    public bool SecondaryWrap { get; init; }

    public double? ContentHorizontalMargin { get; init; }
}

/// <summary>Payload for <see cref="ShellFunctionPanel"/>'s events.</summary>
public sealed class ShellFunctionPanelEventArgs : EventArgs
{
    public ShellFunctionPanelEventArgs(int index, ShellFunctionPanelItem? item)
    {
        Index = index;
        Item = item;
    }

    /// <summary>Focused row, or -1 when the panel is empty.</summary>
    public int Index { get; }

    /// <summary>The focused row, or null when the panel is empty.</summary>
    public ShellFunctionPanelItem? Item { get; }
}

/// <summary>
/// The function-control panel: the flyout a nav-band icon opens, drawn to the
/// console's own geometry (<see cref="ShellFunctionPanelMetrics"/>).
///
/// It is code-templated so it can be dropped into a window or a preview without
/// an external theme, and its navigation state is independent of the render
/// surface so it stays testable headless.
/// </summary>
public sealed class ShellFunctionPanel : TemplatedControl
{
    /// <summary>Panel fill. The shell's own dialog plate colour.</summary>
    private static readonly IBrush PlateBrush =
        new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0F));

    private static readonly IBrush TextBrush = Brushes.White;

    private static readonly IBrush RowHighlightBrush =
        new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    public static readonly StyledProperty<IReadOnlyList<ShellFunctionPanelItem>?> ItemsProperty =
        AvaloniaProperty.Register<ShellFunctionPanel, IReadOnlyList<ShellFunctionPanelItem>?>(nameof(Items));

    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<ShellFunctionPanel, string?>(nameof(Header));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellFunctionPanel, int>(nameof(SelectedIndex), -1);

    /// <summary>Authored pixels to host pixels, for a scaled surface.</summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ShellFunctionPanel, double>(nameof(Scale), 1.0);

    private StackPanel? _rowHost;
    private TextBlock? _headerText;
    private Border? _header;
    private readonly List<Border> _rows = new();
    private readonly List<Ps5ToggleSwitch> _toggles = new();

    public ShellFunctionPanel()
    {
        Focusable = true;
        Template = BuildTemplate();
        GotFocus += (_, _) => QueueFocusRect();
        LostFocus += (_, _) => ShellFocusRing.For(this)?.Release(this);
    }

    /// <summary>Raised when the focused row changes.</summary>
    public event EventHandler<ShellFunctionPanelEventArgs>? SelectionChanged;

    /// <summary>Raised when a row is activated.</summary>
    public event EventHandler<ShellFunctionPanelEventArgs>? ItemActivated;

    public IReadOnlyList<ShellFunctionPanelItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The focused native row surface, used as a popup-menu anchor.</summary>
    public Control? SelectedRowAnchor =>
        SelectedIndex >= 0 && SelectedIndex < _rows.Count ? _rows[SelectedIndex] : null;

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>Rows currently held.</summary>
    public int Count => Items?.Count ?? 0;

    internal IReadOnlyList<Ps5ToggleSwitch> RenderedToggles => _toggles;

    /// <summary>The focused row, or null.</summary>
    public ShellFunctionPanelItem? SelectedItem =>
        Items is { } items && SelectedIndex >= 0 && SelectedIndex < items.Count
            ? items[SelectedIndex]
            : null;

    /// <summary>Settled height at the current row count, in authored pixels.</summary>
    public double PanelHeight => ShellFunctionPanelMetrics.HeightFor(Items);

    /// <summary>Moves the focus by <paramref name="delta"/> rows, without wrapping.</summary>
    public void MoveFocus(int delta)
    {
        if (Count == 0 || delta == 0)
        {
            return;
        }

        var direction = Math.Sign(delta);
        var next = SelectedIndex;
        while (true)
        {
            var candidate = next + direction;
            if (candidate < 0 || candidate >= Count)
            {
                return;
            }

            next = candidate;
            if (Items?[next].IsEnabled == true)
            {
                SetSelectedIndex(next);
                return;
            }
        }
    }

    /// <summary>Focuses a row, clamped to the panel's range.</summary>
    public void SetSelectedIndex(int index)
    {
        if (Count == 0)
        {
            SetCurrentValue(SelectedIndexProperty, -1);
            return;
        }

        SetCurrentValue(SelectedIndexProperty, Math.Clamp(index, 0, Count - 1));
    }

    /// <summary>Activates the focused row.</summary>
    public void ActivateSelected()
    {
        if (SelectedItem is { IsEnabled: true } item)
        {
            ItemActivated?.Invoke(this, new ShellFunctionPanelEventArgs(SelectedIndex, item));
        }
    }

    /// <summary>Re-publishes the selected row to the shared native focus plane.</summary>
    public void RefreshFocusRect() => QueueFocusRect();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (Count > 0 && SelectedIndex < 0)
            {
                SetCurrentValue(SelectedIndexProperty, 0);
            }
            else if (Count == 0)
            {
                SetCurrentValue(SelectedIndexProperty, -1);
            }

            Rebuild();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            UpdateRowVisuals();
            QueueFocusRect();
            SelectionChanged?.Invoke(this, new ShellFunctionPanelEventArgs(SelectedIndex, SelectedItem));
        }
        else if (change.Property == ScaleProperty || change.Property == HeaderProperty)
        {
            Rebuild();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                MoveFocus(-1);
                e.Handled = true;
                return;
            case Key.Down:
                MoveFocus(1);
                e.Handled = true;
                return;
            case Key.Enter:
                ActivateSelected();
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _rowHost = e.NameScope.Find<StackPanel>("PART_Rows");
        _headerText = e.NameScope.Find<TextBlock>("PART_HeaderText");
        _header = e.NameScope.Find<Border>("PART_Header");
        Rebuild();
        QueueFocusRect();
    }

    private FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var header = new Border
        {
            Name = "PART_Header",
            Height = ShellFunctionPanelMetrics.HeaderHeight,
            Padding = new Thickness(ShellFunctionPanelMetrics.HeaderPadding),
            Opacity = ShellFunctionPanelMetrics.HeaderOpacity,
        };
        header.RegisterInNameScope(ns);

        var headerText = new TextBlock
        {
            Name = "PART_HeaderText",
            Foreground = TextBrush,
            FontSize = ShellFontSize.Small,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        headerText.RegisterInNameScope(ns);
        header.Child = headerText;

        var rows = new StackPanel
        {
            Name = "PART_Rows",
            Orientation = Orientation.Vertical,
            Margin = new Thickness(
                ShellFunctionPanelMetrics.ListBodyMarginHorizontal,
                0,
                ShellFunctionPanelMetrics.ListBodyMarginHorizontal,
                ShellFunctionPanelMetrics.ListBodyMarginBottom),
        };
        rows.RegisterInNameScope(ns);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(header);
        stack.Children.Add(rows);

        var scroll = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        var plate = new Border
        {
            Name = "PART_Plate",
            Background = PlateBrush,
            CornerRadius = new CornerRadius(ShellFunctionPanelMetrics.CornerRadius),
            ClipToBounds = true,
            Child = scroll,
        };
        plate.RegisterInNameScope(ns);
        return plate;
    });

    private void Rebuild()
    {
        if (_rowHost is null)
        {
            return;
        }

        double scale = Scale > 0 ? Scale : 1.0;

        Width = ShellFunctionPanelMetrics.Width * scale;
        MinHeight = ShellFunctionPanelMetrics.MinHeight * scale;
        MaxHeight = ShellFunctionPanelMetrics.MaxHeight * scale;

        if (_headerText is not null)
        {
            _headerText.Text = Header ?? string.Empty;
        }

        if (_header is not null)
        {
            // A panel with no header still reserves nothing: the source only
            // renders the header block when there is one to render.
            _header.IsVisible = !string.IsNullOrEmpty(Header);
            _header.Height = ShellFunctionPanelMetrics.HeaderHeight * scale;
            _header.Padding = new Thickness(ShellFunctionPanelMetrics.HeaderPadding * scale);
        }

        _rowHost.Children.Clear();
        _rows.Clear();
        _toggles.Clear();

        var items = Items;
        if (items is null)
        {
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!string.IsNullOrWhiteSpace(item.SectionHeader))
            {
                var hasPriorSection = items.Take(i)
                    .Any(candidate => !string.IsNullOrWhiteSpace(candidate.SectionHeader));
                _rowHost.Children.Add(new Border
                {
                    Height = ShellFunctionPanelMetrics.SectionHeaderHeight * scale,
                    Margin = new Thickness(
                        ShellFunctionPanelMetrics.SectionHeaderHorizontalMargin * scale,
                        hasPriorSection
                            ? ShellFunctionPanelMetrics.SectionHeaderTopMargin * scale
                            : 0,
                        ShellFunctionPanelMetrics.SectionHeaderHorizontalMargin * scale,
                        ShellFunctionPanelMetrics.SectionHeaderBottomMargin * scale),
                    Child = new TextBlock
                    {
                        Text = item.SectionHeader,
                        Foreground = TextBrush,
                        FontSize = ShellFontSize.XSmall,
                        Opacity = 0.7,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }
            var hasLeading = item.LeadingImage is not null ||
                !string.IsNullOrWhiteSpace(item.LeadingIconId);

            var label = new TextBlock
            {
                Text = item.Title,
                Foreground = TextBrush,
                FontSize = ShellFontSize.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = item.ContentCentered ? TextAlignment.Center : TextAlignment.Left,
                HorizontalAlignment = item.ContentCentered
                    ? HorizontalAlignment.Center
                    : HorizontalAlignment.Stretch,
                FontWeight = item.IsBold ? FontWeight.Bold : FontWeight.Normal,
                Opacity = item.IsEnabled || !item.DimWhenDisabled ? 1.0 : 0.4,
            };

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 4 * scale,
                Margin = item.ContentHorizontalMargin is { } horizontalMargin
                    ? new Thickness(horizontalMargin * scale, 0)
                    : new Thickness(
                        item.ContentCentered
                        ? 0
                        : !hasLeading
                            ? ShellFunctionPanelMetrics.HeaderPadding * scale
                            : 0,
                        0,
                        0,
                        0),
            };
            textStack.Children.Add(label);
            if (!string.IsNullOrWhiteSpace(item.SecondaryText))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = item.SecondaryText,
                    Foreground = TextBrush,
                    FontSize = ShellFontSize.XXXSmall,
                    Opacity = item.IsEnabled || !item.DimWhenDisabled ? 0.7 : 0.32,
                    TextTrimming = item.SecondaryWrap
                        ? TextTrimming.None
                        : TextTrimming.CharacterEllipsis,
                    TextWrapping = item.SecondaryWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextAlignment = item.ContentCentered ? TextAlignment.Center : TextAlignment.Left,
                    HorizontalAlignment = item.ContentCentered
                        ? HorizontalAlignment.Center
                        : HorizontalAlignment.Stretch,
                    LineHeight = item.SecondaryLineHeight ?? double.NaN,
                });
            }

            var leading = new Border
            {
                Width = !hasLeading
                    ? 0
                    : item.LeadingImage is null
                        ? ShellFunctionPanelMetrics.LeftIconSize * scale
                        : 64 * scale,
                Height = !hasLeading
                    ? 0
                    : item.LeadingImage is null
                        ? ShellFunctionPanelMetrics.LeftIconSize * scale
                        : 64 * scale,
                Margin = !hasLeading
                    ? default
                    : item.LeadingImage is null
                        ? new Thickness(
                            ShellFunctionPanelMetrics.LeftIconMarginLeft * scale,
                            0,
                            ShellFunctionPanelMetrics.LeftIconMarginRight * scale,
                            0)
                        : new Thickness(24 * scale, 0, 20 * scale, 0),
                Child = item.LeadingImage is not null
                    ? new Image
                    {
                        Source = item.LeadingImage,
                        Stretch = Stretch.UniformToFill,
                    }
                    : string.IsNullOrWhiteSpace(item.LeadingIconId)
                        ? null
                        : new Ps5IconPresenter
                        {
                            IconId = item.LeadingIconId,
                            Tint = Colors.White,
                            OverrideDeclaredFill = true,
                        },
            };

            var trailingState = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12 * scale,
                Margin = string.IsNullOrWhiteSpace(item.TrailingText) &&
                    item.TrailingIndicatorColor is null &&
                    item.ToggleValue is null
                    ? default
                    : new Thickness(16 * scale, 0, 8 * scale, 0),
            };
            if (!string.IsNullOrWhiteSpace(item.TrailingText))
            {
                trailingState.Children.Add(new TextBlock
                {
                    Text = item.TrailingText,
                    Foreground = TextBrush,
                    FontSize = ShellFontSize.Normal,
                    Opacity = item.IsEnabled || !item.DimWhenDisabled ? 0.7 : 0.4,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (item.TrailingIndicatorColor is { } indicatorColor)
            {
                trailingState.Children.Add(new Ellipse
                {
                    Width = 12 * scale,
                    Height = 12 * scale,
                    Fill = new SolidColorBrush(indicatorColor),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            if (item.ToggleValue is { } toggleValue)
            {
                var toggle = new Ps5ToggleSwitch
                {
                    Width = 96 * scale,
                    Height = 48 * scale,
                    Focusable = false,
                    IsHitTestVisible = false,
                    IsToggleEnabled = false,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var animateStateChange = item.ToggleAnimationStartValue is { } startValue &&
                    startValue != toggleValue;
                toggle.SetState(
                    animateStateChange
                        ? item.ToggleAnimationStartValue.GetValueOrDefault()
                        : toggleValue,
                    animate: false);
                if (animateStateChange)
                {
                    toggle.SetState(toggleValue, animate: true);
                }
                _toggles.Add(toggle);
                trailingState.Children.Add(toggle);
            }

            var iconBox = new Border
            {
                Width = ShellFunctionPanelMetrics.IconSize * scale,
                Height = ShellFunctionPanelMetrics.IconSize * scale,
                Margin = new Thickness(ShellFunctionPanelMetrics.RightIconMarginHorizontal * scale, 0),
                Child = item.Glyph is null
                    ? null
                    : new TextBlock
                    {
                        Text = item.Glyph,
                        Foreground = TextBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
            };

            var timestamp = new TextBlock
            {
                Text = item.TimestampText ?? string.Empty,
                MinWidth = string.IsNullOrEmpty(item.TimestampText) ? 0 : 48 * scale,
                MaxWidth = string.IsNullOrEmpty(item.TimestampText) ? 0 : 80 * scale,
                Margin = string.IsNullOrEmpty(item.TimestampText)
                    ? default
                    : new Thickness(24 * scale, 24 * scale, 0, 0),
                FontSize = ShellFontSize.XXXSmall,
                Foreground = TextBrush,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var newMarker = new Border
            {
                Width = item.IsNew ? 24 * scale : 0,
                Height = item.IsNew ? 24 * scale : 0,
                Margin = item.IsNew
                    ? new Thickness(4 * scale, 0, 10 * scale, 0)
                    : default,
                VerticalAlignment = VerticalAlignment.Center,
                Child = item.IsNew
                    ? new Ellipse
                    {
                        Width = 8 * scale,
                        Height = 8 * scale,
                        Fill = Brushes.White,
                    }
                    : null,
            };

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
            Grid.SetColumn(leading, 0);
            content.Children.Add(leading);
            Grid.SetColumn(textStack, 1);
            content.Children.Add(textStack);
            Grid.SetColumn(timestamp, 2);
            content.Children.Add(timestamp);
            Grid.SetColumn(newMarker, 3);
            content.Children.Add(newMarker);
            Grid.SetColumn(trailingState, 4);
            content.Children.Add(trailingState);
            Grid.SetColumn(iconBox, 5);
            content.Children.Add(iconBox);

            var focusSurface = new Border
            {
                Height = item.ExactHeight is { } exactHeight ? exactHeight * scale : double.NaN,
                MinHeight = item.ExactHeight is null
                    ? Math.Max(ShellFunctionPanelMetrics.ListItemMinHeight, item.MinHeight) * scale
                    : 0,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderThickness = item.ShowSeparator
                    ? new Thickness(0, 0, 0, 2 * scale)
                    : default,
                Width = item.FocusWidthRatio is > 0 and < 1
                    ? ShellFunctionPanelMetrics.Width * item.FocusWidthRatio * scale
                    : double.NaN,
                HorizontalAlignment = item.FocusWidthRatio is > 0 and < 1
                    ? HorizontalAlignment.Center
                    : HorizontalAlignment.Stretch,
                Child = content,
            };
            var row = new Border
            {
                Height = focusSurface.Height,
                MinHeight = focusSurface.MinHeight,
                Margin = new Thickness(0, 0, 0, item.BottomMargin * scale),
                Child = focusSurface,
            };

            int captured = i;
            row.PointerEntered += (_, _) =>
            {
                if (item.IsEnabled)
                {
                    Focus();
                    SetSelectedIndex(captured);
                }
            };
            row.PointerPressed += (_, _) =>
            {
                if (item.IsEnabled)
                {
                    SetSelectedIndex(captured);
                    ActivateSelected();
                }
            };

            _rows.Add(focusSurface);
            _rowHost.Children.Add(row);
        }

        UpdateRowVisuals();
        QueueFocusRect();
    }

    private void UpdateRowVisuals()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].Background = i == SelectedIndex ? RowHighlightBrush : Brushes.Transparent;
        }
    }

    private void QueueFocusRect() =>
        Dispatcher.UIThread.Post(PushFocusRect, DispatcherPriority.Render);

    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (!IsEffectivelyVisible || !IsFocused ||
                SelectedIndex < 0 || SelectedIndex >= _rows.Count)
            {
                ring.Release(this);
                return;
            }

            var row = _rows[SelectedIndex];
            if (row.Bounds.Width <= 0 || row.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            ring.Radius = 0;
            ring.LineScale = ShellFunctionPanelMetrics.FocusLineScale;
            ring.Claim(this, new Rect(row.Bounds.Size).TransformToAABB(transform));
        }
        catch
        {
            // A panel entering/leaving the visual tree simply misses one frame.
        }
    }
}
