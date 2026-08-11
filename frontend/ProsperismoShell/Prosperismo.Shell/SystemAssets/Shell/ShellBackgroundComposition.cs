// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// Wave colour preset. The shell background's procedural wave is not a single
/// effect with a colour property: it is a preset index handed to the renderer
/// every frame, and the preset picks the whole palette. The numbering is the
/// up with what the renderer expects), and <see cref="HomeScreen"/> is both the
/// home value and the layer-wide default.
/// </summary>
public enum ShellWavePreset : uint
{
    /// <summary>Obsolete in 4.03; kept so the numbering matches.</summary>
    InitialSetup = 0,

    /// <summary>Obsolete in 4.03; kept so the numbering matches.</summary>
    MiniApp = 1,

    /// <summary>Control-centre / system-area surfaces.</summary>
    SystemArea = 2,

    /// <summary>Obsolete in 4.03; kept so the numbering matches.</summary>
    MusicUnlimited = 3,

    /// <summary>The home screen, and the background layer's own default.</summary>
    HomeScreen = 4,

    /// <summary>No wave at all; the image and basemat carry the frame.</summary>
    NoWave = 5,

    /// <summary>Flat black.</summary>
    Black = 6,

    /// <summary>What's New feed.</summary>
    WhatsNew = 7,

    /// <summary>Obsolete in 4.03; kept so the numbering matches.</summary>
    MusicUnlimitedSplash = 8,

    /// <summary>User sign-in screen.</summary>
    Login = 9,

    /// <summary>Sign-in screen with no user logged in.</summary>
    LoginNoUserLogined = 10,

    /// <summary>Boot sequence.</summary>
    Boot = 11,

    /// <summary>PlayStation Store.</summary>
    Store = 12,

    /// <summary>Video app.</summary>
    PsVideo = 13,

    /// <summary>Custom theme flow, slot 0.</summary>
    ThemeFlow0 = 14,

    /// <summary>Custom theme flow, slot 1.</summary>
    ThemeFlow1 = 15,

    /// <summary>Custom theme flow, slot 2.</summary>
    ThemeFlow2 = 16,

    /// <summary>Custom theme flow, slot 3.</summary>
    ThemeFlow3 = 17,

    /// <summary>Custom theme flow, slot 4.</summary>
    ThemeFlow4 = 18,

    /// <summary>Custom theme flow, slot 5.</summary>
    ThemeFlow5 = 19,

    /// <summary>Custom theme flow, slot 6.</summary>
    ThemeFlow6 = 20,

    /// <summary>Custom theme flow, slot 7.</summary>
    ThemeFlow7 = 21,
}

/// <summary>
/// Light-particle render mode. This is the second index the background layer
/// pushes to the renderer every frame (alongside <see cref="ShellWavePreset"/>);
/// it selects both whether light particles exist and how they are emitted. The
/// values are sparse on purpose: the high nibble is a mode family, and the
/// rather than to the light modes named here.
/// </summary>
public enum ShellLightRenderMode
{
    /// <summary>Background renders with the explicit light-particle override disabled.</summary>
    NoParticle = 65,

    /// <summary>Welcome screen fading out, no particles.</summary>
    InitialWelcomeNoParticle = 66,

    /// <summary>Particles rise from the bottom edge. Sign-in, initial setup, welcome.</summary>
    Bottom = 67,

    /// <summary>Particles spread outward from the centre. Shutdown.</summary>
    Spread = 68,

    /// <summary>Cold-boot animation.</summary>
    ColdBoot = 69,

    /// <summary>Warm-boot animation.</summary>
    WarmBoot = 70,

    /// <summary>First-run boot animation.</summary>
    InitialBoot = 71,

    /// <summary>Shutdown.</summary>
    Shutdown = 72,

    /// <summary>Flat black; nothing renders.</summary>
    Black = 78,

    /// <summary>Nothing at all; the layer's value before any state is applied.</summary>
    None = 79,
}

/// <summary>
/// How a light mode seeds and moves its particles. The shell has no separate
/// pattern field - the emission shape is implied by the light render mode - so
/// it is derived here by <see cref="ShellBackgroundComposition.PatternFor"/>.
/// <see cref="None"/>, <see cref="Bottom"/> and <see cref="Spread"/> are the
/// steady HOME selects <see cref="None"/>.
/// </summary>
public enum ShellParticlePattern
{
    /// <summary>No emission; the field is inert. Every non-emitting light mode maps here.</summary>
    None,

