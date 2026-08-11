// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class DesktopLauncherMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "prosperismo-desktop-metadata-tests", Guid.NewGuid().ToString("N"));

    public DesktopLauncherMetadataTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void QtStatusDisplayDataMatchesTheCompatibilityIndicator()
    {
        Assert.Equal(
            new GameStatusDisplay("Unknown", "#8a8a8a"),
            GameStatusInfo.GetDisplay(GameStatus.Unknown));
        Assert.Equal("Main menu", GameStatusInfo.GetDisplayName(GameStatus.MainMenu));
        Assert.Equal("#2fb344", GameStatusInfo.GetColor(GameStatus.InGame));
        Assert.Equal("#f2c94c", GameStatusInfo.GetColor(GameStatus.Logo));
        Assert.Equal("#e55353", GameStatusInfo.GetColor(GameStatus.DoesntBoot));
        Assert.Equal(
            new[]
            {
                GameStatus.Unknown,
                GameStatus.MainMenu,
                GameStatus.InGame,
                GameStatus.Logo,
                GameStatus.DoesntBoot,
            },
            GameStatusInfo.Values);
    }

    [Fact]
    public void TitleIdIsNormalizedAndMetadataIsAtomicallyPersisted()
    {
        var store = CreateStore();
        var gamePath = Path.Combine(_directory, "games", "Astro", "eboot.bin");

        store.Save(
            " cusa00001 ",
            gamePath,
            new DesktopLauncherMetadata
            {
                FirmwareVersion = " 3.00 ",
                Status = GameStatus.InGame,
                Comment = "Runs past the logo",
            });

        var loaded = store.Load("CUSA00001", Path.Combine(_directory, "games", "other", "eboot.bin"));
        Assert.Equal("CUSA00001", loaded.TitleId);
        Assert.Equal("3.00", loaded.FirmwareVersion);
        Assert.Equal(GameStatus.InGame, loaded.Status);
        Assert.Equal("Runs past the logo", loaded.Comment);
        Assert.True(File.Exists(store.FilePath));
        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"status\": \"InGame\"", json);
        Assert.Contains("\"comment\": \"Runs past the logo\"", json);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void PathFallbackUsesAStableSafeIdentityWhenTitleIdIsMissing()
    {
        var store = CreateStore();
        var originalPath = Path.Combine(_directory, "games", "../games", "No Title", "eboot.bin");

        store.Save(null, originalPath, new DesktopLauncherMetadata
        {
            Status = GameStatus.Logo,
            Comment = "Title ID is not present",
        });

        var normalizedPath = Path.GetFullPath(originalPath);
        var loaded = store.Load(null, normalizedPath + Path.DirectorySeparatorChar);

        Assert.Equal(GameStatus.Logo, loaded.Status);
        Assert.Equal("Title ID is not present", loaded.Comment);
        Assert.StartsWith("PATH:", DesktopLauncherMetadataStore.KeyFor(null, normalizedPath));
        Assert.DoesNotContain('/', DesktopLauncherMetadataStore.KeyFor(null, normalizedPath)!);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, DesktopLauncherMetadataStore.KeyFor(null, normalizedPath)!);
    }

    [Fact]
    public void GameEntryExposesFirmwareAndPersistsEditableCompatibilityFields()
    {
        var store = CreateStore();
        var gamePath = Path.Combine(_directory, "game", "eboot.bin");
        var game = new GameEntry(
            "Astro's PLAYROOM",
            "cusa00002",
            "01.000.000",
            gamePath,
            123,
            null,
            null,
            metadataStore: store,
            firmwareVersion: "4.03");

        Assert.Equal("4.03", game.FirmwareVersion);
        Assert.Equal(GameStatus.Unknown, game.CompatibilityStatus);
        Assert.Equal("Unknown", game.CompatibilityStatusText);
        Assert.Equal("#8a8a8a", game.CompatibilityStatusColor);

        game.CompatibilityStatus = GameStatus.MainMenu;
        game.Comment = "Playable in the main menu";

        var reloaded = new GameEntry(
            "Astro's PLAYROOM",
            "CUSA00002",
            "01.000.000",
            gamePath,
            123,
            null,
            null,
            metadataStore: store,
            firmwareVersion: "4.03");

        Assert.Equal(GameStatus.MainMenu, reloaded.Status);
        Assert.Equal("Main menu", reloaded.CompatibilityStatusDisplay.DisplayName);
        Assert.Equal("#2f80ed", reloaded.CompatibilityStatusDisplay.ColorHex);
        Assert.Equal("Playable in the main menu", reloaded.Comment);
    }

    [Fact]
    public void GameEntryReadsRequiredFirmwareVersionFromParamJson()
    {
        var gameDirectory = Path.Combine(_directory, "game");
        Directory.CreateDirectory(Path.Combine(gameDirectory, "sce_sys"));
        File.WriteAllText(
            Path.Combine(gameDirectory, "sce_sys", "param.json"),
            "{\"requiredSystemSoftwareVersion\":\"0x0301000000000000\"}");

        var game = new GameEntry(
            "Firmware game",
            null,
            null,
            Path.Combine(gameDirectory, "eboot.bin"),
            0,
            null,
            null,
            metadataStore: CreateStore());

        Assert.Equal("3.01", game.FirmwareVersion);
    }

    [Fact]
    public void LegacyQtStatusSpellingIsAccepted()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(
            store.FilePath,
            "{\"cusa00003\":{\"status\":\"DoesntBoot\",\"comment\":\"Stops at logo\"}}");

        var loaded = store.Load(" cusa00003 ", null);

        Assert.Equal(GameStatus.DoesntBoot, loaded.Status);
        Assert.Equal("Stops at logo", loaded.Comment);
    }

    private DesktopLauncherMetadataStore CreateStore() =>
        new(Path.Combine(_directory, "user", "compatibility_db.json"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
