// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;

namespace Prosperismo.GUI.Controls;

/// <summary>Actions exposed by NPXS40002's function-control profile module.</summary>
public enum ShellProfilePanelAction
{
    OnlineStatus,
    ViewProfile,
    ViewTrophies,
    SwitchUser,
    LogOut,
}

/// <summary>Typed row payload shared by HOME and Control Center profile entry points.</summary>
public sealed record ShellProfilePanelActionTag(ShellProfilePanelAction Action);

/// <summary>
/// Host projection of <c>@rnps-ppr/function-control-profile</c>. The local
/// focus on Profile (index 1).
/// </summary>
public static class ShellProfilePanelComposer
{
    public const int OfflineInitialSelectedIndex = 1;

    public static IReadOnlyList<ShellFunctionPanelItem> ComposeOffline() =>
    [
        new ShellFunctionPanelItem(
            "Online Status",
            tag: new ShellProfilePanelActionTag(ShellProfilePanelAction.OnlineStatus))
        {
            LeadingIconId = "person_online",
            TrailingText = "Offline",
            TrailingIndicatorColor = Color.Parse("#8A8D93"),
            IsEnabled = false,
            DimWhenDisabled = false,
        },
        new ShellFunctionPanelItem(
            "Profile",
            tag: new ShellProfilePanelActionTag(ShellProfilePanelAction.ViewProfile))
        {
            LeadingIconId = "ps_user",
        },
        new ShellFunctionPanelItem(
            "Trophies",
            tag: new ShellProfilePanelActionTag(ShellProfilePanelAction.ViewTrophies))
        {
            LeadingIconId = "trophies",
        },
        new ShellFunctionPanelItem(
            "Switch User",
            tag: new ShellProfilePanelActionTag(ShellProfilePanelAction.SwitchUser))
        {
            LeadingIconId = "person",
        },
        new ShellFunctionPanelItem(
            "Log Out",
            tag: new ShellProfilePanelActionTag(ShellProfilePanelAction.LogOut))
        {
            LeadingIconId = "logout",
            ShowSeparator = false,
        },
    ];
}
