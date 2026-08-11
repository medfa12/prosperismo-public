// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>One decoded <c>BackgroundLayer::ResourcesCs</c> state.</summary>
public readonly record struct Ps5ParticleComputeState(
    int NumParticles,
    double ParticleMaxAcceleration1,
    double ParticleCurlSpeedP);

/// <summary>One exact 36-byte <c>ParticleRendezVousParam</c>.</summary>
public readonly record struct Ps5ParticleRendezvousState(
    Vector3 Center,
    Vector3 Weight,
    double BeginDistance,
    double EndDistance,
    double Acceleration);

/// <summary>Decoded coldboot state for one large-particle compute bank.</summary>
public readonly record struct Ps5LargeParticleComputeState(
    int NumParticles,
    int MaxParticleId,
    int OffsetParticle,
    int IndexStridePerParticle,
    double ParticleMinLife,
    double ParticleMaxLife,
    Vector3 ParticleSpawnRangeMax,
    Vector3 ParticleSpawnRangeMin,
    double ParticleMaxAcceleration1,
    Vector3 ParticleCurlSizeP,
    double ParticleCurlSpeedP,
    double ParticleCurlTimeRateP,
    int NumRendezvousPoints,
    Ps5ParticleRendezvousState Rendezvous0,
    Ps5ParticleRendezvousState Rendezvous1);

/// <summary>One decoded <c>ResourcesLargeParticleVsPs</c> state.</summary>
public readonly record struct Ps5LargeParticleDrawState(
    int NumParticles,
    double ParticleColorValue,
    double Transparency,
    double ParMinSize,
    double ParMaxSize);

/// <summary>The coldboot resource values consumed by one rendered frame.</summary>
public readonly record struct Ps5ColdBootParticleFrame(
    double NativeTime,
    Ps5ParticleComputeState Small0,
    Ps5ParticleComputeState Small1,
    Ps5LargeParticleComputeState Large0Compute,
    Ps5LargeParticleComputeState Large1Compute,
    Ps5LargeParticleDrawState Large0,
    Ps5LargeParticleDrawState Large1);

/// <summary>
/// Executable form of the decoded 4.03 <c>coldboot</c> particle records.
/// performs the same clamped interpolation as the native evaluator.
/// </summary>
public static class Ps5ColdBootParticleTimeline
{
    public const double NativeDuration = 8.5;

    /// <summary>
    /// Samples the native 0..8.5 authoring domain at a normalised boot progress.
    /// The native end key is aligned with <see cref="BootIntroTimeline.TotalDuration"/>.
    /// </summary>
    public static Ps5ColdBootParticleFrame SampleAtProgress(double progress) =>
        SampleNativeTime(Math.Clamp(progress, 0.0, 1.0) * NativeDuration);

