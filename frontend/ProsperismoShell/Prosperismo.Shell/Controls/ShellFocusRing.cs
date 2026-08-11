// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Controls;

/// <summary>Which of the highlight's four states the renderer is in.</summary>
public enum ShellFocusState
{
    /// <summary>Nothing focused; the highlight is not on screen.</summary>
    Hidden,

    /// <summary>Fading in, after the in-motion delay.</summary>
    Showing,

    /// <summary>Settled on a target.</summary>
    Shown,

    /// <summary>Fading out.</summary>
    Hiding,
}

/// <summary>
/// The two independently sized focus surfaces produced for one focused target.
/// </summary>
/// <remarks>
/// <para><see cref="AreaFocusRect"/> is the focused widget's own arranged
/// rectangle. <see cref="LineFocusRect"/> is deliberately larger: LineFocus
/// owns its three-pixel band and three-pixel outside offset. The latter is not
/// a second layout target and must never be used to resize AreaFocus.</para>
///
/// <para><see cref="LineRasterRect"/> is a host-only allocation bound. It
/// includes the antialias fringe around LineFocus so the original shader is not
/// clipped by a tightly cropped offscreen surface.</para>
/// </remarks>
public readonly record struct ShellFocusPassGeometry(
    Rect AreaFocusRect,
    Rect LineFocusRect,
    Rect LineRasterRect,
    double AreaRadius,
    double LineRadius,
    double LineBandWidth);

/// <summary>
/// The CPU-side contract of the console's <b>menu</b> focus highlight, recovered
/// from <c>Sce.PlayStation.PUI.UI3.FocusRenderManager</c> and
/// <c>FocusRenderWidget</c>. One rect, one clock, no Avalonia visuals, so the
/// timing is testable without a render surface.
///
/// <para><b>Two passes, not a border.</b> The console draws a focused widget's
/// highlight as two full-quad shader passes — an <em>area</em> pass and a
/// <em>line</em> pass — rendered by a pooled widget that is not a child of the
/// target but is transform-slaved to its screen rect. Neither pass is a stroke
/// in the drawing sense; both are distance-field fills, which is why the line's
/// ring mesh and its plain-quad fallback look identical and why this class
/// exposes geometry rather than a pen.</para>
///
/// <para><b>The corner radius is not ours.</b> There is no focus corner radius
/// anywhere in the recovered source; the highlight inherits the focused widget's
/// own <c>BorderRadius</c>. A hard-coded radius here would be an invention.</para>
///
/// <para><b>What the travel actually is.</b> Not a spring, and not a single
/// tween. A focus move starts two independent timelines: a <b>250 ms geometry
/// warp</b> that interpolates centre, size and radius together, and a
/// <b>300 ms <c>Moving</c> driver</b> that shapes the line. Neither curve
/// reaches 1 at t=1 — they undershoot by 0.1% and 3.1% respectively and are hard
/// snapped at completion. Normalising them to reach 1 eases out where the
/// console cuts.</para>
/// </summary>
public sealed class ShellFocusRingTimeline
{
    // ---- Frame and duration constants (FocusRenderManager) -----------------

    /// <summary><c>SecPerFrame</c>: the renderer's own 60 Hz frame interval.</summary>
    public const double SecPerFrame = 0.01666667;

    /// <summary><c>InMotionDuration</c>.</summary>
    public const double InMotionDuration = 0.3;

    /// <summary>
    /// <c>InMotionDelay</c>, taken from <c>TransitionVariety.ShowingOptionWithDelay</c>.
    /// Only that option's <b>delay</b> is consumed; its 0.5 s duration is
    /// overridden to <see cref="InMotionDuration"/>.
    /// </summary>
    public const double InMotionDelay = 0.2;

    /// <summary><c>OutMotionDuration</c>. No delay on the way out.</summary>
    public const double OutMotionDuration = 0.3;

    /// <summary><c>MovingDuration</c>: the <c>Moving</c> shader driver.</summary>
    public const double MovingDuration = 0.3;

    /// <summary><c>PressingDuration</c>.</summary>
    public const double PressingDuration = 0.3;

    /// <summary><c>WarpAnimationDuration</c>: the geometry interpolation.</summary>
    public const double WarpAnimationDuration = 0.25;

    /// <summary>
    /// <c>DefaultFadeInTime</c>. Zero: the per-widget fade in is instant, and
    /// the visible appearance is entirely the delayed in-motion.
    /// </summary>
    public const double DefaultFadeInTime = 0.0;

    /// <summary><c>DefaultFadeOutTime</c>, on a linear curve.</summary>
    public const double DefaultFadeOutTime = 0.2;

    /// <summary>
    /// <c>DefaultKeyRepeatFadeOutRate</c>: the fade out runs this much faster
    /// while the d-pad is repeating, so a held direction does not leave a trail
    /// of half-faded highlights.
    /// </summary>
    public const double DefaultKeyRepeatFadeOutRate = 2.0;

    // ---- Geometry constants ------------------------------------------------

    /// <summary><c>DefaultLineThickness</c>, in px.</summary>
    public const double LineThickness = 3.0;

    /// <summary><c>DefaultLineOffset</c>: how far outside the rect the band sits.</summary>
    public const double LineOffset = 3.0;

    /// <summary><c>DefaultAreaEdgeFadeLength</c>.</summary>
    public const double AreaEdgeFadeLength = 5.0;

    /// <summary><c>DefaultAreaEdgeFadeOffset</c>.</summary>
    public const double AreaEdgeFadeOffset = 0.0;

    /// <summary><c>LineScaleRatioOnHiding</c>: the cap on the in/out scale.</summary>
    public const double LineScaleRatioOnHiding = 1.2;

    /// <summary>
    /// <c>MaxInOutExtendingLength</c>. This is the numerator of the in/out
    /// <em>scale</em> — <c>min(1 + 80/size, 1.2)</c> — and not, as it is easy to
    /// assume, a distance the highlight stretches toward its target.
    /// </summary>
    public const double MaxInOutExtendingLength = 80.0;

    /// <summary><c>EdgeFadeMinLength</c>.</summary>
    public const double EdgeFadeMinLength = 10.0;

    /// <summary>
    /// <c>AreaRenderingThrethold</c>, the source's spelling. Compared against
    /// the target's fraction of the <b>screen</b> area, not against an opacity:
    /// a target covering 40% or more of the screen gets no area pass at all.
    /// </summary>
    public const double AreaRenderingThreshold = 0.4;

