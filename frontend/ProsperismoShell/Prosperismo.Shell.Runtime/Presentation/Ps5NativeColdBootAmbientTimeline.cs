// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Libs.Presentation;

/// <summary>One native particle-pattern instance active at a global time.</summary>
public readonly record struct Ps5NativePatternInstanceState(
    int Instance,
    int CurrentInstance,
    int Selector,
    double LocalSeconds)
{
    public uint TransitionPatternFlag =>
        checked((uint)(Instance | (CurrentInstance << 4)));
}

public readonly record struct Ps5NativeColorCbBlend(
    Npxs40087ColorCbContract From,
    Npxs40087ColorCbContract To,
    float TargetWeight);

/// <summary>
/// Accepted NPXS40087 coldboot-to-ambient orchestration used by the live
/// records; this class only preserves the verified two-instance ownership
/// schedule and never wraps elapsed time.
/// </summary>
public static class Ps5NativeColdBootAmbientTimeline
{
    public const int ColdBootSelector = 0;
    public const int AmbientSelector = 1;
    // BackgroundLayer.ColdBootDurationTick is 60,000,000 100 ns ticks.
    // NPXS40087's coldboot pattern itself is authored over 0..8.5. Console
    // validation reaches the authored 6.5-second colour/particle action at
    // four managed seconds; the remaining authored interval then runs
    // one-to-one through the six-second selector hand-off and retained HOME.
    public static readonly double ColdBootDurationSeconds = Npxs40087ShellContract.Ambient.ManagedColdBootSeconds;
    // NPXS40087's constructor and reset path write 10.0f to the native light
    // object's +0xCC field. The accepted POC and direct-console cold-boot
    // phase align at a zero-origin light_p presentation clock, so preserve both
    // facts rather than importing the object seed as a visible phase offset.
    public static readonly double FirmwareInitialLightClockSeconds =
        Npxs40087ShellContract.Ambient.FirmwareInitialLightClockSeconds;
    public static readonly double PresentationLightClockOriginSeconds =
        Npxs40087ShellContract.Ambient.PresentationLightClockOriginSeconds;
    public static readonly double LightPaletteTransitionSeconds =
        Npxs40087ShellContract.Ambient.LightPaletteTransitionSeconds;
    public static readonly double ManagedPatternActionSeconds =
        Npxs40087ShellContract.Ambient.ManagedPatternActionSeconds;
    public static readonly double PatternActionSeconds =
        Npxs40087ShellContract.Ambient.AuthoredPatternActionSeconds;
    public static readonly double ManagedHomeLightTransitionSeconds =
        Npxs40087ShellContract.Ambient.ManagedHomeLightTransitionSeconds;
    public static readonly double PatternActionEndSeconds =
        Npxs40087ShellContract.Ambient.AuthoredPatternActionEndSeconds;
    public static readonly double ParticleTransitionSeconds =
        Npxs40087ShellContract.Ambient.AuthoredSelectorTransitionSeconds;
    public static readonly double PreviousInstanceReleaseSeconds =
        Npxs40087ShellContract.Ambient.AuthoredPreviousInstanceReleaseSeconds;

    /// <summary>
    /// Replays the light-record lifecycle around the managed cold-boot gate.
    /// Clearing that bit when the timed renderer starts selects Login preset 9;
    /// the product's skipped-login bridge then applies preset 4 only after the
    /// authored large-particle action ends. Every change uses the native 300 ms
    /// quartic ease-out rather than a host-side hard cut.
    /// </summary>
    public static Ps5NativeColorCbBlend PaletteBlendAtElapsed(
        double elapsedSeconds,
        bool startsFromColdBoot)
    {
        Validate(elapsedSeconds);
        if (!startsFromColdBoot)
        {
            return Fixed(Npxs40087ShellContract.HomePalette);
        }

        if (elapsedSeconds < LightPaletteTransitionSeconds)
        {
            return Transition(
                Npxs40087ShellContract.BootPalette,
                Npxs40087ShellContract.LoginPalette,
                elapsedSeconds);
        }

        if (elapsedSeconds < ManagedHomeLightTransitionSeconds)
        {
            return Fixed(Npxs40087ShellContract.LoginPalette);
        }

        var homeElapsed = elapsedSeconds - ManagedHomeLightTransitionSeconds;
        return homeElapsed < LightPaletteTransitionSeconds
            ? Transition(
                Npxs40087ShellContract.LoginPalette,
                Npxs40087ShellContract.HomePalette,
                homeElapsed)
            : Fixed(Npxs40087ShellContract.HomePalette);

        static Ps5NativeColorCbBlend Fixed(Npxs40087ColorCbContract palette) =>
            new(palette, palette, 1.0f);

        static Ps5NativeColorCbBlend Transition(
            Npxs40087ColorCbContract from,
            Npxs40087ColorCbContract to,
            double elapsed)
        {
            var linear = Math.Clamp(elapsed / LightPaletteTransitionSeconds, 0.0, 1.0);
            var remaining = 1.0 - linear;
            var eased = 1.0 - (remaining * remaining * remaining * remaining);
            return new(from, to, (float)eased);
        }
    }

