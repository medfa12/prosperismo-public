// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// One mote of <see cref="BootIntroField"/>. Positions are normalised, 0 to 1
/// across the frame in both axes, so a resize rescales the field instead of
/// restarting it.
/// </summary>
public struct BootIntroMote
{
    /// <summary>Normalised X, 0 at the left edge.</summary>
    public double X;

    /// <summary>Normalised Y, 0 at the top edge.</summary>
    public double Y;

    /// <summary>Normalised X velocity, per second.</summary>
    public double VelocityX;

    /// <summary>Normalised Y velocity, per second.</summary>
    public double VelocityY;

    /// <summary>Depth, 0 far to 1 near. Near motes are larger, brighter and lean further.</summary>
    public double Depth;

    /// <summary>Per-mote phase offset, so two motes at one place do not move as one.</summary>
    public double Seed;

    /// <summary>Where the mote sits on its phase's colour ramp, 0 to 1.</summary>
    public double Tone;

    /// <summary>Static offset into the dispersion, so the fan is banded rather than smooth.</summary>
    public double SpectrumBias;

    /// <summary>
    /// When this mote goes out during the fadeout, as a fraction of the run to
    /// black. Staggered, so <c>spread_expanded_fadeout</c> thins the field instead
    /// of blinking it out at once: the shader calls this
    /// <c>timeRateForLifeCountDown</c>.
    /// </summary>
    public double DeathBias;

    /// <summary>True when this mote migrates onto the rendezvous line rather than staying in the knot.</summary>
    public bool OnLine;

    /// <summary>Which rosette point of the <c>coldboot</c> set this mote answers to.</summary>
    public byte KnotSlot;

    /// <summary>Which point of the line this mote answers to.</summary>
    public byte LineSlot;

    /// <summary>Which point of the <c>spread_expanded</c> shell this mote answers to.</summary>
    public byte SpreadSlot;

    /// <summary>Jitter around the mote's rendezvous point, so an attractor is a cloud and not a dot.</summary>
    public float JitterX;

    /// <summary>Jitter around the mote's rendezvous point in Y.</summary>
    public float JitterY;

    /// <summary>Alive-ness, 0 to 1. Set by <see cref="BootIntroField.Advance"/>.</summary>
    public double Envelope;

    /// <summary>How far inside the bright knot the mote is, 0 to 1. Set by <see cref="BootIntroField.Advance"/>.</summary>
    public double InsideKnot;

    /// <summary>Where the mote sits on the dispersion, 0 to 1. Set by <see cref="BootIntroField.Advance"/>.</summary>
    public double Spectrum;
}

/// <summary>
/// The cold boot's particle field: curl-noise advection over the three rendezvous
/// pixels, so the choreography can be asserted without a window.
///
/// The broad model follows the console's reflected compute interface, as
/// recovered in <c>docs/ps5-background-native.md</c>; the concrete point sets
/// and calibrated forces remain this visualizer's until the native resource
/// records are executed directly.
/// A 64-thread compute shader advects the field with curl noise
/// (<c>particleCurlSizeP</c>, <c>particleCurlSpeedP</c>, <c>particleCurlTimeRateP</c>)
/// and accelerates it toward <c>numRendezVousPoints</c> attractors. "spread" is
/// the name of a rendezvous set, not of an emitter: the three boot patterns differ
/// in which attractors the field is pulled toward and how hard, not in what is
/// emitted. The curl here is analytic - the flow is the curl of a sum of sine
/// potentials - so it is divergence free by construction and the motes form
/// sheets and filaments instead of piling into sinks.
///
/// The compute acceleration and curl-speed transitions are executed from the
/// <see cref="AccelerationScale"/>.
/// </summary>
public sealed class BootIntroField
{
    /// <summary>
    /// Default population. Larger than the ambient background's, because this is a
    /// full-screen hero moment with nothing on top of it: the reference's knot is
    /// a dense weave of filaments and a sparse field reads as scattered dots
    /// however bright each one is.
    /// </summary>
    public const int DefaultCount = 1500;

    /// <summary>Rosette points of the <c>coldboot</c> set.</summary>
    public const int KnotPoints = 6;