    /// <summary><c>FocusStyleListItemTopMargin</c>.</summary>
    public const double ListItemTopMargin = 3.0;

    /// <summary><c>FocusStyleListItemBottomMargin</c>.</summary>
    public const double ListItemBottomMargin = 5.0;

    /// <summary>
    /// <c>outerVertexOffsetFactor</c>, <c>2 - sqrt(2)</c>. Places the band's
    /// outer corner vertices on the rounded rect's diagonal.
    /// </summary>
    public static readonly double OuterVertexOffsetFactor = 2.0 - Math.Sqrt(2.0);

    // ---- Warp constants ----------------------------------------------------

    /// <summary>Distance at which momentum starts rising, in px.</summary>
    public const double MomentumNearDistance = 100.0;

    /// <summary>Distance at which momentum is capped, in px.</summary>
    public const double MomentumFarDistance = 1000.0;

    /// <summary>Momentum for a short hop.</summary>
    public const double MomentumMinimum = 0.5;

    /// <summary>Momentum for a long jump, and the hard cap.</summary>
    public const double MomentumMaximum = 0.9;

    /// <summary>Travel distance that normalises to a full 1.0, in px.</summary>
    public const double WarpDistanceReference = 1920.0;

    /// <summary>Directional strain applied during a warp.</summary>
    public const double WarpStrain = 0.75;

    /// <summary>
    /// Aspect ratio below which the strain is zero: a wide, short target is not
    /// stretched at all.
    /// </summary>
    public const double WarpStrainAspectFloor = 0.25;

    /// <summary>Hard cap on the anisotropic stretch.</summary>
    public const double MaxWarpStretch = 0.2;

    /// <summary>The line's opacity multiplier on <c>Moving</c>.</summary>
    public const double MovingLineOpacityRate = 4.0;

    // Expressed in ticks rather than seconds: TimeSpan.FromSeconds truncates,
    // and losing a hundredth of a tick per frame is enough to make the warp
    // fall a microsecond short of its end.
    /// <summary>One frame, exactly.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromTicks(166_667);

    // ---- UNRECOVERED -------------------------------------------------------

    /// <summary>
    /// UNRECOVERED. The area pass's per-pixel intensity field.
    ///
    /// <para>Everything feeding the area pass is recovered — its rect, its
    /// radius, its opacity, its screen-area gate, its colour table and its
    /// 0.8 alpha gamma. What that pass computes per pixel is not: it is a
    /// compiled PSSL fragment shader in the system's script runtime library,
    /// located but not disassembled, and the signed-distance field, the colour
    /// table indexing and the glow falloff all live inside it.</para>
    ///
    /// <para>It is null on purpose, and <see cref="ShellFocusRing"/> draws no
    /// area pass while it is. The highlight is therefore line-only and visibly
    /// short of the console, which is the honest state: a hand-picked glow
    /// opacity would be indistinguishable from the real one and would quietly
    /// become "recovered".</para>
    /// </summary>
    public static Func<double, double>? AreaPixelIntensity => null;

    /// <summary>
    /// UNRECOVERED. The line pass's per-pixel field, for the same reason. The
    /// band's geometry is fully recovered and is what this class exposes; how
    /// the shader indexes the colour table across it is not.
    /// </summary>
    public static Func<double, double>? LinePixelIntensity => null;

    /// <summary>
    /// The bundled <c>image_focus_noise</c> texture sampled by both passes.
    /// </summary>
    public static byte[]? FocusNoiseTexture => Ps5FocusNoiseTexture.TryGetPayload();

    private ShellFocusState _state = ShellFocusState.Hidden;
    private Rect _from;
    private Rect _to;
    private double _fromRadius;
    private double _toRadius;
    private double _showElapsed;
    private double _warpElapsed = WarpAnimationDuration;
    private double _moveElapsed = MovingDuration;
    private double _fadeElapsed;
    private double _pressElapsed = double.MaxValue;
    private double _momentum;
    private double _distance;
    private double _angle;
    private bool _keyRepeating;

    /// <summary>Free-running clock, in seconds.</summary>
    public double Clock { get; private set; }

    /// <summary>
    /// Synchronises the shader clock to the UI manager's absolute elapsed
    /// it does not pause while the focus widget is hidden.
    /// </summary>
    internal void SynchronizeClock(TimeSpan elapsed)
    {
        if (elapsed >= TimeSpan.Zero && double.IsFinite(elapsed.TotalSeconds))
        {
            Clock = elapsed.TotalSeconds;
        }
    }

    /// <summary>Which state the highlight is in.</summary>
    public ShellFocusState State => _state;

    /// <summary>True while the highlight is on screen or fading off it.</summary>
    public bool IsVisible => _state != ShellFocusState.Hidden;

    /// <summary>True while anything still needs advancing.</summary>
    public bool NeedsTick => IsVisible;

    /// <summary>The rect the highlight is travelling away from.</summary>
    public Rect FromRect => _from;

    /// <summary>The rect the highlight is travelling to.</summary>
    public Rect TargetRect => _to;

    /// <summary>Corner radius of the target, inherited from the focused widget.</summary>
    public double TargetBorderRadius => _toRadius;

    /// <summary>True until the geometry warp has finished.</summary>
    public bool IsWarping => _warpElapsed < WarpAnimationDuration;

    /// <summary>True until the <c>Moving</c> driver has reached zero.</summary>
    public bool IsMoving => _moveElapsed < MovingDuration;

    /// <summary>
    /// Momentum captured on the first frame of the current warp, from the
    /// travel distance: <c>clamp(map(d, 100, 1000, 0.5, 0.9), 0, 0.9)</c>. A
    /// longer jump gets a curve that starts higher and therefore settles sooner.
    /// </summary>
    public double Momentum => _momentum;

    /// <summary>Travel distance of the current warp, in px.</summary>
    public double TravelDistance => _distance * WarpDistanceReference;

    /// <summary>
    /// Normalised travel distance, <c>min(1, d / 1920)</c>. Drives the stretch.
    /// </summary>
    public double NormalisedDistance => _distance;

    /// <summary>Travel angle, in radians, as the warp captured it.</summary>
    public double TravelAngle => _angle;

    /// <summary>
    /// The <c>Showing</c> shader parameter, 0..1. Rises on
    /// <see cref="InOutAnimationCurve"/> after <see cref="InMotionDelay"/> and
    /// falls the same way with no delay.
    /// </summary>
    public double Showing
    {
        get
        {
            switch (_state)
            {
                case ShellFocusState.Shown:
                    return 1.0;

                case ShellFocusState.Showing:
                {
                    double t = (_showElapsed - InMotionDelay) / InMotionDuration;
                    return InOutAnimationCurve(Clamp01(t));
                }

                case ShellFocusState.Hiding:
                    return 1.0 - InOutAnimationCurve(Clamp01(_showElapsed / OutMotionDuration));

                default:
                    return 0.0;
            }
        }
    }

