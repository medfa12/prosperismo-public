// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Shell;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// The colours the boot sequence draws with, taken out of the console's own
/// tables where the tables hold up and off the reference where they do not.
///
/// Two tables in <c>system_ex/app/NPXS40087/eboot.bin</c> hold every colour the
/// background layer draws: 21 wave presets at vaddr <c>0xbd1fd0</c> and 37 plate
/// records at <c>0xbd0ed0</c>. Every one of them is expanded into a lookup
/// texture by a cubic Hermite (Catmull-Rom) spline, never a lerp, which is what
/// <see cref="ShellColorRamp"/> implements and why this file borrows it rather
/// than baking its own.
///
/// What survived a measurement against the 3.00 recovery movie and what did not:
///
/// <list type="bullet">
///   <item><description><b>Wave preset 2, ramp0 of set 0</b> is phase 1's blue and
///     matches. Used as authored.</description></item>
///   <item><description><b>Wave preset 9, ramp0 of set 0</b> is the colorchange's
///     gold and matches. Its stops are a saturated yellow while the movie's motes
///     read warm white, and those reconcile: a mote is additive and the tonemap
///     desaturates the top end. Used as authored.</description></item>
///   <item><description><b>Plate record 21</b> does not match. Its stops carry
///     green at 0.28 of blue; the movie's plate carries it at 0.041. At the bloom
///     the movie's plate row reads 8-bit (2, 3, 41) where record 21 driven to the
///     same blue would read (5, 21, 41).</description></item>
///   <item><description><b>Plate record 9</b> does not match either. It is a
///     yellow at hue 48 with no blue in any stop or in its light; the movie's warm
///     plate is a copper at hue 21 whose linear channel ratio is 1 : 0.57 : 0.39.
///     Solving for record 9 plus any neutral fill drives the plate term negative.
///     Its luminance run down the frame is consistent, so it is kept for that and
///     the colour comes from the haze.</description></item>
/// </list>
///
/// Record 21's <b>light</b> colour does hold up, and it explains the teal: at the
/// colorchange the whole plate flashes hue 203 for about 140 ms, and #5BAECE over
/// that record's navy lands at hue 202.
/// </summary>
public static class BootIntroPalette
{
    /// <summary>
    /// The stop positions almost every wave preset shares. Only the first and last
    /// four between them are spaced evenly across that span, which the recorded
    /// ones are consistent with.
    /// </summary>
    private static readonly double[] WavePositions =
    {
        0.0, 0.146718, 0.265600, 0.317100, 0.382400, 0.753800, 0.872045, 1.0,
    };

    /// <summary>Wave preset 2, "blue, brighter": phase 1's motes.</summary>
    private static readonly uint[] BlueHex =
    {
        0x0001FF, 0x002DFF, 0x0077E5, 0x0051FF, 0x0073FF, 0x0020FF, 0x0AA0FF, 0x0009FF,
    };

    /// <summary>Wave preset 9, "gold": the colorchange's motes.</summary>
    private static readonly uint[] GoldHex =
    {
        0x807800, 0x807D00, 0xF2F500, 0x938500, 0xC39900, 0xCBA100, 0x665000, 0x946B00,
    };

    /// <summary>
    /// The dispersion the knot is split through: one sweep of the spectrum, eight
    /// builder does not change.
    ///
    /// This is the one invented ramp in the sequence. It is never the frame's
    /// colour - it only tints motes that are inside the bright knot, and only
    /// while <see cref="BootIntroFrame.Rainbow"/> is up, so what it produces is
    /// gold with a spectrum visible inside the light rather than a repaint.
    /// </summary>
    private static readonly ShellRampStop[] SpectrumStops =
    {
        new(1.00, 0.16, 0.10, 0.000),
        new(1.00, 0.46, 0.06, 0.145),
        new(1.00, 0.88, 0.18, 0.290),
        new(0.34, 1.00, 0.38, 0.430),
        new(0.10, 0.95, 0.92, 0.570),
        new(0.16, 0.50, 1.00, 0.715),
        new(0.56, 0.22, 1.00, 0.860),
        new(1.00, 0.18, 0.70, 1.000),
    };

    /// <summary>
    /// The blue plate, from the movie's own per-row median, normalised to its
    /// brightest row. Nearly pure blue: this is the plate record 21 is not.
    /// </summary>
    private static readonly ShellRampStop[] BluePlateStops =
    {
        new(0.000, 0.000, 0.285, 0.00),
        new(0.027, 0.041, 1.000, 0.36),
        new(0.027, 0.027, 0.402, 1.00),
    };

    /// <summary>Plate record 9's gradient, as authored. These records are stored linear.</summary>
    private static readonly ShellRampStop[] GoldPlateStops =
    {
        new(0.8314, 0.6588, 0.0, 0.00),
        new(0.7216, 0.5647, 0.0, 0.36),
        new(0.6078, 0.4588, 0.0, 1.00),
    };

