// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Shell;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellBackgroundOwnershipTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void TitleArtSuppressesNativeRoomOnlyWhenDecoded(
        bool hasTitleSelection,
        bool isPlateLoaded,
        bool isTitleArt,
        bool expected)
    {
        Assert.Equal(
            expected,
            ShellBackground.TitleOwnsBackdrop(
                hasTitleSelection,
                isPlateLoaded,
                isTitleArt));
    }

    [Fact]
    public void ColdBootOwnsTheBackgroundEvenWhenInitialTitleArtIsReady()
    {
        Assert.False(ShellBackground.ShouldSuppressNativeParticles(
            ShellGlobalBackgroundState.ColdBootAnimation,
            titleOwnsBackdrop: true));
        Assert.True(ShellBackground.ShouldSuppressNativeParticles(
            ShellGlobalBackgroundState.NoParticle,
            titleOwnsBackdrop: true));
    }
}
