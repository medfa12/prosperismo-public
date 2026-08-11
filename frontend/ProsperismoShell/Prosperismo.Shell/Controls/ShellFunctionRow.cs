// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>One icon in a <see cref="ShellFunctionRow"/>.</summary>
public sealed record ShellFunctionItem
{
    public ShellFunctionItem(string title, string glyph, object? tag = null, ShellIcon? icon = null)
    {
        Title = title ?? string.Empty;
        Glyph = glyph ?? string.Empty;
        Tag = tag;
        Icon = icon;
    }

    /// <summary>Caption shown under the focused icon.</summary>
    public string Title { get; init; }

    /// <summary>Single-character mark drawn inside the icon.</summary>
    public string Glyph { get; init; }

    /// <summary>Caller payload round-tripped through the row's events.</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// The system-shell icon to draw instead of <see cref="Glyph"/> when a
    /// unavailable.
    /// </summary>
    public ShellIcon? Icon { get; init; }
}

/// <summary>Payload for <see cref="ShellFunctionRow"/>'s selection events.</summary>
public sealed class ShellFunctionEventArgs : EventArgs
{
    public ShellFunctionEventArgs(int index, ShellFunctionItem? item)
    {
        Index = index;
        Item = item;
    }

    /// <summary>Focused index, or -1 when the row is empty.</summary>
    public int Index { get; }

    /// <summary>The focused item, or null when the row is empty.</summary>
    public ShellFunctionItem? Item { get; }
}

/// <summary>
/// A mass-spring-damper solved analytically and exposed as an easing curve, so
/// the shell's recovered spring parameters can drive an Avalonia transition
/// without standing up a per-frame integrator.
///
/// The shell states its focus motion as stiffness / damping / mass rather than
/// as a duration, which is why those three are what this takes. Every spring the
/// switcher uses is overdamped or critically damped, so the displacement is a
/// sum of two decaying exponentials with no oscillation; that closed form is
/// what <see cref="Ease"/> evaluates. <see cref="SettleSeconds"/> is where the
/// spring has effectively arrived, and is the duration the transition should be
/// given so the curve is not cut short or stretched.
/// </summary>
public sealed class ShellSpringEasing : Easing
{
    // React Native calls a spring done when it is within 1/1000 of its target,
    // so that is where the settle time is measured.
    private const double RestThreshold = 0.001;

    private readonly double _slow;
    private readonly double _fast;
    private readonly double _settleValue;

    public ShellSpringEasing(double stiffness, double damping, double mass)
    {
        double omega = Math.Sqrt(stiffness / mass);
        double zeta = damping / (2.0 * Math.Sqrt(stiffness * mass));

        // Guard the critically damped case, where the two roots coincide and the
        // two-exponential form degenerates; nudging them apart keeps the closed
        // form finite and is far below anything the eye resolves.
        double spread = Math.Max(1e-6, Math.Sqrt(Math.Max(0.0, (zeta * zeta) - 1.0)));

        _slow = omega * (zeta - spread);
        _fast = omega * (zeta + spread);

        // Time for the slow mode to decay under the rest threshold.
        SettleSeconds = Math.Log(_fast / ((_fast - _slow) * RestThreshold)) / _slow;
        _settleValue = Displacement(SettleSeconds);
    }

    /// <summary>Seconds until the spring is within 1/1000 of its target.</summary>
    public double SettleSeconds { get; }

    public override double Ease(double progress)
    {
        double p = Math.Clamp(progress, 0.0, 1.0);

        // Renormalise so the curve lands exactly on 1: the settle point is a
        // thousandth short by construction, and a transition that stops a
        // thousandth short leaves a visible seam on a scaled 168 px tile.
        return Displacement(p * SettleSeconds) / _settleValue;
    }

    private double Displacement(double t)
    {
        double a = _slow;
        double b = _fast;
        return 1.0 - (((b * Math.Exp(-a * t)) - (a * Math.Exp(-b * t))) / (b - a));
    }
}

