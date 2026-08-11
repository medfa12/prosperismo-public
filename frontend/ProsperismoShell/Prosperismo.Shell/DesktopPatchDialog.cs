// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI;

/// <summary>
/// Avalonia translation of Kyty's experimental patch selector. It edits only
/// the enabled flags in the already-existing native patch plan.
/// </summary>
internal sealed class DesktopPatchDialog : Window
{
    private readonly string _path;
    private readonly JsonObject _root;
    private readonly JsonArray _patches;
    private readonly List<CheckBox> _checks = [];
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    private DesktopPatchDialog(string gameName, string path, JsonObject root, JsonArray patches)
    {
        _path = path;
        _root = root;
        _patches = patches;

        Title = $"Patches (Experimental) — {gameName}";
        Classes.Add("psDesktopDialog");
        Width = 640;
        Height = 480;
        MinWidth = 480;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFF5F7FA"));

        var list = new StackPanel { Spacing = 4, Margin = new Thickness(10) };
        for (var index = 0; index < patches.Count; index++)
        {
            var patch = patches[index] as JsonObject;
            var check = new CheckBox
            {
                Content = patch?["name"]?.GetValue<string>() ?? $"Patch {index + 1}",
                IsChecked = patch?["enabled"]?.GetValue<bool?>() ?? true,
                Padding = new Thickness(6, 4),
            };
            _checks.Add(check);
            list.Children.Add(check);
        }

        _status.Text = $"Loaded {patches.Count} patch(es) from {path}.";
        _status.Foreground = new SolidColorBrush(Color.Parse("#FF5B6573"));

        var apply = new Button
        {
            Content = "Apply selection",
            IsEnabled = patches.Count > 0,
            Classes = { "accent" },
        };
        var close = new Button { Content = "Close", Classes = { "ghost" } };
        apply.Click += (_, _) => Save();
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { apply, close },
        };
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10),
        };
        footer.Children.Add(_status);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        var rootGrid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        rootGrid.Children.Add(new ScrollViewer { Content = list });
        Grid.SetRow(footer, 1);
        rootGrid.Children.Add(footer);
        Content = rootGrid;
    }

    public static DesktopPatchDialog? TryCreate(
        string gameName,
        string titleId,
        string emulatorExecutablePath,
        out string? error)
    {
        error = null;
        try
        {
            var path = PatchPlanStore.ResolveExistingPlan(emulatorExecutablePath, titleId);
            if (path is null)
            {
                error = $"No local patch file exists for {titleId}.";
                return null;
            }

            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var patches = root?["patches"] as JsonArray;
            if (root is null || patches is null)
            {
                error = $"The patch file for {titleId} has no patches array.";
                return null;
            }

            return new DesktopPatchDialog(gameName, path, root, patches);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private void Save()
    {
        try
        {
            for (var index = 0; index < _patches.Count && index < _checks.Count; index++)
            {
                if (_patches[index] is JsonObject patch)
                {
                    patch["enabled"] = _checks[index].IsChecked == true;
                }
            }

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Patch file has no parent directory.");
            var temporary = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            _status.Text = "Patch selection saved.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not save patch selection: {exception.Message}";
        }
    }
}
