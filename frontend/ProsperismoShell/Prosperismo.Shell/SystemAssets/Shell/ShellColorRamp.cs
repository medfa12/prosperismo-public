// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// One colour stop of a <see cref="ShellColorRamp"/>: a linear RGB triple and the
/// the background layer stores its ramps as <c>float4 (r, g, b, position)</c> and
/// the position lives in <c>.w</c>.
/// </summary>
/// <param name="R">Linear red, 0 to 1.</param>
/// <param name="G">Linear green, 0 to 1.</param>
/// <param name="B">Linear blue, 0 to 1.</param>
/// <param name="Position">Position along the ramp, 0 to 1.</param>
public readonly record struct ShellRampStop(double R, double G, double B, double Position);

/// <summary>
/// ramps are <c>float2 (alpha, position)</c> arrays evaluated by the same spline
/// as the colour ramps, which is why they share the evaluator below.
/// </summary>
/// <param name="Value">Amount, clamped to 0..1 on evaluation.</param>
/// <param name="Position">Position along the ramp, 0 to 1.</param>
public readonly record struct ShellScalarStop(double Value, double Position);

/// <summary>
/// A colour ramp baked into a lookup table, interpolated exactly the way the
/// console's background layer interpolates its own.
///
/// wave ramp textures and the 37 plate records alike - is built by a routine that
/// walks the stop list and evaluates a <b>cubic Hermite (Catmull-Rom) spline</b>
/// between the bracketing stops. Interior tangents are <c>0.5 * (p[k+1] - p[k-1])</c>,
/// the first segment uses <c>m0 = p1 - p0</c> and the last uses
/// <c>m1 = p[n-1] - p[n-2]</c>. A straight line through the same stops is visibly
/// flatter through the mid-tones, which is why nothing here is allowed to be a
/// lerp. See <c>docs/ps5-background-native.md</c>.
///
/// The ramp is sampled every frame per mote, so the spline is evaluated once at
/// 4320 texels for a wave ramp and 2160 for a plate ramp; the same order of
/// magnitude is used here.
/// </summary>
public sealed class ShellColorRamp
{
    public const int DefaultResolution = 2160;

    public const int WaveResolution = 4320;

    private readonly float[] _table;
    private readonly int _resolution;

    /// <summary>
    /// Bakes a ramp from its stops. Stops are taken in the order given and their
    /// </summary>
    /// <param name="stops">Colour stops, at least one.</param>
    /// <param name="resolution">Texels to bake; clamped to 2..65536.</param>
    public ShellColorRamp(ReadOnlySpan<ShellRampStop> stops, int resolution = DefaultResolution)
    {
        if (stops.Length == 0)
        {
            throw new ArgumentException("A ramp needs at least one stop.", nameof(stops));
        }

        _resolution = Math.Clamp(resolution, 2, 65536);
        _table = new float[_resolution * 3];

        for (var i = 0; i < _resolution; i++)
        {
            Evaluate(stops, (double)i / (_resolution - 1), out var r, out var g, out var b);
            var offset = i * 3;
            _table[offset] = (float)r;
            _table[offset + 1] = (float)g;
            _table[offset + 2] = (float)b;
        }
    }

    /// <summary>Baked texel count.</summary>
    public int Resolution => _resolution;

    /// <summary>
    /// Samples the baked table at <paramref name="t"/>, clamped to 0..1. Nearest
    /// texel: at this resolution the neighbouring texels differ by well under a
    /// display step, and the hot path cannot afford a second interpolation.
    /// </summary>
    /// <param name="t">Ramp position.</param>
    /// <param name="r">Linear red out.</param>
    /// <param name="g">Linear green out.</param>
    /// <param name="b">Linear blue out.</param>
    public void Sample(double t, out float r, out float g, out float b)
    {
        var index = (int)((Math.Clamp(t, 0.0, 1.0) * (_resolution - 1)) + 0.5);
        var offset = index * 3;
        r = _table[offset];
        g = _table[offset + 1];
        b = _table[offset + 2];
    }

