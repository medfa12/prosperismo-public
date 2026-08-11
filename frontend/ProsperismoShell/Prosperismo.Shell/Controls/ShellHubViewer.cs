// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Geometry and motion of the hub: the surface that takes over when the row
/// hands focus downwards.
///
/// From HOME m130, the module that owns the transition:
///
/// <code>
/// var _ = SYSTEM_HEIGHT + VERTICAL_HEIGHT_CHANGE;      // 126 + 40 = 166
/// homeContainer:               { transform: [{ translateY: [home, hub] -> [0, -_] }] }
/// experienceSwitcherContainer: { opacity:    [hidden, visible] -> [0, 1] }
/// // both driven by spring(..., SPRING_OPTIONS_FAST)
/// </code>
///
/// and from HomeContainer (m490) the hub's own inset:
///
/// <code>
/// marginTop: SCALED_EXP_SIZE - VERTICAL_HEIGHT_CHANGE   // 168 - 40 = 128
/// </code>
///
/// So opening the hub does not scroll a page: it lifts the whole home surface
/// by exactly the nav band plus the hub's own ride, taking the band and the row
/// off the top of the screen together, while the switcher fades rather than
/// sliding with it. The two motions are separate values on one spring, which is
/// why the row can be gone before the lift finishes.
/// </summary>
public static class ShellHubMetrics
{
    /// <summary>
    /// How far the home surface lifts,
    /// <c>SYSTEM_HEIGHT + VERTICAL_HEIGHT_CHANGE</c>.
    /// </summary>
    public const double HomeLift =
        ShellNavBand.SystemHeight + ShellTileRow.VerticalHeightChange;

    /// <summary>
    /// The hub's inset under the row,
    /// <c>SCALED_EXP_SIZE - VERTICAL_HEIGHT_CHANGE</c>.
    /// </summary>
    public const double MarginTop =
        ShellTileRow.ScaledExperienceSize - ShellTileRow.VerticalHeightChange;

    // ---- HubHeader, m402 with stylesheet m170 -----------------------------

    /// <summary><c>container.marginTop</c>, which is MINIMIZED_EXP_MARGIN_TOP.</summary>
    public const double HeaderMarginTop = ShellTileRow.MinimizedExpMarginTop;

    /// <summary><c>container.marginLeft</c>, which is MINIMIZED_EXP_MARGIN_LEFT.</summary>
    public const double HeaderMarginLeft = ShellTileRow.MinimizedExpMarginLeft;

    /// <summary><c>container.marginRight</c>.</summary>
    public const double HeaderMarginRight = ShellTileRow.ScaledExpMarginLeft;

    /// <summary><c>image</c>, which is MINIMIZED_EXP_SIZE.</summary>
    public const double HeaderIconSize = ShellTileRow.MinimizedExpSize;

    /// <summary>
    /// <c>image.borderRadius</c>. Note this is 12, not the 16 a resting tile
    /// carries nor the 0.150943 ratio the switcher holds: the minimized icon
    /// keeps a flat 12.
    /// </summary>
    public const double HeaderIconRadius = 12.0;

    /// <summary>
    /// <c>image.marginRight</c>, which is MINIMIZED_TITLE_MARGIN_LEFT: the gap
    /// between the icon and the title reads as either, and is one number.
    /// </summary>
    public const double HeaderIconMarginRight = ShellTitleMetrics.MinimizedTitleMarginLeft;

    /// <summary><c>separatorText.width</c>.</summary>
    public const double SeparatorWidth = 2.0;

    /// <summary><c>separatorText.top</c> and <c>.bottom</c>.</summary>
    public const double SeparatorInset = 6.0;

    /// <summary><c>separatorText.left</c>.</summary>
    public const double SeparatorLeft = 12.0;

    /// <summary><c>tagText.marginLeft</c>.</summary>
    public const double TagTextMarginLeft = 26.0;

    /// <summary><c>matadataIconContainer.marginLeft</c>, the source's spelling.</summary>
    public const double MetadataIconContainerMarginLeft = 12.0;

    /// <summary><c>entitlementIconId</c> and <c>storageIconId</c>, both square.</summary>
    public const double MetadataIconSize = 42.0;
}

