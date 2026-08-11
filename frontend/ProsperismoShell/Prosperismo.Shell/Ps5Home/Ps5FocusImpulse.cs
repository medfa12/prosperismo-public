// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The direction argument of <c>SetFocusChangeEffect</c>: the shell's
/// <c>FourWayDirection</c>, in its own numeric order.
/// </summary>
public enum Ps5FocusDirection
{
    /// <summary>0.</summary>
    Up = 0,

    /// <summary>1.</summary>
    Down = 1,

    /// <summary>2.</summary>
    Left = 2,

    /// <summary>3.</summary>
    Right = 3,
}

/// <summary>
/// The <b>backdrop</b> half of the console's focus feedback, recovered from the
/// native <c>ShellUIBackgroundLayer</c> export <c>SetFocusChangeEffectNative</c>
/// and the three functions behind it.
///
/// <para><b>It is not a ring, and this is the whole point of the class.</b> The
/// managed background layer pushes the focused widget's rect to native every
/// frame, and it is tempting to assume the rect is drawn. It is not. The native
/// side reads the rect's <em>centre</em> and discards <c>width</c>,
/// <c>height</c> and <c>repeat</c> outright — they are loaded into registers and
/// never used. What it writes into the 3D background scene's constant buffer is
/// four things: a world-space <b>point</b>, a scalar <b>inverse radius</b>, a
/// unit <b>direction</b> (the d-pad axis that caused the move), and an
/// <b>amplitude</b> that decays geometrically per frame.</para>
///
/// <para>So the effect is a directional impulse composited into the backdrop —
/// which is why on a console the focus reads as part of the background rather
/// than as a border on the tile. Anything that draws a rect here has
/// misunderstood the call.</para>
///
/// <para><b>The radius is fixed.</b> <c>7.68 / s</c>, where <c>s</c> is the
/// camera's world half-extent, is exactly <c>0.008 / k</c> with <c>k</c> the
/// world units per screen pixel — i.e. <b>125 screen pixels at 1920x1080,
/// independent of the focused widget's size</b>. A big tile and a small icon
/// disturb the backdrop identically.</para>
///
/// <para><b>What is not here.</b> The impulse's per-pixel falloff and its colour
/// live in a shader that has not been identified — see
/// <see cref="Falloff"/>. This class therefore models the state the console
/// uploads and nothing about how it looks.</para>
/// </summary>
public sealed class Ps5FocusImpulse
{
    /// <summary>
    /// Amplitude the impulse is armed at, an immediate in the native code.
    /// </summary>
    public const double InitialAmplitude = 100.0;

    /// <summary>
    /// Per-tick amplitude decay. Applied once per background update, not once
    /// per elapsed second, so it is a frame-rate dependent constant exactly as
    /// the console has it.
    /// </summary>
    public const double AmplitudeDecay = 0.95;

    /// <summary>Amplitude below which the native code snaps to zero.</summary>
    public const double AmplitudeCutoff = 0.001;

    /// <summary>
    /// Minimum background-scene opacity for a focus event to be accepted. Both
    /// native gates test this same value, so an invisible backdrop swallows the
    /// impulse rather than queueing it.
    /// </summary>
    public const double MinimumSceneOpacity = 1e-4;

    /// <summary>
    /// The radius numerator. <c>7.68 = 0.008 * 960</c>, which is what makes the
    /// effect a fixed 125 px wide.
    /// </summary>
    public const double RadiusNumerator = 7.679999351501465;

    /// <summary>The pixel radius <see cref="RadiusNumerator"/> works out to.</summary>
    public const double RadiusPixels = 125.0;

    /// <summary>World units per screen pixel, before the camera scale: 1/960.</summary>
    public const double InversePixelScale = 0.0010416667209938169;

    /// <summary>Reference screen centre X. The native code is hard-coded to 1920x1080.</summary>
    public const double ScreenCentreX = 960.0;

    /// <summary>Reference screen centre Y.</summary>
    public const double ScreenCentreY = 540.0;

    private double _amplitude;

