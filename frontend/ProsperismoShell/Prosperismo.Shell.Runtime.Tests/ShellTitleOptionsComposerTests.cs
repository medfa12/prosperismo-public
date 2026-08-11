// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Linq;
using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellTitleOptionsComposerTests
{
    [Fact]
    public void IdleTitleComposesOnlyBackedHomeRowsAndNeverAddsPlay()
    {
        var model = ShellTitleOptionsComposer.ComposeHome(FullFacts());

        Assert.Equal(ShellTitleOptionsOwner.HomeTile, model.Owner);
        Assert.DoesNotContain(model.Items, item => item.Label == "Play");
        Assert.Equal(
            [
                ShellTitleOptionActionKind.OpenFolder,
                ShellTitleOptionActionKind.ConfigureGame,
                ShellTitleOptionActionKind.RemoveFromLibrary,
                ShellTitleOptionActionKind.CopyPath,
                ShellTitleOptionActionKind.CopyTitleId,
            ],
            model.Items.Select(item => item.Action));
        Assert.All(model.Items, item => Assert.True(item.IsEnabled));
        Assert.Null(model.Items.Single(item => item.Action == ShellTitleOptionActionKind.OpenFolder).MenuId);
        Assert.Null(model.Items.Single(item => item.Action == ShellTitleOptionActionKind.ConfigureGame).MenuId);
        Assert.Equal(
            ShellTitleOptionMenuIds.ApplicationRemoveFromHome,
            model.Items.Single(item => item.Action == ShellTitleOptionActionKind.RemoveFromLibrary).MenuId);
        Assert.Equal(2, model.Items.Select(item => item.Section).Distinct().Count());
    }

    [Fact]
    public void CloseApplicationCarriesItsSonyIdOnlyForTheRunningSelectedTitle()
    {
        var idle = ShellTitleOptionsComposer.ComposeHome(FullFacts());
        var running = ShellTitleOptionsComposer.ComposeHome(FullFacts(IsSelectedTitleRunning: true));

        Assert.DoesNotContain(idle.Items, item => item.Action == ShellTitleOptionActionKind.CloseApplication);
        var close = Assert.Single(running.Items, item =>
            item.Action == ShellTitleOptionActionKind.CloseApplication);
        Assert.Equal(ShellTitleOptionMenuIds.ApplicationClose, close.MenuId);
        Assert.True(close.IsEnabled);
    }

    [Fact]
    public void MissingTitleIdOmitsTitleIdBoundHostExtensions()
    {
        var model = ShellTitleOptionsComposer.ComposeHome(FullFacts(
            TitleId: null,
            CanConfigureGame: false,
            CanCopyTitleId: false));

        Assert.DoesNotContain(model.Items, item => item.Action == ShellTitleOptionActionKind.ConfigureGame);
        Assert.DoesNotContain(model.Items, item => item.Action == ShellTitleOptionActionKind.CopyTitleId);
    }

    [Fact]
    public void MissingPathOmitsPathBoundActionsButKeepsLocalLibraryOperation()
    {
        var model = ShellTitleOptionsComposer.ComposeHome(FullFacts(
            Path: null,
            CanOpenFolder: false,
            CanCopyPath: false));

        Assert.DoesNotContain(model.Items, item => item.Action == ShellTitleOptionActionKind.OpenFolder);
        Assert.DoesNotContain(model.Items, item => item.Action == ShellTitleOptionActionKind.CopyPath);
        Assert.Contains(model.Items, item => item.Action == ShellTitleOptionActionKind.RemoveFromLibrary);
    }

    [Fact]
    public void HomeAndHubOverflowRemainDifferentOwnersAndPayloads()
    {
        var home = ShellTitleOptionsComposer.ComposeHome(FullFacts());
        var hub = ShellTitleOptionsComposer.ComposeHubOverflow(
            [new ShellGameHubAction(ShellGameHubActionKind.ConfigureGame, "Game settings")]);

        Assert.Equal(ShellTitleOptionsOwner.HomeTile, home.Owner);
        Assert.Equal(ShellTitleOptionsOwner.GameHubOverflow, hub.Owner);
        var hubItem = Assert.Single(hub.Items);
        Assert.Equal(ShellTitleOptionActionKind.ConfigureGame, hubItem.Action);
        Assert.Null(hubItem.MenuId);
        Assert.Equal(0, hubItem.Section);
        Assert.DoesNotContain(hub.Items, item => item.Action == ShellTitleOptionActionKind.RemoveFromLibrary);
    }

    [Fact]
    public void ContextMenuUsesExactlyOneSectionBreakForTheComposedHomeRows()
    {
        var model = ShellTitleOptionsComposer.ComposeHome(FullFacts());
        var menu = new ShellContextMenu();
        menu.SetEntries(model.Items.Select(item => new ShellMenuEntry(item.Label)
        {
            MenuId = item.MenuId,
            Icon = item.Icon,
            Section = item.Section,
            IsEnabled = item.IsEnabled,
        }));

        Assert.Equal(model.Items.Count, menu.Rows.Count);
        Assert.Equal(model.Items.Count + 1, menu.Items.Count);
    }

    [Fact]
    public void LabelOnlyPopupRowsRemoveTheIconGutterAndRouteControllerActivation()
    {
        var activations = 0;
        var menu = new ShellContextMenu();
        menu.SetEntries(
        [
            new ShellMenuEntry("Unavailable")
            {
                IsEnabled = false,
                ShowIconGutter = false,
            },
            new ShellMenuEntry("Play", () => activations++)
            {
                ShowIconGutter = false,
            },
            new ShellMenuEntry("Go to game hub")
            {
                ShowIconGutter = false,
            },
        ]);

        Assert.Equal(1, menu.ControllerSelectedIndex);
        Assert.Equal(0, ShellContextMenu.GutterOf(menu.Rows[1]));
        Assert.True(menu.MoveControllerFocus(1));
        Assert.Equal(2, menu.ControllerSelectedIndex);
        Assert.True(menu.MoveControllerFocus(-1));
        Assert.True(menu.ActivateFromController());
        Assert.Equal(1, activations);
    }

    [Theory]
    [InlineData("/games/a/eboot.bin", "/games/a/eboot.bin", true)]
    [InlineData("/games/a/eboot.bin", "/games/b/eboot.bin", false)]
    [InlineData(null, "/games/a/eboot.bin", false)]
    public void RunningSessionIdentityCannotFollowSelection(string? selectedPath, string? runningPath, bool expected)
    {
        Assert.Equal(expected, ShellTitleOptionsComposer.IsCurrentRunningTitle(
            selectedPath, runningPath, StringComparison.Ordinal));
    }

    private static ShellTitleOptionHostFacts FullFacts(
        string? TitleId = "PPSA00001",
        string? Path = "/games/example/eboot.bin",
        bool CanOpenFolder = true,
        bool CanConfigureGame = true,
        bool CanRemoveFromLibrary = true,
        bool CanCopyPath = true,
        bool CanCopyTitleId = true,
        bool IsSelectedTitleRunning = false) => new(
            TitleId,
            Path,
            CanOpenFolder,
            CanConfigureGame,
            CanRemoveFromLibrary,
            CanCopyPath,
            CanCopyTitleId,
            IsSelectedTitleRunning);
}
