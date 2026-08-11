// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace Prosperismo.GUI;

/// <summary>
/// The home shell's parametric ease-out curve family. The shell does not use
/// cubic beziers for its primary transitions: it builds curves from two
/// parameters, a tail sharpness ("flat") and an anticipation amount ("back").
/// For the pure ease-out case the curve is
/// <c>y = 1 - (1 - min(x * i, 1))^r</c> with <c>r = 9 * flat + 1</c> and
/// <c>i = 400 / (600 * flat + 200)</c>.
///
/// Reproducing the curve exactly is cheaper than approximating it with a
/// spline, so <see cref="ShellMotion.EaseOutBreeze"/> (flat 0.4, r ~= 4.6) and
/// <see cref="ShellMotion.EaseOutBlast"/> (flat 1.0, r = 10) are the real
/// curves rather than <c>SplineEasing</c> stand-ins.
/// </summary>
public sealed class ShellEaseOut : Easing
{
    private readonly double _power;
    private readonly double _compression;

    public ShellEaseOut()
        : this(0.4)
    {
    }

    public ShellEaseOut(double flat)
    {
        _power = (9.0 * flat) + 1.0;
        _compression = 400.0 / ((600.0 * flat) + 200.0);
    }

    public override double Ease(double progress)
    {
        var x = Math.Clamp(progress * _compression, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - x, _power);
    }
}

/// <summary>
/// Home-shell motion for the option menu and the modal surfaces.
///
/// The numbers here are the shell's own: a 300 ms default transition, a modal
/// show of 250 ms on the sharp ease-out with a 50 ms lead-in delay, a modal
/// hide of 300 ms linear, and a one-frame (16.67 ms at 60 fps) per-item
/// stagger for grid/tile reveals. The stagger belongs to the tile-appear path
/// only; the shell's menus have no per-row stagger, so the menu motion here is
/// the panel transition alone.
///
/// Delays are expressed as a held first segment inside the key frames rather
/// than <see cref="Animation.Delay"/> so that the opening state is applied on
/// the very first frame; otherwise a surface would flash at its resting
/// opacity for the length of the delay.
/// </summary>
public sealed class ShellMotion
{
    /// <summary>Gentle ease-out; the shell's default curve.</summary>
    public static readonly Easing EaseOutBreeze = new ShellEaseOut(0.4);

    /// <summary>Aggressive front-loaded ease-out; used for modal show.</summary>
    public static readonly Easing EaseOutBlast = new ShellEaseOut(1.0);

    /// <summary>Modal hide is linear, not eased.</summary>
    public static readonly Easing HideEasing = new LinearEasing();

    /// <summary>The shell's standard transition duration.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>Modal show duration.</summary>
    public static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Modal show lead-in delay.</summary>
    public static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Modal hide duration.</summary>
    public static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>One 60 fps frame; the per-item stagger for grid/tile reveals.
    /// Menus do not use it: the shell has no per-row stagger in the menu path.</summary>
    public static readonly TimeSpan ItemStagger = TimeSpan.FromTicks(166_667);

    /// <summary>How far a surface rises into place, in device-independent pixels.</summary>
    private const double RiseDistance = 10.0;

    private ShellMotion()
    {
    }

    /// <summary>
    /// Set on a <see cref="ContextMenu"/> to give it the option-menu motion:
    /// the panel rises and fades in as one surface (rows do not stagger), and
    /// dismissal is held back for the linear hide.
    /// </summary>
    public static readonly AttachedProperty<bool> MenuMotionProperty =
        AvaloniaProperty.RegisterAttached<ShellMotion, ContextMenu, bool>("MenuMotion");

    /// <summary>
    /// Set on a <see cref="Popup"/> to give its child the modal show motion.
    /// Native popups are torn down synchronously when they close, so only the
    /// show can be animated here; windows use <see cref="HideSurfaceAsync"/>.
    /// </summary>
    public static readonly AttachedProperty<bool> PopupMotionProperty =
        AvaloniaProperty.RegisterAttached<ShellMotion, Popup, bool>("PopupMotion");

    private static readonly AttachedProperty<SurfaceState?> SurfaceStateProperty =
        AvaloniaProperty.RegisterAttached<ShellMotion, Control, SurfaceState?>("SurfaceState");

    private static readonly AttachedProperty<bool> MotionWiredProperty =
        AvaloniaProperty.RegisterAttached<ShellMotion, Control, bool>("MotionWired");

    static ShellMotion()
    {
        MenuMotionProperty.Changed.AddClassHandler<ContextMenu>(static (menu, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                WireMenu(menu);
            }
        });

