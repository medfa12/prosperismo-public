// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>One top-level category from NPXS40008's categoriesList model.</summary>
public sealed record ShellSettingsCategory(
    string ItemId,
    string Label,
    string? IconId,
    string Target);

public sealed class ShellSettingsCategoryEventArgs : EventArgs
{
    public ShellSettingsCategoryEventArgs(ShellSettingsCategory category) => Category = category;

    public ShellSettingsCategory Category { get; }
}

/// <summary>
/// The first bounded Settings integration: NPXS40008's top-level category list
/// with its recovered frame, order, icons and focus navigation.
///
/// <para>The native <c>MenuListItemPS</c> fill remains unrecovered and is not
/// guessed. The two-unit separator is different: NPXS40008 module 24 declares
/// it directly, and the retail capture confirms the icon/title inset.</para>
/// </summary>
public sealed class ShellSettingsCategoryList : Panel
{
    // NPXS40008.js module 24 (StyleValues), exact.
    public const double ListTop = 186;
    public const double ListLeft = 304;
    public const double ListWidth = 1312;
    public const double ListHeight = 894;
    // Native MenuListItemPS owns this value, so it is absent from the JS. A
    // 980x552 retail capture measures adjacent row centres 51-52 px apart;
    // scaled back to 1920x1080 that is 100-102 units. 102 is the capture metric,
    // not the unrelated 152-unit saved-data row previously borrowed here.
    public const double CapturedRowPitch = 102;
    public const double FocusMargin = 3;

    private static readonly IBrush SeparatorBrush =
        new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
    // Reused from NPXS40008's saved-data/LongTextListItem family. These values
    // are exact for those widgets, not proof of MenuListItemPS internals.
    public const double SeparatorMargin = 16;
    public const double ImageMarginRight = 20;

    // IconPS's authoring box and LongTextListItem's title token are recovered.
    public const double IconSize = 64;
    public const double TitleMarginLeft = 16;
    public const double TitleMarginRight = 48;
    public const double TitleMarginTop = 27;
    public const double TitleMarginBottom = 27;

    private const int VisibleRows = 9;
    public const int DefaultSelectedIndex = 0;

    /// <summary>
    /// Prosperismo's settings groups rendered through the recovered NPXS40008
    /// list contract. The content is intentionally ours; the layout, focus and
    /// icon plumbing are the console shell's.
    /// </summary>
    public static IReadOnlyList<ShellSettingsCategory> Categories { get; } =
    [
        new("id_prosperismo_general", "General", "system", "ProsperismoGeneral"),
        new("id_prosperismo_graphics", "Graphics", "screen_and_video", "ProsperismoGraphics"),
        new("id_prosperismo_audio_ui", "Audio and Interface", "sound_speaking", "ProsperismoAudioUi"),
        new("id_prosperismo_emulation", "Emulation", "games_and_apps", "ProsperismoEmulation"),
        new("id_prosperismo_logging", "Logging", "notification", "ProsperismoLogging"),
        new("id_prosperismo_environment", "Environment", "network", "ProsperismoEnvironment"),
        // commonly replaced by jailbreak payloads (the local 12.40 oracle is
        // neutral information pictogram instead of inheriting modified art.
        new("id_prosperismo_about", "About Prosperismo", "information", "ProsperismoAbout"),
    ];

    private readonly Canvas _viewport;
    private readonly ShellSettingsRouteTransition? _routeTransition;
    private readonly Dictionary<int, Control> _visibleRows = new();
    private int _selectedIndex = DefaultSelectedIndex;
    private int _firstVisibleIndex;

    public ShellSettingsCategoryList()
    {
        Width = Ps5DesignSpace.Width;
        Height = Ps5DesignSpace.Height;
        Background = Brushes.Transparent;
        Focusable = true;
        ClipToBounds = true;

        var heading = new TextBlock
        {
            Text = "Settings",
            FontSize = Ps5FontScale.SizeLarge,
            FontWeight = FontWeight.Light,
            Foreground = Brushes.White,
            Margin = new Thickness(96, 82, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (Ps5FontLibrary.TryGet(Ps5FontFace.Light) is { } headingFont)
        {
            heading.FontFamily = headingFont;
        }
        Children.Add(heading);

        _viewport = new Canvas
        {
            Width = ListWidth,
            Height = ListHeight,
            Margin = new Thickness(ListLeft, ListTop, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
        };
        Children.Add(_viewport);

        _routeTransition = new ShellSettingsRouteTransition(this);

        GotFocus += (_, _) =>
        {
            RebuildRows();
            QueueFocusRect();
        };
        EffectiveViewportChanged += (_, _) => QueueFocusRect();
        LostFocus += (_, _) =>
        {
            RebuildRows();
            ShellFocusRing.For(this)?.Release(this);
        };
        AttachedToVisualTree += (_, _) => QueueFocusRect();
        RebuildRows();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _routeTransition?.Enter();
            }
            else
            {
                _routeTransition?.Cancel();
            }
        }
    }

    public event EventHandler<ShellSettingsCategoryEventArgs>? CategoryActivated;

    /// <summary>
    /// Raised when the native back action leaves the root Settings route.
    /// The window owns the route stack, so the list reports the action rather
    /// than reaching across the presentation boundary itself.
    /// </summary>
    public event EventHandler? BackRequested;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetSelectedIndex(value);
    }