/// <summary>
/// Drives the home-to-hub transition onto the surfaces it moves: the home page
/// lifts by <see cref="ShellHubMetrics.HomeLift"/> and the switcher fades out,
/// both on the console's FAST spring.
///
/// Modelled the way <see cref="ShellEntrance"/> is, and for the same reason:
/// the source runs one <c>Animated.parallel</c> over shared drivers, and
/// letting each control animate itself cannot keep them in step.
/// </summary>
public sealed class ShellHubTransition
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    private readonly ShellSpring _progress = new(0.0005, 0.005);
    private readonly TranslateTransform _homeTransform = new();
    private readonly Stopwatch _stopwatch = new();

    private DispatcherTimer? _timer;
    private Control? _home;
    private Control? _switcher;
    private Control? _switcherOverlay;

    /// <summary>Authored pixels to host pixels.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Host drives <see cref="Advance"/> instead of a timer.</summary>
    public bool ManualClock { get; set; }

    /// <summary>True once the hub is showing or on its way in.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>0 at home, 1 at the hub.</summary>
    public double Progress => _progress.Value;

    /// <summary>How far the home surface is currently lifted, authored pixels.</summary>
    public double HomeTranslateY => -ShellHubMetrics.HomeLift * _progress.Value;

    /// <summary>The switcher's opacity: 1 at home, 0 at the hub.</summary>
    public double SwitcherOpacity => 1.0 - _progress.Value;

    /// <summary>True while the transition still needs frames.</summary>
    public bool IsRunning => !_progress.IsSettled;

    /// <summary>
    /// Points the transition at the surfaces it moves.
    /// </summary>
    /// <param name="home">The whole home surface, which lifts.</param>
    /// <param name="switcher">The experience switcher, which fades.</param>
    /// <param name="switcherOverlay">
    /// Anything that draws over the switcher from outside its visual tree and
    /// therefore does not inherit its opacity. The travelling focus ring is
    /// exactly that: it lives on the window's overlay, so without passing it
    /// here it stays at full brightness over a faded row. In the source there
    /// is no such split - the focus visuals are inside the container that
    /// fades - so this parameter exists only because our ring is hosted
    /// differently, and skipping it is a visible bug rather than a detail.
    /// </param>
    public void Attach(Control? home, Control? switcher, Control? switcherOverlay = null)
    {
        _home = home;
        _switcher = switcher;
        _switcherOverlay = switcherOverlay;
        if (_home is not null)
        {
            _home.RenderTransform = _homeTransform;
            _home.RenderTransformOrigin = RelativePoint.TopLeft;
        }

        Apply();
    }

    /// <summary>Lifts the home surface and fades the switcher out.</summary>
    public void Open()
    {
        IsOpen = true;
        _progress.SpringTo(1.0, ShellSpringConfig.Fast);
        Wake();
    }

    /// <summary>Brings the home surface back down.</summary>
    public void Close()
    {
        IsOpen = false;
        _progress.SpringTo(0.0, ShellSpringConfig.Fast);
        Wake();
    }

    /// <summary>Puts the transition at its target immediately.</summary>
    public void SettleNow()
    {
        _progress.Settle();
        Apply();
        StopTimer();
    }

    /// <summary>Advances the transition.</summary>
    public void Advance(TimeSpan delta)
    {
        bool busy = _progress.Advance(delta.TotalSeconds);
        Apply();
        if (!busy)
        {
            StopTimer();
        }
    }

    private void Apply()
    {
        _homeTransform.Y = HomeTranslateY * Scale;
        if (_switcher is not null)
        {
            _switcher.Opacity = SwitcherOpacity;
        }

        // Kept in step with the switcher rather than given its own driver: they
        // are one surface on the console and only two here.
        if (_switcherOverlay is not null)
        {
            _switcherOverlay.Opacity = SwitcherOpacity;
        }
    }

    private void Wake()
    {
        if (ManualClock || _progress.IsSettled)
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                _timer.Tick += (_, _) =>
                {
                    var delta = _stopwatch.Elapsed;
                    _stopwatch.Restart();
                    Advance(delta);
                };
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            SettleNow();
        }
    }

    private void StopTimer()
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

/// <summary>
/// The hub's header: the running title's minimized icon with its name beside
/// it, HOME m402 with stylesheet m170.
///
/// The icon is MINIMIZED_EXP_SIZE at MINIMIZED_EXP_MARGIN_LEFT and
/// MINIMIZED_EXP_MARGIN_TOP, so the same 80 px box at (48, 48) the shell shows
/// when a title is running. Its radius is a flat 12, not the switcher's ratio.
/// </summary>
public sealed class ShellHubHeader : TemplatedControl
{
    private static readonly IBrush TextBrush = Brushes.White;

