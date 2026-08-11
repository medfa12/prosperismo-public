// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using Prosperismo.GUI;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets.Shell;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class MainWindowBackgroundOwnershipTests
{
    [Fact]
    public void MainWindowHasOneCompiledShellBackgroundOwner()
    {
        var backgrounds = typeof(MainWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => field.FieldType == typeof(ShellBackground))
            .ToArray();

        var owner = Assert.Single(backgrounds);
        Assert.Equal("HomePlate", owner.Name);
        Assert.Null(typeof(MainWindow).GetField(
            "SettingsBackground",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

    [Theory]
    [InlineData(ShellPresentationMode.Sony, 0, true)]
    [InlineData(ShellPresentationMode.Sony, 1, true)]
    [InlineData(ShellPresentationMode.Desktop, 0, false)]
    [InlineData(ShellPresentationMode.Desktop, 1, false)]
    [InlineData(ShellPresentationMode.Sony, 2, false)]
    public void PersistentOwnerStaysMountedAcrossSonyHomeAndSettings(
        ShellPresentationMode presentationMode,
        int pageIndex,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.PersistentBackgroundIsVisibleFor(presentationMode, pageIndex));
    }

    [Fact]
    public void SettingsRouteHasNoIndependentClockOrBackgroundSurface()
    {
        var fields = typeof(MainWindow).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.Contains(fields, field => field.Name == "ShellBackgroundSurfaceHost");
        Assert.Contains(fields, field => field.Name == "SonySettingsSurfaceHost");
        Assert.Single(fields, field => field.FieldType == typeof(ShellBackground));
    }

    [Theory]
    [InlineData(ShellPresentationMode.Desktop, 0, true, false)]
    [InlineData(ShellPresentationMode.Desktop, 1, false, false)]
    [InlineData(ShellPresentationMode.Sony, 0, false, true)]
    [InlineData(ShellPresentationMode.Sony, 1, false, false)]
    public void DesktopAndSonyLibrarySurfacesAreMutuallyExclusive(
        ShellPresentationMode presentationMode,
        int pageIndex,
        bool desktopLibraryExpected,
        bool sonyHomeExpected)
    {
        Assert.Equal(
            desktopLibraryExpected,
            MainWindow.DesktopLibrarySurfaceIsVisibleFor(presentationMode, pageIndex));
        Assert.Equal(
            sonyHomeExpected,
            MainWindow.SonyHomeSurfaceIsVisibleFor(presentationMode, pageIndex));
    }

    [Theory]
    [InlineData(ShellPresentationMode.Desktop, false)]
    [InlineData(ShellPresentationMode.Sony, true)]
    public void OnlySonyPresentationOwnsAmbientBackgroundAndMusic(
        ShellPresentationMode presentationMode,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.UsesSonyAmbientFor(presentationMode));
    }

    [Theory]
    [InlineData(ShellPresentationMode.Sony, false, false)]
    [InlineData(ShellPresentationMode.Sony, true, true)]
    [InlineData(ShellPresentationMode.Desktop, false, true)]
    [InlineData(ShellPresentationMode.Desktop, true, true)]
    public void ColdBootExclusivelyBlocksSonyUiState(
        ShellPresentationMode presentationMode,
        bool sonyShellReady,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShellUiIsReadyFor(presentationMode, sonyShellReady));
    }

    [Theory]
    [InlineData(ShellPresentationMode.Desktop, 0, true)]
    [InlineData(ShellPresentationMode.Desktop, 1, false)]
    [InlineData(ShellPresentationMode.Sony, 0, false)]
    [InlineData(ShellPresentationMode.Sony, 1, false)]
    public void OnlyDesktopLibraryRestoresDesktopFocus(
        ShellPresentationMode presentationMode,
        int pageIndex,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.RestoresDesktopLibraryFocusFor(presentationMode, pageIndex));
    }
}
