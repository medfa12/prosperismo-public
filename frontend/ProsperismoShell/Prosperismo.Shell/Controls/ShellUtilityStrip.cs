// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The hub's utility strip, HOME m171 (<c>UtilityContainer</c>).
///
/// <code>
/// container:     { maxWidth: 416, marginTop: 8, flexDirection: "row" }
/// iconContainer: { width: 56, marginLeft: 48, alignItems: "center" }
/// icon:          { width: 56, height: 56 }
/// textContainer: { position: "absolute", top: 56, width: 336, marginTop: 16 }
/// text:          { fontSize: FontSizePS.SizeXSmall }
/// </code>
///
/// It is the same icon rhythm as the nav band - a 56 px icon on a 48 px gap,
/// with an absolutely placed label hanging below it - but not the same
/// component, and the two differ where it matters: the nav band's label strip
/// is 368 wide sitting 4 px under its icon (HOME m143), this one is 336 wide
/// sitting 16 px under. Sharing one set of numbers between them would be wrong
/// in both places.
/// </summary>
public static class ShellUtilityStripMetrics
{
    /// <summary><c>container.maxWidth</c>. The strip never grows past this.</summary>
    public const double MaxWidth = 416.0;

    /// <summary><c>container.marginTop</c>.</summary>
    public const double MarginTop = 8.0;

    /// <summary><c>icon</c> is 56 square, as in the nav band.</summary>
    public const double IconSize = 56.0;

    /// <summary><c>iconContainer.marginLeft</c>: the gap before each icon.</summary>
    public const double IconMarginLeft = 48.0;

    /// <summary>Icon to icon distance, the gap plus the box.</summary>
    public const double IconPitch = IconSize + IconMarginLeft;

    /// <summary><c>textContainer.top</c>: the label hangs from the icon's foot.</summary>
    public const double LabelTop = IconSize;

    /// <summary>
    /// <c>textContainer.width</c>. Wider than the icon so a long label spreads
    /// either side rather than wrapping, and narrower than the nav band's 368.
    /// </summary>
    public const double LabelWidth = 336.0;

    /// <summary>
    /// <c>textContainer.marginTop</c>. Four times the nav band's, which is what
    /// gives the hub's utility labels their looser feel.
    /// </summary>
    public const double LabelMarginTop = 16.0;

    /// <summary>Width the strip occupies for <paramref name="count"/> icons,
    /// held to <see cref="MaxWidth"/>.</summary>
    public static double WidthFor(int count) =>
        Math.Min(MaxWidth, Math.Max(0, count) * IconPitch);

    /// <summary>How many icons fit before the strip hits its cap.</summary>
    public static int IconsThatFit => (int)(MaxWidth / IconPitch);
}

/// <summary>One icon in a <see cref="ShellUtilityStrip"/>.</summary>
public sealed record ShellUtilityItem
{
    public ShellUtilityItem(string label, string glyph, object? tag = null)
    {
        Label = label ?? string.Empty;
        Glyph = glyph ?? string.Empty;
        Tag = tag;
    }

    /// <summary>Caption under the icon, shown only while it is focused.</summary>
    public string Label { get; init; }

    /// <summary>Mark drawn inside the 56 px box.</summary>
    public string Glyph { get; init; }

    /// <summary>Caller payload round-tripped through the strip's events.</summary>
    public object? Tag { get; init; }
}

/// <summary>Payload for <see cref="ShellUtilityStrip"/>'s events.</summary>
public sealed class ShellUtilityEventArgs : EventArgs
{
    public ShellUtilityEventArgs(int index, ShellUtilityItem? item)
    {
        Index = index;
        Item = item;
    }

    /// <summary>Focused index, or -1 when the strip is empty.</summary>
    public int Index { get; }

    /// <summary>The focused item, or null.</summary>
    public ShellUtilityItem? Item { get; }
}

/// <summary>
/// The hub's utility strip drawn to the console's geometry
/// (<see cref="ShellUtilityStripMetrics"/>): a row of 56 px icons on a 48 px
/// gap, each with a label that hangs 16 px below it and is only shown for the
/// focused icon.
/// </summary>
public sealed class ShellUtilityStrip : TemplatedControl
{
    private static readonly IBrush IconBrush = Brushes.White;

    private static readonly IBrush IconPlateBrush =
        new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    /// <summary>Opacity of an icon the strip is not pointing at (HOME m96).</summary>
    public const double UnfocusedOpacity = 0.6;

    public static readonly StyledProperty<IReadOnlyList<ShellUtilityItem>?> ItemsProperty =
        AvaloniaProperty.Register<ShellUtilityStrip, IReadOnlyList<ShellUtilityItem>?>(nameof(Items));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellUtilityStrip, int>(nameof(SelectedIndex), -1);

    /// <summary>Authored pixels to host pixels.</summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ShellUtilityStrip, double>(nameof(Scale), 1.0);

