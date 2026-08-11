// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Shell;
using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5NativeFrameTimeStatisticsTests
{
    [Fact]
    public void SummaryReportsNearestRankPercentilesAndStrictSixtyHertzBudget()
    {
        var samples = Enumerable.Range(1, 100).Select(static value => (double)value);

        var summary = Ps5NativeFrameTimeStatistics.Summarize(samples);

        Assert.Equal(100, summary.Samples);
        Assert.Equal(50.0, summary.P50Milliseconds);
        Assert.Equal(95.0, summary.P95Milliseconds);
        Assert.Equal(99.0, summary.P99Milliseconds);
        Assert.Equal(16, summary.OnBudgetSamples);
        Assert.False(summary.Sustained);
    }

    [Fact]
    public void PhaseFilterSeparatesColdBootFromRetainedAmbient()
    {
        var ambient = new Ps5NativeFrameTimeStatistics(
            2,
            Ps5NativeFrameTimingPhase.Ambient);

        Assert.Null(ambient.Record(1.0, 5.0, 1920, 1080));
        Assert.Null(ambient.Record(
            Ps5NativeColdBootAmbientTimeline.ColdBootDurationSeconds,
            8.0,
            1920,
            1080));
        var summary = ambient.Record(20.0, 9.0, 1920, 1080);

        Assert.NotNull(summary);
        Assert.Equal(8.0, summary.Value.P50Milliseconds);
        Assert.True(summary.Value.Sustained);
    }
}