/// <summary>
/// A horizontal row of destination icons on the experience switcher's geometry.
///
/// NOT ON THE HOME SURFACE. The console's home has exactly one row of tiles and
/// the installed titles are its tiles, so the 168 band belongs to
/// <see cref="ShellTileRow"/> alone. Nothing in the home bundle draws a plate, a
/// border or a shadow behind a switcher tile, and the system destinations
/// (search, settings, profile) are the 56 px icons in the 126 nav band, reached
/// as their own focus region. Filling the switcher band with Library, Search,
/// Add folder, Rescan and Options put two of them on the screen twice and made
/// the console's most recognisable band read as a settings toolbar.
///
/// Kept because the geometry it implements is the real switcher geometry and a
/// later surface may want a destination row of its own. If one is added it is
/// our design choice, not the console's, and should say so where it is used.
///
/// Geometry is the shell's own, at its 1920x1080 design resolution and scaled
/// as a whole through <see cref="LayoutScale"/>:
/// icons rest at 106 px square and enlarge to 168 px when focused, a scale of
/// 168/106 (~1.585) applied as a render transform rather than a layout reflow.
/// The corner radius rides that transform, so the constant radius to side ratio
/// holds at both ends: 16 on the resting 106 box, 25.3585 on the focused 168
/// one. The row band is 168 px tall with a 40 px caption strip beneath it, and
/// at most 11 icons are shown.
///
/// Spacing is two gaps, not one: 8 px between resting icons and 16 px either
/// side of the focused one. The focus move is a spring rather than a tween.
/// Every number carries its source in the constants block below; they came out
/// of the shell's own bundles, so please do not round them off.
///
/// Navigation is clamped at both ends (no wrap); vertical movement is the
/// host's business and is routed through <see cref="ShellFocusGraph"/>.
/// All navigation state is surface-independent so it can be driven headless.
/// </summary>
public sealed class ShellFunctionRow : TemplatedControl
{
    // ---- Shell geometry constants (1920x1080 design resolution) -----------

    /// <summary>Resting icon size (the shell's EXPERIENCE_SIZE).</summary>
    public const double ExperienceSize = 106;

    /// <summary>Focused icon size (the shell's SCALED_EXP_SIZE).</summary>
    public const double ScaledExperienceSize = 168;

    /// <summary>Focus enlarge factor, 168/106.</summary>
    public const double ExperienceScale = ScaledExperienceSize / ExperienceSize;

    /// <summary>Icon corner radius at rest (BORDER_RADIUS, ps5-rn-layout.md
    /// §2.3, HOME m25:3224).</summary>
    public const double BorderRadius = 16;

    /// <summary>
    /// Radius as a fraction of the box side, 16/106 = 25.3585/168. The shell
    /// keeps this ratio constant rather than the radius, so a radius is always
    /// derived from a side length (ps5-rn-layout.md §1.5, "Radius to side
    /// ratio"). Naming it stops the 16 from being reused on a 168 box.
    /// </summary>
    public const double RadiusToSideRatio = BorderRadius / ExperienceSize;

    /// <summary>
    /// Corner radius of the focused icon, 168/106*16 = 25.3585. Declared by the
    /// shell as that literal expression on `focusContainer` (ps5-rn-layout.md
    /// §2.3, HOME m25:3236): art carrying a 16 px radius at 106 px shows a
    /// 25.36 px radius once scaled to 168, so the focus shape is declared at the
    /// scaled value to match what the viewer sees.
    /// </summary>
    public const double FocusedBorderRadius = ScaledExperienceSize * RadiusToSideRatio;

    /// <summary>Height of the icon band.</summary>
    public const double RowHeight = 168;

    /// <summary>Caption strip under the icon band (VERTICAL_HEIGHT_CHANGE).</summary>
    public const double CaptionHeight = 40;

    /// <summary>Total design height of the control.</summary>
    public const double DesignHeight = RowHeight + CaptionHeight;