    private StackPanel? _host;
    private readonly List<Border> _icons = new();
    private readonly List<TextBlock> _labels = new();

    public ShellUtilityStrip()
    {
        Focusable = true;
        Template = BuildTemplate();
    }

    /// <summary>Raised when the focused icon changes.</summary>
    public event EventHandler<ShellUtilityEventArgs>? SelectionChanged;

    /// <summary>Raised when an icon is activated.</summary>
    public event EventHandler<ShellUtilityEventArgs>? ItemActivated;

    public IReadOnlyList<ShellUtilityItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>Icons currently held.</summary>
    public int Count => Items?.Count ?? 0;

    /// <summary>The focused icon, or null.</summary>
    public ShellUtilityItem? SelectedItem =>
        Items is { } items && SelectedIndex >= 0 && SelectedIndex < items.Count
            ? items[SelectedIndex]
            : null;

    /// <summary>Authored width the strip occupies at its current count.</summary>
    public double StripWidth => ShellUtilityStripMetrics.WidthFor(Count);

    /// <summary>Moves the focus without wrapping.</summary>
    public void MoveFocus(int delta)
    {
        if (Count == 0)
        {
            return;
        }

        SetSelectedIndex(Math.Clamp(SelectedIndex + delta, 0, Count - 1));
    }

    /// <summary>Focuses an icon, clamped to the strip's range.</summary>
    public void SetSelectedIndex(int index)
    {
        if (Count == 0)
        {
            SetCurrentValue(SelectedIndexProperty, -1);
            return;
        }

        SetCurrentValue(SelectedIndexProperty, Math.Clamp(index, 0, Count - 1));
    }

    /// <summary>Activates the focused icon.</summary>
    public void ActivateSelected()
    {
        if (SelectedItem is { } item)
        {
            ItemActivated?.Invoke(this, new ShellUtilityEventArgs(SelectedIndex, item));
        }
    }

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
            UpdateVisuals();
            SelectionChanged?.Invoke(this, new ShellUtilityEventArgs(SelectedIndex, SelectedItem));
        }
        else if (change.Property == ScaleProperty)
        {
            Rebuild();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                return;
            case Key.Right:
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
        _host = e.NameScope.Find<StackPanel>("PART_Icons");
        Rebuild();
    }

    private static FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var icons = new StackPanel
        {
            Name = "PART_Icons",
            Orientation = Orientation.Horizontal,
        };
        icons.RegisterInNameScope(ns);
        return icons;
    });

    private void Rebuild()
    {
        if (_host is null)
        {
            return;
        }

        double scale = Scale > 0 ? Scale : 1.0;
        MaxWidth = ShellUtilityStripMetrics.MaxWidth * scale;

        // `container.marginTop: 8` is the strip's offset from whatever precedes
        // it in the source's flex column. It is deliberately NOT applied here:
        // a host that positions the strip explicitly owns its Margin, and a
        // control that overwrites its parent's placement will silently ignore
        // it. The number is on ShellUtilityStripMetrics for a host to use.

        _host.Children.Clear();
        _icons.Clear();
        _labels.Clear();

        var items = Items;
        if (items is null)
        {
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];

            var glyph = new TextBlock
            {
                Text = item.Glyph,
                Foreground = IconBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var icon = new Border
            {
                Width = ShellUtilityStripMetrics.IconSize * scale,
                Height = ShellUtilityStripMetrics.IconSize * scale,
                CornerRadius = new CornerRadius(ShellUtilityStripMetrics.IconSize * scale / 2.0),
                Background = IconPlateBrush,
                Child = glyph,
            };

            // The label is absolutely placed under the icon and centred on it,
            // so a long caption spreads either side instead of widening the
            // strip.
            var label = new TextBlock
            {
                Text = item.Label,
                Foreground = IconBrush,
                Width = ShellUtilityStripMetrics.LabelWidth * scale,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, ShellUtilityStripMetrics.LabelMarginTop * scale, 0, 0),
                IsVisible = false,
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(icon);
            stack.Children.Add(label);

            var slot = new Border
            {
                Width = ShellUtilityStripMetrics.IconSize * scale,
                Margin = new Thickness(ShellUtilityStripMetrics.IconMarginLeft * scale, 0, 0, 0),
                Child = stack,
                ClipToBounds = false,
            };

            int captured = i;
            slot.PointerEntered += (_, _) => SetSelectedIndex(captured);
            slot.PointerPressed += (_, _) =>
            {
                SetSelectedIndex(captured);
                ActivateSelected();
            };

            _icons.Add(slot);
            _labels.Add(label);
            _host.Children.Add(slot);
        }

        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        for (int i = 0; i < _icons.Count; i++)
        {
            bool focused = i == SelectedIndex;
            _icons[i].Opacity = focused ? 1.0 : UnfocusedOpacity;

            // Only the focused icon names itself, the way the band does.
            _labels[i].IsVisible = focused;
        }
    }
}