    /// <summary>
    /// The <c>Moving</c> shader parameter: 1 the instant a move starts, falling
    /// to 0 across <see cref="MovingDuration"/> on
    /// <see cref="MovingAnimationCurve"/>, then snapping.
    /// </summary>
    public double Moving
    {
        get
        {
            if (_moveElapsed >= MovingDuration)
            {
                return 0.0;
            }

            return 1.0 - MovingAnimationCurve(Clamp01(_moveElapsed / MovingDuration));
        }
    }

    /// <summary>
    /// The <c>Pressing</c> parameter. A two-keyframe pulse rather than a hold:
    /// it rises to 1 over <see cref="PressingDuration"/> and falls back to 0
    /// over another, then is forced to zero.
    /// </summary>
    public double Pressing
    {
        get
        {
            if (_pressElapsed >= PressingDuration * 2.0)
            {
                return 0.0;
            }

            if (_pressElapsed < PressingDuration)
            {
                return PressingAnimationCurve(Clamp01(_pressElapsed / PressingDuration));
            }

            double t = (_pressElapsed - PressingDuration) / PressingDuration;
            return 1.0 - PressingAnimationCurve(Clamp01(t));
        }
    }

    /// <summary>
    /// The per-widget fade ratio. Fade in is instant
    /// (<see cref="DefaultFadeInTime"/> is zero); fade out is linear over
    /// <see cref="DefaultFadeOutTime"/>, at
    /// <see cref="DefaultKeyRepeatFadeOutRate"/> while a direction is held.
    /// </summary>
    public double FadeRatio
    {
        get
        {
            if (_state != ShellFocusState.Hiding)
            {
                return 1.0;
            }

            double rate = _keyRepeating ? DefaultKeyRepeatFadeOutRate : 1.0;
            return Clamp01(_fadeElapsed * rate / DefaultFadeOutTime);
        }
    }

    /// <summary>
    /// The opacity both passes are scaled from, before their own terms.
    /// </summary>
    public double BaseOpacity => _state switch
    {
        ShellFocusState.Showing => FadeRatio,
        ShellFocusState.Shown => 1.0,
        ShellFocusState.Hiding => 1.0 - FadeRatio,
        _ => 0.0,
    };

    /// <summary>The area pass's opacity, <c>base * Showing</c>.</summary>
    public double AreaOpacity => Math.Max(0.0, BaseOpacity * Showing);

    /// <summary>
    /// The line pass's opacity, <c>max(0, base * Showing * (1 - 4*Moving))</c>.
    ///
    /// <para>The factor of four is the whole character of a PlayStation focus
    /// move and it is easy to miss. It means the band is completely invisible
    /// until <c>Moving</c> falls below 0.25, roughly the first 45% of a 300 ms
    /// move. The highlight does not slide between tiles; it goes out, the
    /// geometry arrives, and it comes back on at a position it has very nearly
    /// already reached. Drawing the band throughout is what makes a port look
    /// like it is dragging a rectangle around.</para>
    /// </summary>
    public double LineOpacity => Math.Max(
        0.0,
        BaseOpacity * Showing * (1.0 - (MovingLineOpacityRate * Moving)));

    /// <summary>
    /// The in/out scale: the highlight comes in from larger and leaves larger,
    /// by <c>min(1 + 80/size, 1.2)</c> relaxed to 1 as <see cref="Showing"/>
    /// rises. A small target scales in from much further out than a big one.
    /// </summary>
    public double InOutScale
    {
        get
        {
            double n = Math.Max(Math.Max(_to.Width, _to.Height), 1.0);
            double scale = Math.Min(1.0 + (MaxInOutExtendingLength / n), LineScaleRatioOnHiding);
            return Lerp(scale, 1.0, Showing);
        }
    }

    /// <summary>
    /// Half-width of the line band, in px:
    /// <c>thickness + lerp(thickness, 0, Showing)</c>, doubled and offset while
    /// pressing. So the band is twice as wide the instant it appears and settles
    /// to <see cref="LineThickness"/>.
    /// </summary>
    public double BandWidth
    {
        get
        {
            double w = LineThickness + Lerp(LineThickness, 0.0, Showing);
            if (Pressing > 0.0)
            {
                w += w + LineOffset;
            }

            return w;
        }
    }

    /// <summary>
    /// <c>StrokePosition</c>: <c>(1 - 4*Moving) * (1 - Pressing)</c>.
    /// </summary>
    public double StrokePosition =>
        (1.0 - (MovingLineOpacityRate * Moving)) * (1.0 - Pressing);

    /// <summary>
    /// The anisotropic stretch magnitude applied along <see cref="TravelAngle"/>
    /// during a move: <c>min(0.2, strain * Moving * distance)</c>, where strain
    /// is 0.75 unless the target is wider than four times its height, in which
    /// case it is zero.
    /// </summary>
    public double WarpStretch
    {
        get
        {
            double width = Math.Max(_to.Width, 1.0);
            double ratio = Math.Max(_to.Height, 1.0) / width;
            double strain = ratio < WarpStrainAspectFloor ? 0.0 : WarpStrain;
            return Math.Min(MaxWarpStretch, strain * Moving * _distance);
        }
    }

    /// <summary>
    /// The current rect. Centre, width and height all interpolate together on
    /// <see cref="WarpAnimationCurve"/> across
    /// <see cref="WarpAnimationDuration"/>, pre-advanced by one frame so the
    /// first drawn frame is already under way.
    /// </summary>
    public Rect CurrentRect
    {
        get
        {
            if (!IsWarping)
            {
                return _to;
            }

            double k = WarpAnimationCurve(WarpProgress, _momentum);
            double cx = Lerp(_from.Center.X, _to.Center.X, k);
            double cy = Lerp(_from.Center.Y, _to.Center.Y, k);
            double w = Math.Max(0.0, Lerp(_from.Width, _to.Width, k));
            double h = Math.Max(0.0, Lerp(_from.Height, _to.Height, k));
            return new Rect(cx - (w / 2.0), cy - (h / 2.0), w, h);
        }
    }