    public ShellSettingsCategory SelectedCategory => Categories[_selectedIndex];

    /// <summary>Moves one row, clamped at the list edges like ListViewPS.</summary>
    public void MoveSelection(int delta)
    {
        SetSelectedIndex(Math.Clamp(_selectedIndex + Math.Sign(delta), 0, Categories.Count - 1));
    }

    public void ActivateSelected() =>
        CategoryActivated?.Invoke(this, new ShellSettingsCategoryEventArgs(SelectedCategory));

    /// <summary>Requests the root Settings route's native back transition.</summary>
    public void RequestBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Back:
                RequestBack();
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
        }
    }

    private void SetSelectedIndex(int value)
    {
        var selected = Math.Clamp(value, 0, Categories.Count - 1);
        if (_selectedIndex == selected)
        {
            QueueFocusRect();
            return;
        }

        _selectedIndex = selected;
        if (_selectedIndex < _firstVisibleIndex)
        {
            _firstVisibleIndex = _selectedIndex;
        }
        else if (_selectedIndex >= _firstVisibleIndex + VisibleRows)
        {
            _firstVisibleIndex = _selectedIndex - VisibleRows + 1;
        }

        RebuildRows();
        QueueFocusRect();
    }

    private void RebuildRows()
    {
        _viewport.Children.Clear();
        _visibleRows.Clear();
        var last = Math.Min(Categories.Count, _firstVisibleIndex + VisibleRows);
        for (var index = _firstVisibleIndex; index < last; index++)
        {
            var category = Categories[index];
            var capturedIndex = index;
            var row = BuildRow(category);
            Canvas.SetTop(row, (index - _firstVisibleIndex) * CapturedRowPitch);
            Canvas.SetLeft(row, 0);
            row.PointerEntered += (_, _) =>
            {
                SetSelectedIndex(capturedIndex);
                Focus();
            };
            row.PointerPressed += (_, e) =>
            {
                SetSelectedIndex(capturedIndex);
                Focus();
                ShellFocusRing.For(this)?.SetPressed(true);
                e.Handled = true;
            };
            row.PointerReleased += (_, e) =>
            {
                ShellFocusRing.For(this)?.SetPressed(false);
                if (_selectedIndex == capturedIndex)
                {
                    ActivateSelected();
                }
                e.Handled = true;
            };
            _viewport.Children.Add(row);
            _visibleRows[index] = row;
        }
    }

    private static Control BuildRow(ShellSettingsCategory category)
    {
        var row = new Grid
        {
            Width = ListWidth,
            Height = CapturedRowPitch,
            Background = Brushes.Transparent,
            ColumnDefinitions = new ColumnDefinitions($"{TitleMarginLeft},{IconSize},{ImageMarginRight},*,{TitleMarginRight}"),
        };

        var icon = new Ps5IconPresenter
        {
            IconId = category.IconId,
            Width = IconSize,
            Height = IconSize,
            VerticalAlignment = VerticalAlignment.Center,
            Tint = Colors.White,
            // Settings focus is an outline/wash around the row. Unlike the
            // compact HOME icon button, its pictogram never inverts.
            OverrideDeclaredFill = true,
        };
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);

        var title = new TextBlock
        {
            Text = category.Label,
            FontSize = Ps5FontScale.SizeNormal,
            FontWeight = FontWeight.Light,
            LineHeight = Ps5FontScale.LineHeight(Ps5FontToken.SizeNormal),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (Ps5FontLibrary.TryGet(Ps5FontFace.Light) is { } titleFont)
        {
            title.FontFamily = titleFont;
        }
        Grid.SetColumn(title, 3);
        row.Children.Add(title);

        // SettingsList module 24: absolute, width 100%, height 2, bottom 0.
        // MenuListItemPS starts the visible hairline at the title gutter, as
        // confirmed by the retail frame (the icon column remains open).
        var separator = new Border
        {
            Height = ShellSettingsMetrics.SeparatorHeight,
            Margin = new Thickness(TitleMarginLeft + IconSize + ImageMarginRight, 0, 0, 0),
            Background = SeparatorBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(separator, 0);
        Grid.SetColumnSpan(separator, 5);
        row.Children.Add(separator);

        return row;
    }

    private void QueueFocusRect()
    {
        Dispatcher.UIThread.Post(PushFocusRect, DispatcherPriority.Render);
    }

    private void PushFocusRect()
    {
        if (!IsEffectivelyVisible || !IsFocused || ShellFocusRing.For(this) is not { } ring ||
            !_visibleRows.TryGetValue(_selectedIndex, out var row) ||
            row.TransformToVisual(ring) is not { } transform)
        {
            return;
        }

        // Settings' capture-approved treatment keeps the line on the same
        // arranged owner rectangle as the travelling AreaFocus wash.  This
        // also selects the target-resolution line evaluator: the generic
        // native path has no Settings line-scale input and otherwise restores
        // its full 3 px band instead of this surface's 1.5 px line.
        var rowRect = new Rect(row.Bounds.Size);
        ring.Radius = 0;
        ring.LineScale = ShellSettingsMetrics.FocusLineScale;
        ring.Claim(this, rowRect.TransformToAABB(transform), lineMatchesArea: true);
    }
}
