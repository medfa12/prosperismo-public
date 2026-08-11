// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace Prosperismo.GUI.Controls;

/// <summary>Which NPXS40003 App Switcher section owns a local row.</summary>
public enum ShellAppSwitcherSection
{
    Active,
    RecentGame,
}

/// <summary>Backend identity carried by one App Switcher title row.</summary>
public sealed record ShellAppSwitcherEntryTag(
    string Path,
    ShellAppSwitcherSection Section);

/// <summary>Actions in the selected App Switcher title's option route.</summary>
public enum ShellAppSwitcherAction
{
    BackToGame,
    CloseGame,
    PlayGame,
    GoToGameHub,
}

/// <summary>Backend identity carried by an App Switcher option row.</summary>
public sealed record ShellAppSwitcherActionTag(
    string Path,
    ShellAppSwitcherAction Action);

/// <summary>
/// presents an active app first and then at most two games sorted by their
/// console last-played date. Prosperismo substitutes its own persisted local
/// launch order and never inserts a title absent from the current library.
/// </summary>
public static class ShellAppSwitcherComposer
{
    public const int RecentGameLimit = 2;
    public const int PersistedHistoryLimit = 16;

    public static IReadOnlyList<ShellFunctionPanelItem> Compose(
        IReadOnlyList<GameEntry> library,
        string? runningPath,
        IReadOnlyList<string>? recentPaths,
        StringComparison pathComparison)
    {
        ArgumentNullException.ThrowIfNull(library);

        var rows = new List<ShellFunctionPanelItem>();
        var running = FindByPath(library, runningPath, pathComparison);
        if (running is not null)
        {
            rows.Add(ToRow(running, ShellAppSwitcherSection.Active, "Active"));
        }

        var seen = new HashSet<string>(PathComparer(pathComparison));
        if (running is not null)
        {
            seen.Add(running.Path);
        }

        foreach (var path in recentPaths ?? Array.Empty<string>())
        {
            var game = FindByPath(library, path, pathComparison);
            if (game is null || !seen.Add(game.Path))
            {
                continue;
            }

            rows.Add(ToRow(
                game,
                ShellAppSwitcherSection.RecentGame,
                rows.Any(row => row.Tag is ShellAppSwitcherEntryTag
                    { Section: ShellAppSwitcherSection.RecentGame })
                    ? null
                    : "Last played games"));
            if (rows.Count(row => row.Tag is ShellAppSwitcherEntryTag
                { Section: ShellAppSwitcherSection.RecentGame }) >= RecentGameLimit)
            {
                break;
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(new ShellFunctionPanelItem("No recent games or apps")
            {
                SecondaryText = "Games you launch locally will appear here.",
                IsEnabled = false,
                DimWhenDisabled = false,
                ContentCentered = true,
                ShowSeparator = false,
                MinHeight = 210,
            });
        }

        return rows;
    }

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeOptions(
        GameEntry game,
        ShellAppSwitcherSection section,
        bool canReturnToRunningGame,
        bool canCloseRunningGame,
        bool canLaunch,
        bool canOpenHub)
    {
        ArgumentNullException.ThrowIfNull(game);
        var rows = new List<ShellFunctionPanelItem>();
        if (section == ShellAppSwitcherSection.Active)
        {
            if (canReturnToRunningGame)
            {
                rows.Add(ActionRow(game, "Back to game", ShellAppSwitcherAction.BackToGame));
            }
            if (canOpenHub)
            {
                rows.Add(ActionRow(game, "Go to game hub", ShellAppSwitcherAction.GoToGameHub));
            }
            if (canCloseRunningGame)
            {
                rows.Add(ActionRow(game, "Close game", ShellAppSwitcherAction.CloseGame));
            }
        }
        else
        {
            if (canLaunch)
            {
                rows.Add(ActionRow(game, "Play", ShellAppSwitcherAction.PlayGame));
            }
            if (canOpenHub)
            {
                rows.Add(ActionRow(game, "Go to game hub", ShellAppSwitcherAction.GoToGameHub));
            }
        }

        return rows;
    }

    /// <summary>Moves one launch to the front and bounds persisted history.</summary>
    public static List<string> RecordLaunch(
        IReadOnlyList<string>? history,
        string path,
        StringComparison pathComparison)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (history ?? Array.Empty<string>()).Take(PersistedHistoryLimit).ToList();
        }

        var result = new List<string> { path };
        foreach (var candidate in history ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                string.Equals(candidate, path, pathComparison) ||
                result.Any(existing => string.Equals(existing, candidate, pathComparison)))
            {
                continue;
            }

            result.Add(candidate);
            if (result.Count == PersistedHistoryLimit)
            {
                break;
            }
        }

        return result;
    }

    private static ShellFunctionPanelItem ToRow(
        GameEntry game,
        ShellAppSwitcherSection section,
        string? sectionHeader) =>
        new(game.Name, tag: new ShellAppSwitcherEntryTag(game.Path, section))
        {
            LeadingImage = game.Cover,
            SecondaryText = string.IsNullOrWhiteSpace(game.TitleId) ? null : game.TitleId,
            SectionHeader = sectionHeader,
        };

    private static ShellFunctionPanelItem ActionRow(
        GameEntry game,
        string title,
        ShellAppSwitcherAction action) =>
        new(title, tag: new ShellAppSwitcherActionTag(game.Path, action));

    private static GameEntry? FindByPath(
        IReadOnlyList<GameEntry> library,
        string? path,
        StringComparison comparison) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : library.FirstOrDefault(game => string.Equals(game.Path, path, comparison));

    private static StringComparer PathComparer(StringComparison comparison) => comparison switch
    {
        StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
        StringComparison.InvariantCultureIgnoreCase => StringComparer.InvariantCultureIgnoreCase,
        StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase,
        StringComparison.InvariantCulture => StringComparer.InvariantCulture,
        StringComparison.CurrentCulture => StringComparer.CurrentCulture,
        _ => StringComparer.Ordinal,
    };
}
