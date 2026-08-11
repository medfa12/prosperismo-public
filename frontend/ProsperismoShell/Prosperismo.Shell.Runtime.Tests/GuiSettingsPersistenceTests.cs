// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class GuiSettingsPersistenceTests
{
    [Fact]
    public void DefaultsUseCurrentSchemaAndKytyEmulatorValues()
    {
        var settings = new GuiSettings();

        Assert.Equal(GuiSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(EmulatorResolution.R1280X720, settings.GlobalEmulatorSettings.ScreenResolution);
        Assert.Equal(60, settings.GlobalEmulatorSettings.VblankFrequency);
        Assert.True(settings.GlobalEmulatorSettings.VulkanValidation);
        Assert.True(settings.GlobalEmulatorSettings.ShaderValidation);
        Assert.Equal(ShaderOptimizationMode.Performance, settings.GlobalEmulatorSettings.ShaderOptimization);
        Assert.Equal(EmulatorOutputDirection.Silent, settings.GlobalEmulatorSettings.ShaderLogDirection);
        Assert.Equal("_Shaders", settings.GlobalEmulatorSettings.ShaderLogFolder);
        Assert.False(settings.GlobalEmulatorSettings.CommandBufferDump);
        Assert.Equal("_Buffers", settings.GlobalEmulatorSettings.CommandBufferDumpFolder);
        Assert.Equal(EmulatorOutputDirection.Silent, settings.GlobalEmulatorSettings.PrintfDirection);
        Assert.Equal("_prosperismo.txt", settings.GlobalEmulatorSettings.PrintfOutputFile);
        Assert.Equal(EmulatorProfilerDirection.None, settings.GlobalEmulatorSettings.ProfilerDirection);
        Assert.False(settings.GlobalEmulatorSettings.RenderDoc);
        Assert.True(settings.GlobalEmulatorSettings.NggRectlistDraw);
    }

    [Fact]
    public void AtomicRoundTripPreservesLauncherPreferencesAndNativeSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "gui-settings.json");
        var expected = new GuiSettings
        {
            GameFolders = ["/games/one", "/games/two"],
            ExcludedGames = ["/games/one/eboot.bin"],
            Language = "tr",
            PlayShellMusic = false,
            ShellMusicVolume = 0.25,
            DiscordRichPresence = false,
            RenderResolutionScale = 1.5,
            LastFocusedControlCenterId = "notifications",
            RecentGamePaths = ["/games/two/eboot.bin", "/games/one/eboot.bin"],
            GlobalEmulatorSettings = new EmulatorSettings
            {
                ScreenResolution = EmulatorResolution.R1920X1080,
                VblankFrequency = 120,
                VulkanValidation = false,
                ShaderValidation = false,
                ShaderOptimization = ShaderOptimizationMode.Size,
                ShaderLogDirection = EmulatorOutputDirection.File,
                ShaderLogFolder = "shader-output",
                CommandBufferDump = true,
                CommandBufferDumpFolder = "buffer-output",
                PrintfDirection = EmulatorOutputDirection.File,
                PrintfOutputFile = "guest.log",
                ProfilerDirection = EmulatorProfilerDirection.Network,
                RenderDoc = true,
                NggRectlistDraw = false,
            },
        };

        expected.SaveTo(path);
        expected.LogLevel = "Debug";
        expected.GlobalEmulatorSettings.VblankFrequency = 144;
        expected.SaveTo(path);
        var actual = GuiSettings.LoadFrom(path);

        Assert.Equal(GuiSettings.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.GameFolders, actual.GameFolders);
        Assert.Equal(expected.ExcludedGames, actual.ExcludedGames);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.PlayShellMusic, actual.PlayShellMusic);
        Assert.Equal(expected.ShellMusicVolume, actual.ShellMusicVolume);
        Assert.Equal(expected.DiscordRichPresence, actual.DiscordRichPresence);
        Assert.Equal(expected.RenderResolutionScale, actual.RenderResolutionScale);
        Assert.Equal(expected.LastFocusedControlCenterId, actual.LastFocusedControlCenterId);
        Assert.Equal(expected.RecentGamePaths, actual.RecentGamePaths);
        Assert.Equal("Debug", actual.LogLevel);
        Assert.Equal(expected.GlobalEmulatorSettings, actual.GlobalEmulatorSettings);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void LegacyOrInvalidJsonNormalizesWithoutDiscardingLauncherPreferences()
    {
        const string json = """
            {
              "SchemaVersion": 0,
              "Language": "fr",
              "PlayUiSounds": false,
              "GameFolders": [null, "", "/games/kept"],
              "RecentGamePaths": [null, "", "/games/a/eboot.bin", "/games/a/eboot.bin"],
              "EnvironmentToggles": null,
              "GlobalEmulatorSettings": {
                "ScreenResolution": "invalid",
                "VblankFrequency": -1,
                "ShaderOptimization": "Turbo",
                "ShaderLogDirection": 99,
                "ShaderLogFolder": null,
                "CommandBufferDumpFolder": " ",
                "PrintfOutputFile": "",
                "ProfilerDirection": "invalid"
              }
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);
        var defaults = new EmulatorSettings();

        Assert.Equal(GuiSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("fr", settings.Language);
        Assert.False(settings.PlayUiSounds);
        Assert.Equal(["/games/kept"], settings.GameFolders);
        Assert.Empty(settings.EnvironmentToggles);
        Assert.Equal(["/games/a/eboot.bin"], settings.RecentGamePaths);
        Assert.Equal("home", settings.LastFocusedControlCenterId);
        Assert.Equal(defaults, settings.GlobalEmulatorSettings);
    }

    [Fact]
    public void NullGlobalSettingsNormalizeToKytyDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson(
            """{"Language":"de","GlobalEmulatorSettings":null}""");

        Assert.Equal("de", settings.Language);
        Assert.Equal(new EmulatorSettings(), settings.GlobalEmulatorSettings);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"prosperismo-gui-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