    /// <summary>
    /// The current corner radius. Interpolated on
    /// <see cref="MovingAnimationCurve"/> — a <b>different curve</b> from the
    /// rect, so the corners round off ahead of the box settling.
    /// </summary>
    public double CurrentRadius
    {
        get
        {
            if (!IsWarping)
            {
                return _toRadius;
            }

            return Lerp(_fromRadius, Math.Max(_toRadius, 0.0), MovingAnimationCurve(WarpProgress));
        }
    }

    /// <summary>
    /// Warp progress, with the console's one-frame lead:
    /// <c>(elapsed + SecPerFrame) / 0.25</c>.
    /// </summary>
    public double WarpProgress => Clamp01((_warpElapsed + SecPerFrame) / WarpAnimationDuration);

    /// <summary>
    /// <c>InOutAnimationCurve</c>, which the source also uses verbatim as
    /// <c>PressingAnimationCurve</c>: <c>1 - (1 - t/2)^10</c>.
    ///
    /// <para>It reaches only 0.99902 at t=1. The remainder is discarded by the
    /// snap when the animation completes.</para>
    /// </summary>
    /// <param name="t">Normalised time.</param>
    public static double InOutAnimationCurve(double t) =>
        t > 0.0 ? 1.0 - Math.Pow(1.0 - (t * 0.5), 10.0) : 0.0;

    /// <summary><c>PressingAnimationCurve</c>, defined identically.</summary>
    /// <param name="t">Normalised time.</param>
    public static double PressingAnimationCurve(double t) => InOutAnimationCurve(t);

    /// <summary>
    /// <c>MovingAnimationCurve</c>: the same shape at power 5, reaching 0.96875
    /// at t=1. The 3.1% shortfall is the deliberate undershoot the snap resolves.
    /// </summary>
    /// <param name="t">Normalised time.</param>
    public static double MovingAnimationCurve(double t) =>
        t > 0.0 ? 1.0 - Math.Pow(1.0 - (t * 0.5), 5.0) : 0.0;

    /// <summary>
    /// <c>WarpAnimationCurve</c>: <c>1 - (1 - momentum) * (1 - t/2)^10</c>.
    ///
    /// <para>Momentum enters as a <b>starting offset</b>, not a rate. At t=0 the
    /// curve is already at <c>momentum</c>, so a long jump is 50% covered on its
    /// first frame and a short one is too. The highlight does not travel across
    /// the gap; it arrives and settles the remainder.</para>
    /// </summary>
    /// <param name="t">Normalised time.</param>
    /// <param name="momentum">Momentum captured at the start of the warp.</param>
    public static double WarpAnimationCurve(double t, double momentum) =>
        1.0 - ((1.0 - momentum) * Math.Pow(1.0 - (t * 0.5), 10.0));

    /// <summary>
    /// The momentum a jump of <paramref name="distance"/> px gets.
    /// </summary>
    /// <param name="distance">Centre-to-centre travel, in px.</param>
    public static double MomentumFor(double distance)
    {
        double t = (distance - MomentumNearDistance) / (MomentumFarDistance - MomentumNearDistance);
        double mapped = Lerp(MomentumMinimum, MomentumMaximum, t);
        return Math.Clamp(mapped, 0.0, MomentumMaximum);
    }

    /// <summary>
    /// The 2x2 warp distortion, an anisotropic scale of
    /// <see cref="WarpStretch"/> along <see cref="TravelAngle"/>. Returned in
    /// row-major order; it is symmetric, so the two off-diagonal terms are equal.
    /// </summary>
    public (double M11, double M12, double M21, double M22) WarpDistortionMatrix()
    {
        double s = WarpStretch;
        if (!(s > 0.0))
        {
            return (1.0, 0.0, 0.0, 1.0);
        }

        double c = Math.Cos(_angle);
        double n = Math.Sin(_angle);
        double a = 1.0;
        double b = 1.0 / (1.0 - s);
        double off = n * c * (a - b);
        return (((c * c) * a) + ((n * n) * b), off, off, ((n * n) * a) + ((c * c) * b));
    }

    /// <summary>
    /// Whether the area pass renders for a target of <paramref name="rect"/> on
    /// a screen of <paramref name="screen"/>. The gate is on the fraction of the
    /// screen covered, so a full-width row loses its glow entirely and shows
    /// only the band.
    /// </summary>
    /// <param name="rect">Target rect.</param>
    /// <param name="screen">Screen size.</param>
    public static bool ShouldRenderArea(Rect rect, Size screen)
    {
        if (!(screen.Width > 0.0) || !(screen.Height > 0.0))
        {
            return false;
        }

        double coverage = (rect.Width / screen.Width) * (rect.Height / screen.Height);
        return coverage < AreaRenderingThreshold;
    }

    /// <summary>
    /// The <c>ListItem</c> focus style's rect transform, the only style that
    /// alters geometry: 3 px off the top, 8 px off the height.
    /// </summary>
    /// <param name="rect">The target rect.</param>
    public static Rect ApplyListItemStyle(Rect rect) => new(
        rect.X,
        rect.Y + ListItemTopMargin,
        rect.Width,
        Math.Max(0.0, rect.Height - ListItemTopMargin - ListItemBottomMargin));

    /// <summary>
    /// Produces the two distinct UI3 focus-pass geometries for
    /// <paramref name="areaFocusRect"/>.
    /// </summary>
    /// <remarks>
    /// The recovered vertex path gives AreaFocus the exact owner rectangle and
    /// expands only the LineFocus plane. In particular, a Settings row must
    /// not apply <see cref="ApplyListItemStyle"/> here: its AreaFocus wash is
    /// clipped to the row Avalonia arranged, while the line's 3 px thickness
    /// and 3 px outside offset create the visible exterior plane.
    /// </remarks>
    public static ShellFocusPassGeometry CreatePassGeometry(
        Rect areaFocusRect,
        double radius,
        double showing,
        double pressing,
        double inOutScale,
        double lineScale,
        bool lineMatchesArea = false)
    {
        var scale = Math.Clamp(lineScale, 0.25, 2.0);
        var inOut = Math.Max(0.0, inOutScale);
        var lineExtent = lineMatchesArea
            ? 0.0
            : (LineThickness + LineOffset) * inOut * scale;
        var lineFocusRect = areaFocusRect.Inflate(lineExtent);

        var lineBandWidth = LineThickness + Lerp(LineThickness, 0.0, Clamp01(showing));
        if (pressing > 0.0)
        {
            lineBandWidth += lineBandWidth + LineOffset;
        }

        lineBandWidth *= scale;
        var lineRasterRect = lineFocusRect.Inflate((lineBandWidth * 0.5) + 1.0);
        var areaRadius = Math.Max(
            0.0,
            Math.Min(radius, Math.Min(areaFocusRect.Width, areaFocusRect.Height) / 2.0));
        var lineRadius = Math.Max(
            0.0,
            Math.Min(areaRadius + lineExtent, Math.Min(lineFocusRect.Width, lineFocusRect.Height) / 2.0));
        return new ShellFocusPassGeometry(
            areaFocusRect,
            lineFocusRect,
            lineRasterRect,
            areaRadius,
            lineRadius,
            lineBandWidth);
    }

