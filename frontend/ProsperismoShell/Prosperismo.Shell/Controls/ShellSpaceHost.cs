// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The Games / Media space transition, ported from HOME m584
/// (<c>useSpaceAnimation</c>, reached from
/// <c>components/ExperienceSwitcher/index.tsx</c> m513).
///
/// The source, with its minified names kept so the two can be diffed by eye:
///
/// <code>
/// var e = useAnimatedValue(() => 1920 * t.indexOf(n));
/// var S = { transform: [{ translateX: e }] };
/// var m = useAnimatedValues(() => Array(t.length).fill(1));
/// // spaceStyles["space" + i] = { opacity: m[i] }
/// useEffect(() => {
///     var u = t.indexOf(n),
///         a = m.map((n, t) => {
///             var e = t === u ? 1 : 0;
///             return spring(n, {toValue: e, ...SPRING_OPTIONS_SLOW, ...(!e &amp;&amp; {animated: false})});
///         });
///     Animated.sequence([
///         timing(e, { toValue: 1920 * -u, duration: 0 }),
///         Animated.parallel(a)
///     ]).start()
/// }, [n, e, m, t]);
/// </code>
///
/// Two details here are easy to get wrong and both are load-bearing:
///
/// <list type="bullet">
/// <item>
/// The pan is <c>duration: 0</c>. Switching space does <b>not</b> slide the
/// strand across; the row jumps a whole screen width instantly and the change
/// reads entirely as a cross-fade. Animating the pan looks smoother and is
/// wrong.
/// </item>
/// <item>
/// The outgoing space carries <c>animated: false</c>, so only the arriving
/// space is sprung. The one being left drops to transparent on the same frame
/// as the jump, which is what stops the two spaces ever being visible at once
/// across the seam.
/// </item>
/// </list>
///
/// The sequence is a jump followed by the fades, so the pan lands before the
/// first fade frame and no space is ever drawn at the wrong offset.
/// </summary>
public sealed class ShellSpaceHost : Panel
{
    /// <summary>
    /// The pan step. This is the shell's design width, not the control's: the
    /// source hard-codes 1920 and the surrounding Viewbox does the scaling, so
    /// a space always sits exactly one authored screen from its neighbour.
    /// </summary>
    public const double SpacePitch = 1920.0;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    /// <summary>Which space is showing. Index into this panel's children.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<ShellSpaceHost, int>(nameof(SelectedIndex));

    /// <summary>
    /// Run no timer of our own; the host drives <see cref="Advance"/>.
    ///
    /// This used to settle the fade outright, which made it the only control in
    /// the shell whose ManualClock meant something different from every other
    /// one, and left the default (false) starting a real timer even where a
    /// caller was already advancing it. That double-drive is what made these
    /// tests fail on some runs and not others.
    /// </summary>
    public static readonly StyledProperty<bool> ManualClockProperty =
        AvaloniaProperty.Register<ShellSpaceHost, bool>(nameof(ManualClock));

    private readonly TranslateTransform _pan = new();
    private readonly Stopwatch _stopwatch = new();
    private ShellSpring[] _opacity = Array.Empty<ShellSpring>();
    private DispatcherTimer? _timer;
    private int _appliedFor = -1;

    public ShellSpaceHost()
    {
        RenderTransform = _pan;
        RenderTransformOrigin = RelativePoint.TopLeft;
        ClipToBounds = false;
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public bool ManualClock
    {
        get => GetValue(ManualClockProperty);
        set => SetValue(ManualClockProperty, value);
    }

    /// <summary>True while any space is still fading.</summary>
    public bool HasPendingMotion
    {
        get
        {
            foreach (var spring in _opacity)
            {
                if (!spring.IsSettled)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Advances the fades by <paramref name="delta"/>. Public so a test can
    /// drive the transition without a dispatcher.
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        double seconds = delta.TotalSeconds;
        bool moving = false;
        for (int i = 0; i < _opacity.Length; i++)
        {
            moving |= _opacity[i].Advance(seconds);
        }

        ApplyOpacities();

        if (!moving)
        {
            StopTimer();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIndexProperty)
        {
            Retarget();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Every space is a full authored screen; the panel is as wide as all of
        // them laid end to end so the pan has somewhere to move to.
        double height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(SpacePitch, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(SpacePitch * Math.Max(1, Children.Count), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureSprings();
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(i * SpacePitch, 0.0, SpacePitch, finalSize.Height));
        }

        // Seed on the first arrange so the panel does not fade in from nothing
        // on the way up: the source starts every opacity at 1 and lets the
        // effect knock the unselected ones down.
        if (_appliedFor < 0)
        {
            Retarget(seed: true);
        }

        return finalSize;
    }

    private void EnsureSprings()
    {
        if (_opacity.Length == Children.Count)
        {
            return;
        }

        var next = new ShellSpring[Children.Count];
        for (int i = 0; i < next.Length; i++)
        {
            // Opacity is 0..1, so the rest thresholds are far tighter than the
            // strand's, which works in pixels.
            next[i] = i < _opacity.Length ? _opacity[i] : new ShellSpring(0.001, 0.005);
        }

        _opacity = next;
        _appliedFor = -1;
    }

    private void Retarget(bool seed = false)
    {
        EnsureSprings();
        if (_opacity.Length == 0)
        {
            return;
        }

        int selected = Math.Clamp(SelectedIndex, 0, _opacity.Length - 1);
        if (!seed && _appliedFor == selected)
        {
            return;
        }

        _appliedFor = selected;

        // timing(..., duration: 0): the pan is not animated. It is applied
        // before the fades so the arriving space is already in place.
        _pan.X = -SpacePitch * selected;

        for (int i = 0; i < _opacity.Length; i++)
        {
            if (i == selected)
            {
                if (seed)
                {
                    _opacity[i].SnapTo(1.0);
                }
                else
                {
                    _opacity[i].SpringTo(1.0, ShellSpringConfig.Slow);
                }
            }
            else
            {
                // `animated: false` on the outgoing space.
                _opacity[i].SnapTo(0.0);
            }
        }

        ApplyOpacities();
        Wake();
    }

    private void ApplyOpacities()
    {
        int count = Math.Min(_opacity.Length, Children.Count);
        for (int i = 0; i < count; i++)
        {
            double value = _opacity[i].Value;
            Children[i].Opacity = value <= 0.0 ? 0.0 : (value >= 1.0 ? 1.0 : value);
            // A fully transparent space must not eat input meant for the one on
            // screen; the source unmounts it, we hide it.
            Children[i].IsHitTestVisible = value > 0.0;
        }
    }

    private void Wake()
    {
        if (ManualClock || !HasPendingMotion)
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                _timer.Tick += OnTick;
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            // No dispatcher: the transition simply arrives.
            SettleNow();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _stopwatch.Elapsed;
        _stopwatch.Restart();
        Advance(delta);
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

    /// <summary>Drops every fade onto its target.</summary>
    public void SettleNow()
    {
        foreach (var spring in _opacity)
        {
            spring.Settle();
        }

        ApplyOpacities();
        StopTimer();
    }
}