    /// <summary>Points along the rendezvous line.</summary>
    public const int LinePoints = 16;

    /// <summary>Points on the <c>spread_expanded</c> shell.</summary>
    public const int SpreadPoints = 14;

    /// <summary>Longest interval applied in one <see cref="Advance"/>.</summary>
    public const double MaxStepSeconds = 0.10;

    /// <summary>
    /// Converts this visualizer's calibrated force scale into normalised units
    /// </summary>
    public const double AccelerationScale = 0.20;

    /// <summary>Share of the population that belongs to the knot rather than to the line.</summary>
    private const double KnotShare = 0.62;

    /// <summary>Distance at which a rendezvous pull reaches full strength, in frame widths.</summary>
    private const double SoftenRadius = 0.20;

    /// <summary>Damping while the knot is holding together.</summary>
    private const double HoldDamping = 2.2;

    // Three octaves of an analytic curl, the same construction the ambient
    // background uses. Amplitudes fall and frequencies rise, so the coarse octave
    // carries the drift and the fine ones only stir it.
    private static readonly double[] CurlAmplitude = { 1.0, 0.42, 0.18 };
    private static readonly double[] CurlFrequencyX = { 3.1, 7.3, 13.7 };
    private static readonly double[] CurlFrequencyY = { 2.7, 6.1, 12.9 };
    private static readonly double[] CurlTimeRateX = { 0.11, 0.19, 0.31 };
    private static readonly double[] CurlTimeRateY = { 0.09, 0.23, 0.27 };

    /// <summary>
    /// Scales the raw curl gradient into frame widths per second. Fast enough that
    /// a mote crosses several trail half-lives of distance, which is what turns
    /// its history into a filament rather than a smear on the spot.
    /// </summary>
    private const double CurlSpeed = 0.030;

    /// <summary>How fast a mote's velocity chases the flow field, per second.</summary>
    private const double VelocityResponse = 3.2;

    /// <summary>Where the knot sits when the sequence opens.</summary>
    private const double KnotStartX = 0.40;
    private const double KnotStartY = 0.40;

    /// <summary>Where it has drifted to by the colorchange.</summary>
    private const double KnotEndX = 0.585;
    private const double KnotEndY = 0.545;

    /// <summary>Radius of the knot's rosette, in frame heights.</summary>
    private const double KnotRadius = 0.075;

    /// <summary>
    /// The frame the field is authored against. Positions are normalised over the
    /// whole frame in both axes, so a radius quoted in heights has to be divided by
    /// this in X or every round thing in the sequence comes out as a horizontal
    /// smear - which is exactly what the first pass drew.
    /// </summary>
    private const double Aspect = 16.0 / 9.0;

    /// <summary>How far inside this radius a mote counts as being in the light.</summary>
    private const double KnotLightRadius = 0.20;

    private readonly BootIntroMote[] _motes;
    private readonly double[] _knotX = new double[KnotPoints];
    private readonly double[] _knotY = new double[KnotPoints];
    private readonly double[] _lineX = new double[LinePoints];
    private readonly double[] _lineY = new double[LinePoints];
    private readonly double[] _spreadX = new double[SpreadPoints];
    private readonly double[] _spreadY = new double[SpreadPoints];