    /// <summary>Most icons the shell will show in this row (MAX_TILES).</summary>
    public const int MaxTiles = 11;

    /// <summary>
    /// Air between two resting icons (itemMargin, ps5-rn-layout.md §2.3 strand
    /// construction, HOME m201:14577-14587). The row is a dense ribbon, not a
    /// dock: 8 px, giving the 114 resting pitch below.
    /// </summary>
    public const double ItemMargin = 8;

    /// <summary>
    /// Air either side of the focused icon (focusedMargin, same source). The
    /// focused icon overhangs its resting box by 31 px per side, so it needs its
    /// own larger clearance; one uniform gap cannot express both.
    /// </summary>
    public const double FocusedMargin = 16;

    /// <summary>Resting pitch, 106 + 8 = 114 (ps5-rn-layout.md §2.4).</summary>
    public const double RestingPitch = ExperienceSize + ItemMargin;

    /// <summary>
    /// Half the overhang the focus enlarge adds, `w*s/2 - w/2` = 84 - 53 = 31.
    /// The shell calls this `offset` in its position solver (ps5-rn-layout.md
    /// §2.4, HOME m531:38282-38367).
    /// </summary>
    public const double FocusOffset = (ScaledExperienceSize - ExperienceSize) / 2;

    /// <summary>
    /// Extra shift applied to every icon past the focused one, `offset + fm - im`
    /// = 31 + 16 - 8 = 39 (ps5-rn-layout.md §2.4). This is what turns the single
    /// resting pitch into the shell's two-gap row.
    /// </summary>
    public const double FocusSpread = FocusOffset + FocusedMargin - ItemMargin;

    // Optical size of the mark inside a resting tile, as a fraction of the tile.
    // Shared by the glyph's font size and the icon art's height so swapping one
    // for the other does not change the tile's weight.
    private const double MarkFraction = 0.42;

    // The focus move is a spring, not a tween: the strand runs one
    // Animated.spring per icon for scale and translateX together, and its own
    // default is what runs because the springOptions atom stays undefined
    // (ps5-rn-layout.md §9.2, HOME m530:38151).
    private const double FocusSpringStiffness = 400;
    private const double FocusSpringDamping = 50;
    private const double FocusSpringMass = 0.2;

    private static readonly ShellSpringEasing FocusSpring =
        new(FocusSpringStiffness, FocusSpringDamping, FocusSpringMass);

    private static readonly TimeSpan FocusDuration =
        TimeSpan.FromSeconds(FocusSpring.SettleSeconds);

    // Opacity and the caption stay on a tween: ANIMATION.TIMING.DEFAULT is
    // 300 ms with easeOutBreezePS, and only the transform pair is sprung
    // (ps5-rn-layout.md §9.1 HOME m719:51173, §9.5 HOME m747:54249-54250).
    private static readonly TimeSpan OpacityDuration = TimeSpan.FromMilliseconds(300);

    // Boot reveal stagger. 60 ms is the switcher's own Animated.stagger
    // (ps5-rn-layout.md §9.4, HOME m843:61237); the 16.67 ms this used to carry
    // is the hub scene enter, a different surface, and reads as one frame.
    private const double StaggerMs = 60;

    // ---- Palette (App.axaml's shell colours) ------------------------------
    private static readonly IBrush IconFill = new SolidColorBrush(Color.Parse("#17191E"));
    private static readonly IBrush IconFillFocused = new SolidColorBrush(Color.Parse("#232833"));
    private static readonly IBrush IconBorder = new SolidColorBrush(Color.Parse("#1AFFFFFF"));
    // Marks where focus would return when this row is NOT the active region.
    // Deliberately neutral, not the focus cyan: only the scene's single
    // travelling ring is allowed to read as focus.
    private static readonly IBrush RememberedBorder = new SolidColorBrush(Color.Parse("#4DFFFFFF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#B3FFFFFF"));

    // Focus is drawn by the scene's single travelling ring (ShellFocusRing),
    // not by a per-icon glow; icons keep only their own contact shadow.
    private static readonly BoxShadows RestShadow =
        BoxShadows.Parse("0 4 12 0 #40000000");

