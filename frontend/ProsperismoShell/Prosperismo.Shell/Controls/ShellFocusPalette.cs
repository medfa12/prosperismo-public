// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The console's focus highlight, recovered from the managed shell's
/// <c>Sce.PlayStation.PUI.UI3.FocusRenderManager</c> and
/// survived).
///
/// None of this is in the JavaScript. <c>FocusLayerPS</c> is a native component
/// and the bundle only declares that a thing is focusable; the highlight's
/// colour, thickness, noise and curves all live on the managed side. An earlier
/// version of our ring was a flat cyan chosen by eye, which is what made it read
/// as not-quite-PlayStation however right the geometry was.
///
/// What the console actually draws is two widgets, a line and an area, both
/// tinted by a seven stop table that runs cyan to blue to lavender to
/// periwinkle to pink to peach to rose. The <c>FilterColor</c> on each widget
/// is white, meaning no tint: the colour comes entirely from this table, which
/// is why sampling one value out of it and calling it "the focus colour" cannot
/// look right.
/// </summary>
public static class ShellFocusPalette
{
    /// <summary>
    /// Forces the shader's paper-white output branch. The compiled shader gates
    /// this conversion on its display-mode uniform; desktop SDR uses the direct
    /// colour table, while an HDR host can opt into the measured branch.
    /// </summary>
    public const string PaperWhiteOutputEnvironmentVariable =
        "PROSPERISMO_PS5_FOCUS_PAPER_WHITE";

