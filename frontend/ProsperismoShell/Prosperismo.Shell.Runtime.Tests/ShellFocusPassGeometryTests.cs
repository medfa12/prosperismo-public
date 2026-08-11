// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using Avalonia;
using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellFocusPassGeometryTests
{
    [Fact]
    public void SettingsKeepsTheThinLineOnTheSameRectAsItsAreaShimmer()
    {
        var arrangedRow = new Rect(304, 186, 1312, 152);

        var geometry = ShellFocusRingTimeline.CreatePassGeometry(
            arrangedRow,
            radius: 12,
            showing: 1,
            pressing: 0,
            inOutScale: 1,
            lineScale: ShellSettingsMetrics.FocusLineScale,
            lineMatchesArea: true);

        Assert.Equal(arrangedRow, geometry.AreaFocusRect);
        Assert.Equal(arrangedRow, geometry.LineFocusRect);
        Assert.Equal(new Rect(302.25, 184.25, 1315.5, 155.5), geometry.LineRasterRect);
        Assert.Equal(12, geometry.AreaRadius);
        Assert.Equal(12, geometry.LineRadius);
        Assert.Equal(1.5, geometry.LineBandWidth);
    }

    [Fact]
    public void SettingsLineKeepsTheAnimatedBandAtTargetResolution()
    {
        var raster = ShellFocusBand.ResolveRasterSize(
            new Rect(0, 0, 1316, 108),
            renderAtTargetResolution: true);

        Assert.Equal(1316, raster.Width);
        Assert.Equal(108, raster.Height);
    }

    [Fact]
    public void WideSettingsBandKeepsIndependentHorizontalAndVerticalMargins()
    {
        var body = new Rect(304, 186, 1312, 152);
        var target = body.Inflate(1.75);
        var ratios = ShellFocusBand.ResolveBodyRatios(target, body);

        Assert.Equal(body.Width, ratios.X * target.Width, 6);
        Assert.Equal(body.Height, ratios.Y * target.Height, 6);
        Assert.True(ratios.X > ratios.Y);
    }

    [Fact]
    public void LineFocusKeepsItsRecoveredOutsidePlaneDuringTheInOutScale()
    {
        var geometry = ShellFocusRingTimeline.CreatePassGeometry(
            new Rect(100, 200, 400, 120),
            radius: 0,
            showing: 0,
            pressing: 0,
            inOutScale: 1.2,
            lineScale: 1);

        // (3 px thickness + 3 px offset) * 1.2 = 7.2 px on every side.
        Assert.Equal(new Rect(92.8, 192.8, 414.4, 134.4), geometry.LineFocusRect);
        // The appearance band is twice the settled 3 px thickness.
        Assert.Equal(6, geometry.LineBandWidth);
    }

    [Fact]
    public void FocusMoveRetainsTheRecoveredThreeTenthsAndQuarterSecondTimelines()
    {
        var timeline = new ShellFocusRingTimeline();
        var first = new Rect(0, 0, 100, 80);
        var second = new Rect(400, 0, 100, 80);

        timeline.ShowAt(first, 8);
        timeline.Advance(TimeSpan.FromSeconds(ShellFocusRingTimeline.InMotionDelay));
        Assert.Equal(0, timeline.Showing);
        timeline.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(timeline.Showing > 0);

        timeline.Retarget(second, 8);
        Assert.True(timeline.IsWarping);
        Assert.True(timeline.IsMoving);
        timeline.Advance(TimeSpan.FromMilliseconds(249));
        Assert.True(timeline.IsWarping);
        timeline.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(timeline.IsWarping);
        Assert.True(timeline.IsMoving);
        timeline.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(timeline.IsMoving);
    }

    [Fact]
    public void ShimmerKeepsTheSharedFiveSecondClockAndBeginsItsSweepAfterTheQuietWindow()
    {
        var quiet = ShellFocusPalette.Shimmer(2.999);
        var sweep = ShellFocusPalette.Shimmer(3.5);
        var nextCycle = ShellFocusPalette.Shimmer(8.5);

        // The first (leading) shimmer channel remains parked for the recovered
        // three-second quiet interval. The second is phase-shifted to create
        // the diagonal sweep; both still use the same process-global 5 s clock.
        Assert.Equal(-1, quiet.X, 8);
        Assert.True(sweep.X > -1);
        Assert.Equal(sweep.X, nextCycle.X, 6);
        Assert.Equal(sweep.Y, nextCycle.Y, 6);
    }
}
