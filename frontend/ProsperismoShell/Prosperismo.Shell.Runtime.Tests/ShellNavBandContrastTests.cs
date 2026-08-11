// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellNavBandContrastTests
{
    [Fact]
    public void MainMenuIconDarkensProgressivelyWithFocusDisc()
    {
        var restScale = ShellNavBand.SystemIconSizeNoGlance / ShellNavBand.SystemIconSize;
        var middleScale = (restScale + 1.0) * 0.5;

        var rest = ShellNavBand.FocusedIconChannelForScale(restScale);
        var middle = ShellNavBand.FocusedIconChannelForScale(middleScale);
        var focused = ShellNavBand.FocusedIconChannelForScale(1.0);

        Assert.Equal((byte)255, rest);
        Assert.InRange(middle, (byte)42, (byte)254);
        Assert.Equal((byte)41, focused);
        Assert.True(
            ShellNavBand.FocusBackgroundOpacityForScale(restScale) <
            ShellNavBand.FocusBackgroundOpacityForScale(middleScale));
        Assert.True(
            ShellNavBand.FocusBackgroundOpacityForScale(middleScale) <
            ShellNavBand.FocusBackgroundOpacityForScale(1.0));
    }
}
