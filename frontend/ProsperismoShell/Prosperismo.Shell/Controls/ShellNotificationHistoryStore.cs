// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Durable host storage for the NotificationDb2-shaped list projection.
/// Avalonia image objects and process-owned action callbacks are intentionally
/// excluded: neither can be invoked truthfully after a launcher restart.
/// </summary>
internal sealed class ShellNotificationHistoryStore
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumDocumentBytes = 4 * 1024 * 1024;
    private const int MaximumIdentityCharacters = 1_024;
    private const int MaximumTextCharacters = 65_536;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    internal ShellNotificationHistoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
    }

    internal string FilePath => _path;

    internal IReadOnlyList<ShellNotificationHistoryEntry> Load(int limit)
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumDocumentBytes)
            {
                return [];
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            var document = JsonSerializer.Deserialize<PersistedDocument>(
                stream,
                SerializerOptions);
            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return [];
            }

            return (document.Entries ?? [])
                .Select(ToRuntimeEntry)
                .Where(static entry => entry is not null)
                .Cast<ShellNotificationHistoryEntry>()
                .GroupBy(static entry => entry.Id, StringComparer.Ordinal)
                .Select(static group => group.MaxBy(entry => entry.UpdatedAt)!)
                .OrderByDescending(static entry => entry.CreatedAt)
                .Take(limit)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException)
        {
            return [];
        }
    }

    internal void Save(IReadOnlyList<ShellNotificationHistoryEntry> entries)
    {
        try
        {
            var document = new PersistedDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Entries = entries
                    .Where(static entry => entry.State != ShellNotificationHistoryState.Deleted)
                    .OrderBy(static entry => entry.CreatedAt)
                    .Select(ToPersistedEntry)
                    .ToList(),
            };
            SettingsPersistence.WriteAtomically(_path, document, SerializerOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or NotSupportedException)
        {
            // Notification history is best-effort just like launcher settings.
        }
    }

    private static PersistedEntry ToPersistedEntry(ShellNotificationHistoryEntry entry)
    {
        var request = entry.Request;
        return new PersistedEntry
        {
            Id = entry.Id,
            NotificationId = request.NotificationId,
            UserId = request.UserId,
            BundleName = request.BundleName,
            UseCaseId = request.UseCaseId,
            PrimaryText = request.PrimaryText,
            SecondaryText = request.SecondaryText,
            TertiaryText = request.TertiaryText,
            DetailText = request.DetailText,
            Surface = request.Surface,
            Placement = request.Placement,
            LargeText = request.LargeText,
            ReplaceAlways = request.ReplaceAlways,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            State = entry.State,
        };
    }

    private static ShellNotificationHistoryEntry? ToRuntimeEntry(PersistedEntry persisted)
    {
        var id = Normalize(persisted.Id, MaximumIdentityCharacters);
        if (string.IsNullOrWhiteSpace(id) ||
            persisted.Surface is not (ShellNotificationSurface.Informative or
                ShellNotificationSurface.Interactive) ||
            !Enum.IsDefined(persisted.Placement) ||
            !Enum.IsDefined(persisted.State) ||
            persisted.State == ShellNotificationHistoryState.Deleted)
        {
            return null;
        }

        var createdAt = persisted.CreatedAt;
        var updatedAt = persisted.UpdatedAt < createdAt
            ? createdAt
            : persisted.UpdatedAt;
        return new ShellNotificationHistoryEntry(
            id,
            new ShellNotificationRequest
            {
                NotificationId = Normalize(persisted.NotificationId, MaximumIdentityCharacters),
                UserId = Normalize(persisted.UserId, MaximumIdentityCharacters),
                BundleName = Normalize(persisted.BundleName, MaximumIdentityCharacters),
                UseCaseId = Normalize(persisted.UseCaseId, MaximumIdentityCharacters),
                PrimaryText = Normalize(persisted.PrimaryText, MaximumTextCharacters),
                SecondaryText = Normalize(persisted.SecondaryText, MaximumTextCharacters),
                TertiaryText = Normalize(persisted.TertiaryText, MaximumTextCharacters),
                DetailText = Normalize(persisted.DetailText, MaximumTextCharacters),
                Surface = persisted.Surface,
                Placement = persisted.Placement,
                LargeText = persisted.LargeText,
                ReplaceAlways = persisted.ReplaceAlways,
                Actions = Array.Empty<ShellNotificationAction>(),
            },
            createdAt,
            updatedAt,
            persisted.State);
    }

    private static string? Normalize(string? value, int maximumCharacters)
    {
        if (value is null || value.Length <= maximumCharacters)
        {
            return value;
        }

        return value[..maximumCharacters];
    }

    private sealed class PersistedDocument
    {
        public int SchemaVersion { get; set; }
        public List<PersistedEntry>? Entries { get; set; }
    }

    private sealed class PersistedEntry
    {
        public string? Id { get; set; }
        public string? NotificationId { get; set; }
        public string? UserId { get; set; }
        public string? BundleName { get; set; }
        public string? UseCaseId { get; set; }
        public string? PrimaryText { get; set; }
        public string? SecondaryText { get; set; }
        public string? TertiaryText { get; set; }
        public string? DetailText { get; set; }
        public ShellNotificationSurface Surface { get; set; }
        public ShellNotificationPlacement Placement { get; set; }
        public bool LargeText { get; set; }
        public bool ReplaceAlways { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public ShellNotificationHistoryState State { get; set; }
    }
}