    public static double NativeSecondsAtElapsed(double elapsedSeconds)
    {
        Validate(elapsedSeconds);
        if (elapsedSeconds <= ManagedPatternActionSeconds)
        {
            return elapsedSeconds *
                (PatternActionSeconds / ManagedPatternActionSeconds);
        }

        if (elapsedSeconds <= ColdBootDurationSeconds)
        {
            return PatternActionSeconds +
                ((elapsedSeconds - ManagedPatternActionSeconds) *
                 ((ParticleTransitionSeconds - PatternActionSeconds) /
                  (ColdBootDurationSeconds - ManagedPatternActionSeconds)));
        }

        return ParticleTransitionSeconds +
            (elapsedSeconds - ColdBootDurationSeconds);
    }

    /// <summary>Returns the 60 Hz simulation step in the authored pattern domain.</summary>
    public static long ResourceStepAtElapsed(double elapsedSeconds)
    {
        Validate(elapsedSeconds);
        return ResourceStepAtNativeSeconds(NativeSecondsAtElapsed(elapsedSeconds));
    }

    /// <summary>Returns the 60 Hz simulation step for an authored clock value.</summary>
    public static long ResourceStepAtNativeSeconds(double nativeSeconds)
    {
        Validate(nativeSeconds);
        return checked((long)Math.Floor(
            (nativeSeconds * Npxs40087ShellContract.Ambient.ResourceStepHertz) + 1e-9));
    }

    /// <summary>The authored selector time sampled by one simulation step.</summary>
    public static double NativeSecondsAtResourceStep(long resourceStep)
    {
        if (resourceStep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceStep));
        }

        return resourceStep /
            (double)Npxs40087ShellContract.Ambient.ResourceStepHertz;
    }

    /// <summary>
    /// The zero-origin <c>light_p</c> clock in the same authored domain as the
    /// particle pattern. The accepted native POC submits its recovered frame
    /// time to both paths; using managed wall time here delayed the blue room's
    /// authored folds until after the particle colour change.
    /// </summary>
    public static double LightSecondsAtElapsed(double elapsedSeconds)
    {
        Validate(elapsedSeconds);
        return PresentationLightClockOriginSeconds + NativeSecondsAtElapsed(elapsedSeconds);
    }

    public static bool IsBeforePatternActionAtElapsed(double elapsedSeconds) =>
        IsBeforePatternAction(NativeSecondsAtElapsed(elapsedSeconds));

    public static IReadOnlyList<Ps5NativePatternInstanceState> SampleElapsed(
        double elapsedSeconds) =>
        Sample(NativeSecondsAtElapsed(elapsedSeconds));

    public static bool IsBeforePatternAction(double elapsedSeconds)
    {
        Validate(elapsedSeconds);
        return elapsedSeconds < PatternActionSeconds;
    }

    public static IReadOnlyList<Ps5NativePatternInstanceState> Sample(double elapsedSeconds)
    {
        Validate(elapsedSeconds);
        if (elapsedSeconds < ParticleTransitionSeconds)
        {
            return
            [
                new Ps5NativePatternInstanceState(
                    Instance: 0,
                    CurrentInstance: 0,
                    Selector: ColdBootSelector,
                    LocalSeconds: elapsedSeconds),
            ];
        }

        var incoming = new Ps5NativePatternInstanceState(
            Instance: 1,
            CurrentInstance: 1,
            Selector: AmbientSelector,
            LocalSeconds: elapsedSeconds - ParticleTransitionSeconds);
        if (elapsedSeconds <= PreviousInstanceReleaseSeconds)
        {
            return
            [
                new Ps5NativePatternInstanceState(
                    Instance: 0,
                    CurrentInstance: 1,
                    Selector: ColdBootSelector,
                    LocalSeconds: elapsedSeconds),
                incoming,
            ];
        }

        return [incoming];
    }

    private static void Validate(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }
    }
}