    /// <summary>Builds a field of <paramref name="count"/> motes from a fixed seed.</summary>
    /// <param name="count">Mote count; clamped to 0..2048.</param>
    /// <param name="seed">Random seed; the same seed always yields the same field.</param>
    public BootIntroField(int count = DefaultCount, int seed = 0x8007)
    {
        var random = new Random(seed);
        _motes = new BootIntroMote[Math.Clamp(count, 0, 2048)];

        for (var i = 0; i < _motes.Length; i++)
        {
            // Two populations, because the reference has two. The knot is a tight
            // bright cluster and carries the whole depth range, so the resolved
            // heads live in it; the line is a thin bed of far dust drawn across
            // the frame, and forcing it shallow is what stops it competing with
            // the knot for the eye. A field where both are the same thing is the
            // haze the earlier renderer measured as a fog.
            var inKnot = random.NextDouble() < KnotShare;
            var depth = random.NextDouble();
            depth = inKnot ? depth * depth : depth * depth * 0.42;

            var angle = random.NextDouble() * Math.Tau;
            var radius = inKnot
                ? KnotRadius * (0.20 + (0.80 * random.NextDouble()))
                : 0.20 + (0.55 * random.NextDouble());

            _motes[i] = new BootIntroMote
            {
                X = KnotStartX + (radius * Math.Cos(angle) / Aspect),
                Y = KnotStartY + (radius * Math.Sin(angle)),
                Depth = depth,
                Seed = random.NextDouble() * Math.Tau,
                Tone = random.NextDouble(),
                SpectrumBias = random.NextDouble(),
                DeathBias = 0.25 + (0.75 * random.NextDouble()),
                OnLine = !inKnot,
                KnotSlot = (byte)random.Next(KnotPoints),
                LineSlot = (byte)random.Next(LinePoints),
                SpreadSlot = (byte)random.Next(SpreadPoints),
                JitterX = (float)((random.NextDouble() - 0.5) * 0.22),

                // Thin. The reference's line is a thread with a little scatter
                // above and below it, not a band.
                JitterY = (float)((random.NextDouble() - 0.5) * 0.022),
            };
        }
    }

    /// <summary>Number of motes in the field.</summary>
    public int Count => _motes.Length;

    /// <summary>Seconds of motion applied so far; the flow field's clock.</summary>
    public double Elapsed { get; private set; }

    /// <summary>The live motes. Valid until the next <see cref="Advance"/>.</summary>
    public ReadOnlySpan<BootIntroMote> Motes => _motes;

    /// <summary>Normalised X of the knot, the bright point the sequence is built around.</summary>
    public double KnotX { get; private set; } = KnotStartX;

    /// <summary>Normalised Y of the knot.</summary>
    public double KnotY { get; private set; } = KnotStartY;

    /// <summary>
    /// The analytic curl of a sum of sine potentials. Divergence free, so motes
    /// swirl instead of collecting in sinks.
    /// </summary>
    /// <param name="x">Normalised X.</param>
    /// <param name="y">Normalised Y.</param>
    /// <param name="time">Flow clock in seconds.</param>
    /// <param name="flowX">Flow X out, in raw gradient units.</param>
    /// <param name="flowY">Flow Y out, in raw gradient units.</param>
    public static void Curl(double x, double y, double time, out double flowX, out double flowY)
    {
        double dx = 0.0;
        double dy = 0.0;

        for (var i = 0; i < CurlAmplitude.Length; i++)
        {
            var kx = CurlFrequencyX[i];
            var ky = CurlFrequencyY[i];
            var a = CurlAmplitude[i];

            var px = (kx * x) + (CurlTimeRateX[i] * time);
            var py = (ky * y) + (CurlTimeRateY[i] * time);

            // psi = a * sin(px) * sin(py); the curl of a 2-D potential is
            // (d psi / dy, -d psi / dx).
            dy += a * ky * Math.Sin(px) * Math.Cos(py);
            dx += a * kx * Math.Cos(px) * Math.Sin(py);
        }

        flowX = dy;
        flowY = -dx;
    }

