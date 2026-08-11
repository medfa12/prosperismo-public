// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prosperismo.GUI;

public sealed class PerGameSettings
{
    private static readonly JsonSerializerOptions SerializerOptions =
        SettingsPersistence.CreateSerializerOptions();

    public string? LogLevel { get; set; }

    public int? ImportTraceLimit { get; set; }

    public bool? StrictDynlibResolution { get; set; }

    public bool? LogToFile { get; set; }

    public List<string>? EnvironmentToggles { get; set; }

    [JsonIgnore]
    public bool IsEmpty =>
        LogLevel is null &&
        ImportTraceLimit is null &&
        StrictDynlibResolution is null &&
        LogToFile is null &&
        EnvironmentToggles is null;

    public static string DirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "user", "custom_configs");

    public static string PathFor(string titleId) =>
        Path.Combine(DirectoryPath, SanitizeTitleId(titleId) + ".json");

    public static PerGameSettings? Load(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        try
        {
            var path = PathFor(titleId);
            if (File.Exists(path))
            {
                return NormalizeFromJson(File.ReadAllText(path));
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // A null list inherits global settings; only entries in a present list are sanitized.
    internal static PerGameSettings? NormalizeFromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<PerGameSettings>(json, SerializerOptions);
        if (settings?.EnvironmentToggles is { } toggles)
        {
            settings.EnvironmentToggles = toggles.Where(entry => !string.IsNullOrEmpty(entry)).ToList();
        }

        return settings;
    }

    public void Save(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return;
        }

        try
        {
            var path = PathFor(titleId);
            if (IsEmpty)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            SettingsPersistence.WriteAtomically(path, this, SerializerOptions);
        }
        catch (Exception)
        {
        }
    }

    internal static string SanitizeTitleId(string titleId)
    {
        var trimmed = titleId.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed.Length == 0 ? "UNKNOWN" : trimmed;
    }
}

/// <summary>
/// A complete native-emulator profile for one normalized game installation.
/// Title ID is descriptive metadata only and never determines identity.
/// </summary>
public sealed record PerGameEmulatorSettingsProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string GamePath { get; set; } = string.Empty;

    public string? TitleId { get; set; }

    public EmulatorSettings Settings { get; set; } = new();
}

/// <summary>
/// Persists complete per-game emulator profiles by normalized installation
/// path. A title-ID file is migrated only when exactly one matching install is
/// known, so duplicate copies or versions can never inherit each other's data.
/// </summary>
public sealed class PerGameEmulatorSettingsStore
{
    private const string ProfileFilePrefix = "game-";
    private static readonly JsonSerializerOptions SerializerOptions =
        SettingsPersistence.CreateSerializerOptions();

    public PerGameEmulatorSettingsStore(string? directoryPath = null)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(directoryPath)
            ? PerGameSettings.DirectoryPath
            : Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public string ProfilePathFor(string gamePath)
    {
        var identity = NormalizeGamePath(gamePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(DirectoryPath, $"{ProfileFilePrefix}{hash}.json");
    }

    public PerGameEmulatorSettingsProfile? Load(
        string gamePath,
        string? titleId = null,
        IEnumerable<string>? matchingTitleInstallPaths = null)
    {
        var identity = NormalizeGamePath(gamePath);
        var profilePath = ProfilePathFor(identity);
        var current = LoadProfile(profilePath, identity, titleId);
        if (current is not null)
        {
            return current;
        }

        if (!CanMigrateLegacy(identity, matchingTitleInstallPaths) || string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        var legacyPath = Path.Combine(
            DirectoryPath,
            PerGameSettings.SanitizeTitleId(titleId) + ".json");
        var migrated = LoadLegacyNativeProfile(legacyPath, identity, titleId);
        if (migrated is null)
        {
            return null;
        }

        Save(migrated);
        return migrated;
    }

    public void Save(PerGameEmulatorSettingsProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalized = NormalizeProfile(profile.GamePath, profile.TitleId, profile.Settings);
        SettingsPersistence.WriteAtomically(
            ProfilePathFor(normalized.GamePath),
            normalized,
            SerializerOptions);
    }

    public void Save(string gamePath, string? titleId, EmulatorSettings settings) =>
        Save(NormalizeProfile(gamePath, titleId, settings));

    public void Delete(string gamePath)
    {
        var path = ProfilePathFor(gamePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string NormalizeGamePath(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath.Trim()));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static PerGameEmulatorSettingsProfile NormalizeProfile(
        string gamePath,
        string? titleId,
        EmulatorSettings? settings) => new()
    {
        SchemaVersion = PerGameEmulatorSettingsProfile.CurrentSchemaVersion,
        GamePath = NormalizeGamePath(gamePath),
        TitleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim(),
        Settings = SettingsPersistence.NormalizeEmulatorSettings(settings),
    };

    private static bool CanMigrateLegacy(
        string requestedIdentity,
        IEnumerable<string>? matchingTitleInstallPaths)
    {
        if (matchingTitleInstallPaths is null)
        {
            return false;
        }

        var candidates = matchingTitleInstallPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGamePath)
            .Distinct(PathComparer)
            .ToArray();

        return candidates.Length == 1 && PathComparer.Equals(candidates[0], requestedIdentity);
    }

    private static PerGameEmulatorSettingsProfile? LoadProfile(
        string path,
        string expectedIdentity,
        string? fallbackTitleId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var profile = JsonSerializer.Deserialize<PerGameEmulatorSettingsProfile>(
                File.ReadAllText(path),
                SerializerOptions);
            if (profile is null || string.IsNullOrWhiteSpace(profile.GamePath))
            {
                return null;
            }

            var storedIdentity = NormalizeGamePath(profile.GamePath);
            if (!PathComparer.Equals(storedIdentity, expectedIdentity))
            {
                return null;
            }

            return NormalizeProfile(
                storedIdentity,
                profile.TitleId ?? fallbackTitleId,
                profile.Settings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PerGameEmulatorSettingsProfile? LoadLegacyNativeProfile(
        string path,
        string gamePath,
        string titleId)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var legacy = JsonSerializer.Deserialize<LegacyNativeProfile>(
                File.ReadAllText(path),
                SerializerOptions);
            var settings = legacy?.Settings ?? legacy?.EmulatorSettings;
            return settings is null ? null : NormalizeProfile(gamePath, titleId, settings);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class LegacyNativeProfile
    {
        public EmulatorSettings? Settings { get; set; }

        public EmulatorSettings? EmulatorSettings { get; set; }
    }
}

public sealed record EffectiveLaunchSettings(
    string LogLevel,
    int ImportTraceLimit,
    bool StrictDynlibResolution,
    bool LogToFile,
    IReadOnlyList<string> EnvironmentToggles)
{
    public static EffectiveLaunchSettings Resolve(GuiSettings global, PerGameSettings? perGame) => new(
        perGame?.LogLevel ?? global.LogLevel,
        perGame?.ImportTraceLimit ?? global.ImportTraceLimit,
        perGame?.StrictDynlibResolution ?? global.StrictDynlibResolution,
        perGame?.LogToFile ?? global.LogToFile,
        perGame?.EnvironmentToggles ?? global.EnvironmentToggles);
}
