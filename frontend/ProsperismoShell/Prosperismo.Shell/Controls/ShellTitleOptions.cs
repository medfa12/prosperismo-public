// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// title-option payload; the Hub ellipsis is a distinct local CTA overflow.
/// They deliberately do not share an action contract or an anchor.
/// </summary>
public enum ShellTitleOptionsOwner
{
    HomeTile,
    GameHubOverflow,
}

/// <summary>Actions the active host can perform for one local title.</summary>
public enum ShellTitleOptionActionKind
{
    OpenFolder,
    ConfigureGame,
    RemoveFromLibrary,
    CopyPath,
    CopyTitleId,
    CloseApplication,
}

/// <summary>
/// Known HOME option-menu handler vocabulary. The native payload decides which
/// of these are applicable; this is not a menu declaration.
/// </summary>
public static class ShellTitleOptionMenuIds
{
    public const string CheckPatch = "MENU_ID_CHECK_PATCH";
    public const string SaveDataManagement = "MENU_ID_SAVE_DATA_MANAGEMENT";
    public const string GameDataManagement = "MENU_ID_GAME_DATA_MANAGEMENT";
    public const string ApplicationDelete = "MENU_ID_APPLICATION_DELETE";
    public const string ApplicationMultiDelete = "MENU_ID_APPLICATION_MULTI_DELETE";
    public const string ApplicationRemoveFromHome = "MENU_ID_APPLICATION_REMOVE_FROM_HOME";
    public const string UpdateHistory = "MENU_ID_UPDATE_HISTORY";
    public const string MoveToExternalStorage = "MENU_ID_MOVE_TO_EXTERNAL_STORAGE";
    public const string MoveToInternalStorage = "MENU_ID_MOVE_TO_INTERNAL_STORAGE";
    public const string ApplicationInformation = "MENU_ID_APPLICATION_INFORMATION";
    public const string IntellectualPropertyNotices = "MENU_ID_INTELLECTUAL_PROPERTY_NOTICES";
    public const string ApplicationClose = "MENU_ID_APPLICATION_CLOSE";
}

/// <summary>Facts captured from the selected title and current local session.</summary>
public sealed record ShellTitleOptionHostFacts(
    string? TitleId,
    string? Path,
    bool CanOpenFolder,
    bool CanConfigureGame,
    bool CanRemoveFromLibrary,
    bool CanCopyPath,
    bool CanCopyTitleId,
    bool IsSelectedTitleRunning);

/// <summary>One typed row before it is bound to a UI callback.</summary>
public sealed record ShellTitleOptionItem(
    ShellTitleOptionActionKind Action,
    string Label,
    string? MenuId,
    ShellMenuIcon Icon,
    int Section,
    bool IsEnabled = true);

/// <summary>A caller-specific option-menu payload.</summary>
public sealed record ShellTitleOptionsModel(
    ShellTitleOptionsOwner Owner,
    IReadOnlyList<ShellTitleOptionItem> Items);

/// <summary>
/// HOME calls <c>VshNMHomeUIOptionMenu.getOptionMenu({titleId})</c> and gets a
/// dynamic payload, so no permanent Play row or fixed twelve-item list belongs
/// here. MENU_ID_APPLICATION_REMOVE_FROM_HOME is an ASSUMED mapping for the
/// host's non-destructive local-library hide action; all other host extensions
/// </summary>
public static class ShellTitleOptionsComposer
{
    public static ShellTitleOptionsModel ComposeHome(ShellTitleOptionHostFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var items = new List<ShellTitleOptionItem>();
        if (facts.IsSelectedTitleRunning)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.CloseApplication,
                "Close application",
                ShellTitleOptionMenuIds.ApplicationClose,
                ShellMenuIcons.CloseApplication,
                Section: 0));
        }

        if (facts.CanOpenFolder)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.OpenFolder,
                "Open game folder",
                MenuId: null,
                ShellMenuIcons.Folder,
                Section: 0));
        }

        if (facts.CanConfigureGame)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.ConfigureGame,
                "Game settings…",
                MenuId: null,
                ShellMenuIcons.Settings,
                Section: 0));
        }

        if (facts.CanRemoveFromLibrary)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.RemoveFromLibrary,
                "Remove from library",
                ShellTitleOptionMenuIds.ApplicationRemoveFromHome,
                ShellMenuIcons.Delete,
                Section: 1));
        }

        if (facts.CanCopyPath)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.CopyPath,
                "Copy path",
                MenuId: null,
                ShellMenuIcons.Copy,
                Section: 1));
        }

        if (facts.CanCopyTitleId)
        {
            items.Add(new ShellTitleOptionItem(
                ShellTitleOptionActionKind.CopyTitleId,
                "Copy title ID",
                MenuId: null,
                ShellMenuIcons.Copy,
                Section: 1));
        }

        return new ShellTitleOptionsModel(ShellTitleOptionsOwner.HomeTile, items);
    }

    /// <summary>
    /// Maps only the already-composed Hub overflow action list. It never calls
    /// the HOME composer, so the two native payload owners stay separate.
    /// </summary>
    public static ShellTitleOptionsModel ComposeHubOverflow(
        IReadOnlyList<ShellGameHubAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var items = new List<ShellTitleOptionItem>(actions.Count);
        foreach (var action in actions)
        {
            if (action.Kind == ShellGameHubActionKind.ConfigureGame)
            {
                items.Add(new ShellTitleOptionItem(
                    ShellTitleOptionActionKind.ConfigureGame,
                    action.Label,
                    MenuId: null,
                    ShellMenuIcons.Settings,
                    Section: 0));
            }
        }

        return new ShellTitleOptionsModel(ShellTitleOptionsOwner.GameHubOverflow, items);
    }

    /// <summary>Session identity is path-based so two titles with no title id
    /// cannot be confused by a selection change while a close dialog is open.</summary>
    public static bool IsCurrentRunningTitle(string? selectedPath, string? runningPath,
        StringComparison pathComparison) =>
        !string.IsNullOrWhiteSpace(selectedPath) &&
        !string.IsNullOrWhiteSpace(runningPath) &&
        string.Equals(selectedPath, runningPath, pathComparison);
}