    /// <summary>Particles rise from the bottom edge. Sign-in, initial setup, welcome.</summary>
    Bottom,

    /// <summary>Particles spread outward from the centre. Shutdown and the boot animations.</summary>
    Spread,
}

/// <summary>
/// Exact result of the NPXS40087 4.03 light-mode dispatcher for the native
/// large-particle owner. A null raw state means the dispatcher does not call
/// the particle state setter for that mode.
/// </summary>
public readonly record struct Ps5NativeParticleModeRoute(
    int DispatcherCommand,
    int? RawState,
    float LayerWeight);

/// <summary>
/// Basemat shape. The basemat is the mat the shell lays between the background
/// composite and the UI so foreground content stays readable: it is what an
/// emulator would otherwise call "the scrim", except that its shape, colour and
/// cross-fade are all parameters. <c>EllipseWide</c> and <c>EllipseNarrow</c>
/// are both 3 in 4.03, so only one ellipse survives and it is spelled
/// <see cref="Ellipse"/> here.
/// </summary>
public enum ShellBasematType
{
    /// <summary>No mat; the background composite is shown untouched.</summary>
    None = 0,

    /// <summary>Uniform mat over the whole frame. Used behind modal surfaces.</summary>
    Flat = 1,

    /// <summary>Vertical gradient mat.</summary>
    Linear = 2,

    /// <summary>Elliptical mat. The only ellipse variant left in 4.03.</summary>
    Ellipse = 3,
}

/// <summary>
/// Global background state. The shell drives the background layer through this
/// one enum; the layer turns each state into a
/// <see cref="ShellLightRenderMode"/> (and, for the boot and welcome states, a
/// alias of <see cref="ColdBootAnimation"/>.
/// </summary>
public enum ShellGlobalBackgroundState
{
    /// <summary>Layer idle; background opacity is forced to zero.</summary>
    None = 0,

    /// <summary>Flat black.</summary>
    Black = 1,

    /// <summary>Cold-boot animation, 6 s.</summary>
    ColdBootAnimation = 2,

    BootAnimation = ColdBootAnimation,

    /// <summary>Warm-boot animation, 3 s.</summary>
    WarmBootAnimation = 3,

    /// <summary>First-run boot movie.</summary>
    InitialBootAnimation = 4,

    /// <summary>Initial setup / onboarding.</summary>
    InitialSetup = 5,

    /// <summary>Welcome screen animation.</summary>
    InitialWelcomeScreenAnimation = 6,

    /// <summary>Welcome screen fade-out, 1333.33 ms.</summary>
    InitialWelcomeScreenFadeOutAnimation = 7,

    /// <summary>User sign-in.</summary>
    Login = 8,

    /// <summary>Steady state with particles rising from the bottom.</summary>
    ParticleBottom = 9,

    /// <summary>Steady state with spreading particles.</summary>
    ParticleSpread = 10,

    /// <summary>Steady state without particles. This is the home screen.</summary>
    NoParticle = 11,

    /// <summary>Shutdown.</summary>
    Shutdown = 12,

    /// <summary>
    /// Shutdown animation. Absent from 3.00, which is why this enum previously
    /// skipped it and placed the fade-out one lower.
    /// </summary>
    ShutdownAnimation = 13,

    /// <summary>Shutdown fade-out, 3 s.</summary>
    FadeOutShutdownAnimation = 14,
}

/// <summary>
/// actually uses. Kept free of control state so the mapping, the opacity ramps
/// and the basemat brushes are all unit-testable without a rendering surface.
///
/// Layer order, bottom to top: the clear colour, the background image slots
/// (the blurred variant first, then the sharp one over it, then an optional
/// overlay), the wave, the light particles, and finally the basemat under the
/// UI. The wave and particles are separate native effects; this class only
/// carries their recovered state and composition contracts. See
/// <c>docs/ps5-background.md</c>.
/// </summary>
public static class ShellBackgroundComposition
{
    /// <summary>
    /// The wave preset the background layer selects for itself at construction,
    /// and the value the home screen uses.
    /// </summary>
    public const ShellWavePreset DefaultWavePreset = ShellWavePreset.HomeScreen;

