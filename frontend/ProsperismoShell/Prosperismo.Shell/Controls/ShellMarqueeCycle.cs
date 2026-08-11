// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Controls;

/// <summary>How fast a marquee runs, <c>MarqueeLabelSpeed</c>.</summary>
public enum ShellMarqueeSpeed
{
    /// <summary>Coefficient 0.25.</summary>
    VerySlow = -2,

    /// <summary>Coefficient 0.5.</summary>
    Slow = -1,

    /// <summary>Coefficient 1.0, the default.</summary>
    Normal = 0,

    /// <summary>Coefficient 1.5.</summary>
    Fast = 1,
}

/// <summary>Where a marquee is in its cycle, <c>MarqueeStatus</c>.</summary>
public enum ShellMarqueeStatus
{
    /// <summary>Parked at the start, waiting out the dwell.</summary>
    StopAtLeft,

    /// <summary>Scrolling.</summary>
    Moving,

    /// <summary>Reached the end, fading back to the start.</summary>
    StopAtRight,

    /// <summary>Text fits, so it never moves.</summary>
    NoMoveByShortText,
}

/// <summary>
/// The scroll cycle of the console's marquee label, taken from the managed
/// shell's <c>Sce.PlayStation.PUI.UI3.MarqueeLabelElement</c> (4.03, the only
///
/// Kept apart from the control that draws it for two reasons: the cycle is
/// pure arithmetic and deserves to be tested as such, and measuring text needs
/// a font manager that a plain test host does not have.
///
/// The source advances position as
///
/// <code>
/// num = marqueeBasePosX - velocity * accessibilityVelocityCoef * (elapsedTime / 16.6667f);
/// </code>
///
/// so <c>velocity</c> is pixels per 60 Hz frame rather than per second, and the
/// shipped default of 1.0 is 60 px/s. Reading it as pixels per second runs the
/// marquee sixty times too slowly and still looks plausible in a still.
///
/// At the end the element does not reverse: it fades out over 0.3 s, snaps back
/// to the start, fades in over 0.25 s, and only then re-arms the 2 s dwell.
/// </summary>
public sealed class ShellMarqueeCycle
{
    /// <summary><c>ResetMarqueeDelayTime</c>: the pause before a pass, in ms.</summary>
    public const double DwellMs = 2000.0;

    /// <summary>
    /// The frame the velocity is quoted against. The source divides elapsed
    /// milliseconds by this, which is what makes velocity per-frame.
    /// </summary>
    public const double ReferenceFrameMs = 16.6667;

    /// <summary>Shipped default <c>velocity</c>, in pixels per reference frame.</summary>
    public const double DefaultVelocity = 1.0;

    /// <summary><c>marqueeFadeOutAnimOption</c>, 0.3 s.</summary>
    public const double FadeOutMs = 300.0;

    /// <summary><c>marqueeFadeInAnimOption</c>, 0.25 s.</summary>
    public const double FadeInMs = 250.0;

    private double _elapsedMs;
    private double _dwellRemainingMs = DwellMs;
    private double _fadeMs;
    private bool _fadingOut;
    private bool _fadingIn;

    /// <summary>Pixels per reference frame. Defaults to the shipped value.</summary>
    public double Velocity { get; set; } = DefaultVelocity;

    /// <summary>The accessibility speed setting.</summary>
    public ShellMarqueeSpeed Speed { get; set; } = ShellMarqueeSpeed.Normal;

    /// <summary>Current scroll offset, zero at the start of the text.</summary>
    public double Offset { get; private set; }

    /// <summary>Opacity of the text, which drops only during the turnaround.</summary>
    public double Opacity { get; private set; } = 1.0;

    /// <summary>Where the cycle is.</summary>
    public ShellMarqueeStatus Status { get; private set; } = ShellMarqueeStatus.StopAtLeft;

    /// <summary>
    /// <c>GetAccessibilityVelocityCoef</c>. The accessibility setting scales the
    /// rate rather than replacing it.
    /// </summary>
    public static double VelocityCoefficient(ShellMarqueeSpeed speed) => speed switch
    {
        ShellMarqueeSpeed.Fast => 1.5,
        ShellMarqueeSpeed.Normal => 1.0,
        ShellMarqueeSpeed.Slow => 0.5,
        ShellMarqueeSpeed.VerySlow => 0.25,
        _ => 1.0,
    };

    /// <summary>Pixels travelled per second at the current settings.</summary>
    public double PixelsPerSecond =>
        Velocity * VelocityCoefficient(Speed) * (1000.0 / ReferenceFrameMs);

    /// <summary>Puts the cycle back to a fresh dwell at the start.</summary>
    public void Reset()
    {
        Offset = 0.0;
        Opacity = 1.0;
        _elapsedMs = 0.0;
        _fadeMs = 0.0;
        _fadingIn = false;
        _fadingOut = false;
        _dwellRemainingMs = DwellMs;
        Status = ShellMarqueeStatus.StopAtLeft;
    }

    /// <summary>Marks the label as one that fits and will never move.</summary>
    public void SetShort()
    {
        Reset();
        Status = ShellMarqueeStatus.NoMoveByShortText;
    }

    /// <summary>
    /// Advances by <paramref name="deltaMs"/>.
    /// <paramref name="scrollDistance"/> is how far the text can travel before
    /// its end is flush with the right edge.
    /// </summary>
    /// <returns>True while the cycle still needs frames.</returns>
    public bool Advance(double deltaMs, double scrollDistance)
    {
        if (Status == ShellMarqueeStatus.NoMoveByShortText || scrollDistance <= 0.0)
        {
            return false;
        }

        if (!(deltaMs > 0.0) || double.IsNaN(deltaMs))
        {
            return true;
        }

        if (_fadingOut)
        {
            _fadeMs += deltaMs;
            Opacity = Math.Clamp(1.0 - (_fadeMs / FadeOutMs), 0.0, 1.0);
            if (_fadeMs >= FadeOutMs)
            {
                // ResetMarqueePosition, then fade back in.
                Offset = 0.0;
                _elapsedMs = 0.0;
                _fadingOut = false;
                _fadingIn = true;
                _fadeMs = 0.0;
                Status = ShellMarqueeStatus.StopAtLeft;
            }

            return true;
        }

        if (_fadingIn)
        {
            _fadeMs += deltaMs;
            Opacity = Math.Clamp(_fadeMs / FadeInMs, 0.0, 1.0);
            if (_fadeMs >= FadeInMs)
            {
                _fadingIn = false;
                Opacity = 1.0;
                // The dwell is re-armed only once the fade has finished.
                _dwellRemainingMs = DwellMs;
            }

            return true;
        }

        if (_dwellRemainingMs > 0.0)
        {
            _dwellRemainingMs -= deltaMs;
            Status = ShellMarqueeStatus.StopAtLeft;
            return true;
        }

        _elapsedMs += deltaMs;
        Status = ShellMarqueeStatus.Moving;
        Offset = Velocity * VelocityCoefficient(Speed) * (_elapsedMs / ReferenceFrameMs);

        if (Offset >= scrollDistance)
        {
            Offset = scrollDistance;
            Status = ShellMarqueeStatus.StopAtRight;
            _fadingOut = true;
            _fadeMs = 0.0;
        }

        return true;
    }
}