    /// <summary>
    /// Advances the field. Allocation free: every buffer is pre-allocated and the
    /// motes are walked by reference.
    /// </summary>
    /// <param name="seconds">Elapsed time; clamped to <see cref="MaxStepSeconds"/>.</param>
    /// <param name="frame">The timeline's gains for this instant.</param>
    public void Advance(double seconds, in BootIntroFrame frame)
    {
        if (seconds <= 0.0 || _motes.Length == 0)
        {
            return;
        }

        seconds = Math.Min(seconds, MaxStepSeconds);
        Elapsed += seconds;

        var progress = frame.Progress;
        BuildPoints(progress);

        // The target-set crossfade is still measured from the reference. Its
        // force and curl magnitude, however, now come from the decoded coldboot
        // compute resource rather than the disproved 30/300 interpretation.
        var toSpread = BootIntroTimeline.SmoothStep(
            BootIntroTimeline.ColorChangeAt,
            BootIntroTimeline.ColorChangeAt + BootIntroTimeline.FlashSpan,
            progress);
        var toFadeout = BootIntroTimeline.SmoothStep(
            BootIntroTimeline.FadeoutAt, BootIntroTimeline.BlackAt, progress);
        var band = BootIntroTimeline.SmoothStep(BootIntroTimeline.SeedAt, BootIntroTimeline.BandAt, progress);

        var compute = frame.ParticleResources.Small0;
        var acceleration = compute.ParticleMaxAcceleration1 * AccelerationScale;
        var damping = HoldDamping;

        var response = Math.Min(1.0, seconds * VelocityResponse);
        var decay = Math.Exp(-damping * seconds);
        var time = Elapsed;
        // The colorchange stirs as hard as it pushes. Without this the spread
        // acceleration wins outright and every mote runs straight down a spoke.
        var curlGain = CurlSpeed * (compute.ParticleCurlSpeedP / 1.2);

        for (var i = 0; i < _motes.Length; i++)
        {
            ref var mote = ref _motes[i];

            // Where this mote is trying to be. The line grows in over phase 1, the
            // shell takes over at the colorchange, and the shell keeps receding
            // through the fadeout so the field streaks outward as it dies.
            var targetX = _knotX[mote.KnotSlot];
            var targetY = _knotY[mote.KnotSlot];
            if (mote.OnLine)
            {
                targetX += ((_lineX[mote.LineSlot] + mote.JitterX) - targetX) * band;
                targetY += ((_lineY[mote.LineSlot] + mote.JitterY) - targetY) * band;
            }

            if (toSpread > 0.0)
            {
                // A rendezvous point is a point, and a field that lands exactly on
                // one is a blob. Fourteen attractors with fourteen blobs on them is
                // what the first spread drew; the scatter is what turns each of
                // them back into the cloud the reference actually shows, and it is
                // per mote so the shape holds still while the field moves through
                // it.
                var scatter = 0.34 + (0.66 * mote.SpectrumBias);
                var grow = 1.0 + (2.2 * toFadeout);
                var shellX = KnotX + ((_spreadX[mote.SpreadSlot] - KnotX) * scatter * grow)
                             + (mote.JitterX * 1.6);
                var shellY = KnotY + ((_spreadY[mote.SpreadSlot] - KnotY) * scatter * grow)
                             + (mote.JitterY * 6.0);
                targetX += (shellX - targetX) * toSpread;
                targetY += (shellY - targetY) * toSpread;
            }

            Curl(mote.X, mote.Y, time + mote.Seed, out var flowX, out var flowY);
            var depthScale = 0.55 + (0.9 * mote.Depth);

            // Constant-magnitude pull, softened inside SoftenRadius so motes orbit
            // an attractor instead of chattering on it.
            var dx = targetX - mote.X;
            var dy = targetY - mote.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            var pull = acceleration * Math.Min(distance / SoftenRadius, 1.0) / Math.Max(distance, 1e-6);

            var wantX = (flowX * curlGain * depthScale) + (dx * pull);
            var wantY = (flowY * curlGain * depthScale) + (dy * pull);

            mote.VelocityX += (wantX - mote.VelocityX) * response;
            mote.VelocityY += (wantY - mote.VelocityY) * response;
            mote.VelocityX *= decay;
            mote.VelocityY *= decay;
            mote.X += mote.VelocityX * seconds;
            mote.Y += mote.VelocityY * seconds;

            Shade(ref mote, progress);
        }
    }