    /// <summary>
    /// Basemat default colour, from the renderer's own linear-RGB triple
    /// (0.00784, 0.01568, 0.03137) which is #020408 in 8-bit sRGB steps - the
    /// same near-black the rest of the shell palette is built on.
    /// </summary>
    public static readonly Color BasematColor = Color.FromRgb(0x02, 0x04, 0x08);

    /// <summary>Basemat cross-fade when the caller does not name one, in milliseconds.</summary>
    public const double BasematDurationMilliseconds = 1000.0;

    /// <summary>One 60 fps frame in milliseconds; the tick the opacity ramps are quoted per.</summary>
    public const double FrameMilliseconds = 1000.0 / 60.0;

    /// <summary>Per-frame multiplier while the wave is fading out.</summary>
    public const double WaveFadeOutFactor = 0.9;

    /// <summary>Per-frame increment while the wave is fading in; clamped at 1.</summary>
    public const double WaveFadeInStep = 0.01;

    /// <summary>Below this the renderer treats a layer as invisible.</summary>
    public const double VisibleOpacityThreshold = 0.0001;

    /// <summary>A background transition that has not finished within this is abandoned.</summary>
    public const double TransitionTimeoutMilliseconds = 10000.0;

    /// <summary>Focused-rect ring-buffer depth used to pick a transition's centre point.</summary>
    public const int FocusSlots = 3;

    /// <summary>A focus move under this many pixels of Manhattan travel is ignored.</summary>
    public const double FocusMoveThreshold = 10.0;

    /// <summary>How long a recorded focus position has to settle before it can seed a transition.</summary>
    public const double FocusSettleMilliseconds = 100.0;

    /// <summary>Reference width the renderer normalises transition centres against.</summary>
    public const double ReferenceWidth = 1920.0;

    /// <summary>Reference height the renderer normalises transition centres against.</summary>
    public const double ReferenceHeight = 1080.0;

    /// <summary>Upper clamp on a game-capture background's scale factor.</summary>
    public const double MaxCaptureScale = 2.0;

    /// <summary>Below this a capture scale is replaced by the display's pixel density.</summary>
    public const double MinCaptureScale = 0.1;

    /// <summary>Cold-boot animation length, in milliseconds.</summary>
    public const double ColdBootMilliseconds = 6000.0;

    /// <summary>Warm-boot animation length, in milliseconds.</summary>
    public const double WarmBootMilliseconds = 3000.0;

    /// <summary>Welcome-screen fade-out length, in milliseconds.</summary>
    public const double WelcomeFadeOutMilliseconds = 1333.3334;

    /// <summary>Shutdown fade-out length, in milliseconds.</summary>
    public const double ShutdownFadeOutMilliseconds = 3000.0;

    /// <summary>
    /// Light mode the shell picks for a global background state. This is the
    /// without light particles: <see cref="ShellGlobalBackgroundState.NoParticle"/>
    /// is the only state that also selects the home background music.
    /// </summary>
    public static ShellLightRenderMode LightModeFor(ShellGlobalBackgroundState state)
    {
        return state switch
        {
            ShellGlobalBackgroundState.None => ShellLightRenderMode.None,
            ShellGlobalBackgroundState.Black => ShellLightRenderMode.Black,
            ShellGlobalBackgroundState.ColdBootAnimation => ShellLightRenderMode.ColdBoot,
            ShellGlobalBackgroundState.WarmBootAnimation => ShellLightRenderMode.WarmBoot,
            ShellGlobalBackgroundState.InitialBootAnimation => ShellLightRenderMode.Black,
            ShellGlobalBackgroundState.InitialSetup => ShellLightRenderMode.Bottom,
            ShellGlobalBackgroundState.InitialWelcomeScreenAnimation => ShellLightRenderMode.Bottom,
            ShellGlobalBackgroundState.InitialWelcomeScreenFadeOutAnimation =>
                ShellLightRenderMode.InitialWelcomeNoParticle,
            ShellGlobalBackgroundState.Login => ShellLightRenderMode.Bottom,
            ShellGlobalBackgroundState.ParticleBottom => ShellLightRenderMode.Bottom,
            ShellGlobalBackgroundState.ParticleSpread => ShellLightRenderMode.Spread,
            ShellGlobalBackgroundState.NoParticle => ShellLightRenderMode.NoParticle,
            ShellGlobalBackgroundState.Shutdown => ShellLightRenderMode.Spread,
            ShellGlobalBackgroundState.FadeOutShutdownAnimation => ShellLightRenderMode.Black,
            _ => ShellLightRenderMode.None,
        };
    }

