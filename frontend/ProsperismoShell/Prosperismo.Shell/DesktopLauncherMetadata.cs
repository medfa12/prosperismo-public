// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prosperismo.GUI;

/// <summary>
/// Compatibility states used by the original Qt launcher.
/// </summary>
public enum GameStatus
{
    Unknown,
    MainMenu,
    InGame,
    Logo,
    DoesntBoot,
}

/// <summary>
/// The text and indicator color presented for a compatibility state.
/// Colors intentionally match configurationItem.cpp in the Qt launcher.
/// </summary>
public readonly record struct GameStatusDisplay(string Text, string Color)
{
    public string DisplayName => Text;

    public string ColorHex => Color;
}

public static class GameStatusInfo
{
    public static IReadOnlyList<GameStatus> Values { get; } =
    [
        GameStatus.Unknown,
        GameStatus.MainMenu,
        GameStatus.InGame,
        GameStatus.Logo,
        GameStatus.DoesntBoot,
    ];

    public static GameStatusDisplay GetDisplay(GameStatus status) => status switch
    {
        GameStatus.MainMenu => new("Main menu", "#2f80ed"),
        GameStatus.InGame => new("In game", "#2fb344"),
        GameStatus.Logo => new("Logo", "#f2c94c"),
        GameStatus.DoesntBoot => new("Doesn't boot", "#e55353"),
        _ => new("Unknown", "#8a8a8a"),
    };

    public static string GetDisplayName(GameStatus status) => GetDisplay(status).Text;

    public static string GetColor(GameStatus status) => GetDisplay(status).Color;

    public static string GetStatusText(GameStatus status) => GetDisplayName(status);

    public static string GetStatusColor(GameStatus status) => GetColor(status);

    internal static GameStatus Parse(string? text)
    {
        var normalized = text?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mainmenu" or "main menu" => GameStatus.MainMenu,
            "ingame" or "in game" => GameStatus.InGame,
            "logo" => GameStatus.Logo,
            "doesntboot" or "doesn't boot" => GameStatus.DoesntBoot,
            _ => GameStatus.Unknown,
        };
    }
}

/// <summary>
/// The local metadata associated with one launcher entry. The Qt database
/// from the installed game's param.json and is retained here when available.
/// </summary>
public sealed class DesktopLauncherMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string? TitleId { get; set; }

    public string? GamePath { get; set; }

    public string? FirmwareVersion { get; set; }

    public GameStatus Status { get; set; } = GameStatus.Unknown;

    public string Comment { get; set; } = string.Empty;

    public static DesktopLauncherMetadata Load(
        string? titleId,
        string? gamePath,
        string? filePath = null) =>
        new DesktopLauncherMetadataStore(filePath).Load(titleId, gamePath);

    public void Save(string? filePath = null) =>
        new DesktopLauncherMetadataStore(filePath).Save(TitleId, GamePath, this);

    internal DesktopLauncherMetadata Normalize(string? titleId, string? gamePath)
    {
        SchemaVersion = CurrentSchemaVersion;
        TitleId = DesktopLauncherMetadataStore.NormalizeTitleId(titleId);
        GamePath = DesktopLauncherMetadataStore.NormalizeGamePath(gamePath);
        FirmwareVersion = string.IsNullOrWhiteSpace(FirmwareVersion)
            ? null
            : FirmwareVersion.Trim();
        Comment ??= string.Empty;
        Status = Enum.IsDefined(Status) ? Status : GameStatus.Unknown;
        return this;
    }
}

