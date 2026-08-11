// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Action exposed by the Qt configuration-list context menu.
/// </summary>
public enum DesktopLibraryContextAction
{
    Run,
    OpenGameFolder,
    ViewTrophies,
    Patches,
    RemoveSaveData,
    EditCustomSettings,
    ClearCustomSettings,
}

/// <summary>Event data for an action associated with one game row.</summary>
public class DesktopLibraryGameEventArgs : EventArgs
{
    public DesktopLibraryGameEventArgs(GameEntry game) => Game = game;

    public GameEntry Game { get; }
}

/// <summary>Event data for a context-menu action.</summary>
public sealed class DesktopLibraryContextActionEventArgs : DesktopLibraryGameEventArgs
{
    public DesktopLibraryContextActionEventArgs(GameEntry game, DesktopLibraryContextAction action)
        : base(game) => Action = action;

    public DesktopLibraryContextAction Action { get; }
}

/// <summary>Event data for an editable status or comment cell.</summary>
public sealed class DesktopLibraryValueEditEventArgs : DesktopLibraryGameEventArgs
{
    public DesktopLibraryValueEditEventArgs(GameEntry game, string value)
        : base(game) => Value = value;

    public string Value { get; }
}

/// <summary>Event data for a clicked table header.</summary>
public sealed class DesktopLibrarySortEventArgs : EventArgs
{
    public DesktopLibrarySortEventArgs(int column) => Column = column;

    public int Column { get; }
}

/// <summary>
/// Compact desktop configuration list translated from Kyty's Qt launcher.
/// The control owns presentation and event routing; MainWindow owns scanning,
/// persistence, compatibility data, and launch policy.
/// </summary>
public partial class DesktopLibrarySurface : UserControl
{
    private GameEntry? _contextGame;

    public DesktopLibrarySurface()
    {
        InitializeComponent();
    }

    /// <summary>The existing host-facing game list accessor.</summary>
    public ListBox Games => DesktopGames;

    /// <summary>The existing host-facing search accessor.</summary>
    public TextBox SearchBox => DesktopSearchBox;

    public Button GlobalSettingsButton => DesktopGlobalSettingsButton;

    /// <summary>The Qt edit action, retained under the old host-facing name.</summary>
    public Button GameSettingsButton => DesktopGameSettingsButton;

    public Button EditButton => DesktopGameSettingsButton;

    public Button ClearCustomSettingsButton => DesktopClearCustomSettingsButton;

    public Button AddFolderButton => DesktopAddFolderButton;

    public Button RescanButton => DesktopRescanButton;

    public Button OpenFileButton => DesktopOpenFileButton;

    public Button BigPictureButton => DesktopBigPictureButton;

    public GameEntry? SelectedGame => DesktopGames.SelectedItem as GameEntry;

    public string SettingsFileText
    {
        get => DesktopSettingsFileText.Text ?? string.Empty;
        set => DesktopSettingsFileText.Text = value;
    }

    public string EmulatorText
    {
        get => DesktopEmulatorText.Text ?? string.Empty;
        set => DesktopEmulatorText.Text = value;
    }

    public string VersionText
    {
        get => DesktopVersionText.Text ?? string.Empty;
        set => DesktopVersionText.Text = value;
    }

    public event EventHandler? GlobalSettingsRequested;
    public event EventHandler? AddFolderRequested;
    public event EventHandler? RescanRequested;
    public event EventHandler? OpenFileRequested;
    public event EventHandler? BigPictureRequested;

    /// <summary>
    /// Compatibility event retained for MainWindow's existing integration;
    /// edit requests also raise the more explicit typed event below.
    /// </summary>
    public event EventHandler? GameSettingsRequested;

    public event EventHandler<DesktopLibraryGameEventArgs>? EditCustomSettingsRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? ClearCustomSettingsRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? LaunchRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? DoubleClickLaunchRequested;
    public event EventHandler<DesktopLibraryContextActionEventArgs>? ContextActionRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? OpenGameFolderRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? ViewTrophiesRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? PatchesRequested;
    public event EventHandler<DesktopLibraryGameEventArgs>? RemoveSaveDataRequested;
    public event EventHandler<DesktopLibraryValueEditEventArgs>? StatusEditRequested;
    public event EventHandler<DesktopLibraryValueEditEventArgs>? CommentEditRequested;
    public event EventHandler<DesktopLibrarySortEventArgs>? SortRequested;

    public bool IsLibraryEmpty
    {
        get => DesktopEmptyState.IsVisible;
        set
        {
            DesktopEmptyState.IsVisible = value;
            DesktopGames.IsVisible = !value;
        }
    }

    public bool IsBigPictureEnabled
    {
        get => DesktopBigPictureButton.IsEnabled;
        set => DesktopBigPictureButton.IsEnabled = value;
    }

    public bool IsGameSettingsEnabled
    {
        get => DesktopGameSettingsButton.IsEnabled;
        set => DesktopGameSettingsButton.IsEnabled = value;
    }

