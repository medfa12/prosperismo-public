// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Animation.Easings;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// <c>ParametricAnimationCurve</c>, the shell's own easing family, recovered on
/// branch <c>re/ui-motion-spec</c> and verified byte-identical between
/// <c>Sce.PlayStation.PUI</c> and the React Native bundle.
///
/// <para>The curve is not a bezier and cannot be expressed as one. It takes two
/// parameters and nothing else:</para>
/// <list type="bullet">
///   <item><description><b>tipping point</b> <c>p</c> — where the curve turns
///   over. Zero or below is a pure ease-out; one or above is a pure ease-in; in
///   between the curve eases out of an ease-in at <c>p</c>.</description></item>
///   <item><description><b>momentum</b> <c>m</c> — how hard it moves, folded
///   into the exponent <c>n = 9m + 1</c> and into the input compression
///   <c>k</c>.</description></item>
/// </list>
///
/// <para><b>This class invents nothing.</b> The formula below is transcribed,
/// that wants a different feel needs a citation, not a new constant here.</para>
/// </summary>
public sealed class Ps5AnimationCurve : Easing
{
    private readonly double _n;
    private readonly double _k;

    private Ps5AnimationCurve(double tippingPoint, double momentum)
    {
        TippingPoint = tippingPoint;
        Momentum = momentum;
        _n = (9.0 * momentum) + 1.0;

        // 0.8 / (0.6m + 0.2) * 0.5. The half is part of the constant, not a
        // separate normalisation: at m = 1 it makes k exactly 0.5, so an
        // ease-out only ever consumes the first half of its own input range.
        _k = 0.8 / ((0.6 * momentum) + 0.2) * 0.5;
    }

    /// <summary>The <c>p</c> the curve was created with.</summary>
    public double TippingPoint { get; }

    /// <summary>The <c>m</c> the curve was created with.</summary>
    public double Momentum { get; }

    /// <summary>The exponent <c>n = 9m + 1</c>.</summary>
    public double Exponent => _n;

    /// <summary>The input compression <c>k</c>.</summary>
    public double Compression => _k;

    /// <summary><c>EaseOutBlast</c>: <c>(0, 1)</c>.</summary>
    public static Ps5AnimationCurve EaseOutBlast { get; } = Create(0.0, 1.0);

    /// <summary><c>EaseOutBreeze</c>: <c>(0, 0.4)</c>.</summary>
    public static Ps5AnimationCurve EaseOutBreeze { get; } = Create(0.0, 0.4);

    /// <summary><c>EaseSmoothOutBlast</c>: <c>(0.05, 1)</c>.</summary>
    public static Ps5AnimationCurve EaseSmoothOutBlast { get; } = Create(0.05, 1.0);

    /// <summary>
    /// <c>EaseSmoothOutBreeze</c>: <c>(0.05, 0.4)</c>. Also
    /// <c>DefaultScreenTransitionCurve</c> — the curve the shell moves whole
    /// scenes on, and the one to reach for when a screen changes.
    /// </summary>
    public static Ps5AnimationCurve EaseSmoothOutBreeze { get; } = Create(0.05, 0.4);

    /// <summary><c>EaseFlyingOutBlast</c>: <c>(-0.4, 1)</c>.</summary>
    public static Ps5AnimationCurve EaseFlyingOutBlast { get; } = Create(-0.4, 1.0);

    /// <summary><c>EaseFlyingOutBreeze</c>: <c>(-0.4, 0.4)</c>.</summary>
    public static Ps5AnimationCurve EaseFlyingOutBreeze { get; } = Create(-0.4, 0.4);

    /// <summary><c>DefaultEaseIn</c>: <c>(1, 1)</c>.</summary>
    public static Ps5AnimationCurve DefaultEaseIn { get; } = Create(1.0, 1.0);

    /// <summary><c>DefaultScreenTransitionCurve</c>, by its own name.</summary>
    public static Ps5AnimationCurve DefaultScreenTransition => EaseSmoothOutBreeze;

    /// <summary>
    /// Builds the curve for a <c>(p, m)</c> pair.
    /// </summary>
    /// <param name="tippingPoint">Tipping point <c>p</c>.</param>
    /// <param name="momentum">Momentum <c>m</c>.</param>
    public static Ps5AnimationCurve Create(double tippingPoint, double momentum) =>
        new(tippingPoint, momentum);

    /// <inheritdoc/>
    public override double Ease(double progress) => Evaluate(progress);

    /// <summary>
    /// The curve itself, on <c>t</c> in <c>[0, 1]</c>.
    /// </summary>
    /// <param name="t">Normalised time.</param>
    public double Evaluate(double t)
    {
        var p = TippingPoint;

        // The input is compressed by k, and an ease-in additionally starts
        // DefaultEaseIn reaches exactly 1 while the ease-outs stop just short.
        var x = t * _k;
        if (p > 0.0)
        {
            x += (1.0 - _k) * p;
        }

        x = Math.Min(1.0, x);

        if (p <= 0.0)
        {
            return 1.0 - (Math.Pow(1.0 - x, _n) * (1.0 + p));
        }

        if (p >= 1.0)
        {
            return Math.Pow(x, _n);
        }

        return x < p
            ? Math.Pow(x / p, _n) * p
            : ((1.0 - Math.Pow(1.0 - ((x - p) / (1.0 - p)), _n)) * (1.0 - p)) + p;
    }
}

/// <summary>
/// <c>AnimationOption</c>, the shell's animation descriptor. Durations and
/// delays are in <b>seconds</b>, which is the one thing about this record that
/// is easy to get wrong and expensive to notice.
/// </summary>
/// <param name="Duration">Duration, in seconds.</param>
/// <param name="Delay">Delay before the animation starts, in seconds.</param>
/// <param name="Curve">Easing curve, or null for the platform default.</param>
/// <param name="Repeat">Repeat count.</param>
public readonly record struct Ps5AnimationOption(
    double Duration,
    double Delay = 0.0,
    Ps5AnimationCurve? Curve = null,
    int Repeat = 0)
{
    /// <summary><see cref="Duration"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan DurationTime => TimeSpan.FromSeconds(Duration);

    /// <summary><see cref="Delay"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan DelayTime => TimeSpan.FromSeconds(Delay);
}