/// <summary>
/// Atomic local persistence for the Qt-compatible compatibility metadata.
/// Title IDs are the primary identity, with a SHA-256 path key for entries
/// without a usable title ID. This keeps arbitrary path characters out of
/// JSON identity keys while preserving exact path identity in the value.
/// </summary>
public sealed class DesktopLauncherMetadataStore
{
    private const string PathKeyPrefix = "PATH:";

    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new GameStatusJsonConverter() },
    };

    private static readonly Lazy<DesktopLauncherMetadataStore> DefaultStore =
        new(() => new DesktopLauncherMetadataStore());

    public DesktopLauncherMetadataStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(AppContext.BaseDirectory, "user", "compatibility_db.json")
            : filePath);
    }

    public static DesktopLauncherMetadataStore Default => DefaultStore.Value;

    public string FilePath { get; }

    public DesktopLauncherMetadata Load(string? titleId, string? gamePath)
    {
        var normalizedTitleId = NormalizeTitleId(titleId);
        var normalizedPath = NormalizeGamePath(gamePath);
        if (normalizedTitleId is null && normalizedPath is null)
        {
            return new DesktopLauncherMetadata();
        }

        lock (SyncRoot)
        {
            var entries = ReadEntries();
            var keys = KeysFor(normalizedTitleId, normalizedPath);
            foreach (var key in keys)
            {
                if (!entries.TryGetValue(key, out var entry))
                {
                    continue;
                }

                return NormalizeEntry(entry, normalizedTitleId, normalizedPath);
            }
        }

        return new DesktopLauncherMetadata
        {
            TitleId = normalizedTitleId,
            GamePath = normalizedPath,
        };
    }

    public DesktopLauncherMetadata LoadFor(GameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Load(entry.TitleId, entry.Path);
    }

    public void Save(string? titleId, string? gamePath, DesktopLauncherMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var normalizedTitleId = NormalizeTitleId(titleId);
        var normalizedPath = NormalizeGamePath(gamePath);
        var key = KeyFor(normalizedTitleId, normalizedPath);
        if (key is null)
        {
            return;
        }

        lock (SyncRoot)
        {
            var entries = ReadEntries();
            entries[key] = NormalizeEntry(metadata, normalizedTitleId, normalizedPath);
            WriteEntries(entries);
        }
    }

    public void SaveFor(GameEntry entry, DesktopLauncherMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Save(entry.TitleId, entry.Path, metadata);
    }

    public void SetStatus(string? titleId, string? gamePath, GameStatus status)
    {
        var metadata = Load(titleId, gamePath);
        metadata.Status = Enum.IsDefined(status) ? status : GameStatus.Unknown;
        Save(titleId, gamePath, metadata);
    }

    public void SetComment(string? titleId, string? gamePath, string? comment)
    {
        var metadata = Load(titleId, gamePath);
        metadata.Comment = comment ?? string.Empty;
        Save(titleId, gamePath, metadata);
    }

    public static string? NormalizeTitleId(string? titleId)
    {
        var normalized = titleId?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return null;
        }

        return normalized;
    }

    public static string? NormalizeGamePath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return null;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath.Trim()));
            return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? KeyFor(string? titleId, string? gamePath)
    {
        var normalizedTitleId = NormalizeTitleId(titleId);
        if (normalizedTitleId is not null)
        {
            // This is deliberately the same key shape as the Qt JSON file.
            return normalizedTitleId;
        }

        var normalizedPath = NormalizeGamePath(gamePath);
        if (normalizedPath is null)
        {
            return null;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return PathKeyPrefix + digest;
    }

    private Dictionary<string, DesktopLauncherMetadata> ReadEntries()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Dictionary<string, DesktopLauncherMetadata>(StringComparer.Ordinal);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, DesktopLauncherMetadata>(StringComparer.Ordinal);
            }

            var entries = new Dictionary<string, DesktopLauncherMetadata>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entry = property.Value;
                var metadata = new DesktopLauncherMetadata
                {
                    SchemaVersion = ReadInt(entry, "SchemaVersion") ?? DesktopLauncherMetadata.CurrentSchemaVersion,
                    TitleId = ReadString(entry, "TitleId"),
                    GamePath = ReadString(entry, "GamePath"),
                    FirmwareVersion = ReadString(entry, "FirmwareVersion"),
                    Status = GameStatusInfo.Parse(ReadString(entry, "Status")),
                    Comment = ReadString(entry, "Comment") ?? string.Empty,
                };

                // The Qt file uses lower-case field names. Its parser also
                // ignores malformed values entry-by-entry, so do the same.
                if (ReadString(entry, "status") is { } status)
                {
                    metadata.Status = GameStatusInfo.Parse(status);
                }

                if (ReadString(entry, "comment") is { } comment)
                {
                    metadata.Comment = comment;
                }

                var key = property.Name.StartsWith(PathKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? property.Name.ToUpperInvariant()
                    : NormalizeTitleId(property.Name);
                if (key is not null)
                {
                    entries[key] = metadata;
                }
            }

            return entries;
        }
        catch (Exception)
        {
            // A corrupt local cache must not prevent the launcher from opening.
            return new Dictionary<string, DesktopLauncherMetadata>(StringComparer.Ordinal);
        }
    }

    private void WriteEntries(Dictionary<string, DesktopLauncherMetadata> entries)
    {
        // SettingsPersistence performs a flushed temporary write followed by
        // replace/move, matching QSaveFile's crash-safe local update behavior.
        SettingsPersistence.WriteAtomically(
            FilePath,
            entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SerializerOptions);
    }

    private static IEnumerable<string> KeysFor(string? titleId, string? gamePath)
    {
        if (titleId is not null)
        {
            yield return titleId;
        }

        if (KeyFor(null, gamePath) is { } pathKey && !string.Equals(pathKey, titleId, StringComparison.Ordinal))
        {
            yield return pathKey;
        }
    }

    private static DesktopLauncherMetadata NormalizeEntry(
        DesktopLauncherMetadata entry,
        string? titleId,
        string? gamePath)
    {
        entry ??= new DesktopLauncherMetadata();
        return entry.Normalize(titleId, gamePath);
    }

    private static string? ReadString(JsonElement objectElement, string propertyName)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement objectElement, string propertyName)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed class GameStatusJsonConverter : JsonConverter<GameStatus>
    {
        public override GameStatus Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return GameStatusInfo.Parse(reader.GetString());
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) &&
                Enum.IsDefined(typeof(GameStatus), value))
            {
                return (GameStatus)value;
            }

            return GameStatus.Unknown;
        }

        public override void Write(
            Utf8JsonWriter writer,
            GameStatus value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : GameStatus.Unknown.ToString());
        }
    }
}