    internal static bool UsePaperWhiteOutput =>
        string.Equals(
            Environment.GetEnvironmentVariable(PaperWhiteOutputEnvironmentVariable),
            "1",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            Environment.GetEnvironmentVariable(PaperWhiteOutputEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>FocusRenderManager.DefaultColorTable</c>, in order. Quoted as the
    /// source writes them, including the ones it leaves as fractions.
    /// </summary>
    public static IReadOnlyList<Color> ColorTable { get; } = new[]
    {
        FromUnit(0.8f, 1f, 1f),
        FromUnit(0.78039217f, 0.8901961f, 1f),
        FromUnit(0.8980392f, 0.8980392f, 1f),
        FromUnit(11f / 15f, 0.76862746f, 79f / 85f),
        FromUnit(47f / 51f, 0.78039217f, 0.8745098f),
        FromUnit(1f, 0.8745098f, 0.7490196f),
        FromUnit(1f, 0.8f, 0.8f),
    };

    // ---- FocusRenderManager geometry --------------------------------------

    /// <summary><c>DefaultLineThickness</c>.</summary>
    public const double LineThickness = 3.0;

    /// <summary><c>DefaultLineOffset</c>: how far the line sits outside the box.</summary>
    public const double LineOffset = 3.0;

    /// <summary><c>DefaultAreaEdgeFadeLength</c>.</summary>
    public const double AreaEdgeFadeLength = 5.0;

    /// <summary><c>DefaultAreaEdgeFadeOffset</c>.</summary>
    public const double AreaEdgeFadeOffset = 0.0;

    /// <summary><c>LineScaleRatioOnHiding</c>: the line swells as it goes.</summary>
    public const double LineScaleRatioOnHiding = 1.2;

    // ---- Opacity and shaping ----------------------------------------------

    /// <summary><c>LineMinOpacity</c>. The line never fully disappears.</summary>
    public const double LineMinOpacity = 0.065;

    /// <summary><c>AreaMinOpacity</c>. The wash does.</summary>
    public const double AreaMinOpacity = 0.0;

    /// <summary><c>LineAlphaGamma</c>.</summary>
    public const double LineAlphaGamma = 1.0;

    /// <summary><c>AreaAlphaGamma</c>: the wash is shaped, the line is not.</summary>
    public const double AreaAlphaGamma = 0.8;

    /// <summary><c>LineNoiseScale</c> and <c>AreaNoiseScale</c>, both 5.</summary>
    public const double NoiseScale = 5.0;

    /// <summary><c>FocusRenderWidget.EdgeFadeMinLength</c>.</summary>
    public const double EdgeFadeMinLength = 10.0;

    /// <summary><c>FocusRenderWidget.AreaRenderingThrethold</c>, the source's spelling.</summary>
    public const double AreaRenderingThreshold = 0.4;

    /// <summary>
    /// <c>AreaOpacityDecreaseRateBySize</c>. The wash fades as the focused
    /// target grows, so a large tile does not get a large slab of glow.
    /// </summary>
    public const double AreaOpacityDecreaseRateBySize = 30.0;

    /// <summary>
    /// <c>AreaOpacityMinimumDecreaseValueBySize</c>: the floor that decrease
    /// cannot push the wash below.
    /// </summary>
    public const double AreaOpacityMinimumDecreaseValueBySize = 0.5;

    // ---- Timing ------------------------------------------------------------

    /// <summary><c>MovingDuration</c>: a focus move.</summary>
    public const double MovingSeconds = 0.3;

    /// <summary><c>InMotionDuration</c>.</summary>
    public const double InSeconds = 0.3;

    /// <summary><c>OutMotionDuration</c>.</summary>
    public const double OutSeconds = 0.3;

    /// <summary><c>PressingDuration</c>.</summary>
    public const double PressingSeconds = 0.3;

    /// <summary><c>WarpAnimationDuration</c>.</summary>
    public const double WarpSeconds = 0.25;

    /// <summary><c>FocusRenderWidget.DefaultFadeInTime</c>: the ring appears at once.</summary>
    public const double FadeInSeconds = 0.0;

    /// <summary><c>FocusRenderWidget.DefaultFadeOutTime</c>.</summary>
    public const double FadeOutSeconds = 0.2;

    // ---- Idle motion -------------------------------------------------------

    /// <summary>
    /// <c>NoiseMoveFrequency</c>. The noise lookup does not scroll linearly: it
    /// orbits a unit circle at this rate in radians per second, so a stationary
    /// highlight breathes rather than drifting.
    /// </summary>
    public const double NoiseMoveFrequency = 0.25;

    /// <summary>One full noise revolution, <c>2*pi / 0.25</c> seconds.</summary>
    public const double NoisePeriodSeconds = 2.0 * Math.PI / NoiseMoveFrequency;

    /// <summary><c>ShimmerSpeed</c>.</summary>
    public const double ShimmerSpeed = 1.0;

    /// <summary>
    /// <c>ShimmerFrequency</c>. With speed 1 this is the pulse period in
    /// seconds: the sweep occupies the last two seconds of every five and the
    /// highlight is parked for the other three.
    /// </summary>
    public const double ShimmerFrequency = 5.0;

    /// <summary>
    /// Quantises a CPU-rendered focus surface on the native renderer's 60 Hz
    /// cadence. Geometry, noise and shimmer all consume the same frame clock.
    /// </summary>
    internal static int AnimationFrame(double seconds) =>
        (int)Math.Floor(seconds * 60.0);

    /// <summary>
    /// The noise offset at time <paramref name="seconds"/>:
    /// <c>(sin(0.25 t), cos(0.25 t))</c>. Both the line and the area use it.
    /// </summary>
    public static (double X, double Y) NoiseOffset(double seconds)
    {
        double a = seconds * NoiseMoveFrequency;
        return (Math.Sin(a), Math.Cos(a));
    }

    /// <summary>
    /// The shimmer pair at time <paramref name="seconds"/>. Each channel is
    /// <c>cos(pi * max((t mod 5) - 4, -1))</c>, the second evaluated half a
    /// second ahead, so both sit at -1 for three seconds and then sweep -1 to
    /// +1 to -1 across the remaining two.
    ///
    /// Area only. The line never shimmers - it has the noise orbit and its tone
    /// curve and nothing else.
    /// </summary>
    public static (double X, double Y) Shimmer(double seconds)
    {
        return (Channel(seconds), Channel(seconds + 0.5));

        static double Channel(double t)
        {
            double phase = (t * ShimmerSpeed) % ShimmerFrequency;
            double v = Math.Max(phase - ShimmerFrequency + 1.0, -1.0);
            return Math.Cos(v * Math.PI);
        }
    }

    /// <summary>
    /// The shimmer's 0..1 envelope, for a renderer that wants a brightness
    /// pulse rather than the raw pair. Zero while parked.
    /// </summary>
    public static double ShimmerEnvelope(double seconds) =>
        (Shimmer(seconds).X + 1.0) * 0.5;

    // ---- How the gradient is indexed ---------------------------------------

    /// <summary>
    /// The exponent the line applies to the noise before using it as the
    /// colour-table coordinate: <c>pow(saturate(noise), 1.5)</c>.
    /// </summary>
    public const double LineNoiseExponent = 1.5;

    /// <summary>
    /// The colour-table coordinate for the <b>line</b>, from its disassembled
    /// pixel shader.
    ///
    /// The ring's geometry contributes nothing to hue - only to alpha. The
    /// outline's iridescence is driven purely by the noise field, raised to
    /// 1.5, and that value alone indexes the seven stops.
    ///
    /// This corrects an earlier implementation that indexed the gradient by
    /// position along the box, which produced a fixed rainbow sweep rather than
    /// the console's drifting one.
    /// </summary>
    public static double LineTableCoordinate(double noise) =>
        Math.Pow(Math.Clamp(noise, 0.0, 1.0), LineNoiseExponent);

    /// <summary>
    /// (0,0), (.2,.25), (.9,.9), (1,1). The coefficients are the exact
    /// natural-cubic spline produced by ToneCurveParam.CalculateCurveParams.
    /// The shader evaluates a segment using x relative to that segment's
    /// lower midpoint.
    /// </summary>
    public static double LineToneCurve(double value)
    {
        double x = Math.Clamp(value, 0.0, 1.0);
        if (x <= 0.2)
        {
            return Cubic(x,
                0.0,
                0.06742977958332985,
                10.114466937499461,
                -21.008079177080553);
        }

        if (x <= 0.9)
        {
            return Cubic(x - 0.2,
                0.25,
                1.592247053333448,
                -2.490380568748873,
                2.2032464762493706);
        }

        return Cubic(x - 0.9,
            0.9,
            1.3444865771716008,
            2.1364370313748053,
            -55.81302803090816);

        static double Cubic(double t, double a, double b, double c, double d) =>
            Math.Clamp(a + (t * (b + (t * (c + (t * d))))), 0.0, 1.0);
    }

    /// <summary>
    /// The colour-table coordinate for the <b>area</b>, which is its own
    /// composite intensity - the same scalar that becomes the pixel's alpha.
    /// Brighter parts of the glow land further along the gradient.
    /// </summary>
    public static double AreaTableCoordinate(double intensity) =>
        Math.Clamp(intensity, 0.0, 1.0);

    /// <summary>
    /// The colour a highlight of the given intensity should actually be.
    /// </summary>
    /// <remarks>
    /// The AreaFocus disassembly feeds the computed intensity directly to the
    /// colour-table sample at 0x0374. There is no reversal or hand-shaped curve
    /// between those operations, so this method intentionally adds neither.
    /// </remarks>
    public static Color AreaColorFor(double intensity) =>
        ConvertForActiveOutput(Sample(AreaTableCoordinate(intensity)));

    /// <summary>The line table sample after the same shader output transform.</summary>
    public static Color LineColorFor(double coordinate) =>
        ConvertForActiveOutput(Sample(coordinate));

    /// <summary>
    /// The noise lookup coordinate, in the shader's own form:
    /// <c>(St / scale + change) * 0.5 + 0.5</c>.
    ///
    /// Two details that are easy to get backwards. <c>u_NoiseScale</c> is a
    /// <b>divisor</b> - the shader takes its reciprocal - so a larger scale
    /// means larger, slower features rather than tighter ones. And
    /// <c>u_NoiseChangeParam</c> is <b>added</b> before the remap, so the
    /// orbit is a scroll offset; it never multiplies anything and never
    /// touches the distance field.
    /// </summary>
    public static (double U, double V) NoiseUv(double stX, double stY, double seconds)
    {
        var (cx, cy) = NoiseOffset(seconds);
        double u = ((stX / NoiseScale) + cx) * 0.5 + 0.5;
        double v = ((stY / NoiseScale) + cy) * 0.5 + 0.5;
        return (u, v);
    }

    /// <summary>
    /// The area's shimmer term: the two channels are the two <b>ends of a
    /// diagonal ramp</b> across the highlight, and the result is halved.
    /// That is why there are two of them and why one leads the other by half a
    /// second - together they sweep a band along the quad's anti-diagonal.
    /// </summary>
    public static double ShimmerAcross(double seconds, double diagonal)
    {
        var (a, b) = Shimmer(seconds);
        double t = Math.Clamp(diagonal, 0.0, 1.0);
        return (a + ((b - a) * t)) * 0.5;
    }

    /// <summary>
    /// The diagonal ramp the shimmer is interpolated along:
    /// <c>0.5 + 0.25*(St.y - St.x)</c>, running 0 to 1 across the quad's
    /// anti-diagonal.
    /// </summary>
    public static double DiagonalRamp(double stX, double stY) =>
        Math.Clamp(0.5 + (0.25 * (stY - stX)), 0.0, 1.0);

    /// <summary>
    /// What a press does to the area's intensity: pulls it toward this
    /// constant rather than scaling it.
    /// </summary>
    public const double PressingIntensity = 0.15;

    // ---- Curves ------------------------------------------------------------

    /// <summary>
    /// <c>InOutAnimationCurve</c> and <c>PressingAnimationCurve</c>, which the
    /// source defines identically:
    /// <c>t &gt; 0 ? 1 - (1 - t * 0.5)^10 : 0</c>.
    /// </summary>
    public static double InOutCurve(double t) =>
        t > 0.0 ? 1.0 - Math.Pow(1.0 - (t * 0.5), 10.0) : 0.0;

    /// <summary>
    /// <c>MovingAnimationCurve</c>: the same shape at power 5, so a move eases
    /// out less sharply than an appearance.
    /// </summary>
    public static double MovingCurve(double t) =>
        t > 0.0 ? 1.0 - Math.Pow(1.0 - (t * 0.5), 5.0) : 0.0;

    /// <summary>
    /// The table as a brush across <paramref name="rect"/>. The stops are
    /// spread evenly, which is what makes the ring read as one iridescent
    /// sweep rather than seven bands.
    /// </summary>
    public static IBrush Brush(Rect rect, double opacity = 1.0)
    {
        var stops = new GradientStops();
        for (int i = 0; i < ColorTable.Count; i++)
        {
            var c = ColorTable[i];
            byte a = (byte)Math.Clamp(Math.Round(opacity * 255.0), 0.0, 255.0);
            stops.Add(new GradientStop(
                Color.FromArgb(a, c.R, c.G, c.B),
                ColorTable.Count == 1 ? 0.0 : (double)i / (ColorTable.Count - 1)));
        }

        // Swept along the box's diagonal so a wide tile and a tall one both
        // show the whole table rather than a slice of it.
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }

    /// <summary>One stop of the table, for callers that need a single colour.</summary>
    public static Color At(int index) =>
        ColorTable[Math.Clamp(index, 0, ColorTable.Count - 1)];

    /// <summary>
    /// The table sampled at <paramref name="t"/> in 0..1, linearly between
    /// stops. This is what makes the ring one sweep rather than seven bands.
    /// </summary>
    public static Color Sample(double t)
    {
        int n = ColorTable.Count;
        if (n == 1)
        {
            return ColorTable[0];
        }

        double x = Math.Clamp(t, 0.0, 1.0) * (n - 1);
        int i = (int)Math.Floor(x);
        if (i >= n - 1)
        {
            return ColorTable[n - 1];
        }

        double f = x - i;
        var a = ColorTable[i];
        var b = ColorTable[i + 1];
        return Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * f)),
            (byte)Math.Round(a.G + ((b.G - a.G) * f)),
            (byte)Math.Round(a.B + ((b.B - a.B) * f)));
    }

    /// <summary>
    /// The source's model: a widget's <c>FilterColor</c> multiplies the table
    /// rather than replacing it, and ships as white, meaning no tint. A caller
    /// that wants a tinted ring passes a colour here; white leaves the table
    /// exactly as the console draws it.
    /// </summary>
    public static Color Filter(Color tableColor, Color filter) => Color.FromRgb(
        (byte)(tableColor.R * filter.R / 255),
        (byte)(tableColor.G * filter.G / 255),
        (byte)(tableColor.B * filter.B / 255));

    private static Color ConvertForActiveOutput(Color color)
    {
        if (!UsePaperWhiteOutput)
        {
            return color;
        }

        var (r, g, b) = Ps5FocusField.ConvertFocusOutput(
            color.R / 255.0,
            color.G / 255.0,
            color.B / 255.0);
        return Color.FromRgb(
            (byte)Math.Clamp(Math.Round(r * 255.0), 0.0, 255.0),
            (byte)Math.Clamp(Math.Round(g * 255.0), 0.0, 255.0),
            (byte)Math.Clamp(Math.Round(b * 255.0), 0.0, 255.0));
    }

    /// <summary>
    /// The table's mean. Useful only where a gradient cannot be used; it is not
    /// "the focus colour" and the console never draws it.
    /// </summary>
    public static Color Average()
    {
        double r = 0, g = 0, b = 0;
        foreach (var c in ColorTable)
        {
            r += c.R;
            g += c.G;
            b += c.B;
        }

        int n = ColorTable.Count;
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Color FromUnit(float r, float g, float b) => Color.FromRgb(
        (byte)Math.Clamp(Math.Round(r * 255.0), 0.0, 255.0),
        (byte)Math.Clamp(Math.Round(g * 255.0), 0.0, 255.0),
        (byte)Math.Clamp(Math.Round(b * 255.0), 0.0, 255.0));
}
