// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellSearchContractTests
{
    [Fact]
    public void UsesRecoveredSearchFrame()
    {
        Assert.Equal(126, ShellSearchSurface.ContainerLeft);
        Assert.Equal(134, ShellSearchSurface.ResultsTop);
        Assert.Equal(1576, ShellSearchMetrics.ContentWidth);
        Assert.Equal(72, ShellSearchMetrics.InputHeight);
        Assert.Equal(4, ShellSearchMetrics.Columns);
        Assert.Equal(370, ShellSearchMetrics.TileWidth);
        Assert.Equal(8, ShellSearchMetrics.ItemsPerStrand);
    }

    [Fact]
    public void LocalProjectionCapsAtEightAndNavigatesInputAndGrid()
    {
        var search = new ShellSearchSurface();
        search.SetItems(Enumerable.Range(1, 10)
            .Select(index => new ShellSearchItem($"Game {index}"))
            .ToArray());

        Assert.Equal(8, search.Count);
        Assert.Equal(-1, search.SelectedIndex);
        search.MoveVertical(1);
        Assert.Equal(0, search.SelectedIndex);
        search.MoveHorizontal(1);
        Assert.Equal(1, search.SelectedIndex);
        search.MoveVertical(1);
        Assert.Equal(5, search.SelectedIndex);
        search.MoveVertical(-1);
        Assert.Equal(1, search.SelectedIndex);
        search.MoveVertical(-1);
        Assert.Equal(-1, search.SelectedIndex);
    }
}