    /// <summary>Plate record 21's light colour, #5BAECE: the teal the colorchange flashes.</summary>
    public static readonly (double R, double G, double B) TealLight = (0.355, 0.682, 0.807);

    /// <summary>Plate record 9's light colour, #D46900.</summary>
    public static readonly (double R, double G, double B) GoldLight = (0.8314, 0.4118, 0.0);

    /// <summary>
    /// The room the warm phase is lit in. Record 9 has no blue anywhere and the
    /// movie's warm frames plainly do; this is where that comes from, and it is
    /// also why the reference's whole frame comes up at the colorchange rather
    /// than only its gradient. Measured: linear 1 : 0.55 : 0.445.
    /// </summary>
    public static readonly (double R, double G, double B) WarmHaze = (1.00, 0.55, 0.445);

    /// <summary>The shaft's warm tint, measured off the reference's top-left patch: hue 20, saturation 0.13.</summary>
    public static readonly (double R, double G, double B) ShaftWarm = (1.00, 0.80, 0.62);

    private static ShellColorRamp? s_blue;
    private static ShellColorRamp? s_gold;
    private static ShellColorRamp? s_spectrum;
    private static ShellColorRamp? s_bluePlate;
    private static ShellColorRamp? s_goldPlate;

    /// <summary>Phase 1's mote ramp, baked once.</summary>
    public static ShellColorRamp Blue =>
        s_blue ??= new ShellColorRamp(FromHex(BlueHex), ShellColorRamp.WaveResolution);

    /// <summary>The colorchange's mote ramp, baked once.</summary>
    public static ShellColorRamp Gold =>
        s_gold ??= new ShellColorRamp(FromHex(GoldHex), ShellColorRamp.WaveResolution);

    /// <summary>The dispersion inside the knot, baked once.</summary>
    public static ShellColorRamp Spectrum =>
        s_spectrum ??= new ShellColorRamp(SpectrumStops, ShellColorRamp.WaveResolution);

    /// <summary>The blue plate's vertical gradient, baked once.</summary>
    public static ShellColorRamp BluePlate => s_bluePlate ??= new ShellColorRamp(BluePlateStops);

    /// <summary>The gold plate's vertical gradient, baked once.</summary>
    public static ShellColorRamp GoldPlate => s_goldPlate ??= new ShellColorRamp(GoldPlateStops);

    /// <summary>
    /// A mote's colour: the phase's own ramp, then as much of the dispersion as the
    /// frame's rainbow term and the mote's place in the knot allow.
    /// </summary>
    /// <param name="frame">The frame's gains.</param>
    /// <param name="depth">Where the mote sits on its ramp, 0 to 1.</param>
    /// <param name="spectrum">Where the mote sits on the dispersion, 0 to 1.</param>
    /// <param name="insideKnot">How far inside the bright knot the mote is, 0 to 1.</param>
    /// <param name="r">Linear red out.</param>
    /// <param name="g">Linear green out.</param>
    /// <param name="b">Linear blue out.</param>
    public static void SampleMote(
        in BootIntroFrame frame,
        double depth,
        double spectrum,
        double insideKnot,
        out double r,
        out double g,
        out double b)
    {
        Blue.Sample(depth, out var blueR, out var blueG, out var blueB);

        double baseR = blueR, baseG = blueG, baseB = blueB;
        if (frame.GoldMix > 0.0)
        {
            Gold.Sample(depth, out var goldR, out var goldG, out var goldB);
            baseR += (goldR - baseR) * frame.GoldMix;
            baseG += (goldG - baseG) * frame.GoldMix;
            baseB += (goldB - baseB) * frame.GoldMix;
        }

        // The split only happens where the light is: a mote out at the edge of the
        // frame is lit by nothing and stays the phase's own colour. This is the
        // whole difference between a prism inside the blob and a repainted frame.
        var split = frame.Rainbow * insideKnot;
        if (split <= 0.004)
        {
            r = baseR;
            g = baseG;
            b = baseB;
            return;
        }

        Spectrum.Sample(spectrum, out var specR, out var specG, out var specB);
        r = baseR + ((specR - baseR) * split);
        g = baseG + ((specG - baseG) * split);
        b = baseB + ((specB - baseB) * split);
    }

    // The recovered ramp tables are quoted as 8-bit renderings of the stored
    // floats, which is how the dumps print them; this is that rendering read back,
    // not a colour-space conversion.
    private static ShellRampStop[] FromHex(uint[] hex)
    {
        var stops = new ShellRampStop[hex.Length];
        for (var i = 0; i < hex.Length; i++)
        {
            stops[i] = new ShellRampStop(
                ((hex[i] >> 16) & 0xFF) / 255.0,
                ((hex[i] >> 8) & 0xFF) / 255.0,
                (hex[i] & 0xFF) / 255.0,
                WavePositions[i]);
        }

        return stops;
    }
}
