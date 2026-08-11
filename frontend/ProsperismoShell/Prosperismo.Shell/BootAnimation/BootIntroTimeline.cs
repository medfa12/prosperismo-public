// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Shell;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// Everything one frame of the boot sequence needs to know about where it is in
/// the run: the gains for each layer, which palette the motes are on, and how far
/// the dispersion inside the knot has opened.
/// </summary>
public readonly struct BootIntroFrame
{
    public Ps5ColdBootParticleFrame ParticleResources { get; init; }

    /// <summary>Position in the run, 0 to 1.</summary>
    public double Progress { get; init; }

    /// <summary>Phase 1's master energy. Drives the blue plate and the core.</summary>
    public double BlueEnergy { get; init; }

    /// <summary>Gain on the blue plate.</summary>
    public double PlateBlue { get; init; }

    /// <summary>Gain on the teal flash: plate record 21's own light, blown out.</summary>
    public double PlateTeal { get; init; }

    /// <summary>Gain on the gold plate, record 9.</summary>
    public double PlateGold { get; init; }

    /// <summary>Gain on the warm haze that fills the room after the colorchange.</summary>
    public double Haze { get; init; }

    /// <summary>Gain on the shaft from off the top-left corner.</summary>
    public double Shaft { get; init; }

    /// <summary>0 draws the shaft white, 1 draws it warm. The console cools it as it fades.</summary>
    public double ShaftWarmth { get; init; }

    /// <summary>The colorchange's master gain, the reference's own luminance sweep.</summary>
    public double Warm { get; init; }

    /// <summary>Blue to gold on the motes, 0 to 1.</summary>
    public double GoldMix { get; init; }

    /// <summary>
    /// How far the spectrum has opened inside the bright knot. Peaks at the
    /// colorchange and settles to a trace, so the gold arrives split and then
    /// closes back up.
    /// </summary>
    public double Rainbow { get; init; }

    /// <summary>Gain on every mote.</summary>
    public double Particles { get; init; }

    /// <summary>Gain on the knot's own light.</summary>
    public double CoreEnergy { get; init; }

    /// <summary>How far the warm phase has slid toward neutral, 0 to 1.</summary>
    public double Desaturate { get; init; }

    /// <summary>True once the run is over.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>
/// The cold-boot sequence's clock and its per-frame gains, with no renderer and no
/// Avalonia attached so every beat can be asserted without a window.
///
/// The sequence identity and total duration are the console's.
/// <c>BGLayerNative::BeginBootupSequenceNative</c>
/// in <c>system_ex/app/NPXS40087/eboot.bin</c> runs three particle patterns and
/// then ends the welcome animation:
///
/// <list type="number">
///   <item><description><c>coldboot</c>, at <c>0xbb4dc4</c>.</description></item>
///   <item><description><c>coldboot/colorchange/colorchange</c> with
///     <c>spread_expanded</c>.</description></item>
///   <item><description>the same colorchange with <c>spread_expanded_fadeout</c>,
///     then the welcome-animation end.</description></item>
/// </list>
///
/// <c>BackgroundLayer.ColdBootDurationTick</c> is 60,000,000 at 100 ns a tick, so
/// the whole thing is 6000 ms, and that tick is the same unit as a .NET tick,
/// which is why <see cref="TotalDuration"/> is literally
/// <c>TimeSpan.FromTicks</c> of it.
///
/// recovery movie - a sweep of mean luminance and mean red-minus-blue at 480x270 -
/// expressed as fractions of that movie's 13.313 s and scaled onto the 6000 ms.
/// The movie is a reference and never an asset: nothing here reads it, and the
/// emulator ships no video at all.
/// </summary>
public static class BootIntroTimeline
{
    /// <summary><c>BackgroundLayer.ColdBootDurationTick</c>, at the shell's 100 ns tick.</summary>
    public const long ColdBootDurationTicks = 60_000_000;

    // ---- Measured beats, as fractions of the run ----

    /// <summary>The black hold ends and the first seed of light appears.</summary>
    public const double SeedAt = 0.045;

    /// <summary>The bloom is at its brightest.</summary>
    public const double BloomPeakAt = 0.094;

    /// <summary>The rendezvous line has grown across the frame.</summary>
    public const double BandAt = 0.150;

    /// <summary>The colorchange step.</summary>
    public const double ColorChangeAt = 0.630;

    /// <summary>How long the colorchange takes, as a fraction of the run: a 146 ms snap.</summary>
    public const double FlashSpan = 0.02438;

    /// <summary>The plate leaves black, into the teal.</summary>
    public const double TealInAt = 0.63160;

    /// <summary>The teal is at its fullest.</summary>
    public const double TealPeakAt = 0.64290;

    /// <summary>The gold plate has landed and the teal is gone.</summary>
    public const double WarmInAt = 0.65540;

    /// <summary>The run to black begins: <c>spread_expanded_fadeout</c>.</summary>
    public const double FadeoutAt = 0.89650;

    /// <summary>Fully black.</summary>
    public const double BlackAt = 0.972;

    /// <summary>
    /// The reference's peak mean luminance, which <see cref="WarmLevels"/> is
    /// quoted as fractions of.
    /// </summary>
    public const double ReferencePeakLuminance = 0.4244;

    /// <summary>
    /// The colorchange's brightness, sampled every 18 frames off the reference and
    /// expressed as fractions of its own peak. This is the master gain for
    /// everything the warm phase lights, so the exposure is measured rather than
    /// chosen. Splined, like every other ramp in this layer.
    /// </summary>
    public static readonly ShellScalarStop[] WarmLevels =
    {
        new(0.000, 0.00000), new(0.000, ColorChangeAt), new(0.141, TealInAt),
        new(0.671, TealPeakAt), new(1.000, WarmInAt), new(0.926, 0.67670),
        new(0.908, 0.69930), new(0.762, 0.72190), new(0.618, 0.74440),
        new(0.597, 0.76690), new(0.571, 0.81200), new(0.501, 0.85710),
        new(0.385, 0.90220), new(0.259, 0.92480), new(0.109, 0.94740),
        new(0.010, 0.97000), new(0.000, 0.98120), new(0.000, 1.00000),
    };

    // ---- Gains ----
    //
    // One measured curve drives the warm phase; these turn it into the four
    // things the colorchange lights. Named rather than inlined because they are
    // what a capture run tunes.

    /// <summary>
    /// Peak of the blue plate. Solved rather than chosen: at the bloom the
    /// reference's plate row reads 8-bit blue 41, and the compositor's
    /// <c>x / (1 + x)</c> roll-off reaches 41/255 at x = 0.192 against a ramp
    /// normalised to 1.
    /// </summary>
    public const double BluePlateGain = 0.192;

    /// <summary>The plate's resting level before the seed. The reference never opens on true black.</summary>
    public const double BluePlateFloor = 0.010;

    /// <summary>
    /// Peak of the teal flash. The reference's fullest teal row reads
    /// (0.118, 0.255, 0.369), and record 21's light at #5BAECE under the glow lobe
    /// reaches that at 1.05.
    /// </summary>
    public const double TealGain = 1.05;

    /// <summary>
    /// Peak of the gold plate. Small, and it is the residual: solving the
    /// reference's warm row for the haze first leaves record 9 accounting for
    /// under a tenth of the frame.
    /// </summary>
    public const double GoldPlateGain = 0.087;

    /// <summary>
    /// Peak of the warm haze, which carries most of the warm phase's colour. The
    /// reference's warm row needs x = 0.536 in blue and record 9 has none, so the
    /// haze has to supply all of it.
    /// </summary>
    public const double HazeGain = 1.50;

    /// <summary>Peak of the shaft.</summary>
    public const double ShaftGain = 0.75;

    /// <summary>
    /// Peak mote gain across the colorchange, relative to phase 1. Large, and it
    /// has to be: the warm plate sits at x = 1.5 into a <c>x / (1 + x)</c>
    /// roll-off, so a mote that only adds a few tenths moves the pixel by five per
    /// cent and disappears. The reference's gold ribbons are plainly legible over
    /// its bright plate, which means they are carrying several times the plate's
    /// own energy where they land.
    /// </summary>
    public const double WarmMoteGain = 8.0;

    /// <summary>How far the warm phase slides toward neutral: 0.35 of mean saturation down to 0.13.</summary>
    public const double DesaturateDepth = 0.62;

    /// <summary>How long that slide takes, as a fraction of the run.</summary>
    public const double DesaturateSpan = 0.10;

    /// <summary>
    /// How far the plate settles after the bloom. Deeper than the field's own
    /// settle: in the reference the plate's blue falls by a factor of 4.3 in
    /// linear light between the bloom and the plateau while the frame's mean
    /// luminance moves by only 1.28, because at the bloom the knot is compact and
    /// it is the plate glow that fills the frame.
    /// </summary>
    private const double PlateSettleDepth = 0.767;

    /// <summary>How far the field settles after the bloom.</summary>
    private const double FieldSettleDepth = 0.32;

    /// <summary>How long the settle takes, as a fraction of the run.</summary>
    private const double SettleSpan = 0.158;

    /// <summary>How long the dispersion inside the knot takes to close back up.</summary>
    private const double RainbowSpan = 0.20;

    /// <summary>The trace of dispersion the knot keeps once the flare has closed.</summary>
    private const double RainbowFloor = 0.30;

    /// <summary>The whole sequence: 6000 ms.</summary>
    public static TimeSpan TotalDuration => TimeSpan.FromTicks(ColdBootDurationTicks);

    /// <summary>
    /// The gains at a point in the run.
    /// </summary>
    /// <param name="elapsed">Time since the sequence started.</param>
    /// <returns>Every scalar the renderer needs for that frame.</returns>
    public static BootIntroFrame Sample(TimeSpan elapsed)
    {
        var progress = Math.Clamp(elapsed.Ticks / (double)ColdBootDurationTicks, 0.0, 1.0);
        return SampleAt(progress, elapsed.Ticks >= ColdBootDurationTicks);
    }

    /// <summary>
    /// The gains at a fraction of the run. Split out so a test can walk the
    /// timeline without building <see cref="TimeSpan"/>s.
    /// </summary>
    /// <param name="progress">Position in the run, 0 to 1.</param>
    /// <param name="complete">Whether the run has finished.</param>
    /// <returns>Every scalar the renderer needs for that frame.</returns>
    public static BootIntroFrame SampleAt(double progress, bool complete = false)
    {
        progress = Math.Clamp(progress, 0.0, 1.0);

        var seed = SmoothStep(SeedAt * 0.55, SeedAt * 1.6, progress);
        var bloom = SmoothStep(SeedAt * 1.2, BloomPeakAt, progress);
        var settle = SmoothStep(BloomPeakAt, BloomPeakAt + SettleSpan, progress);

        var goldMix = SmoothStep(ColorChangeAt, ColorChangeAt + FlashSpan, progress);

        var blueEnergy = seed * (0.10 + (0.90 * bloom)) * (1.0 - (FieldSettleDepth * settle)) * (1.0 - goldMix);
        var plateEnergy = seed * (0.10 + (0.90 * bloom)) * (1.0 - (PlateSettleDepth * settle)) * (1.0 - goldMix);
        var fade = 1.0 - SmoothStep(FadeoutAt, BlackAt, progress);

        // The measured luminance sweep is an output, and the renderer works in
        // linear light behind a tonemap, so it is run back through that transfer
        // before it is used as a gain. Nothing is fitted: it is the analytic
        // inverse of what the compositor's pack step applies.
        var level = ShellColorRamp.EvaluateScalar(WarmLevels, progress);
        var warm = WarmGain(level);

        // The colorchange is not blue to gold. For about 140 ms the whole plate
        // flashes teal first, and that teal is plate record 21's own light colour
        // (#5BAECE, hue 197) blown out: the step overdrives the light term of the
        // plate that is already there and swaps the record underneath it a tenth
        // of a second later.
        var teal = SmoothStep(TealInAt - 0.0067, TealPeakAt, progress)
                   * (1.0 - SmoothStep(TealPeakAt, WarmInAt, progress));
        var gold = SmoothStep(TealPeakAt - 0.005, WarmInAt, progress);
        var desaturate = DesaturateDepth * SmoothStep(WarmInAt, WarmInAt + DesaturateSpan, progress);

        // The dispersion inside the knot. It flares with the colorchange, which is
        // when the white core is brightest and a prism has the most light to
        // split, and closes to a trace over the next fifth of the run.
        var rainbow = goldMix * (RainbowFloor + ((1.0 - RainbowFloor)
            * (1.0 - SmoothStep(ColorChangeAt + FlashSpan, ColorChangeAt + RainbowSpan, progress))));

        return new BootIntroFrame
        {
            ParticleResources = Ps5ColdBootParticleTimeline.SampleAtProgress(progress),
            Progress = progress,
            BlueEnergy = blueEnergy,
            PlateBlue = (1.0 - goldMix) * (BluePlateFloor + (BluePlateGain * plateEnergy)),
            PlateTeal = TealGain * warm * teal,
            PlateGold = GoldPlateGain * warm * gold * (1.0 - desaturate),
            Haze = HazeGain * warm * gold,
            Shaft = (0.05 * blueEnergy) + (ShaftGain * warm * gold),
            ShaftWarmth = 1.0 - desaturate,
            Warm = warm,
            GoldMix = goldMix,
            Rainbow = rainbow,
            Particles = seed * (0.15 + (0.85 * bloom)) * (1.0 - (0.30 * settle))
                        * (((1.0 - goldMix) * fade) + (WarmMoteGain * goldMix * warm)),
            CoreEnergy = blueEnergy + (1.10 * goldMix * warm),
            Desaturate = desaturate,
            IsComplete = complete || progress >= 1.0,
        };
    }

    /// <summary>
    /// Turns a target mean luminance, as a fraction of the reference's peak, into
    /// the gain that produces it. The analytic inverse of the compositor's
    /// <c>x / (1 + x)</c> roll-off, normalised so the peak comes out at 1.
    /// </summary>
    /// <param name="level">Target luminance, as a fraction of the reference's peak.</param>
    /// <returns>Gain, 0 to 1.</returns>
    public static double WarmGain(double level)
    {
        return Invert(Math.Clamp(level, 0.0, 1.0)) / Invert(1.0);

        // y = x / (1 + x)  =>  x = y / (1 - y)
        static double Invert(double value)
        {
            var y = value * ReferencePeakLuminance;
            return y / Math.Max(1.0 - y, 1e-6);
        }
    }

    /// <summary>The Hermite step every gain in this layer eases with.</summary>
    /// <param name="from">Where the step begins.</param>
    /// <param name="to">Where it ends.</param>
    /// <param name="at">The point to evaluate.</param>
    /// <returns>0 to 1.</returns>
    public static double SmoothStep(double from, double to, double at)
    {
        if (to <= from)
        {
            return at >= to ? 1.0 : 0.0;
        }

        var t = Math.Clamp((at - from) / (to - from), 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }
}
