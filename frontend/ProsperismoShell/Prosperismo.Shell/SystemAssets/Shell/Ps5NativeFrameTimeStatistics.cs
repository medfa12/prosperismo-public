// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.SystemAssets.Shell;

internal enum Ps5NativeFrameTimingPhase
{
    Any,
    ColdBoot,
    Ambient,
}

internal readonly record struct Ps5NativeFrameTimeSummary(
    int Samples,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    int OnBudgetSamples,
    double BudgetMilliseconds)
{
    internal double OnBudgetPercent => Samples == 0
        ? 0.0
        : OnBudgetSamples * 100.0 / Samples;

    internal bool Sustained => Samples > 0 && OnBudgetSamples == Samples;
}

/// <summary>
/// Opt-in, bounded timing collector for the complete native background frame.
/// It measures the compute/draw/readback/composite path, not PNG capture or
/// Avalonia layout, and emits one machine-readable summary when full.
/// </summary>
internal sealed class Ps5NativeFrameTimeStatistics
{
    internal const string SampleCountEnvironmentVariable =
        "PROSPERISMO_PS5_FRAME_TIMING_SAMPLES";
    internal const string PhaseEnvironmentVariable =
        "PROSPERISMO_PS5_FRAME_TIMING_PHASE";
    internal const double SixtyHertzBudgetMilliseconds = 1000.0 / 60.0;

    private readonly double[] _samples;
    private readonly Ps5NativeFrameTimingPhase _phase;
    private int _count;
    private bool _reported;

    internal Ps5NativeFrameTimeStatistics(
        int sampleCount,
        Ps5NativeFrameTimingPhase phase = Ps5NativeFrameTimingPhase.Any)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }
        _samples = new double[sampleCount];
        _phase = phase;
    }

    internal static Ps5NativeFrameTimeStatistics? TryCreateFromEnvironment()
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable(SampleCountEnvironmentVariable),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sampleCount) || sampleCount is <= 0 or > 10_000)
        {
            return null;
        }

        var phaseText = Environment.GetEnvironmentVariable(PhaseEnvironmentVariable);
        var phase = phaseText?.Trim().ToLowerInvariant() switch
        {
            null or "" or "any" => Ps5NativeFrameTimingPhase.Any,
            "coldboot" => Ps5NativeFrameTimingPhase.ColdBoot,
            "ambient" => Ps5NativeFrameTimingPhase.Ambient,
            _ => throw new InvalidDataException(
                $"{PhaseEnvironmentVariable} must be any, coldboot, or ambient"),
        };
        return new Ps5NativeFrameTimeStatistics(sampleCount, phase);
    }

    internal Ps5NativeFrameTimeSummary? Record(
        double elapsedSeconds,
        double frameMilliseconds,
        int width,
        int height)
    {
        if (_reported || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0.0 ||
            !double.IsFinite(frameMilliseconds) || frameMilliseconds < 0.0 ||
            !Includes(elapsedSeconds))
        {
            return null;
        }

        _samples[_count++] = frameMilliseconds;
        if (_count != _samples.Length)
        {
            return null;
        }

        _reported = true;
        var summary = Summarize(_samples);
        Console.Error.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"native frame timing summary phase={PhaseName(_phase)} " +
                $"size={width}x{height} samples={summary.Samples} " +
                $"p50={summary.P50Milliseconds:0.00}ms " +
                $"p95={summary.P95Milliseconds:0.00}ms " +
                $"p99={summary.P99Milliseconds:0.00}ms " +
                $"budget={summary.BudgetMilliseconds:0.00}ms " +
                $"on-budget={summary.OnBudgetSamples}/{summary.Samples} " +
                $"({summary.OnBudgetPercent:0.0}%) sustained={summary.Sustained.ToString().ToLowerInvariant()}"));
        return summary;
    }

    internal static Ps5NativeFrameTimeSummary Summarize(IEnumerable<double> samples)
    {
        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0 || ordered.Any(static value =>
                !double.IsFinite(value) || value < 0.0))
        {
            throw new ArgumentException("frame-time samples must be finite and non-negative", nameof(samples));
        }

        return new Ps5NativeFrameTimeSummary(
            ordered.Length,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered.Count(static value => value <= SixtyHertzBudgetMilliseconds),
            SixtyHertzBudgetMilliseconds);
    }

    private bool Includes(double elapsedSeconds) => _phase switch
    {
        Ps5NativeFrameTimingPhase.Any => true,
        Ps5NativeFrameTimingPhase.ColdBoot =>
            elapsedSeconds < Ps5NativeColdBootAmbientTimeline.ColdBootDurationSeconds,
        Ps5NativeFrameTimingPhase.Ambient =>
            elapsedSeconds >= Ps5NativeColdBootAmbientTimeline.ColdBootDurationSeconds,
        _ => false,
    };

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Count) - 1,
            0,
            ordered.Count - 1);
        return ordered[index];
    }

    private static string PhaseName(Ps5NativeFrameTimingPhase phase) => phase switch
    {
        Ps5NativeFrameTimingPhase.Any => "any",
        Ps5NativeFrameTimingPhase.ColdBoot => "coldboot",
        Ps5NativeFrameTimingPhase.Ambient => "ambient",
        _ => "unknown",
    };
}