    /// <summary>
    /// Number of decay ticks from <see cref="InitialAmplitude"/> to
    /// <see cref="AmplitudeCutoff"/>: <c>ln(1e-5) / ln(0.95)</c>, about 225
    /// ticks or 3.75 s at 60 Hz. It is visually spent long before that — down
    /// to 1.0 in 90 ticks.
    /// </summary>
    public static double DecayTicks =>
        Math.Log(AmplitudeCutoff / InitialAmplitude) / Math.Log(AmplitudeDecay);

    /// <summary>
    /// Current amplitude. Zero when no impulse is live.
    /// </summary>
    public double Amplitude => _amplitude;

    /// <summary>True while the impulse still has amplitude left.</summary>
    public bool IsLive => _amplitude > 0.0;

    /// <summary>Impulse centre X, in world units.</summary>
    public double WorldX { get; private set; }

    /// <summary>Impulse centre Y, in world units.</summary>
    public double WorldY { get; private set; }

    /// <summary>
    /// Reciprocal radius, in inverse world units — the form the constant buffer
    /// stores. The native code writes it into two adjacent slots.
    /// </summary>
    public double InverseRadius { get; private set; }

    /// <summary>Impulse direction X, one of 0, 0, +1, -1.</summary>
    public double DirectionX { get; private set; }

    /// <summary>Impulse direction Y, one of +1, -1, 0, 0.</summary>
    public double DirectionY { get; private set; }

    /// <summary>
    /// Impulse direction Z. Always written as zero; the effect is planar.
    /// </summary>
    public double DirectionZ => 0.0;

    /// <summary>
    /// UNRECOVERED. The per-pixel falloff and colour of the impulse.
    ///
    /// <para>The seven live floats the call writes are recovered, and so is
    /// their constant-buffer slot (bytes 0x140-0x15B of a 0x540-stride
    /// per-eye block). The shader that <em>reads</em> that slot is not
    /// identified: all 160 AMDGPU ELFs embedded in the shell executable were
    /// carved and named, and none of their reflected constant layouts matches
    /// the 0x540 stride. Two shaders touch the byte range, but their buffers
    /// are 0x198 long and their reflection assigns those offsets to unrelated
    /// mesh colours, so the coincidence is an offset collision and not a
    /// binding.</para>
    ///
    /// <para>It is null on purpose. Without it the impulse cannot be
    /// composited, so a backdrop that consumes this class must leave the effect
    /// off rather than substitute a glow — a plausible falloff here would be
    /// indistinguishable from the real one and would quietly become
    /// "recovered".</para>
    /// </summary>
    public static Func<double, double>? Falloff => null;

    /// <summary>
    /// UNRECOVERED. The constant-buffer destination of the amplitude. The
    /// amplitude lives at <c>+0x1bc</c> in the staging object, which is outside
    /// every one of the four blocks the per-frame builder copies, so where the
    /// GPU reads it from is not established.
    /// </summary>
    public static int? AmplitudeBufferOffset => null;

    /// <summary>
    /// UNRECOVERED. The nine floats following the impulse in its 64-byte
    /// constant-buffer block (<c>0x15C</c> to <c>0x17F</c>). The focus call does
    /// not touch them and no other writer was found.
    /// </summary>
    public static int? TrailingBlockFieldCount => null;

    /// <summary>
    /// The direction vector for <paramref name="direction"/>, from the two
    /// four-entry tables the native code indexes.
    ///
    /// <para>Note that the shell's own <c>SendFocusCurrentRect</c> always passes
    /// <see cref="Ps5FocusDirection.Up"/>, so the ordinary per-frame push is
    /// always <c>(0, +1)</c>; the other three only appear on an explicit
    /// directional call.</para>
    /// </summary>
    /// <param name="direction">The four-way direction.</param>
    public static (double X, double Y) DirectionVector(Ps5FocusDirection direction) => direction switch
    {
        Ps5FocusDirection.Up => (0.0, 1.0),
        Ps5FocusDirection.Down => (0.0, -1.0),
        Ps5FocusDirection.Left => (1.0, 0.0),
        Ps5FocusDirection.Right => (-1.0, 0.0),
        _ => (0.0, 0.0),
    };