    /// <summary>True when a light mode emits particles at all.</summary>
    public static bool EmitsParticles(ShellLightRenderMode mode)
    {
        return mode is ShellLightRenderMode.Bottom
            or ShellLightRenderMode.Spread
            or ShellLightRenderMode.ColdBoot
            or ShellLightRenderMode.WarmBoot
            or ShellLightRenderMode.InitialBoot;
    }

    /// <summary>
    /// Emission pattern a light mode uses. The boot modes are the spreading
    /// burst; sign-in and the steady particle states rise from the bottom edge.
    /// Every mode that does not emit maps to <see cref="ShellParticlePattern.None"/>,
    /// which is exactly the set <see cref="EmitsParticles"/> rejects - the home
    /// screen included.
    /// </summary>
    public static ShellParticlePattern PatternFor(ShellLightRenderMode mode)
    {
        return mode switch
        {
            ShellLightRenderMode.Bottom => ShellParticlePattern.Bottom,
            ShellLightRenderMode.Spread => ShellParticlePattern.Spread,
            ShellLightRenderMode.ColdBoot => ShellParticlePattern.Spread,
            ShellLightRenderMode.WarmBoot => ShellParticlePattern.Spread,
            ShellLightRenderMode.InitialBoot => ShellParticlePattern.Spread,
            _ => ShellParticlePattern.None,
        };
    }

    /// <summary>
    /// Exact emission pattern for a global background state. In particular,
    /// steady HOME maps to <see cref="ShellParticlePattern.None"/>; background
    /// motion there belongs to the separate native wave renderer, not to an
    /// invented particle drift.
    /// </summary>
    /// <param name="state">Global background state.</param>
    public static ShellParticlePattern PatternFor(ShellGlobalBackgroundState state) =>
        PatternFor(LightModeFor(state));

    /// <summary>
    /// Maps the managed light-mode command to the native particle-state call.
    /// This is the jump table at NPXS40087 4.03 <c>0xBB03B8</c>, reached by
    /// dispatcher <c>0x72E60</c>; its state setter call is at <c>0x731EA</c>.
    /// </summary>
    public static Ps5NativeParticleModeRoute NativeParticleRouteFor(
        ShellLightRenderMode mode)
    {
        return mode switch
        {
            ShellLightRenderMode.NoParticle => new((int)mode, null, 1.0f),
            ShellLightRenderMode.InitialWelcomeNoParticle => new((int)mode, null, 0.2f),
            ShellLightRenderMode.Bottom => new((int)mode, 1, 1.0f),
            ShellLightRenderMode.Spread => new((int)mode, 2, 0.3333333433f),
            ShellLightRenderMode.ColdBoot => new((int)mode, 3, 1.0f),
            ShellLightRenderMode.WarmBoot => new((int)mode, 4, 1.0f),
            ShellLightRenderMode.InitialBoot => new((int)mode, 6, 0.3333333433f),
            ShellLightRenderMode.Black => new((int)mode, null, -1.0f),
            ShellLightRenderMode.None => new((int)mode, null, -1.0f),
            _ => new((int)mode, null, -1.0f),
        };
    }

    /// <summary>Exact native particle-state route for a global background state.</summary>
    public static Ps5NativeParticleModeRoute NativeParticleRouteFor(
        ShellGlobalBackgroundState state) =>
        NativeParticleRouteFor(LightModeFor(state));

    /// <summary>
    /// True when a light mode draws a background composite at all.
    /// <see cref="ShellLightRenderMode.Black"/> paints flat black and
    /// <see cref="ShellLightRenderMode.None"/> paints nothing; every other mode
    /// runs the image slots, the wave and, where it has them, the particles.
    /// </summary>
    public static bool DrawsBackground(ShellLightRenderMode mode)
    {
        return mode is not (ShellLightRenderMode.Black or ShellLightRenderMode.None);
    }

