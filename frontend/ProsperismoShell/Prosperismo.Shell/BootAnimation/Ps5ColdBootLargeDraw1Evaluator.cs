// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// Exact routed state of coldboot's second
/// <c>ResourcesLargeParticleVsPs</c> bank.
/// </summary>
public readonly record struct Ps5ColdBootLargeDraw1State(
    double NativeTime,
    int NumParticles,
    double Transparency,
    double ParMinSize,
    double ParMaxSize);

/// <summary>
/// Executes the decoded field-16 initialization and field-21 curves which
/// mutate coldboot <c>large_draw[1]</c>. This is a resource event player, not a
/// particle renderer: it deliberately has no position or sprite geometry.
/// </summary>
public sealed class Ps5ColdBootLargeDraw1Evaluator
{
    public const double CountStart = 6.0;
    public const double CountEnd = 7.5;
    public const double FadeInEnd = 6.5;
    public const double SizeStart = 6.5;

    // Exact f32 promoted to double from field 21 / event 6.
    public const double SizeEnd = 6.900000095367432;

    public const double FadeOutStart = 7.0;
    public const double FadeOutEnd = 7.5;
    public const double ShutdownTime = 8.5;

    public Ps5ColdBootLargeDraw1Evaluator() => Reset();

    public Ps5ColdBootLargeDraw1State State { get; private set; }

    public void Reset() => State = new Ps5ColdBootLargeDraw1State(
        0.0,
        NumParticles: 0,
        Transparency: 0.0,
        ParMinSize: 15.0,
        ParMaxSize: 30.0);

    /// <summary>
    /// Advances the event player to an authored pattern time. Going backward
    /// reconstructs from the field-16 initialization.
    /// </summary>
    public Ps5ColdBootLargeDraw1State AdvanceTo(double nativeTime)
    {
        nativeTime = Math.Clamp(nativeTime, 0.0, ShutdownTime);
        if (nativeTime < State.NativeTime)
        {
            Reset();
        }

        var previous = State.NativeTime;
        var count = State.NumParticles;
        var transparency = State.Transparency;
        var minSize = State.ParMinSize;
        var maxSize = State.ParMaxSize;

        if (Overlaps(previous, nativeTime, CountStart, CountEnd))
        {
            // Native opcode 2 uses vcvttsd2si: truncate toward zero.
            count = (int)Interpolate(20.0, 40.0, CountStart, CountEnd, nativeTime);
        }

        if (Overlaps(previous, nativeTime, CountStart, FadeInEnd))
        {
            transparency = Interpolate(0.0, 0.1, CountStart, FadeInEnd, nativeTime);
        }

        if (Overlaps(previous, nativeTime, SizeStart, SizeEnd))
        {
            minSize = Interpolate(15.0, 0.0, SizeStart, SizeEnd, nativeTime);
            maxSize = Interpolate(30.0, 0.0, SizeStart, SizeEnd, nativeTime);
        }

        if (Overlaps(previous, nativeTime, FadeOutStart, FadeOutEnd))
        {
            transparency = Interpolate(0.1, 0.0, FadeOutStart, FadeOutEnd, nativeTime);
        }

        if (previous <= ShutdownTime && nativeTime >= ShutdownTime)
        {
            count = 0;
        }

        State = new Ps5ColdBootLargeDraw1State(
            nativeTime,
            count,
            transparency,
            minSize,
            maxSize);
        return State;
    }

    public static Ps5ColdBootLargeDraw1State Sample(double nativeTime)
    {
        var evaluator = new Ps5ColdBootLargeDraw1Evaluator();
        return evaluator.AdvanceTo(nativeTime);
    }

    private static bool Overlaps(
        double previous,
        double current,
        double start,
        double end) => current >= start && previous <= end;

    private static double Interpolate(
        double from,
        double to,
        double start,
        double end,
        double at)
    {
        var amount = Math.Clamp((at - start) / (end - start), 0.0, 1.0);
        return from + ((to - from) * amount);
    }
}
