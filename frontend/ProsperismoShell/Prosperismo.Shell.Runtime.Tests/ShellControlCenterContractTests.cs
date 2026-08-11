// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.HLE.Host;
using Prosperismo.HLE.Host.Windows;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellControlCenterContractTests
{
    [Fact]
    public void ExposesRecoveredFunctionRowAndPopupGeometry()
    {
        Assert.Equal(1920, ShellControlCenter.DesignWidth);
        Assert.Equal(1080, ShellControlCenter.DesignHeight);
        Assert.Equal(147, ShellControlCenter.BarHeight);
        Assert.Equal(112, ShellControlCenter.ButtonCellWidth);
        Assert.Equal(56, ShellControlCenter.ButtonHitSize);
        Assert.Equal(48, ShellControlCenter.IconSize);
        Assert.Equal(54, ShellControlCenter.IconTop);
        Assert.Equal(64, ShellControlCenter.IconContainerSize);
        Assert.Equal(20, ShellControlCenter.SecondaryBadgeSize);
        Assert.Equal(-7, ShellControlCenter.SecondaryBadgeTop);
        Assert.Equal(35, ShellControlCenter.SecondaryBadgeLeft);
        Assert.Equal(190, ShellControlCenter.PanelBottom);
        Assert.Equal(56, ShellFunctionPanelMetrics.LeftIconSize);
        Assert.Equal(8, ShellFunctionPanelMetrics.ListBodyMarginHorizontal);
        Assert.Equal(2.0 / 3.0, ShellFunctionPanelMetrics.FocusLineScale);
        Assert.Equal(250, ShellControlCenter.OpenDuration.TotalMilliseconds);
        Assert.Equal(100, ShellControlCenter.CloseDuration.TotalMilliseconds);
    }

    [Fact]
    public void ConsoleTourInventoryUsesExactControlIdsAndBundledArt()
    {
        var ids = ShellControlCenter.ConsoleTourItems.Select(item => item.Id).ToArray();
        Assert.Equal(
            ["home", "apps", "notifications", "gaming-lounge", "music", "sound", "mic", "controller", "profile", "power"],
            ids);

        foreach (var item in ShellControlCenter.ConsoleTourItems)
        {
            Assert.Contains("iconid_" + item.IconId, Ps5BundledIconLibrary.IconIds);
        }

        Assert.Contains("iconid_home", Ps5BundledIconLibrary.IconIds);
        Assert.Contains("iconid_notification_off", Ps5BundledIconLibrary.IconIds);
        Assert.Contains("iconid_new", Ps5BundledIconLibrary.IconIds);
    }

    [Fact]
    public void NotificationFunctionControlProjectsDndAndNewBackendState()
    {
        Assert.Equal("notification", ShellControlCenter.NotificationIconId(false));
        Assert.Equal("notification_off", ShellControlCenter.NotificationIconId(true));
        Assert.False(ShellControlCenter.ShouldShowNotificationNewBadge(0));
        Assert.True(ShellControlCenter.ShouldShowNotificationNewBadge(1));
    }

    [Fact]
    public void SinglePressWrapsButHeldRepeatClampsAtTheEdges()
    {
        Assert.Equal(9, ShellControlCenter.MoveIndex(0, 10, -1, allowEdgeWrap: true));
        Assert.Equal(0, ShellControlCenter.MoveIndex(9, 10, 1, allowEdgeWrap: true));
        Assert.Equal(0, ShellControlCenter.MoveIndex(0, 10, -1, allowEdgeWrap: false));
        Assert.Equal(9, ShellControlCenter.MoveIndex(9, 10, 1, allowEdgeWrap: false));
    }

    [Fact]
    public void PersistedFocusRestoresByControlIdAndFallsBackToHome()
    {
        var controlCenter = new ShellControlCenter
        {
            Items = ShellControlCenter.ConsoleTourItems,
        };

        controlCenter.RestoreSelectedItem("notifications");
        Assert.Equal("notifications", controlCenter.SelectedItem?.Id);

        controlCenter.RestoreSelectedItem("hidden-or-removed-control");
        Assert.Equal("home", controlCenter.SelectedItem?.Id);
    }

    [Fact]
    public void ClosedPanelRejectsBackendListRefresh()
    {
        var controlCenter = new ShellControlCenter();
        controlCenter.UpdateOpenPanelItems(
            "notifications",
            [new ShellFunctionPanelItem("Notification")]);

        Assert.False(controlCenter.IsPanelOpen);
        Assert.Null(controlCenter.PanelOwnerId);
    }

    [Theory]
    [InlineData(0, 84)]
    [InlineData(1800, 1184)]
    [InlineData(720, 450)]
    public void PopupAnchorIsCenteredThenClampedToSystemMargins(double buttonX, double expected)
    {
        Assert.Equal(expected, ShellControlCenter.PopupLeft(buttonX));
    }

    [Fact]
    public void HostStateCarriesShellOwnedPsAndCaptureButtons()
    {
        Assert.NotEqual(HostGamepadButtons.None, HostGamepadButtons.PsButton);
        Assert.NotEqual(HostGamepadButtons.None, HostGamepadButtons.Create);
        Assert.Equal(
            HostGamepadButtons.None,
            HostGamepadButtons.PsButton & HostGamepadButtons.Create);
    }

    [Fact]
    public void DualSenseReportMapsCreateAndPsToShellOwnedButtons()
    {
        Assert.Equal(
            HostGamepadButtons.Create,
            WindowsDualSenseReader.DecodeButtons(8, 0x10, 0));
        Assert.Equal(
            HostGamepadButtons.PsButton,
            WindowsDualSenseReader.DecodeButtons(8, 0, 0x01));
    }

    [Fact]
    public void XInputGuideMapsToShellOwnedPsButton()
    {
        Assert.Equal(
            HostGamepadButtons.PsButton,
            WindowsXInputReader.DecodeButtons(0x0400));
        Assert.Equal(
            HostGamepadButtons.Options | HostGamepadButtons.PsButton,
            WindowsXInputReader.DecodeButtons(0x0410));
    }

    [Fact]
    public void FunctionPanelHeightIncludesRecoveredListBottomMargin()
    {
        Assert.Equal(216, ShellFunctionPanelMetrics.HeightFor(1));
        Assert.Equal(292, ShellFunctionPanelMetrics.HeightFor(2));
        Assert.Equal(810, ShellFunctionPanelMetrics.HeightFor(8));
        Assert.Equal(
            356,
            ShellFunctionPanelMetrics.HeightFor(
            [
                new ShellFunctionPanelItem("Notification 1") { MinHeight = 130 },
                new ShellFunctionPanelItem("Notification 2") { MinHeight = 130 },
            ]));
    }

    [Fact]
    public void FunctionPanelHeightIncludesRecoveredSwitcherSectionBands()
    {
        Assert.Equal(
            498,
            ShellFunctionPanelMetrics.HeightFor(
            [
                new ShellFunctionPanelItem("Active") { SectionHeader = "Active" },
                new ShellFunctionPanelItem("Recent 1") { SectionHeader = "Last played games" },
                new ShellFunctionPanelItem("Recent 2"),
            ]));
    }

    [Fact]
    public void AppSwitcherKeepsActiveFirstAndCapsRecentGamesAtTwo()
    {
        var active = Game("Active", "/games/active/eboot.bin", "PPSA00001");
        var recent1 = Game("Recent 1", "/games/recent1/eboot.bin", "PPSA00002");
        var recent2 = Game("Recent 2", "/games/recent2/eboot.bin", "PPSA00003");
        var ignored = Game("Ignored", "/games/ignored/eboot.bin", "PPSA00004");

        var rows = ShellAppSwitcherComposer.Compose(
            [active, recent1, recent2, ignored],
            active.Path,
            [active.Path, recent1.Path, recent2.Path, ignored.Path],
            StringComparison.Ordinal);

        Assert.Equal(["Active", "Recent 1", "Recent 2"], rows.Select(row => row.Title));
        Assert.Equal("Active", rows[0].SectionHeader);
        Assert.Equal("Last played games", rows[1].SectionHeader);
        Assert.Null(rows[2].SectionHeader);
        Assert.Equal(
            [ShellAppSwitcherSection.Active, ShellAppSwitcherSection.RecentGame,
                ShellAppSwitcherSection.RecentGame],
            rows.Select(row => Assert.IsType<ShellAppSwitcherEntryTag>(row.Tag).Section));
    }

    [Fact]
    public void AppSwitcherActionsFollowFirmwareVocabularyAndHostCapabilities()
    {
        var game = Game("Game", "/games/game/eboot.bin", "PPSA00001");
        var active = ShellAppSwitcherComposer.ComposeOptions(
            game,
            ShellAppSwitcherSection.Active,
            canReturnToRunningGame: true,
            canCloseRunningGame: true,
            canLaunch: false,
            canOpenHub: false);
        var recent = ShellAppSwitcherComposer.ComposeOptions(
            game,
            ShellAppSwitcherSection.RecentGame,
            canReturnToRunningGame: false,
            canCloseRunningGame: false,
            canLaunch: true,
            canOpenHub: true);

        Assert.Equal(["Back to game", "Close game"], active.Select(row => row.Title));
        Assert.Equal(["Play", "Go to game hub"], recent.Select(row => row.Title));
    }

    [Fact]
    public void AppSwitcherLaunchHistoryIsMostRecentFirstDistinctAndBounded()
    {
        var existing = Enumerable.Range(0, ShellAppSwitcherComposer.PersistedHistoryLimit + 4)
            .Select(index => $"/games/{index}/eboot.bin")
            .ToArray();

        var updated = ShellAppSwitcherComposer.RecordLaunch(
            existing,
            existing[3],
            StringComparison.Ordinal);

        Assert.Equal(existing[3], updated[0]);
        Assert.Equal(ShellAppSwitcherComposer.PersistedHistoryLimit, updated.Count);
        Assert.Equal(1, updated.Count(path => path == existing[3]));
    }

    [Fact]
    public void FunctionPanelNavigationSkipsInformationalConfirmationRows()
    {
        var panel = new ShellFunctionPanel
        {
            Items = ShellNotificationPanelComposer.ComposeDeleteAllConfirm(),
        };
        panel.SetSelectedIndex(1);

        panel.MoveFocus(-1);
        Assert.Equal(1, panel.SelectedIndex);
        panel.MoveFocus(1);
        Assert.Equal(2, panel.SelectedIndex);
    }

    [Fact]
    public void OfflineProfilePanelMatchesFirmwareInventoryAndSkipsDisabledStatus()
    {
        var rows = ShellProfilePanelComposer.ComposeOffline();

        Assert.Equal(
            ["Online Status", "Profile", "Trophies", "Switch User", "Log Out"],
            rows.Select(row => row.Title));
        Assert.False(rows[0].IsEnabled);
        Assert.Equal("Offline", rows[0].TrailingText);
        Assert.Equal(ShellProfilePanelComposer.OfflineInitialSelectedIndex, 1);
        Assert.Equal(
            ["person_online", "ps_user", "trophies", "person", "logout"],
            rows.Select(row => row.LeadingIconId));

        foreach (var iconId in rows.Select(row => row.LeadingIconId))
        {
            Assert.Contains("iconid_" + iconId, Ps5BundledIconLibrary.IconIds);
        }

        var panel = new ShellFunctionPanel { Items = rows };
        panel.SetSelectedIndex(ShellProfilePanelComposer.OfflineInitialSelectedIndex);
        panel.MoveFocus(-1);
        Assert.Equal(1, panel.SelectedIndex);
    }

    [Theory]
    [InlineData(ShellPresentationMode.Sony, false, true)]
    [InlineData(ShellPresentationMode.Sony, true, false)]
    [InlineData(ShellPresentationMode.Desktop, false, false)]
    public void ControlCenterToggleIsSonyOnlyAndYieldsToModalPsLock(
        ShellPresentationMode mode,
        bool modalPsLock,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.CanToggleControlCenter(mode, modalPsLock));
    }

    private static GameEntry Game(string name, string path, string titleId) =>
        new(name, titleId, "01.000.000", path, 1, null, null);
}