    /// <summary>
    /// Retargets onto <paramref name="target"/> with corner radius
    /// <paramref name="radius"/>, starting both timelines.
    /// </summary>
    /// <param name="target">The new target rect.</param>
    /// <param name="radius">The target's own corner radius.</param>
    public void Retarget(Rect target, double radius)
    {
        if (!IsFinite(target))
        {
            return;
        }

        if (!IsVisible)
        {
            ShowAt(target, radius);
            return;
        }

        if (RectsClose(_to, target) && Math.Abs(_toRadius - radius) < 0.5)
        {
            return;
        }

        StartWarp(CurrentRect, CurrentRadius, target, radius);

        if (_state == ShellFocusState.Hiding)
        {
            _state = ShellFocusState.Showing;
            _showElapsed = InMotionDelay;
        }
    }

    /// <summary>
    /// Places the highlight on <paramref name="rect"/> with no travel and runs
    /// the delayed in-motion if it was not already up.
    /// </summary>
    /// <param name="rect">Target rect.</param>
    /// <param name="radius">Target corner radius.</param>
    public void ShowAt(Rect rect, double radius)
    {
        if (!IsFinite(rect))
        {
            return;
        }

        _from = rect;
        _to = rect;
        _fromRadius = radius;
        _toRadius = radius;
        _warpElapsed = WarpAnimationDuration;
        _moveElapsed = MovingDuration;
        _momentum = 0.0;
        _distance = 0.0;
        _angle = 0.0;

        if (_state is ShellFocusState.Hidden or ShellFocusState.Hiding)
        {
            _state = ShellFocusState.Showing;
            _showElapsed = 0.0;
            _fadeElapsed = 0.0;
        }
    }

    /// <summary>Runs the focus-out.</summary>
    public void Hide()
    {
        if (_state is ShellFocusState.Hidden or ShellFocusState.Hiding)
        {
            return;
        }

        _state = ShellFocusState.Hiding;
        _showElapsed = 0.0;
        _fadeElapsed = 0.0;
    }

    /// <summary>Drops the highlight immediately, with no focus-out.</summary>
    public void Reset()
    {
        _state = ShellFocusState.Hidden;
        _showElapsed = 0.0;
        _fadeElapsed = 0.0;
        _warpElapsed = WarpAnimationDuration;
        _moveElapsed = MovingDuration;
        _pressElapsed = double.MaxValue;
    }

    /// <summary>
    /// Marks the d-pad as repeating, which doubles the fade-out rate.
    /// </summary>
    /// <param name="repeating">True while a direction is held.</param>
    public void SetKeyRepeating(bool repeating) => _keyRepeating = repeating;

    /// <summary>
    /// Fires the press pulse. Only a press starts it; the release is already
    /// part of the pulse, so a matching call with false is not needed and does
    /// nothing.
    /// </summary>
    /// <param name="pressed">True to fire the pulse.</param>
    public void SetPressed(bool pressed)
    {
        if (pressed)
        {
            _pressElapsed = 0.0;
        }
    }

    /// <summary>Advances every clock by <paramref name="delta"/>.</summary>
    /// <param name="delta">Elapsed time.</param>
    public void Advance(TimeSpan delta)
    {
        double seconds = delta.TotalSeconds;
        if (!(seconds > 0.0) || double.IsNaN(seconds))
        {
            return;
        }

        Clock += seconds;

        if (_warpElapsed < WarpAnimationDuration)
        {
            _warpElapsed = Math.Min(WarpAnimationDuration, _warpElapsed + seconds);
        }

        if (_moveElapsed < MovingDuration)
        {
            _moveElapsed = Math.Min(MovingDuration, _moveElapsed + seconds);
        }

        if (_pressElapsed < PressingDuration * 2.0)
        {
            _pressElapsed += seconds;
        }

        switch (_state)
        {
            case ShellFocusState.Showing:
                _showElapsed += seconds;
                if (_showElapsed >= InMotionDelay + InMotionDuration)
                {
                    _state = ShellFocusState.Shown;
                }

                break;

            case ShellFocusState.Hiding:
                _showElapsed += seconds;
                _fadeElapsed += seconds;
                if (_showElapsed >= OutMotionDuration)
                {
                    _state = ShellFocusState.Hidden;
                }

                break;
        }
    }