    /// <summary>Samples the decoded resource state at a native authoring time.</summary>
    public static Ps5ColdBootParticleFrame SampleNativeTime(double nativeTime)
    {
        var time = Math.Clamp(nativeTime, 0.0, NativeDuration);

        // Field 13 establishes the small-compute resources at t=0. Field 8
        // activates two banks at t=6.5. Field 18 is evaluated only once the
        // frame overlaps its 7.25..7.5 interval; before that its start value
        // does not overwrite the direct state.
        var acceleration = time < 7.25
            ? 8.5
            : LerpClamped(8.5, 0.01, 7.25, 7.5, time);
        var curlSpeed = time < 7.25
            ? 6.2
            : LerpClamped(1.2, 0.01, 7.25, 7.5, time);
        var small0 = new Ps5ParticleComputeState(time < 6.5 ? 0 : 400, acceleration, curlSpeed);
        var small1 = new Ps5ParticleComputeState(time < 6.5 ? 0 : 1600, acceleration, curlSpeed);

        // Fields 15 and 10 drive the two large-compute banks. Both are shut
        // down by field 10's final direct event at t=8.5.
        var largeStopped = time >= 8.5;
        var large0Compute = new Ps5LargeParticleComputeState(
            NumParticles: largeStopped ? 0 : 4,
            MaxParticleId: 6000,
            OffsetParticle: 0,
            IndexStridePerParticle: 1,
            ParticleMinLife: 8.0,
            ParticleMaxLife: 8.0,
            ParticleSpawnRangeMax: new Vector3(1.2f, 1.2f, 0.8f),
            ParticleSpawnRangeMin: new Vector3(-0.5f, -1.2f, 0.5f),
            ParticleMaxAcceleration1: 0.05,
            ParticleCurlSizeP: Vector3.Zero,
            ParticleCurlSpeedP: 0.05,
            ParticleCurlTimeRateP: 0.0,
            NumRendezvousPoints: 1,
            Rendezvous0: new Ps5ParticleRendezvousState(
                Vector3.Zero,
                new Vector3(0.70710677f, 0.70710677f, 0.0f),
                0.0,
                1.0,
                10.0),
            Rendezvous1: default);

        var large1Active = time >= 6.0;
        var large1Compute = new Ps5LargeParticleComputeState(
            NumParticles: largeStopped || !large1Active ? 0 : 40,
            MaxParticleId: 6000,
            OffsetParticle: 100,
            IndexStridePerParticle: 1,
            ParticleMinLife: 2.0,
            ParticleMaxLife: 2.0,
            ParticleSpawnRangeMax: new Vector3(40.0f, 40.0f, 40.0f),
            ParticleSpawnRangeMin: new Vector3(-40.0f, -30.0f, 30.0f),
            ParticleMaxAcceleration1: large1Active ? 1000.0 : 0.02,
            ParticleCurlSizeP: large1Active ? new Vector3(1.6f) : Vector3.Zero,
            ParticleCurlSpeedP: large1Active ? 1000.0 : 0.0,
            ParticleCurlTimeRateP: large1Active ? 0.06 : 0.0,
            NumRendezvousPoints: large1Active ? 1 : 0,
            Rendezvous0: large1Active
                ? new Ps5ParticleRendezvousState(
                    new Vector3(0.0f, 0.0f, 50.0f),
                    new Vector3(0.6396021f, 0.6396021f, 0.42640144f),
                    0.0,
                    100.0,
                    -4000.0)
                : default,
            Rendezvous1: large1Active
                ? new Ps5ParticleRendezvousState(
                    new Vector3(0.0f, 0.0f, 30.0f),
                    new Vector3(0.70710677f, 0.70710677f, 0.0f),
                    0.0,
                    40.0,
                    -4000.0)
                : default);

        // Field 21 destinations 0 and 1. The direct field-16 records establish
        // the initial values; the interpolated records below mutate them.
        var value0 = time <= 2.5
            ? LerpClamped(0.0, 0.02, 0.0, 2.5, time)
            : LerpClamped(0.02, 0.1, 2.5, 7.5, time);
        var large0 = new Ps5LargeParticleDrawState(
            largeStopped ? 0 : 4,
            value0,
            LerpClamped(0.6, 0.0, 6.5, 7.0, time),
            LerpClamped(1.0, 1.5, 0.0, 8.0, time),
            LerpClamped(1.4, 2.1, 0.0, 8.0, time));

        var large1State = Ps5ColdBootLargeDraw1Evaluator.Sample(time);
        var large1 = new Ps5LargeParticleDrawState(
            large1State.NumParticles,
            1.0,
            large1State.Transparency,
            large1State.ParMinSize,
            large1State.ParMaxSize);

        return new Ps5ColdBootParticleFrame(
            time,
            small0,
            small1,
            large0Compute,
            large1Compute,
            large0,
            large1);
    }

    private static double LerpClamped(double from, double to, double start, double end, double at)
    {
        var t = Math.Clamp((at - start) / (end - start), 0.0, 1.0);
        return from + ((to - from) * t);
    }
}