    /// <summary>
    /// World units per screen pixel for a camera at
    /// <paramref name="cameraZ"/> with vertical field of view
    /// <paramref name="fovDegrees"/>:
    /// <c>tan(fov/2) * -cameraZ / 960</c>.
    /// </summary>
    /// <param name="fovDegrees">Field of view, in degrees.</param>
    /// <param name="cameraZ">Camera Z. Negated, so a camera in front of the
    /// plane gives a positive scale.</param>
    public static double WorldPerPixel(double fovDegrees, double cameraZ) =>
        WorldHalfExtent(fovDegrees, cameraZ) * InversePixelScale;

    /// <summary>
    /// The camera's world half-extent at the background plane,
    /// <c>tan(fov/2) * -cameraZ</c>. Everything the impulse writes is scaled by
    /// this, which is what keeps the effect the same apparent size however the
    /// scene's camera is set up.
    /// </summary>
    /// <param name="fovDegrees">Field of view, in degrees.</param>
    /// <param name="cameraZ">Camera Z.</param>
    public static double WorldHalfExtent(double fovDegrees, double cameraZ) =>
        Math.Tan(fovDegrees * 0.5 * (Math.PI / 180.0)) * -cameraZ;

    /// <summary>
    /// Arms the impulse at the <b>centre</b> of a focused rect.
    ///
    /// <para>Both screen axes are negated relative to screen space
    /// (<c>960 - x</c>, <c>540 - y</c>), which is the native code's own
    /// convention and not a mistake to be tidied up.</para>
    /// </summary>
    /// <param name="direction">Direction of the focus move.</param>
    /// <param name="centreX">Rect centre X, in screen pixels.</param>
    /// <param name="centreY">Rect centre Y, in screen pixels.</param>
    /// <param name="fovDegrees">Background camera field of view, in degrees.</param>
    /// <param name="cameraZ">Background camera Z.</param>
    /// <param name="sceneOpacity">Background scene opacity. Below
    /// <see cref="MinimumSceneOpacity"/> the impulse is dropped.</param>
    /// <returns>True when the impulse was accepted.</returns>
    public bool Arm(
        Ps5FocusDirection direction,
        double centreX,
        double centreY,
        double fovDegrees,
        double cameraZ,
        double sceneOpacity)
    {
        // Both native gates are the same test, so one check here is faithful.
        if (!(sceneOpacity > MinimumSceneOpacity))
        {
            return false;
        }

        double halfExtent = WorldHalfExtent(fovDegrees, cameraZ);
        if (!double.IsFinite(halfExtent) || halfExtent == 0.0)
        {
            return false;
        }

        double worldPerPixel = halfExtent * InversePixelScale;

        _amplitude = InitialAmplitude;

        var (dx, dy) = DirectionVector(direction);
        DirectionX = dx;
        DirectionY = dy;

        WorldX = (ScreenCentreX - centreX) * worldPerPixel;
        WorldY = (ScreenCentreY - centreY) * worldPerPixel;

        // Stored as a reciprocal, in both slots. 7.68 / halfExtent is 125 px
        // expressed in world units, whatever the camera is doing.
        InverseRadius = RadiusNumerator / halfExtent;
        return true;
    }

    /// <summary>
    /// One background update: decays the amplitude by
    /// <see cref="AmplitudeDecay"/> and snaps it to zero below
    /// <see cref="AmplitudeCutoff"/>.
    ///
    /// <para>This is a per-tick decay, not a per-second one. Feeding it a
    /// variable timestep would be a different effect from the console's.</para>
    /// </summary>
    public void Tick()
    {
        if (!(_amplitude > 0.0))
        {
            return;
        }

        _amplitude *= AmplitudeDecay;
        if (_amplitude < AmplitudeCutoff)
        {
            _amplitude = 0.0;
        }
    }

    /// <summary>Drops the impulse immediately.</summary>
    public void Reset()
    {
        _amplitude = 0.0;
        WorldX = 0.0;
        WorldY = 0.0;
        InverseRadius = 0.0;
        DirectionX = 0.0;
        DirectionY = 0.0;
    }
}