    // Everything the compositor needs that depends on where the mote ended up.
    private void Shade(ref BootIntroMote mote, double progress)
    {
        // Alive-ness. Nothing dies until the fadeout, and then the staggered bias
        // thins the field rather than blinking it out.
        var envelope = BootIntroTimeline.SmoothStep(0.0, BootIntroTimeline.SeedAt * 1.4, progress);
        if (progress > BootIntroTimeline.FadeoutAt)
        {
            var end = BootIntroTimeline.FadeoutAt
                      + ((BootIntroTimeline.BlackAt - BootIntroTimeline.FadeoutAt) * mote.DeathBias);
            envelope *= 1.0 - BootIntroTimeline.SmoothStep(BootIntroTimeline.FadeoutAt, end, progress);
        }

        mote.Envelope = envelope;

        // Screen-round, not normalised-round: X is the long axis of the frame.
        var dx = (mote.X - KnotX) * Aspect;
        var dy = mote.Y - KnotY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));

        // Inside the light, so the dispersion only tints what the knot is actually
        // lighting. Squared, so the split is concentrated in the bright middle
        // rather than washing out to the edge of the cluster.
        var inside = 1.0 - Math.Min(distance / KnotLightRadius, 1.0);
        mote.InsideKnot = inside * inside;

        // Ordered, and dominated by the angle. A mote's place on the spectrum is
        // where it sits around the knot, with only a slight opening outward and a
        // slow rotation on top. The distance term used to be seven times this and
        // the per-mote bias three times, and the result was confetti: neighbouring
        // motes landed on unrelated hues, which is a multi-colour tint rather than
        // a dispersion. Light split by a prism is ordered, so this is too.
        var angle = Math.Atan2(dy, dx) / Math.Tau;
        mote.Spectrum = Wrap(angle + (distance * 0.18) + (mote.SpectrumBias * 0.05) + (progress * 0.16));
    }

    // The three rendezvous sets, rebuilt each frame because the knot travels.
    private void BuildPoints(double progress)
    {
        var travel = BootIntroTimeline.SmoothStep(BootIntroTimeline.SeedAt, BootIntroTimeline.ColorChangeAt, progress);
        KnotX = KnotStartX + ((KnotEndX - KnotStartX) * travel);
        KnotY = KnotStartY + ((KnotEndY - KnotStartY) * travel);

        // The rosette. A ring, tumbling, so the knot holds a shape instead of
        // collapsing and reads as a volume rather than as a row of clumps.
        // Fast enough that the motes' own orbits dominate the knot's travel across
        // the frame. Slower than this and every filament in the knot is a comet
        // pointing the way the whole cluster is moving, which is one streak
        // repeated rather than a weave.
        var spin = Elapsed * 1.05;
        for (var i = 0; i < KnotPoints; i++)
        {
            var angle = spin + (Math.Tau * i / KnotPoints);
            var tilt = 0.55 + (0.45 * Math.Cos(angle + (Elapsed * 0.77)));
            _knotX[i] = KnotX + (KnotRadius * tilt * Math.Cos(angle) / Aspect);
            _knotY[i] = KnotY + (KnotRadius * Math.Sin(angle));
        }

        // The line: the long shallow thread the reference grows across the whole
        // frame from about a seventh of the way in, and that the knot travels
        // along. It sits a little below the knot rather than through it, so the
        // two read as separate things.
        var lineY = KnotY + 0.022;
        for (var i = 0; i < LinePoints; i++)
        {
            var u = (i / (double)(LinePoints - 1) * 2.0) - 1.0;
            _lineX[i] = 0.5 + (u * 0.62);
            _lineY[i] = lineY + (u * 0.048) + (0.012 * Math.Sin(3.1 * u));
        }

        // spread_expanded: the same field pushed out onto a shell. The reference
        // blows the knot into a large open sphere of filaments in two frames at the
        // colorchange, and that is what an expanded rendezvous set does.
        // Wide, and irregular. The reference's colorchange throws the field across
        // most of the frame rather than onto a tidy ring, and a ring of evenly
        // spaced points at one radius draws a starburst of straight spokes.
        for (var i = 0; i < SpreadPoints; i++)
        {
            var angle = (Math.Tau * i / SpreadPoints) + (Elapsed * 0.16)
                        + (0.5 * Math.Sin((i * 1.7) + (Elapsed * 0.6)));
            var radius = 0.40 + (0.20 * Math.Sin((i * 2.4) + Elapsed));
            _spreadX[i] = KnotX + (radius * Math.Cos(angle) / Aspect);
            _spreadY[i] = KnotY + (radius * Math.Sin(angle) * 0.72);
        }
    }

    private static double Wrap(double value)
    {
        value %= 1.0;
        return value < 0.0 ? value + 1.0 : value;
    }
}