    /// <summary>
    /// Advances the wave's opacity by one 60 fps frame. Showing the wave ramps
    /// it up in +0.01 steps to 1; hiding it decays it by x0.9 a frame, which is
    /// a ~0.4 s tail rather than a hard cut.
    /// </summary>
    /// <param name="opacity">Current wave opacity.</param>
    /// <param name="showWave">Whether the wave should be visible.</param>
    public static double AdvanceWaveOpacity(double opacity, bool showWave)
    {
        if (double.IsNaN(opacity))
        {
            opacity = 0.0;
        }

        opacity = Math.Clamp(opacity, 0.0, 1.0);
        return showWave
            ? Math.Min(1.0, opacity + WaveFadeInStep)
            : opacity * WaveFadeOutFactor;
    }

    /// <summary>
    /// Advances the wave's opacity over an elapsed wall-clock interval by
    /// applying <see cref="AdvanceWaveOpacity"/> once per whole 60 fps frame,
    /// happens to be. Leftover time under one frame is dropped; the caller
    /// passes it in again on the next tick.
    /// </summary>
    /// <param name="opacity">Current wave opacity.</param>
    /// <param name="showWave">Whether the wave should be visible.</param>
    /// <param name="milliseconds">Elapsed time to apply; values over 1 s are clamped.</param>
    public static double AdvanceWaveOpacity(double opacity, bool showWave, double milliseconds)
    {
        if (milliseconds <= 0)
        {
            return Math.Clamp(double.IsNaN(opacity) ? 0.0 : opacity, 0.0, 1.0);
        }

        var frames = (int)(Math.Min(milliseconds, 1000.0) / FrameMilliseconds);
        for (var i = 0; i < frames; i++)
        {
            opacity = AdvanceWaveOpacity(opacity, showWave);
        }

        return Math.Clamp(double.IsNaN(opacity) ? 0.0 : opacity, 0.0, 1.0);
    }

    /// <summary>
    /// Builds the mat brush for a basemat type, or null for
    /// <see cref="ShellBasematType.None"/>. <see cref="ShellBasematType.Flat"/>
    /// is the uniform dim used behind modals; <see cref="ShellBasematType.Linear"/>
    /// is a vertical wash; <see cref="ShellBasematType.Ellipse"/> is an
    /// elliptical vignette that keeps the middle of the art clear and darkens
    /// toward the edges. Only the colour and the cross-fade of these are
    /// recovered numbers - the shapes approximate a native effect.
    /// </summary>
    /// <param name="type">Mat shape.</param>
    /// <param name="color">Mat colour; the shell's default is <see cref="BasematColor"/>.</param>
    public static IBrush? CreateBasematBrush(ShellBasematType type, Color color)
    {
        switch (type)
        {
            case ShellBasematType.Flat:
                return new SolidColorBrush(Color.FromArgb(0xB8, color.R, color.G, color.B));

            case ShellBasematType.Linear:
                return new LinearGradientBrush
                {
                    StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                    EndPoint = new Avalonia.RelativePoint(0, 1, Avalonia.RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x2E, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(0x6B, color.R, color.G, color.B), 0.55),
                        new GradientStop(Color.FromArgb(0xCC, color.R, color.G, color.B), 1),
                    },
                };

            case ShellBasematType.Ellipse:
                // Narrow ellipse: taller than it is wide, centred a little above
                // the middle so the card row sits in the clear part of the mat.
                return new RadialGradientBrush
                {
                    Center = new Avalonia.RelativePoint(0.5, 0.42, Avalonia.RelativeUnit.Relative),
                    GradientOrigin = new Avalonia.RelativePoint(0.5, 0.42, Avalonia.RelativeUnit.Relative),
                    RadiusX = new Avalonia.RelativeScalar(0.72, Avalonia.RelativeUnit.Relative),
                    RadiusY = new Avalonia.RelativeScalar(0.95, Avalonia.RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(0x2E, color.R, color.G, color.B), 0.46),
                        new GradientStop(Color.FromArgb(0x8C, color.R, color.G, color.B), 0.78),
                        new GradientStop(Color.FromArgb(0xDB, color.R, color.G, color.B), 1),
                    },
                };

            default:
                return null;
        }
    }
}
