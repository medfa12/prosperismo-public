// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Animation.Easings;
using Prosperismo.GUI.SystemAssets.Shell;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// <c>Sce.PlayStation.PUI.UI3.TransitionVariety</c>, measured
/// (<c>docs/ps5-shell-recovery-audit.md</c> §2.3). Every one of these is
/// <c>AnimationCurve.Linear</c> — the shell's chrome transitions carry no
/// easing at all, which is not what anyone guesses.
///
/// <para><b>Seam.</b> The focus-highlight and button-prompt animations are a
/// separate recovery effort and are <em>not</em> here. When they land they land
/// as new members of this class. Until then a caller that needs one should use
/// nothing rather than borrowing a duration from this list because it "feels
/// about right" — see <see cref="FocusHighlight"/>.</para>
/// </summary>
public static class Ps5Transitions
{
    /// <summary>
    /// Direction derived by HOME module 196 from the previous and next strand
    /// indices. The direction is intentionally independent of screen geometry.
    /// </summary>
    public enum HomeSelectionDirection
    {
        None,
        Left,
        Right,
    }

    /// <summary>Six-float uniform block consumed by 4.03 <c>slide_in_p</c>.</summary>
    public readonly record struct NativeSlideParameters(
        double Opacity,
        double Progress,
        double Smoothness,
        double SlideFactor,
        double DisplacementFactor,
        double Direction);

    /// <summary>Ten-float uniform block consumed by 4.03 <c>ripple_p</c>.</summary>
    public readonly record struct NativeRippleParameters(
        double OriginX,
        double OriginY,
        double Opacity,
        double Progress,
        double ProgressPow,
        double Smoothness,
        double Ratio,
        double ScaleFactor,
        double SwirlFactor,
        double FishEye);

    /// <summary><c>LinearPoint1Sec</c>.</summary>
    public static TimeSpan LinearPoint1Sec { get; } = TimeSpan.FromSeconds(0.1);

    /// <summary><c>LinearPoint15Sec</c>. Also <c>MorpheusBGMask</c>.</summary>
    public static TimeSpan LinearPoint15Sec { get; } = TimeSpan.FromSeconds(0.15);

    /// <summary><c>LinearPoint2Sec</c>.</summary>
    public static TimeSpan LinearPoint2Sec { get; } = TimeSpan.FromSeconds(0.2);

    /// <summary><c>LinearPoint3Sec</c>. Also <c>MorpheusDimmer</c>.</summary>
    public static TimeSpan LinearPoint3Sec { get; } = TimeSpan.FromSeconds(0.3);

    /// <summary><c>LinearPoint4Sec</c>. Also <c>MorpheusVr3dDimmer</c>.</summary>
    public static TimeSpan LinearPoint4Sec { get; } = TimeSpan.FromSeconds(0.4);

    /// <summary><c>TransitionVariety.DefaultAnimationDuration</c>.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(0.3);

    /// <summary><c>TransitionVariety.SqueezeEffectDuration</c>.</summary>
    public static TimeSpan SqueezeEffect { get; } = TimeSpan.FromSeconds(0.3);

    /// <summary>
    /// The Settings application's default <c>NavigatorPS</c> route duration.
    /// NPXS40008 leaves <c>routeTransitionType</c> unset for ordinary pages,
    /// which resolves to <c>"default"</c>; PUI names that default's duration
    /// <c>TransitionVariety.DefaultAnimationDuration</c> (300 ms).
    /// </summary>
    public static TimeSpan SettingsRoute { get; } = Default;

    /// <summary>
    /// PUI's <c>DefaultScreenTransitionCurve</c>. The 12.40 shared RN runtime
    /// identifies it as <c>EaseSmoothOutBreeze(0.05, 0.4)</c>. Spatial details
    /// of native <c>NavigatorPS</c> remain native-owned, so callers must not
    /// invent a slide distance when only this opacity curve is available.
    /// </summary>
    public static Ps5AnimationCurve SettingsRouteCurve =>
        Ps5AnimationCurve.DefaultScreenTransition;

