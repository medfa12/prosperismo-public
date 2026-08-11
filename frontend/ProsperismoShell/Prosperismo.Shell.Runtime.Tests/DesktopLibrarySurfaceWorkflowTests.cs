// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Interactivity;
using Prosperismo.GUI;
using Prosperismo.GUI.Controls;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class DesktopLibrarySurfaceWorkflowTests
{
    [Fact]
    public void DesktopActionSurfaceForwardsTheExistingLauncherCommands()
    {
        AvaloniaBitmapTestHost.EnsureInitialized();

        var surface = new DesktopLibrarySurface();
        var game = new GameEntry(
            "Workflow game",
            "CUSA00001",
            "01.000.000",
            Path.Combine(Path.GetTempPath(), "workflow-game", "eboot.bin"),
            0,
            null,
            null);
        surface.Games.ItemsSource = new[] { game };
        surface.Games.SelectedItem = game;
        surface.IsGameSettingsEnabled = true;
        surface.IsClearCustomSettingsEnabled = true;

        var globalSettings = 0;
        var addFolder = 0;
        var rescan = 0;
        var openFile = 0;
        var bigPicture = 0;
        var editSettings = 0;
        var clearSettings = 0;
        var launches = new List<GameEntry>();

        surface.GlobalSettingsRequested += (_, _) => globalSettings++;
        surface.AddFolderRequested += (_, _) => addFolder++;
        surface.RescanRequested += (_, _) => rescan++;
        surface.OpenFileRequested += (_, _) => openFile++;
        surface.BigPictureRequested += (_, _) => bigPicture++;
        surface.EditCustomSettingsRequested += (_, e) =>
        {
            editSettings++;
            Assert.Same(game, e.Game);
        };
        surface.ClearCustomSettingsRequested += (_, e) =>
        {
            clearSettings++;
            Assert.Same(game, e.Game);
        };
        surface.LaunchRequested += (_, e) => launches.Add(e.Game);

        RaiseClick(surface.GlobalSettingsButton);
        RaiseClick(surface.AddFolderButton);
        RaiseClick(surface.RescanButton);
        RaiseClick(surface.OpenFileButton);
        RaiseClick(surface.BigPictureButton);
        RaiseClick(surface.GameSettingsButton);
        RaiseClick(surface.ClearCustomSettingsButton);

        Assert.Equal(1, globalSettings);
        Assert.Equal(1, addFolder);
        Assert.Equal(1, rescan);
        Assert.Equal(1, openFile);
        Assert.Equal(1, bigPicture);
        Assert.Equal(1, editSettings);
        Assert.Equal(1, clearSettings);

        // The context-menu dispatcher is the production path used by Run and
        // the per-title tools. Exercise every existing command so a later UI
        // restyle cannot silently drop one of the Qt launcher operations.
        var contextActions = new List<DesktopLibraryContextAction>();
        surface.ContextActionRequested += (_, e) => contextActions.Add(e.Action);
        var dispatcher = typeof(DesktopLibrarySurface).GetMethod(
            "RaiseContextAction",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(dispatcher);

        foreach (var action in Enum.GetValues<DesktopLibraryContextAction>())
        {
            dispatcher!.Invoke(surface, new object[] { game, action });
        }

        Assert.Equal(Enum.GetValues<DesktopLibraryContextAction>(), contextActions);
        var launch = Assert.Single(launches);
        Assert.Same(game, launch);
    }

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
