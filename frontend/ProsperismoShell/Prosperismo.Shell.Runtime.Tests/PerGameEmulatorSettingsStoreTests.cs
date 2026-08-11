// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class PerGameEmulatorSettingsStoreTests
{
    [Fact]
    public void CompleteProfileRoundTripsWithNormalizedPathAndTitleMetadata()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var gamePath = Path.Combine(directory.Path, "game", ".", "eboot.bin");
        var expectedSettings = new EmulatorSettings
        {
            ScreenResolution = EmulatorResolution.R1920X1080,
            VblankFrequency = 75,
            ShaderOptimization = ShaderOptimizationMode.None,
            RenderDoc = true,
        };

        store.Save(gamePath, " CUSA00001 ", expectedSettings);
        var actual = store.Load(gamePath, "CUSA00001");

        Assert.NotNull(actual);
        Assert.Equal(PerGameEmulatorSettingsProfile.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal(PerGameEmulatorSettingsStore.NormalizeGamePath(gamePath), actual.GamePath);
        Assert.Equal("CUSA00001", actual.TitleId);
        Assert.Equal(expectedSettings, actual.Settings);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void SameTitleAtDifferentPathsHasIndependentIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var firstPath = Path.Combine(directory.Path, "first", "eboot.bin");
        var secondPath = Path.Combine(directory.Path, "second", "eboot.bin");

        store.Save(firstPath, "CUSA00002", new EmulatorSettings { VblankFrequency = 60 });
        store.Save(secondPath, "CUSA00002", new EmulatorSettings { VblankFrequency = 120 });

        Assert.NotEqual(store.ProfilePathFor(firstPath), store.ProfilePathFor(secondPath));
        Assert.Equal(60, store.Load(firstPath, "CUSA00002")?.Settings.VblankFrequency);
        Assert.Equal(120, store.Load(secondPath, "CUSA00002")?.Settings.VblankFrequency);
        Assert.Equal(2, Directory.EnumerateFiles(directory.Path, "game-*.json").Count());
    }

    [Fact]
    public void TitleIdFallbackMigratesOnlyForOneMatchingInstallation()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var gamePath = Path.Combine(directory.Path, "only", "eboot.bin");
        var legacyPath = Path.Combine(directory.Path, "CUSA00003.json");
        File.WriteAllText(
            legacyPath,
            """
            {
              "Settings": {
                "ScreenResolution": "1920x1080",
                "VblankFrequency": 90,
                "VulkanValidation": false
              }
            }
            """);

        Assert.Null(store.Load(gamePath, "CUSA00003"));

        var migrated = store.Load(gamePath, "CUSA00003", [gamePath]);

        Assert.NotNull(migrated);
        Assert.Equal(EmulatorResolution.R1920X1080, migrated.Settings.ScreenResolution);
        Assert.Equal(90, migrated.Settings.VblankFrequency);
        Assert.False(migrated.Settings.VulkanValidation);
        Assert.True(File.Exists(store.ProfilePathFor(gamePath)));
    }

    [Fact]
    public void AmbiguousTitleIdFallbackDoesNotGuessBetweenInstallations()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var firstPath = Path.Combine(directory.Path, "first", "eboot.bin");
        var secondPath = Path.Combine(directory.Path, "second", "eboot.bin");
        File.WriteAllText(
            Path.Combine(directory.Path, "CUSA00004.json"),
            """{"Settings":{"VblankFrequency":90}}""");

        var actual = store.Load(firstPath, "CUSA00004", [firstPath, secondPath]);

        Assert.Null(actual);
        Assert.False(File.Exists(store.ProfilePathFor(firstPath)));
    }

    [Fact]
    public void InvalidOrNullProfileSettingsNormalizeToKytyDefaults()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var gamePath = Path.Combine(directory.Path, "game", "eboot.bin");
        var normalizedPath = PerGameEmulatorSettingsStore.NormalizeGamePath(gamePath)
            .Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            store.ProfilePathFor(gamePath),
            $$"""
            {
              "SchemaVersion": 0,
              "GamePath": "{{normalizedPath}}",
              "TitleId": "CUSA00005",
              "Settings": null
            }
            """);

        var actual = store.Load(gamePath, "CUSA00005");

        Assert.NotNull(actual);
        Assert.Equal(PerGameEmulatorSettingsProfile.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal(new EmulatorSettings(), actual.Settings);
    }

    [Fact]
    public void DeletingCustomProfileRestoresGlobalInheritanceState()
    {
        using var directory = new TemporaryDirectory();
        var store = new PerGameEmulatorSettingsStore(directory.Path);
        var gamePath = Path.Combine(directory.Path, "game", "eboot.bin");
        store.Save(gamePath, "CUSA00006", new EmulatorSettings { VblankFrequency = 120 });

        store.Delete(gamePath);

        Assert.Null(store.Load(gamePath, "CUSA00006"));
        Assert.False(File.Exists(store.ProfilePathFor(gamePath)));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"prosperismo-per-game-settings-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
