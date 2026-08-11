// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The shell's three interaction levels, ported verbatim from the home bundle's
/// own enum (HOME m720, re-exported under the name <c>INTERACTION</c> by
/// HOME m18).
///
/// The distinction we were missing is <see cref="Glanced"/>: the shell does not
/// have a binary focused/not, it has a weaker "the cursor is resting here"
/// level that drives real differences. A glanced action indicator sits at 0.7
/// where a focused one sits at 1.0, and a glanced system icon shows its label
/// where an unfocused one does not.
/// </summary>
public enum ShellInteraction
{
    /// <summary>The pointer or cursor rests here but the region does not own
    /// the scene's focus.</summary>
    Glanced,

    /// <summary>This item owns the focus.</summary>
    Focused,

    /// <summary>The item is being acted on.</summary>
    Action,
}

/// <summary>
/// The four spring presets the home shell animates with (HOME m49). Only the
/// two slow ones clamp overshoot; the fast pair is allowed to pass its target,
/// which is what gives the glance its small snap.
/// </summary>
public static class ShellSprings
{
    /// <summary>stiffness 130, damping 25, mass 1, overshoot clamped.</summary>
    public static readonly ShellSpringEasing Slow = new(130.0, 25.0, 1.0);

    /// <summary>stiffness 100, damping 20, mass 1, overshoot clamped.</summary>
    public static readonly ShellSpringEasing Slower = new(100.0, 20.0, 1.0);

    /// <summary>stiffness 200, damping 100, mass 0.2. The glance spring.</summary>
    public static readonly ShellSpringEasing Fast = new(200.0, 100.0, 0.2);

    /// <summary>stiffness 600, damping 100, mass 0.2.</summary>
    public static readonly ShellSpringEasing Faster = new(600.0, 100.0, 0.2);
}

/// <summary>
/// One spring-driven scalar. Retargeting re-seeds the curve from wherever the
/// value currently sits rather than from its old origin, so a glance that is
/// interrupted half way does not jump back before running.
///
/// Kept free of Avalonia types for the same reason
/// <see cref="ShellFocusRingTimeline"/> is: the glance timing is then testable
/// without a render surface, which matters here because synthetic pointer input
/// does not reach the shell in this environment.
/// </summary>
public sealed class ShellSpringValue
{
    private readonly ShellSpringEasing _curve;
    private double _from;
    private double _target;
    private double _elapsed;

    public ShellSpringValue(ShellSpringEasing curve, double initial)
    {
        _curve = curve ?? throw new ArgumentNullException(nameof(curve));
        _from = initial;
        _target = initial;
        _elapsed = curve.SettleSeconds;
    }

    /// <summary>Where the spring is heading.</summary>
    public double Target => _target;

    /// <summary>True while the spring has not settled on its target.</summary>
    public bool IsAnimating => _elapsed < _curve.SettleSeconds;

    /// <summary>The value right now.</summary>
    public double Value
    {
        get
        {
            if (!IsAnimating)
            {
                return _target;
            }

            double p = Math.Clamp(_elapsed / _curve.SettleSeconds, 0.0, 1.0);
            return _from + ((_target - _from) * _curve.Ease(p));
        }
    }

    /// <summary>Sends the spring to <paramref name="target"/> from wherever it
    /// currently is. A no-op when it is already going there.</summary>
    public void SetTarget(double target)
    {
        if (Math.Abs(target - _target) < 1e-9)
        {
            return;
        }

        _from = Value;
        _target = target;
        _elapsed = 0.0;
    }

    /// <summary>Drops the spring onto <paramref name="value"/> with no travel.</summary>
    public void SnapTo(double value)
    {
        _from = value;
        _target = value;
        _elapsed = _curve.SettleSeconds;
    }

    /// <summary>Advances the spring by <paramref name="delta"/>.</summary>
    public void Advance(TimeSpan delta)
    {
        double seconds = delta.TotalSeconds;
        if (!(seconds > 0.0) || double.IsNaN(seconds) || !IsAnimating)
        {
            return;
        }

        _elapsed = Math.Min(_curve.SettleSeconds, _elapsed + seconds);
    }
}