        PopupMotionProperty.Changed.AddClassHandler<Popup>(static (popup, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                WirePopup(popup);
            }
        });
    }

    public static void SetMenuMotion(ContextMenu menu, bool value) => menu.SetValue(MenuMotionProperty, value);

    public static bool GetMenuMotion(ContextMenu menu) => menu.GetValue(MenuMotionProperty);

    public static void SetPopupMotion(Popup popup, bool value) => popup.SetValue(PopupMotionProperty, value);

    public static bool GetPopupMotion(Popup popup) => popup.GetValue(PopupMotionProperty);

    /// <summary>
    /// Runs the modal show on a surface: 250 ms on the sharp ease-out after a
    /// 50 ms delay, fading up while rising into place.
    /// </summary>
    public static Task ShowSurfaceAsync(Control surface)
    {
        var state = StateOf(surface);
        var token = Restart(state);
        return RunShow(surface, state, token);
    }

    /// <summary>
    /// Runs the modal hide on a surface: 300 ms, linear, fading out in place.
    /// </summary>
    public static Task HideSurfaceAsync(Control surface)
    {
        var state = StateOf(surface);
        var token = Restart(state);
        var fade = Build(TimeSpan.Zero, HideDuration, HideEasing, Visual.OpacityProperty, 1.0, 0.0);
        return RunAsync(fade, surface, token);
    }

    private static void WireMenu(ContextMenu menu)
    {
        if (menu.GetValue(MotionWiredProperty))
        {
            return;
        }

        menu.SetValue(MotionWiredProperty, true);
        var state = StateOf(menu);

        menu.Opened += (_, _) => OnMenuOpened(menu, state);
        menu.Closing += (_, e) => OnMenuClosing(menu, state, e);
        menu.Closed += (_, _) => state.AllowClose = false;
    }

    private static void WirePopup(Popup popup)
    {
        if (popup.GetValue(MotionWiredProperty))
        {
            return;
        }

        popup.SetValue(MotionWiredProperty, true);
        popup.Opened += (_, _) =>
        {
            if (popup.Child is { } child)
            {
                _ = ShowSurfaceAsync(child);
            }
        };
    }

    private static void OnMenuOpened(ContextMenu menu, SurfaceState state)
    {
        // The panel shows as one surface. The shell's menus have no per-row
        // stagger; the 16.67 ms stagger is the grid-tile appear treatment.
        var token = Restart(state);
        _ = RunShow(menu, state, token);
    }

    private static void OnMenuClosing(ContextMenu menu, SurfaceState state, CancelEventArgs e)
    {
        if (state.AllowClose)
        {
            state.AllowClose = false;
            return;
        }

        // The menu is hosted by a popup that only tears down once its own
        // closing pass is not cancelled, so holding the pass open is what
        // buys the 300 ms linear hide. If the popup cannot be reached the
        // menu simply closes as before.
        if (menu.Parent is not Popup popup)
        {
            return;
        }

        e.Cancel = true;

        var token = Restart(state);
        _ = HideThenClose(menu, state, popup, token);
    }

    private static async Task HideThenClose(ContextMenu menu, SurfaceState state, Popup popup, CancellationToken token)
    {
        var fade = Build(TimeSpan.Zero, HideDuration, HideEasing, Visual.OpacityProperty, 1.0, 0.0);
        await RunAsync(fade, menu, token).ConfigureAwait(true);

        if (token.IsCancellationRequested)
        {
            return;
        }

        state.AllowClose = true;
        popup.Close();
    }

    private static Task RunShow(Control surface, SurfaceState state, CancellationToken token)
    {
        var rise = EnsureRise(surface, state);
        var fade = Build(ShowDelay, ShowDuration, EaseOutBlast, Visual.OpacityProperty, 0.0, 1.0);
        var slide = Build(ShowDelay, ShowDuration, EaseOutBlast, TranslateTransform.YProperty, RiseDistance, 0.0);
        return Task.WhenAll(RunAsync(fade, surface, token), RunAsync(slide, rise, token));
    }

    private static async Task RunAsync(Animation animation, Animatable target, CancellationToken token)
    {
        try
        {
            await animation.RunAsync(target, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer transition took over; the previous one simply drops.
        }
    }

    /// <summary>
    /// Builds a single-property animation whose leading <paramref name="delay"/>
    /// is expressed as a held key-frame segment, so the from-value is applied
    /// immediately instead of after the delay.
    /// </summary>
    private static Animation Build(
        TimeSpan delay,
        TimeSpan duration,
        Easing easing,
        AvaloniaProperty property,
        double from,
        double to)
    {
        var total = delay + duration;
        var animation = new Animation
        {
            Duration = total,
            Easing = easing,
            FillMode = FillMode.Forward,
        };

        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0.0),
            Setters = { new Setter(property, from) },
        });

        if (delay > TimeSpan.Zero && total > TimeSpan.Zero)
        {
            animation.Children.Add(new KeyFrame
            {
                Cue = new Cue(delay.TotalMilliseconds / total.TotalMilliseconds),
                Setters = { new Setter(property, from) },
            });
        }

        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1.0),
            Setters = { new Setter(property, to) },
        });

        return animation;
    }

    private static TranslateTransform EnsureRise(Control surface, SurfaceState state)
    {
        if (state.Rise is { } existing && ReferenceEquals(surface.RenderTransform, existing))
        {
            return existing;
        }

        var rise = new TranslateTransform();
        state.Rise = rise;
        surface.RenderTransform = rise;
        return rise;
    }

    private static SurfaceState StateOf(Control surface)
    {
        if (surface.GetValue(SurfaceStateProperty) is { } existing)
        {
            return existing;
        }

        var state = new SurfaceState();
        surface.SetValue(SurfaceStateProperty, state);
        return state;
    }

    /// <summary>
    /// Cancels whatever transition is in flight (which restores the resting
    /// values) and hands back the token for the replacement.
    /// </summary>
    private static CancellationToken Restart(SurfaceState state)
    {
        state.Cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        state.Cancellation = cancellation;
        return cancellation.Token;
    }

    private sealed class SurfaceState
    {
        public CancellationTokenSource? Cancellation { get; set; }

        public TranslateTransform? Rise { get; set; }

        public bool AllowClose { get; set; }
    }
}