    // ---- Styled properties ------------------------------------------------
    public static readonly StyledProperty<IEnumerable<ShellFunctionItem>?> ItemsProperty =
        AvaloniaProperty.Register<ShellFunctionRow, IEnumerable<ShellFunctionItem>?>(nameof(Items));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellFunctionRow, int>(nameof(SelectedIndex), defaultValue: -1);

    public static readonly StyledProperty<double> LayoutScaleProperty =
        AvaloniaProperty.Register<ShellFunctionRow, double>(nameof(LayoutScale), defaultValue: 1.0);

    /// <summary>
    /// X the focused icon's drawn left edge is pinned to while the rest of the
    /// row slides under it. The shell pins it to the content inset; a host that
    /// already carries that inset on the row passes 0.
    /// </summary>
    public static readonly StyledProperty<double> FocusAnchorXProperty =
        AvaloniaProperty.Register<ShellFunctionRow, double>(nameof(FocusAnchorX), defaultValue: 0);

    public static readonly StyledProperty<bool> IsRegionFocusedProperty =
        AvaloniaProperty.Register<ShellFunctionRow, bool>(nameof(IsRegionFocused), defaultValue: true);

    private readonly List<ShellFunctionItem> _items = new();
    private readonly List<IconVisual> _icons = new();
    private Canvas? _surface;
    private TextBlock? _caption;
    private bool _pendingReveal;
    private bool _focusPushQueued;
    private double _viewWidth;

    public ShellFunctionRow()
    {
        Focusable = true;
        Template = BuildTemplate();
        GotFocus += (_, _) => SchedulePushFocusRect();
    }

    /// <summary>Raised whenever the focused icon changes.</summary>
    public event EventHandler<ShellFunctionEventArgs>? SelectionChanged;

    /// <summary>Raised on Enter/Space or a click over the focused icon.</summary>
    public event EventHandler<ShellFunctionEventArgs>? ItemActivated;