/// <summary>
/// The glance treatment a system icon in the nav band runs, ported from the
/// home bundle's <c>useTextAnimation</c> hook (HOME m673, consumed by
/// <c>SystemIcon/iconAndText.tsx</c>, HOME m224).
///
/// Two springs, both on the fast preset, driven off two booleans:
///
/// <list type="bullet">
/// <item>the icon scales between <c>SYSTEM_ICON_SIZE_NO_GLANCE / SYSTEM_ICON_SIZE</c>
/// (48/56) and 1, targeting 1 when the icon is glanced <b>or</b> its modal is
/// open, and</item>
/// <item>the label fades 0 to 1, targeting 1 only when the icon is glanced
/// <b>and</b> its modal is closed.</item>
/// </list>
///
/// The asymmetry is the whole point and is easy to lose in a re-implementation:
/// opening the profile popover keeps the icon large but takes its label away,
/// because the popover itself now names the destination.
/// </summary>
public sealed class ShellGlanceState
{
    /// <summary>Resting icon scale, 48/56 (HOME m143, HOME m673 outputRange).</summary>
    public const double IconRestScale =
        ShellNavBand.SystemIconSizeNoGlance / ShellNavBand.SystemIconSize;

    /// <summary>Action indicator opacity when glanced
    /// (<c>ANIMATION.OPACITY.ACTION_INDICATOR.MIN</c>, HOME:51159).</summary>
    public const double GlancedOpacity = 0.7;

    /// <summary>Action indicator opacity when focused
    /// (<c>ANIMATION.OPACITY.ACTION_INDICATOR.MAX</c>, HOME:51159).</summary>
    public const double FocusedOpacity = 1.0;

    private readonly ShellSpringValue _icon = new(ShellSprings.Fast, 0.0);
    private readonly ShellSpringValue _label = new(ShellSprings.Fast, 0.0);

    private bool _glanced;
    private bool _modalVisible;

    /// <summary>True while the cursor rests on this icon.</summary>
    public bool IsGlanced => _glanced;

    /// <summary>True while this icon's popover is open.</summary>
    public bool IsModalVisible => _modalVisible;

    /// <summary>True while either spring is still running.</summary>
    public bool IsAnimating => _icon.IsAnimating || _label.IsAnimating;

    /// <summary>Scale to apply to the icon, from 48/56 at rest up to 1.</summary>
    public double IconScale => IconRestScale + ((1.0 - IconRestScale) * _icon.Value);

    /// <summary>The glance label's opacity, 0 to 1.</summary>
    public double LabelOpacity => _label.Value;

    /// <summary>The interaction level this icon is currently at.</summary>
    public ShellInteraction Interaction => _glanced
        ? (_modalVisible ? ShellInteraction.Action : ShellInteraction.Focused)
        : ShellInteraction.Glanced;

    /// <summary>Opacity for an indicator riding this state, 0.7 glanced and
    /// 1.0 focused (HOME m737).</summary>
    public double IndicatorOpacity =>
        Interaction == ShellInteraction.Glanced ? GlancedOpacity : FocusedOpacity;

    /// <summary><c>onFocus</c>: the cursor arrived.</summary>
    public void Glance() => SetGlanced(true);

    /// <summary><c>onBlur</c>: the cursor left.</summary>
    public void Blur() => SetGlanced(false);

    /// <summary>Opens or closes this icon's popover.</summary>
    public void SetModalVisible(bool visible)
    {
        if (_modalVisible == visible)
        {
            return;
        }

        _modalVisible = visible;
        Retarget();
    }

    /// <summary>Advances both springs.</summary>
    public void Advance(TimeSpan delta)
    {
        _icon.Advance(delta);
        _label.Advance(delta);
    }

    /// <summary>Drops both springs onto their targets with no travel.</summary>
    public void Settle()
    {
        _icon.SnapTo(_icon.Target);
        _label.SnapTo(_label.Target);
    }

    private void SetGlanced(bool glanced)
    {
        if (_glanced == glanced)
        {
            return;
        }

        _glanced = glanced;
        Retarget();
    }

    // The two targets the hook computes: icon on (glanced || modal), label on
    // (glanced && !modal).
    private void Retarget()
    {
        _icon.SetTarget(_glanced || _modalVisible ? 1.0 : 0.0);
        _label.SetTarget(_glanced && !_modalVisible ? 1.0 : 0.0);
    }
}