    /// <summary>
    /// <c>BGTransition.BasematAnimationDuration</c> on 4.03 and 6.50. It was
    /// 300 ms on 1.12 and 2.x; the 3.x boundary has not been narrowed, so this
    /// value is version-specific and only correct for a 4.03 shell.
    /// </summary>
    public static TimeSpan BasematAnimation { get; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>The curve every <c>TransitionVariety</c> member uses.</summary>
    public static Easing Linear { get; } = new LinearEasing();

    /// <summary>
    /// UNRECOVERED — the focus highlight's duration and curve. Null, and it must
    /// stay null until the recovery pass that owns it produces a number. A
    /// renderer that reads null here should snap the highlight rather than
    /// animate it, so the missing curve is visible as an absence of motion
    /// instead of hiding inside a plausible one.
    /// </summary>
    public static TimeSpan? FocusHighlight => null;

    /// <summary>
    /// UNRECOVERED — the button-prompt fade. Same discipline as
    /// <see cref="FocusHighlight"/>.
    /// </summary>
    public static TimeSpan? ButtonPrompt => null;

    /// <summary>
    /// Recovered duration for the custom-image ripple, slide and fade families
    /// degree formula recovered from native code: it clamps the degree to
    /// 0..3 and evaluates <c>300 + degree * 166.6666717529297</c> milliseconds.
    /// Other transition families remain null until their setup paths are read.
    /// </summary>
    /// <param name="type">Background transition type.</param>
    /// <param name="degree">Background transition degree.</param>
    public static TimeSpan? BackgroundTransition(int type, int degree)
    {
        // 4.03 native owner at 0xa6980, for custom-image types 6..10:
        // duration = 300 + degree * 166.6666717529297 milliseconds.
        if (type is < 6 or > 10 || degree is < 0 or > 3)
        {
            return null;
        }

        const double baseMilliseconds = 300.0;
        const double degreeStepMilliseconds = 166.6666717529297;
        return TimeSpan.FromMilliseconds(baseMilliseconds + (degree * degreeStepMilliseconds));
    }

    /// <summary>
    /// Authored 4.03 <c>slide_in_p</c> parameter record for a degree. Direction
    /// is supplied per request by the type-7/type-8 native direction table.
    /// </summary>
    public static NativeSlideParameters? BackgroundSlide(int degree, double direction)
    {
        if (degree is < 0 or > 3 || direction is not (-1.0 or 1.0))
        {
            return null;
        }

        ReadOnlySpan<double> smoothness = [8.0, 6.0, 4.0, 2.0];
        ReadOnlySpan<double> slideFactor = [0.0, 0.0013, 0.0026, 0.0052];
        ReadOnlySpan<double> displacementFactor = [0.0, 0.0025, 0.005, 0.01];
        return new NativeSlideParameters(
            1.0,
            0.0,
            smoothness[degree],
            slideFactor[degree],
            displacementFactor[degree],
            direction);
    }

    /// <summary>
    /// Native ripple progress curve from the 4.03 transition node. The node
    /// first clamps elapsed/duration to 0..1, then evaluates this polynomial;
    /// the shader receives both this value and <c>pow(value, 2.2)</c>.
    /// </summary>
    public static double BackgroundRippleProgress(double linearProgress)
    {
        double t = Math.Clamp(linearProgress, 0.0, 1.0);
        double x = 1.0 - t;
        double x2 = x * x;
        double x4 = x2 * x2;
        double x8 = x4 * x4;
        return (x2 * ((-0.65 * x4) - 0.2)) + (1.0 - (0.15 * x8));
    }

    /// <summary>
    /// Builds the exact 40-byte <c>ripple_p</c> parameter record selected by
    /// the packed transition degree. <paramref name="originX"/> and
    /// <paramref name="originY"/> are passed through from the native request;
    /// their caller-side coordinate convention is kept explicit rather than
    /// guessed here.
    /// </summary>
    public static NativeRippleParameters? BackgroundRipple(
        int degree,
        double originX,
        double originY,
        double opacity,
        double linearProgress)
    {
        if (degree is < 0 or > 3 ||
            !double.IsFinite(originX) ||
            !double.IsFinite(originY) ||
            !double.IsFinite(opacity) ||
            !double.IsFinite(linearProgress))
        {
            return null;
        }

        ReadOnlySpan<double> smoothness = [8.0, 6.0, 4.0, 2.0];
        ReadOnlySpan<double> scaleFactor = [0.0, 0.0125, 0.025, 0.05];
        ReadOnlySpan<double> swirlFactor = [0.0, 0.025, 0.05, 0.1];
        double progress = BackgroundRippleProgress(linearProgress);
        return new NativeRippleParameters(
            originX,
            originY,
            opacity,
            progress,
            Math.Pow(progress, 2.2),
            smoothness[degree],
            16.0 / 9.0,
            scaleFactor[degree],
            swirlFactor[degree],
            swirlFactor[degree]);
    }

    /// <summary>Derives HOME's selection direction from two strand indices.</summary>
    public static HomeSelectionDirection HomeDirectionFor(int previousIndex, int nextIndex)
    {
        if (previousIndex < 0 || previousIndex == nextIndex)
        {
            return HomeSelectionDirection.None;
        }

        return nextIndex > previousIndex
            ? HomeSelectionDirection.Right
            : HomeSelectionDirection.Left;
    }

    /// <summary>
    /// HOME RN module 196's exact transition selector: a move right requests
    /// <c>SlideInLeft</c>, a move left requests <c>SlideInRight</c>, and a
    /// directionless update requests <c>Fade</c>.
    /// </summary>
    public static ShellLayerBackgroundTransitionType HomeBackgroundTransitionFor(
        HomeSelectionDirection direction) =>
        direction switch
        {
            HomeSelectionDirection.Right =>
                ShellLayerBackgroundTransitionType.CustomImageSlideInLeft,
            HomeSelectionDirection.Left =>
                ShellLayerBackgroundTransitionType.CustomImageSlideInRight,
            _ => ShellLayerBackgroundTransitionType.CustomImageFade,
        };
}