    /// <summary>The icons to display; only the first <see cref="MaxTiles"/> are shown.</summary>
    public IEnumerable<ShellFunctionItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Index of the focused icon, or -1 when empty. Clamps on set.</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetSelectedIndex(value);
    }

    /// <summary>Uniform scale applied to the shell's design geometry so the row
    /// fits the launcher window.</summary>
    public double LayoutScale
    {
        get => GetValue(LayoutScaleProperty);
        set => SetValue(LayoutScaleProperty, value);
    }

    /// <summary>X the focused icon's drawn left edge pins to as the row slides.</summary>
    public double FocusAnchorX
    {
        get => GetValue(FocusAnchorXProperty);
        set => SetValue(FocusAnchorXProperty, value);
    }

    /// <summary>Whether this region currently owns focus. The selected icon
    /// stays enlarged either way (it is the current destination); only the
    /// focus ring and the row's brightness follow it.</summary>
    public bool IsRegionFocused
    {
        get => GetValue(IsRegionFocusedProperty);
        set => SetValue(IsRegionFocusedProperty, value);
    }

    /// <summary>The focused item, or null when the row is empty.</summary>
    public ShellFunctionItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    /// <summary>Number of icons currently held.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// The rect the focus highlight is drawn on, in this row's own coordinates
    /// (the shell's <c>focusImageRectangle</c>), or null when nothing is
    /// focused. The focused icon is laid out at its resting 106 px and enlarged
    /// to 168 px by a render transform about its centre, so the highlight rect is
    /// the enlarged square, centred on the resting slot.
    /// </summary>
    internal Rect? FocusHighlightRect
    {
        get
        {
            if (SelectedIndex < 0 || SelectedIndex >= _icons.Count)
            {
                return null;
            }

            double scale = Math.Max(0.05, LayoutScale);
            var lefts = ResolveLefts(_icons.Count, SelectedIndex, scale);
            double rest = ExperienceSize * scale;
            double band = RowHeight * scale;
            double top = (band - rest) / 2;

            double focused = ScaledExperienceSize * scale;
            double centreX = lefts[SelectedIndex] + (rest / 2);
            double centreY = top + (rest / 2);
            return new Rect(centreX - (focused / 2), centreY - (focused / 2), focused, focused);
        }
    }

    // ---- Navigation (surface-independent) ---------------------------------

    /// <summary>Moves focus by <paramref name="delta"/> icons, clamped at both
    /// ends — the shell's horizontal edges never wrap.</summary>
    internal void MoveFocus(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        SetSelectedIndex(Math.Clamp(SelectedIndex + delta, 0, _items.Count - 1));
    }

    /// <summary>Focuses the icon at <paramref name="index"/>, clamped into range.</summary>
    internal void SetSelectedIndex(int index)
    {
        int target = _items.Count == 0 ? -1 : Math.Clamp(index, 0, _items.Count - 1);
        if (target == SelectedIndex)
        {
            SetValue(SelectedIndexProperty, target);
            return;
        }

        SetValue(SelectedIndexProperty, target);
        UpdateVisuals();
        ShellUiSounds.Play(UiSoundEvent.FocusMove);
        SelectionChanged?.Invoke(this, new ShellFunctionEventArgs(target, SelectedItem));
    }

    /// <summary>Raises <see cref="ItemActivated"/> for the focused icon.</summary>
    internal void ActivateSelected()
    {
        if (SelectedItem is { } item)
        {
            ShellUiSounds.Play(UiSoundEvent.Enter);
            ItemActivated?.Invoke(this, new ShellFunctionEventArgs(SelectedIndex, item));
        }
    }

    // ---- Items plumbing ---------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            RebuildItems();
        }
        else if (change.Property == LayoutScaleProperty)
        {
            BuildIcons();
            UpdateVisuals();
        }
        else if (change.Property == FocusAnchorXProperty)
        {
            UpdateVisuals();
        }
        else if (change.Property == IsRegionFocusedProperty)
        {
            UpdateVisuals();
            SchedulePushFocusRect();
        }
    }

    /// <summary>
    /// Re-publishes the focus rect. The ring lives on the window's overlay, so
    /// the rect it was handed is only valid for one mapping from this row to the
    /// window; a host that scales the whole surface changes that mapping without
    /// ever re-arranging this row, and has to say so.
    /// </summary>
    public void RefreshFocusRect() => SchedulePushFocusRect();

    // ---- Focus ring ------------------------------------------------------

    /// <summary>Queues a retarget of the scene's focus ring for after the next
    /// layout pass.</summary>
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

    /// <summary>
    /// Retargets the scene's single focus ring onto the focused icon. The row
    /// only publishes a rect; the ring owns the travel and the warp.
    /// </summary>
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

            // The ring gets the scaled radius, 25.3585, not the resting 16.
            // Art carrying a 16 px radius at 106 px shows 25.36 once scaled to
            // 168, so the ring has to match what is on screen. Earlier notes
            // described this as a pre-division that lands back on 16; it is a
            // multiplication and it does not, and a ring drawn at 16 around a
            // 25.36 tile shows four corner slivers. Radius tracks side length at
            // a constant ratio (ps5-rn-layout.md §1.5).
            ring.Radius = FocusedBorderRadius * Math.Max(0.05, LayoutScale);
            ring.Claim(this, local.TransformToAABB(transform));
        }
        catch
        {
            // A detached or half-built tree just leaves the ring where it is.
        }
    }

    /// <summary>Drives the ring's 0.3 s press state.</summary>
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

    private void RebuildItems()
    {
        _items.Clear();
        if (Items is { } source)
        {
            _items.AddRange(source.Where(item => item is not null).Take(MaxTiles));
        }

        int clamped = _items.Count == 0 ? -1 : Math.Clamp(SelectedIndex, 0, _items.Count - 1);
        SetValue(SelectedIndexProperty, clamped);

        BuildIcons();
        UpdateVisuals();
        SelectionChanged?.Invoke(this, new ShellFunctionEventArgs(clamped, SelectedItem));
    }

    // ---- Template ---------------------------------------------------------

    private static FuncControlTemplate BuildTemplate()
    {
        return new FuncControlTemplate((_, ns) =>
        {
            var surface = new Canvas { Name = "PART_Surface" };
            surface.RegisterInNameScope(ns);

            var caption = new TextBlock
            {
                Name = "PART_Caption",
                Foreground = TextBrush,
                FontWeight = FontWeight.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Opacity = 0,
                Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = OpacityDuration,
                        Easing = ShellMotion.EaseOutBreeze,
                    },
                },
            };
            caption.RegisterInNameScope(ns);
            surface.Children.Add(caption);

            return new Panel { Children = { surface } };
        });
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Find<Canvas>("PART_Surface");
        _caption = e.NameScope.Find<TextBlock>("PART_Caption");
        BuildIcons();
        UpdateVisuals();
    }

    // ---- Visual build -----------------------------------------------------

    private void BuildIcons()
    {
        if (_surface is null)
        {
            return;
        }

        foreach (var icon in _icons)
        {
            _surface.Children.Remove(icon.Root);
        }

        _icons.Clear();

        double scale = Math.Max(0.05, LayoutScale);
        double rest = ExperienceSize * scale;

        for (int i = 0; i < _items.Count; i++)
        {
            var visual = CreateIcon(_items[i], i, rest, scale);
            _icons.Add(visual);
            _surface.Children.Add(visual.Root);
        }

        _pendingReveal = _items.Count > 0;
    }

    private IconVisual CreateIcon(ShellFunctionItem model, int index, double rest, double scale)
    {
        // The packaged pictogram when one exists, else our mark. The
        // art is a white silhouette on transparency sized for a text run, so it
        // is fitted to the same optical box the glyph occupied.
        Control mark = BuildMark(model, rest);

        // Scale is sprung; opacity stays a tween. See the constants block.
        var transformTransition = new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = FocusDuration,
            Easing = FocusSpring,
        };
        var opacityTransition = new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = OpacityDuration,
            Easing = ShellMotion.EaseOutBreeze,
        };

        // The icon is laid out at its resting 106 px and grows through a render
        // transform, so the radius scales with it and the constant radius to
        // side ratio holds at both ends without being reapplied.
        var root = new Border
        {
            Width = rest,
            Height = rest,
            CornerRadius = new CornerRadius(ExperienceSize * RadiusToSideRatio * scale),
            Background = IconFill,
            BorderBrush = IconBorder,
            BorderThickness = new Thickness(1),
            BoxShadow = RestShadow,
            Child = mark,
            RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = TransformOperations.Parse("scale(0.7)"),
            Opacity = 0,
            Transitions = new Transitions { transformTransition, opacityTransition },
        };

        int captured = index;
        root.PointerEntered += (_, _) => SetSelectedIndex(captured);
        root.PointerPressed += (_, _) =>
        {
            Focus();
            SetSelectedIndex(captured);
            SetRingPressed(true);
            ActivateSelected();
        };
        root.PointerReleased += (_, _) => SetRingPressed(false);
        root.PointerExited += (_, _) => SetRingPressed(false);

        return new IconVisual(root, transformTransition, opacityTransition);
    }

    /// <summary>
    /// The mark drawn inside one tile. A reachable UI3 RCO always owns a known
    /// shell icon: vector <c>iconid_*</c> nodes stay vector and the remaining
    /// PNG nodes are decoded by <see cref="ShellIcons"/>. The caller's glyph is
    /// </summary>
    private static Control BuildMark(ShellFunctionItem model, double rest)
    {
        if (model.Icon is { } icon)
        {
            double side = Math.Max(10, rest * MarkFraction);
            if (ShellIcons.TryGetRcoIconId(icon) is { } iconId)
            {
                return new Ps5IconPresenter
                {
                    IconId = iconId,
                    Tint = Ps5HomeMetrics.IconNormal,
                    Width = side,
                    Height = side,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            if (ShellIcons.EntryNames.ContainsKey(icon))
            {
                return new ShellRcoRasterIcon(icon, side);
            }
        }

        return new TextBlock
        {
            Text = model.Glyph,
            FontSize = Math.Max(11, rest * MarkFraction),
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// Renders a non-<c>iconid_*</c> PNG node (keyguides and inline emoji) once
    /// <see cref="ShellIcons"/> finishes its background read. This is kept
    /// deliberately separate from <see cref="Ps5RcoIconPresenter"/>, whose
    /// namespace is limited to the UI3 <c>iconid_*</c> registry.
    /// </summary>
    private sealed class ShellRcoRasterIcon : Control
    {
        private readonly ShellIcon _icon;

        public ShellRcoRasterIcon(ShellIcon icon, double side)
        {
            _icon = icon;
            Width = side;
            Height = side;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            ShellIcons.Preload();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ShellIcons.Loaded += OnShellIconsLoaded;
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ShellIcons.Loaded -= OnShellIconsLoaded;
            base.OnDetachedFromVisualTree(e);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            if (ShellIcons.TryGet(_icon) is { } art)
            {
                double scale = Math.Min(bounds.Width / art.Size.Width, bounds.Height / art.Size.Height);
                var size = new Size(art.Size.Width * scale, art.Size.Height * scale);
                var target = new Rect(
                    (bounds.Width - size.Width) / 2.0,
                    (bounds.Height - size.Height) / 2.0,
                    size.Width,
                    size.Height);
                context.DrawImage(art, new Rect(art.Size), target);
                return;
            }

            Ps5IconPresenter.DrawUnresolvedMarker(context, bounds, Ps5HomeMetrics.IconNormal);
        }

        private void OnShellIconsLoaded(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(InvalidateVisual);
        }
    }

    // ---- Visual state -----------------------------------------------------

    /// <summary>
    /// Left edge of every icon's resting 106 px box, in control coordinates.
    ///
    /// This is the shell's own position solver (ps5-rn-layout.md §2.4,
    /// HOME m531:38282-38367): items sit at a flat <see cref="RestingPitch"/>
    /// from each other, and everything past the focused icon is pushed out by a
    /// further <see cref="FocusSpread"/> so the enlarged icon clears its
    /// neighbours by exactly <see cref="FocusedMargin"/> while every other seam
    /// stays at <see cref="ItemMargin"/>. One uniform gap cannot do that, which
    /// is why the old single pitch had to be so wide.
    ///
    /// The origin is the shell's too, and it is a pin rather than a centring:
    /// the focused icon's drawn left edge sits on <see cref="FocusAnchorX"/> and
    /// the rest of the row slides under it, running off both ends of the band.
    /// Centring the assembled row instead makes the whole strip shuffle sideways
    /// on every focus move and leaves it out of line with the content strand
    /// below, which pins.
    ///
    /// With nothing focused the shell drops the focus terms and pulls the row
    /// back by half a focused icon, so item 0 lands half an enlarged tile left
    /// of the anchor (ps5-rn-layout.md §2.4, the <c>sel &lt; 0</c> branch).
    /// </summary>
    internal double[] ResolveLefts(int count, int selected, double scale)
    {
        var lefts = new double[Math.Max(0, count)];
        if (count <= 0)
        {
            return lefts;
        }

        double overhang = FocusOffset * scale;

        for (int i = 0; i < count; i++)
        {
            double x = i * RestingPitch * scale;
            if (selected >= 0)
            {
                if (i < selected)
                {
                    x -= FocusSpread * scale;
                }
                else if (i > selected)
                {
                    x += FocusSpread * scale;
                }
            }

            lefts[i] = x;
        }

        // The focused icon grows about its own centre, so its drawn left edge is
        // one overhang left of its resting box: the box has to sit an overhang
        // past the anchor for the drawn edge to land on it.
        double origin = selected >= 0 && selected < count
            ? FocusAnchorX + overhang - lefts[selected]
            : FocusAnchorX - (ScaledExperienceSize * scale / 2);

        for (int i = 0; i < count; i++)
        {
            lefts[i] += origin;
        }

        return lefts;
    }

    private void UpdateVisuals()
    {
        if (_surface is null)
        {
            return;
        }

        double scale = Math.Max(0.05, LayoutScale);
        var lefts = ResolveLefts(_icons.Count, SelectedIndex, scale);
        double rest = ExperienceSize * scale;
        double band = RowHeight * scale;
        double top = (band - rest) / 2;

        for (int i = 0; i < _icons.Count; i++)
        {
            var icon = _icons[i];
            bool focused = i == SelectedIndex;

            Canvas.SetLeft(icon.Root, lefts[i]);
            Canvas.SetTop(icon.Root, top);
            icon.Root.ZIndex = focused ? 1000 : 500 - Math.Abs(i - SelectedIndex);

            var delay = _pendingReveal ? TimeSpan.FromMilliseconds(i * StaggerMs) : TimeSpan.Zero;
            icon.RootTransform.Delay = delay;
            icon.RootOpacity.Delay = delay;

            icon.Root.RenderTransform = focused
                ? TransformOperations.Parse(
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"scale({ExperienceScale})"))
                : TransformOperations.Parse("scale(1)");
            // Resting icons draw at full opacity. The shell's tile stylesheet
            // carries no opacity rule at all (ps5-rn-layout.md §2.5, HOME m210),
            // and the only fade in the row is a tail cue on the 8th, 9th and
            // 10th icons past the selection, saying "there is more past here".
            // Dimming ordinary neighbours spends that signal and leaves size as
            // the only thing carrying focus. The 0.6 dim that does exist in the
            // shell is on unselected space labels (HOME m815:60085), not tiles,
            // so it does not generalise to anything focusable.
            icon.Root.Opacity = 1.0;
            icon.Root.Background = focused ? IconFillFocused : IconFill;

            // The ring supersedes the cyan outline on the focused icon; when
            // this region does not own focus there is no ring, so the icon keeps
            // a hairline tint to show it is still the current destination.
            icon.Root.BorderBrush = focused && !IsRegionFocused ? RememberedBorder : IconBorder;
            icon.Root.BorderThickness = new Thickness(1);
            icon.Root.BoxShadow = RestShadow;
        }

        _pendingReveal = false;

        if (_caption is not null)
        {
            if (SelectedItem is { } selected)
            {
                double width = Math.Max(RestingPitch * scale * 2.4, rest * 3);
                double centre = lefts[SelectedIndex] + (rest / 2);
                _caption.Text = selected.Title;
                _caption.FontSize = Math.Max(10, 15 * scale);
                _caption.Width = width;
                Canvas.SetLeft(_caption, centre - (width / 2));
                Canvas.SetTop(_caption, band + (6 * scale));
                _caption.Foreground = IsRegionFocused ? TextBrush : MutedBrush;
                _caption.Opacity = 1;
            }
            else
            {
                _caption.Opacity = 0;
            }
        }

        SchedulePushFocusRect();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (Math.Abs(size.Width - _viewWidth) > 0.5)
        {
            _viewWidth = size.Width;
            UpdateVisuals();
        }
        else
        {
            SchedulePushFocusRect();
        }

        return size;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        double scale = Math.Max(0.05, LayoutScale);
        return new Size(
            double.IsInfinity(availableSize.Width) ? ExperienceSize * scale : availableSize.Width,
            DesignHeight * scale);
    }

    // ---- Input ------------------------------------------------------------

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
            case Key.Home:
                SetSelectedIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                SetSelectedIndex(_items.Count - 1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    private sealed record IconVisual(
        Border Root,
        TransformOperationsTransition RootTransform,
        DoubleTransition RootOpacity);
}
