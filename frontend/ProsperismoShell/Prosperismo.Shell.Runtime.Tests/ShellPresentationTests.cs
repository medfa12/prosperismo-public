// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Ps5Home;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellPresentationTests
{
    [Fact]
    public void OrdinaryInvocationSelectsTheDesktopLauncher()
    {
        Assert.False(ShellPresentation.TryParseLaunchArguments([], out var mode));
        Assert.Equal(ShellPresentationMode.Desktop, mode);
    }

    [Theory]
    [InlineData("--big-picture")]
    [InlineData("--sony-ui")]
    [InlineData("--ui=sony")]
    public void ExplicitBigPictureFormsSelectSony(string argument)
    {
        Assert.True(ShellPresentation.TryParseLaunchArguments([argument], out var mode));
        Assert.Equal(ShellPresentationMode.Sony, mode);
    }

    [Fact]
    public void UnsupportedLegacyBigPictureSpellingDoesNotSelectSony()
    {
        Assert.False(ShellPresentation.TryParseLaunchArguments(["-bigpicture"], out var mode));
        Assert.Equal(ShellPresentationMode.Desktop, mode);
    }

    [Theory]
    [InlineData(ShellPresentationMode.Desktop, true)]
    [InlineData(ShellPresentationMode.Sony, false)]
    public void DesktopVisualTreatmentIsPresentationScoped(
        ShellPresentationMode mode,
        bool expected)
    {
        Assert.Equal(expected, ShellPresentation.UsesDesktopLauncherVisuals(mode));
    }

    [Fact]
    public void BigPictureHandoffRestartsTheSameExecutableWithAnExplicitRoute()
    {
        var startInfo = ShellPresentation.CreateBigPictureRestartStartInfo("/tmp/Prosperismo");

        Assert.Equal("/tmp/Prosperismo", startInfo.FileName);
        Assert.Equal("/tmp", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(["--big-picture"], startInfo.ArgumentList);
    }

    [Fact]
    public void DesktopHandoffRestartsTheSameExecutableWithAnExplicitRoute()
    {
        var startInfo = ShellPresentation.CreateDesktopRestartStartInfo("/tmp/Prosperismo");

        Assert.Equal("/tmp/Prosperismo", startInfo.FileName);
        Assert.Equal("/tmp", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(["--desktop-ui"], startInfo.ArgumentList);
    }
}
