// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// The shell's damped spring, the same one the tile strand and the focus ring
/// run on: stiffness 400, damping 50, mass 0.2, overshoot clamped. The parameters
/// are deliberately shared so the background moves with the foreground rather
/// than beside it - a focus change that snaps the tiles must not leave the field
/// still swinging.
///
/// Those numbers are overdamped (critical damping for this mass and stiffness is
/// ~17.9, and the damping is 50), so the value approaches its target without
/// crossing it and the clamp is a guard rather than the shape of the motion. The
/// slow root works out at ~8.3 rad/s, i.e. a ~0.12 s time constant and a settle
/// inside about half a second.
///
/// Integrated with explicit Euler on a fixed sub-step, because the raw frame
/// interval is close enough to this spring's stability limit that a dropped frame
/// would otherwise make it ring.
/// </summary>
public struct ShellSpring
{
    /// <summary>Shell spring stiffness.</summary>
    public const double Stiffness = 400.0;

    /// <summary>Shell spring damping.</summary>
    public const double Damping = 50.0;

    /// <summary>Shell spring mass.</summary>
    public const double Mass = 0.2;

    /// <summary>Integration sub-step, in seconds.</summary>
    public const double SubStepSeconds = 1.0 / 480.0;

    /// <summary>Longest interval integrated in one <see cref="Advance"/> call.</summary>
    public const double MaxStepSeconds = 0.25;

    /// <summary>Displacement and velocity below which the spring is called settled.</summary>
    public const double RestEpsilon = 1e-3;

    /// <summary>
    /// Velocity a <see cref="Nudge"/> needs to reach a peak displacement of one,
    /// measured against this integrator. These roots are far apart (-8.3 and
    /// -241.7), so an impulse peaks at a small fraction of the velocity it was
    /// given - which is why a reaction expressed as a raw nudge has to be scaled
    /// by this, or it never shows at all.
    /// </summary>
    public const double ImpulseVelocityForUnitPeak = 571.3;

    private double _value;
    private double _velocity;
    private double _target;

    /// <summary>Creates a spring resting at <paramref name="value"/>.</summary>
    /// <param name="value">Initial value, which is also the initial target.</param>
    public ShellSpring(double value)
    {
        _value = value;
        _velocity = 0.0;
        _target = value;
    }

    /// <summary>Current value.</summary>
    public readonly double Value => _value;

    /// <summary>Current velocity, in units per second.</summary>
    public readonly double Velocity => _velocity;

    /// <summary>Where the spring is heading.</summary>
    public double Target
    {
        readonly get => _target;
        set => _target = value;
    }

    /// <summary>
    /// True once the spring has both reached its target and stopped moving. The
    /// idle field checks this to know the reactive impulse has fully decayed.
    /// </summary>
    public readonly bool IsAtRest =>
        Math.Abs(_value - _target) <= RestEpsilon && Math.Abs(_velocity) <= RestEpsilon;

    /// <summary>Snaps the spring to a value, cancelling any motion.</summary>
    /// <param name="value">Value to rest at; also becomes the target.</param>
    public void SnapTo(double value)
    {
        _value = value;
        _target = value;
        _velocity = 0.0;
    }

    /// <summary>
    /// Kicks the spring's velocity without moving its target, which is how a
    /// one-shot reaction is expressed: the value rises, the restoring force pulls
    /// it back, and it decays to the target instead of holding a new state.
    /// </summary>
    /// <param name="velocityDelta">Velocity to add, in units per second.</param>
    public void Nudge(double velocityDelta)
    {
        _velocity += velocityDelta;
    }

    /// <summary>
    /// Integrates <paramref name="seconds"/> of motion. Longer intervals are
    /// clamped to <see cref="MaxStepSeconds"/> so a stalled window does not
    /// teleport the field when it comes back.
    /// </summary>
    /// <param name="seconds">Elapsed time.</param>
    public void Advance(double seconds)
    {
        if (seconds <= 0.0 || IsAtRest)
        {
            if (IsAtRest)
            {
                _value = _target;
                _velocity = 0.0;
            }

            return;
        }

        var remaining = Math.Min(seconds, MaxStepSeconds);
        while (remaining > 0.0)
        {
            var step = Math.Min(SubStepSeconds, remaining);
            remaining -= step;

            var displacement = _value - _target;
            var acceleration = ((-Stiffness * displacement) - (Damping * _velocity)) / Mass;
            _velocity += acceleration * step;

            var next = _value + (_velocity * step);

            // overshootClamping: crossing the target ends the motion rather than
            // starting a bounce.
            if ((displacement < 0.0 && next > _target) || (displacement > 0.0 && next < _target))
            {
                _value = _target;
                _velocity = 0.0;
                return;
            }

            _value = next;
        }

        if (IsAtRest)
        {
            _value = _target;
            _velocity = 0.0;
        }
    }
}