    /// <summary>
    /// Below the first stop's position it emits the first stop; above the last, the
    /// last. Channels are clamped to 0..1 after interpolation, as the builder does.
    /// </summary>
    /// <param name="stops">Colour stops, at least one.</param>
    /// <param name="t">Ramp position.</param>
    /// <param name="r">Linear red out.</param>
    /// <param name="g">Linear green out.</param>
    /// <param name="b">Linear blue out.</param>
    public static void Evaluate(
        ReadOnlySpan<ShellRampStop> stops, double t, out double r, out double g, out double b)
    {
        if (stops.Length == 0)
        {
            r = 0.0;
            g = 0.0;
            b = 0.0;
            return;
        }

        var last = stops.Length - 1;
        if (stops.Length == 1 || t <= stops[0].Position)
        {
            var stop = stops[0];
            r = Math.Clamp(stop.R, 0.0, 1.0);
            g = Math.Clamp(stop.G, 0.0, 1.0);
            b = Math.Clamp(stop.B, 0.0, 1.0);
            return;
        }

        if (t >= stops[last].Position)
        {
            var stop = stops[last];
            r = Math.Clamp(stop.R, 0.0, 1.0);
            g = Math.Clamp(stop.G, 0.0, 1.0);
            b = Math.Clamp(stop.B, 0.0, 1.0);
            return;
        }

        var k = FindColorSegment(stops, t);
        var span = stops[k + 1].Position - stops[k].Position;
        var u = span > 0.0 ? (t - stops[k].Position) / span : 0.0;

        r = Math.Clamp(Channel(stops, k, u, Component.Red), 0.0, 1.0);
        g = Math.Clamp(Channel(stops, k, u, Component.Green), 0.0, 1.0);
        b = Math.Clamp(Channel(stops, k, u, Component.Blue), 0.0, 1.0);
    }

    /// <summary>
    /// Evaluates a scalar (alpha-style) ramp with the same spline and the same
    /// end-stop behaviour, clamped to 0..1.
    /// </summary>
    /// <param name="stops">Scalar stops, at least one.</param>
    /// <param name="t">Ramp position.</param>
    /// <returns>The interpolated amount.</returns>
    public static double EvaluateScalar(ReadOnlySpan<ShellScalarStop> stops, double t)
    {
        if (stops.Length == 0)
        {
            return 0.0;
        }

        var last = stops.Length - 1;
        if (stops.Length == 1 || t <= stops[0].Position)
        {
            return Math.Clamp(stops[0].Value, 0.0, 1.0);
        }

        if (t >= stops[last].Position)
        {
            return Math.Clamp(stops[last].Value, 0.0, 1.0);
        }

        var k = FindScalarSegment(stops, t);
        var span = stops[k + 1].Position - stops[k].Position;
        var u = span > 0.0 ? (t - stops[k].Position) / span : 0.0;

        var p0 = stops[k].Value;
        var p1 = stops[k + 1].Value;
        var m0 = k == 0 ? p1 - p0 : 0.5 * (stops[k + 1].Value - stops[k - 1].Value);
        var m1 = k + 1 == last
            ? stops[last].Value - stops[last - 1].Value
            : 0.5 * (stops[k + 2].Value - stops[k].Value);

        return Math.Clamp(Hermite(p0, p1, m0, m1, u), 0.0, 1.0);
    }

    /// <summary>
    /// lerp: <c>p0 + u*m0 + u^2*(3*(p1-p0) - 2*m0 - m1) + u^3*(2*(p0-p1) + m0 + m1)</c>.
    /// </summary>
    /// <param name="p0">Value at the start of the segment.</param>
    /// <param name="p1">Value at the end of the segment.</param>
    /// <param name="m0">Tangent at the start.</param>
    /// <param name="m1">Tangent at the end.</param>
    /// <param name="u">Position within the segment, 0 to 1.</param>
    /// <returns>The interpolated value, unclamped.</returns>
    public static double Hermite(double p0, double p1, double m0, double m1, double u)
    {
        var u2 = u * u;
        var u3 = u2 * u;
        return p0
            + (u * m0)
            + (u2 * ((3.0 * (p1 - p0)) - (2.0 * m0) - m1))
            + (u3 * ((2.0 * (p0 - p1)) + m0 + m1));
    }

    // Index of the stop starting the segment that contains t. The tables are
    // what it does and the fastest thing here.
    private static int FindColorSegment(ReadOnlySpan<ShellRampStop> stops, double t)
    {
        for (var i = stops.Length - 2; i > 0; i--)
        {
            if (t >= stops[i].Position)
            {
                return i;
            }
        }

        return 0;
    }

    private static int FindScalarSegment(ReadOnlySpan<ShellScalarStop> stops, double t)
    {
        for (var i = stops.Length - 2; i > 0; i--)
        {
            if (t >= stops[i].Position)
            {
                return i;
            }
        }

        return 0;
    }

    private static double Channel(ReadOnlySpan<ShellRampStop> stops, int k, double u, Component component)
    {
        var last = stops.Length - 1;
        var p0 = Select(stops[k], component);
        var p1 = Select(stops[k + 1], component);
        var m0 = k == 0 ? p1 - p0 : 0.5 * (p1 - Select(stops[k - 1], component));
        var m1 = k + 1 == last
            ? Select(stops[last], component) - Select(stops[last - 1], component)
            : 0.5 * (Select(stops[k + 2], component) - p0);

        return Hermite(p0, p1, m0, m1, u);
    }

    private static double Select(ShellRampStop stop, Component component)
    {
        return component switch
        {
            Component.Red => stop.R,
            Component.Green => stop.G,
            _ => stop.B,
        };
    }

    private enum Component
    {
        Red,
        Green,
        Blue,
    }
}