    private void StartWarp(Rect fromRect, double fromRadius, Rect target, double radius)
    {
        _from = fromRect;
        _fromRadius = fromRadius;
        _to = target;
        _toRadius = radius;

        var delta = target.Center - fromRect.Center;
        double d = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));

        _momentum = MomentumFor(d);
        _distance = Math.Min(1.0, d / WarpDistanceReference);
        _angle = Math.Atan2(delta.Y, delta.X) + Math.PI;

        _warpElapsed = 0.0;
        _moveElapsed = 0.0;
    }

    private static bool RectsClose(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 0.5 &&
        Math.Abs(a.Y - b.Y) < 0.5 &&
        Math.Abs(a.Width - b.Width) < 0.5 &&
        Math.Abs(a.Height - b.Height) < 0.5;

    private static bool IsFinite(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Width > 0.0 && rect.Height > 0.0;

    private static double Clamp01(double value) =>
        double.IsNaN(value) ? 0.0 : Math.Clamp(value, 0.0, 1.0);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}

/// <summary>
/// The shell's travelling focus highlight: one renderer, one rect, drawn on a
/// layer above the scene rather than as a decoration on each widget.
///
/// <para>Call <see cref="Claim"/> from whichever control currently owns focus.
/// It never cross-fades two highlights and it never re-lays-out anything — it
/// retargets its single rect and redraws itself.</para>
///
/// <para>The console composites two shader passes and both are represented
/// moving noise texture. The <em>area</em> is a separate translucent card wash
/// using the recovered signed-distance field, size gate and five-second
/// two-channel shimmer. Their geometry and clocks remain separate: an icon can
/// request a thinner line without turning a card-style wash into a border.</para>
///
/// <para>The band is drawn as a pen on a rounded rect rather than as the
/// console's sixteen-vertex triangle strip. That is not an approximation: the
/// strip is a rasterisation optimisation that covers only the band's pixels
/// while the shader recomputes the band from a distance field anyway, which is
/// also why the console can abandon the strip for a plain quad mid-move without
/// the visual changing.</para>
/// </summary>
public sealed class ShellFocusRing : Control
{
    // FocusRenderManager.startTime is static and captured from CurrentUITime.
    // One process-wide monotonic clock gives every pooled/recreated ring the
    // same phase and continues while no focus widget is visible.
    private static readonly Stopwatch FocusClock = Stopwatch.StartNew();

    /// <summary>
    /// The line widget's <c>FilterColor</c>: white, meaning no tint. The visible
    /// colour comes from <see cref="ShellFocusPalette.ColorTable"/>, not here.
    /// </summary>
    public static readonly Color DefaultStrokeColor = Colors.White;

    /// <summary>The area widget's <c>FilterColor</c>, also white.</summary>
    public static readonly Color DefaultFillColor = Colors.White;

    /// <summary>Corner radius used when the owner has not supplied one.</summary>
    private const double UnsetRadius = 0.0;

    /// <summary>Colour of the ring's stroke.</summary>
    public static readonly StyledProperty<Color> StrokeColorProperty =
        AvaloniaProperty.Register<ShellFocusRing, Color>(nameof(StrokeColor), DefaultStrokeColor);

    /// <summary>Colour of the ring's fill.</summary>
    public static readonly StyledProperty<Color> FillColorProperty =
        AvaloniaProperty.Register<ShellFocusRing, Color>(nameof(FillColor), DefaultFillColor);

    /// <summary>Corner radius inherited from the focused widget.</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<ShellFocusRing, double>(nameof(Radius), UnsetRadius);

    /// <summary>Whether the host drives the clock.</summary>
    public static readonly StyledProperty<bool> ManualClockProperty =
        AvaloniaProperty.Register<ShellFocusRing, bool>(nameof(ManualClock), false);

    /// <summary>Per-widget scale for line thickness and its outside offset.</summary>
    public static readonly StyledProperty<double> LineScaleProperty =
        AvaloniaProperty.Register<ShellFocusRing, double>(nameof(LineScale), 1.0);

    public static readonly StyledProperty<bool> LineMatchesAreaProperty =
        AvaloniaProperty.Register<ShellFocusRing, bool>(nameof(LineMatchesArea), false);

    private readonly ShellFocusRingTimeline _timeline = new();
    private bool _attached;
    private bool _framePending;
    private bool _hasFrameTime;
    private int _frameGeneration;
    private TimeSpan _lastFrameTime;
    private Visual? _trackedHost;
    private object? _owner;
    private Task<Ps5NativeFocusRuntime?>? _nativeRuntimeTask;
    private CancellationTokenSource? _nativeCancellation;
    private bool _nativeFramePending;
    private WriteableBitmap? _nativeBitmap;
    private double _nativeAspect = double.NaN;

    /// <summary>Creates a highlight. It never takes focus or hit-tests.</summary>
    public ShellFocusRing()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        ZIndex = 30_000;
        EffectiveViewportChanged += (_, _) => RequestFrame();
    }

    /// <summary>Line filter colour. White leaves the colour table untinted.</summary>
    public Color StrokeColor
    {
        get => GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    /// <summary>Area filter colour.</summary>
    public Color FillColor
    {
        get => GetValue(FillColorProperty);
        set => SetValue(FillColorProperty, value);
    }

    /// <summary>
    /// The focused widget's own corner radius. There is no focus radius in the
    /// recovered source — the highlight inherits this from its target — so the
    /// owner must set it and a default here would be an invention.
    /// </summary>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>When set, no internal timer runs and the host calls
    /// <see cref="Advance"/>. Used by headless captures and tests.</summary>
    public bool ManualClock
    {
        get => GetValue(ManualClockProperty);
        set => SetValue(ManualClockProperty, value);
    }

    /// <summary>
    /// HOME cards use 1. Compact icon and Settings targets use a smaller value;
    /// their native focus widgets do not present the card-scale line.
    /// </summary>
    public double LineScale
    {
        get => GetValue(LineScaleProperty);
        set => SetValue(LineScaleProperty, value);
    }

    /// <summary>
    /// Compatibility seam for surfaces whose recovered contract explicitly
    /// keeps the line pass on the exact AreaFocus rectangle. Ordinary UI3 and
    /// Settings targets leave this false so LineFocus retains its exterior
    /// plane independently of the AreaFocus shimmer.
    /// </summary>
    public bool LineMatchesArea
    {
        get => GetValue(LineMatchesAreaProperty);
        set => SetValue(LineMatchesAreaProperty, value);
    }

    /// <summary>The timeline this highlight renders.</summary>
    public ShellFocusRingTimeline Timeline => _timeline;

    /// <summary>The control that last claimed the highlight, if any.</summary>
    public object? Owner => _owner;

    internal bool NativeFramePending => _nativeFramePending;

    internal bool NativeFrameAvailable => _nativeBitmap is not null;

    /// <summary>
    /// Finds (or creates) the one highlight for the scene
    /// <paramref name="anchor"/> belongs to. Null when the anchor is not yet in
    /// a tree that can host one, which is normal during construction.
    /// </summary>
    /// <param name="anchor">Any visual in the scene.</param>
    public static ShellFocusRing? For(Visual? anchor)
    {
        try
        {
            if (anchor is null || FindHost(anchor) is not { } host)
            {
                return null;
            }

            foreach (var child in host.Children)
            {
                if (child is ShellFocusRing existing)
                {
                    return existing;
                }
            }

            var ring = new ShellFocusRing();
            host.Children.Add(ring);
            return ring;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Hands the highlight to <paramref name="owner"/> and retargets it onto
    /// <paramref name="rect"/>, in this control's own coordinate space.
    /// </summary>
    /// <param name="owner">The claiming control.</param>
    /// <param name="rect">The target rect.</param>
    public void Claim(object owner, Rect rect, bool lineMatchesArea = false)
    {
        // Resolve the packaged field before the focus transition starts. The
        // native renderer and the CPU wash/band therefore share image_focus_noise
        // existing neutral sampling fallback without changing this geometry.
        Ps5FocusNoiseTexture.Preload();
        _owner = owner;
        LineMatchesArea = lineMatchesArea;
        MoveTo(rect);
    }

    /// <summary>Hides the highlight if <paramref name="owner"/> still holds it.</summary>
    /// <param name="owner">The releasing control.</param>
    public void Release(object owner)
    {
        if (ReferenceEquals(_owner, owner))
        {
            Hide();
        }
    }

    /// <summary>Retargets onto <paramref name="target"/>.</summary>
    /// <param name="target">The new target rect.</param>
    public void MoveTo(Rect target)
    {
        _timeline.Retarget(target, Radius);
        Wake();
    }

    /// <summary>Places the highlight and runs the delayed in-motion.</summary>
    /// <param name="rect">The target rect.</param>
    public void ShowAt(Rect rect)
    {
        _timeline.ShowAt(rect, Radius);
        Wake();
    }

    /// <summary>Runs the focus-out.</summary>
    public void Hide()
    {
        _timeline.Hide();
        Wake();
    }

    /// <summary>Fires the press pulse.</summary>
    /// <param name="pressed">True to fire.</param>
    public void SetPressed(bool pressed)
    {
        _timeline.SetPressed(pressed);
        Wake();
    }

    /// <summary>Advances the highlight and redraws it.</summary>
    /// <param name="delta">Elapsed time.</param>
    public void Advance(TimeSpan delta)
    {
        _timeline.Advance(delta);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        try
        {
            RenderHighlight(context);
        }
        catch
        {
            // The highlight is decoration: a bad frame must never take the
            // scene down with it.
        }
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ManualClockProperty)
        {
            if (ManualClock)
            {
                StopFrames();
            }
            else
            {
                Wake();
            }
        }
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _hasFrameTime = false;
        _nativeCancellation = new CancellationTokenSource();
        Ps5FocusNoiseTexture.Preload();
        _nativeRuntimeTask ??= Task.Run(Ps5NativeFocusRuntime.TryCreate);
        TrackHost();
        RequestFrame();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UntrackHost();
        _nativeCancellation?.Cancel();
        _nativeCancellation?.Dispose();
        _nativeCancellation = null;
        _nativeFramePending = false;
        var nativeBitmap = _nativeBitmap;
        _nativeBitmap = null;
        nativeBitmap?.Dispose();
        _attached = false;
        StopFrames();
        base.OnDetachedFromVisualTree(e);
    }

    private static Panel? FindHost(Visual anchor)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is { } layer)
        {
            return layer;
        }

        // No top-level yet (a preview host, or a tree still being built): fall
        // back to the outermost panel so every row still shares one highlight.
        Panel? outermost = null;
        for (Visual? visual = anchor; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Panel panel)
            {
                outermost = panel;
            }
        }

        return outermost;
    }

    /// <summary>
    /// The highlight is a plane, not a widget decoration, so it stretches over
    /// the whole scene and draws its rect wherever that rect happens to be. Its
    /// host arranges children at their desired size, so it has to ask for the
    /// host's size explicitly.
    /// </summary>
    private void TrackHost()
    {
        try
        {
            UntrackHost();
            if (this.GetVisualParent() is not { } parent)
            {
                return;
            }

            _trackedHost = parent;
            parent.PropertyChanged += OnHostPropertyChanged;
            MatchHost(parent.Bounds);
        }
        catch
        {
            // A host that cannot be tracked just leaves the highlight at its size.
        }
    }

    private void UntrackHost()
    {
        if (_trackedHost is { } host)
        {
            host.PropertyChanged -= OnHostPropertyChanged;
            _trackedHost = null;
        }
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && sender is Visual visual)
        {
            MatchHost(visual.Bounds);
        }
    }

    private void MatchHost(Rect bounds)
    {
        // Width/Height start out NaN ("auto"), and every comparison against NaN
        // is false, so the unset case has to be tested for explicitly.
        if (bounds.Width > 0 && (double.IsNaN(Width) || Math.Abs(Width - bounds.Width) > 0.5))
        {
            Width = bounds.Width;
        }

        if (bounds.Height > 0 && (double.IsNaN(Height) || Math.Abs(Height - bounds.Height) > 0.5))
        {
            Height = bounds.Height;
        }
    }

    private void Wake()
    {
        if (!ManualClock)
        {
            _timeline.SynchronizeClock(FocusClock.Elapsed);
        }

        InvalidateVisual();

        if (ManualClock || !_timeline.NeedsTick)
        {
            return;
        }

        _hasFrameTime = false;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_framePending || !_attached || ManualClock || !_timeline.NeedsTick ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _framePending = true;
        int generation = _frameGeneration;
        topLevel.RequestAnimationFrame(frameTime => OnFrame(frameTime, generation));
    }

    private void OnFrame(TimeSpan frameTime, int generation)
    {
        if (generation != _frameGeneration)
        {
            return;
        }

        _framePending = false;
        if (!_attached || ManualClock || !_timeline.NeedsTick)
        {
            _hasFrameTime = false;
            return;
        }

        var delta = _hasFrameTime
            ? frameTime - _lastFrameTime
            : ShellFocusRingTimeline.FrameInterval;
        _lastFrameTime = frameTime;
        _hasFrameTime = true;
        if (delta <= TimeSpan.Zero || delta > TimeSpan.FromSeconds(0.25))
        {
            delta = ShellFocusRingTimeline.FrameInterval;
        }

        _timeline.Advance(delta);
        _timeline.SynchronizeClock(FocusClock.Elapsed);
        InvalidateVisual();
        RequestFrame();
    }

    private void StopFrames()
    {
        // Avalonia does not cancel an already requested callback. Invalidate
        // its generation so it cannot join a later attachment's frame loop.
        _frameGeneration++;
        _framePending = false;
        _hasFrameTime = false;
    }

    private void RenderHighlight(DrawingContext context)
    {
        if (!ManualClock)
        {
            _timeline.SynchronizeClock(FocusClock.Elapsed);
        }

        if (RenderNativeHighlight(context))
        {
            return;
        }

        // The area pass draws first and underneath. Its field is the recovered
        // rounded-box signed distance from the AreaFocus shader - see
        RenderAreaWash(context);

        double alpha = _timeline.LineOpacity;
        if (alpha <= 0.004)
        {
            return;
        }

        var rect = _timeline.CurrentRect;
        if (rect.Width <= 1.0 || rect.Height <= 1.0)
        {
            return;
        }

        var geometry = ShellFocusRingTimeline.CreatePassGeometry(
            rect,
            _timeline.CurrentRadius,
            _timeline.Showing,
            _timeline.Pressing,
            _timeline.InOutScale,
            LineScale,
            LineMatchesArea);
        if (geometry.LineFocusRect.Width <= 1.0 || geometry.LineFocusRect.Height <= 1.0)
        {
            return;
        }

        // The anisotropic stretch along the travel angle. Symmetric, so it is a
        // pure scale in a rotated frame and can be applied about the centre.
        var (m11, m12, m21, m22) = _timeline.WarpDistortionMatrix();
        var centre = geometry.LineFocusRect.Center;
        var distortion = new Matrix(m11, m12, m21, m22, 0, 0);
        var transform = Matrix.CreateTranslation(-centre.X, -centre.Y)
            * distortion
            * Matrix.CreateTranslation(centre.X, centre.Y);

        // A distance-field band, not a stroked rectangle. A pen measures its
        // width along the path, so it thickens through the corner arcs; the
        // field measures perpendicular to the edge and stays even. See
        // ShellFocusBand.
        using (context.PushTransform(transform))
        {
            ShellFocusBand.Render(
                context,
                geometry.LineFocusRect,
                geometry.LineRadius,
                geometry.LineBandWidth,
                Math.Clamp(alpha, 0.0, 1.0),
                _timeline.Clock,
                renderAtTargetResolution: LineMatchesArea);
        }
    }

    private bool RenderNativeHighlight(DrawingContext context)
    {
        // The recovered native pass exposes the default exterior line plane,
        // but no host input for Settings' exact-row line treatment. Use the
        // shader-derived CPU evaluator for that bounded variant. It preserves
        // the same moving noise/tone field as card focus while evaluating the
        // wide row at target resolution so the thinner line stays crisp.
        if (LineMatchesArea)
        {
            return false;
        }

        var rect = _timeline.CurrentRect;
        if (rect.Width <= 1.0 || rect.Height <= 1.0 || _timeline.LineOpacity <= 0.004)
        {
            return false;
        }

        var geometry = ShellFocusRingTimeline.CreatePassGeometry(
            rect,
            _timeline.CurrentRadius,
            _timeline.Showing,
            _timeline.Pressing,
            _timeline.InOutScale,
            LineScale,
            LineMatchesArea);
        if (geometry.LineRasterRect.Width <= 1.0 || geometry.LineRasterRect.Height <= 1.0)
        {
            return false;
        }

        int width = Math.Max(2, (int)Math.Ceiling(geometry.LineRasterRect.Width));
        int height = Math.Max(2, (int)Math.Ceiling(geometry.LineRasterRect.Height));
        var state = new Ps5NativeFocusFrameState(
            (float)rect.Center.X,
            (float)rect.Center.Y,
            (float)rect.Width,
            (float)rect.Height,
            (float)_timeline.CurrentRadius,
            (float)_timeline.Moving,
            (float)_timeline.Pressing,
            (float)_timeline.Showing,
            (float)_timeline.TravelAngle,
            (float)_timeline.NormalisedDistance,
            (float)_timeline.Clock,
            PassOpacity: 1.0f,
            ViewportScale: 1.0f,
            DisplayMode: ShellFocusPalette.UsePaperWhiteOutput ? 1u : 0u,
            InOutScale: (float)_timeline.InOutScale);
        var request = new Ps5NativeFocusRenderRequest(
            width,
            height,
            state,
            (float)_timeline.AreaOpacity,
            (float)_timeline.LineOpacity,
            RenderArea: true);
        QueueNativeFrame(request);

        var bitmap = _nativeBitmap;
        double aspect = geometry.LineRasterRect.Width / geometry.LineRasterRect.Height;
        if (bitmap is null || !double.IsFinite(_nativeAspect) ||
            Math.Abs(aspect - _nativeAspect) > 0.08)
        {
            return false;
        }

        context.DrawImage(bitmap, new Rect(bitmap.Size), geometry.LineRasterRect);
        return true;
    }

    private void QueueNativeFrame(Ps5NativeFocusRenderRequest request)
    {
        if (_nativeFramePending || _nativeCancellation is not { } cancellation ||
            _nativeRuntimeTask is not { } runtimeTask)
        {
            return;
        }

        _nativeFramePending = true;
        _ = RenderNativeFrameAsync(runtimeTask, request, cancellation.Token);
    }

    private async Task RenderNativeFrameAsync(
        Task<Ps5NativeFocusRuntime?> runtimeTask,
        Ps5NativeFocusRenderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await runtimeTask.ConfigureAwait(false);
            if (runtime is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var frame = await Task.Run(
                async () => await runtime.RenderAsync(request, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (!frame.IsValid || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(
                () => PresentNativeFrame(frame),
                DispatcherPriority.Render,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            _nativeFramePending = false;
        }
    }

    private void PresentNativeFrame(Ps5NativeParticleFrame frame)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        using (var target = bitmap.Lock())
        {
            unsafe
            {
                fixed (byte* source = frame.Rgba.Span)
                {
                    int rowBytes = frame.Width * 4;
                    for (var y = 0; y < frame.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            source + (y * rowBytes),
                            (byte*)target.Address + (y * target.RowBytes),
                            target.RowBytes,
                            rowBytes);
                    }
                }
            }
        }

        var old = _nativeBitmap;
        _nativeBitmap = bitmap;
        _nativeAspect = frame.Width / (double)frame.Height;
        old?.Dispose();
        InvalidateVisual();
    }

    /// <summary>
    /// Draws the area wash under the line.
    /// </summary>
    /// <remarks>
    /// The area rect is the target rect with no line margin - the line sits
    /// outside the target, the wash sits on it. Opacity comes from the timeline's
    /// area term, which is the one that survives once the line has gone to zero.
    /// </remarks>
    private void RenderAreaWash(DrawingContext context)
    {
        var rect = _timeline.CurrentRect;
        if (rect.Width <= 1.0 || rect.Height <= 1.0)
        {
            return;
        }

        var canvas = Bounds;
        double screenWidth = canvas.Width > 0 ? canvas.Width : rect.Width;
        double screenHeight = canvas.Height > 0 ? canvas.Height : rect.Height;
        double radius = Math.Max(
            0.0,
            Math.Min(_timeline.CurrentRadius, Math.Min(rect.Width, rect.Height) / 2.0));

        ShellFocusWash.Render(
            context,
            rect,
            radius,
            _timeline.AreaOpacity,
            screenWidth,
            screenHeight,
            _timeline.Clock,
            _timeline.Moving,
            _timeline.Pressing);
    }
}