    /// <summary>
    /// The host can enable this only when the selected game has a custom
    /// profile, matching Qt's delete button semantics.
    /// </summary>
    public bool IsClearCustomSettingsEnabled
    {
        get => DesktopClearCustomSettingsButton.IsEnabled;
        set => DesktopClearCustomSettingsButton.IsEnabled = value;
    }

    private GameEntry? GetGame(object? sender)
    {
        if (sender is Control control && control.DataContext is GameEntry boundGame)
        {
            return boundGame;
        }

        if (sender is MenuItem menuItem &&
            (menuItem.Parent as ContextMenu ??
             menuItem.FindLogicalAncestorOfType<ContextMenu>()) is { } contextMenu)
        {
            return (contextMenu.PlacementTarget as Control)?.DataContext as GameEntry;
        }

        return sender is MenuItem ? _contextGame : null;
    }

    private void OnGameContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        _contextGame = sender is ContextMenu contextMenu
            ? (contextMenu.PlacementTarget as Control)?.DataContext as GameEntry
            : null;
    }

    private void OnGlobalSettingsClicked(object? sender, RoutedEventArgs e) =>
        GlobalSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddFolderClicked(object? sender, RoutedEventArgs e) =>
        AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private void OnRescanClicked(object? sender, RoutedEventArgs e) =>
        RescanRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenFileClicked(object? sender, RoutedEventArgs e) =>
        OpenFileRequested?.Invoke(this, EventArgs.Empty);

    private void OnBigPictureClicked(object? sender, RoutedEventArgs e) =>
        BigPictureRequested?.Invoke(this, EventArgs.Empty);

    private void OnEditCustomSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedGame is { } game)
        {
            RaiseEditCustomSettings(game);
        }
    }

    private void OnClearCustomSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedGame is { } game)
        {
            RaiseClearCustomSettings(game);
        }
    }

    private void RaiseEditCustomSettings(GameEntry game)
    {
        var args = new DesktopLibraryGameEventArgs(game);
        EditCustomSettingsRequested?.Invoke(this, args);
        GameSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseClearCustomSettings(GameEntry game)
    {
        var args = new DesktopLibraryGameEventArgs(game);
        ClearCustomSettingsRequested?.Invoke(this, args);
    }

    private void OnGamesDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SelectedGame is not { } game)
        {
            return;
        }

        var args = new DesktopLibraryGameEventArgs(game);
        LaunchRequested?.Invoke(this, args);
        DoubleClickLaunchRequested?.Invoke(this, args);
    }

    private void OnSortHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            !int.TryParse(button.Tag?.ToString(), out var column))
        {
            return;
        }

        SortRequested?.Invoke(this, new DesktopLibrarySortEventArgs(column));
    }

    private void OnStatusChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || GetGame(combo) is not { } game ||
            combo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        StatusEditRequested?.Invoke(
            this,
            new DesktopLibraryValueEditEventArgs(game, item.Content?.ToString() ?? string.Empty));
    }

    private void OnCommentLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || GetGame(textBox) is not { } game)
        {
            return;
        }

        CommentEditRequested?.Invoke(
            this,
            new DesktopLibraryValueEditEventArgs(game, textBox.Text ?? string.Empty));
    }

    private void RaiseContextAction(GameEntry game, DesktopLibraryContextAction action)
    {
        ContextActionRequested?.Invoke(
            this,
            new DesktopLibraryContextActionEventArgs(game, action));

        var args = new DesktopLibraryGameEventArgs(game);
        switch (action)
        {
            case DesktopLibraryContextAction.Run:
                LaunchRequested?.Invoke(this, args);
                break;
            case DesktopLibraryContextAction.OpenGameFolder:
                OpenGameFolderRequested?.Invoke(this, args);
                break;
            case DesktopLibraryContextAction.ViewTrophies:
                ViewTrophiesRequested?.Invoke(this, args);
                break;
            case DesktopLibraryContextAction.Patches:
                PatchesRequested?.Invoke(this, args);
                break;
            case DesktopLibraryContextAction.RemoveSaveData:
                RemoveSaveDataRequested?.Invoke(this, args);
                break;
            case DesktopLibraryContextAction.EditCustomSettings:
                RaiseEditCustomSettings(game);
                break;
            case DesktopLibraryContextAction.ClearCustomSettings:
                RaiseClearCustomSettings(game);
                break;
        }
    }

    private void OnRunContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.Run);

    private void OnOpenGameFolderContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.OpenGameFolder);

    private void OnViewTrophiesContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.ViewTrophies);

    private void OnPatchesContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.Patches);

    private void OnRemoveSaveDataContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.RemoveSaveData);

    private void OnEditCustomSettingsContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.EditCustomSettings);

    private void OnClearCustomSettingsContextAction(object? sender, RoutedEventArgs e) =>
        RaiseContextActionIfAvailable(sender, DesktopLibraryContextAction.ClearCustomSettings);

    private void RaiseContextActionIfAvailable(object? sender, DesktopLibraryContextAction action)
    {
        if (GetGame(sender) is { } game)
        {
            RaiseContextAction(game, action);
        }
    }
}