    private static readonly IBrush IconPlateBrush =
        new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));

    private static readonly IBrush SeparatorBrush =
        new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private static readonly IBrush TagBrush =
        new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ShellHubHeader, string?>(nameof(Title));

    // `new` because Control already declares Tag. The header's tag is the small
    // label beside the title, not the general-purpose object slot, and shadowing
    // it deliberately is clearer than renaming a property the XAML binds by name.
    public static new readonly StyledProperty<string?> TagProperty =
        AvaloniaProperty.Register<ShellHubHeader, string?>(nameof(Tag));

    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<ShellHubHeader, IImage?>(nameof(Icon));

    /// <summary>Authored pixels to host pixels.</summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ShellHubHeader, double>(nameof(Scale), 1.0);

    private Border? _iconBox;
    private Image? _iconImage;
    private TextBlock? _title;
    private TextBlock? _tag;
    private Border? _separator;

    public ShellHubHeader()
    {
        Template = BuildTemplate();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public new string? Tag
    {
        get => GetValue(TagProperty);
        set => SetValue(TagProperty, value);
    }

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty
            || change.Property == TagProperty
            || change.Property == IconProperty
            || change.Property == ScaleProperty)
        {
            Apply();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _iconBox = e.NameScope.Find<Border>("PART_IconBox");
        _iconImage = e.NameScope.Find<Image>("PART_Icon");
        _title = e.NameScope.Find<TextBlock>("PART_Title");
        _tag = e.NameScope.Find<TextBlock>("PART_Tag");
        _separator = e.NameScope.Find<Border>("PART_Separator");
        Apply();
    }

    private static FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var icon = new Image { Name = "PART_Icon", Stretch = Stretch.UniformToFill };
        icon.RegisterInNameScope(ns);

        var iconBox = new Border
        {
            Name = "PART_IconBox",
            Background = IconPlateBrush,
            ClipToBounds = true,
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconBox.RegisterInNameScope(ns);

        var title = new TextBlock
        {
            Name = "PART_Title",
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.RegisterInNameScope(ns);

        var separator = new Border
        {
            Name = "PART_Separator",
            Background = SeparatorBrush,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        separator.RegisterInNameScope(ns);

        var tag = new TextBlock
        {
            Name = "PART_Tag",
            Foreground = TagBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tag.RegisterInNameScope(ns);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(iconBox);
        row.Children.Add(title);
        row.Children.Add(separator);
        row.Children.Add(tag);
        return row;
    });

    private void Apply()
    {
        double scale = Scale > 0 ? Scale : 1.0;

        Margin = new Thickness(
            ShellHubMetrics.HeaderMarginLeft * scale,
            ShellHubMetrics.HeaderMarginTop * scale,
            ShellHubMetrics.HeaderMarginRight * scale,
            0);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        if (_iconBox is not null)
        {
            _iconBox.Width = ShellHubMetrics.HeaderIconSize * scale;
            _iconBox.Height = ShellHubMetrics.HeaderIconSize * scale;
            _iconBox.CornerRadius = new CornerRadius(ShellHubMetrics.HeaderIconRadius * scale);
            _iconBox.Margin = new Thickness(0, 0, ShellHubMetrics.HeaderIconMarginRight * scale, 0);
        }

        if (_iconImage is not null)
        {
            _iconImage.Source = Icon;
        }

        if (_title is not null)
        {
            _title.Text = Title ?? string.Empty;
            _title.FontSize = 32 * scale;
        }

        bool hasTag = !string.IsNullOrEmpty(Tag);

        if (_separator is not null)
        {
            _separator.IsVisible = hasTag;
            _separator.Width = ShellHubMetrics.SeparatorWidth * scale;
            _separator.Margin = new Thickness(
                ShellHubMetrics.SeparatorLeft * scale,
                ShellHubMetrics.SeparatorInset * scale,
                0,
                ShellHubMetrics.SeparatorInset * scale);
        }

        if (_tag is not null)
        {
            _tag.IsVisible = hasTag;
            _tag.Text = Tag ?? string.Empty;
            _tag.Margin = new Thickness(ShellHubMetrics.TagTextMarginLeft * scale, 0, 0, 0);
            _tag.FontSize = 22 * scale;
        }
    }
}
