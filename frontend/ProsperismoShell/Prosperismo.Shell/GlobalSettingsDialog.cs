// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI;

/// <summary>
/// Host-neutral result event for a global settings edit. Hosts can apply the
/// typed settings and folder list, or simply read the dialog's public result
/// properties after ShowDialog returns.
/// </summary>
public sealed class GlobalSettingsAppliedEventArgs : EventArgs
{
    public GlobalSettingsAppliedEventArgs(EmulatorSettings settings, IReadOnlyList<string> gameFolders)
    {
        Settings = settings;
        GameFolders = gameFolders;
    }

    public EmulatorSettings Settings { get; }

    public IReadOnlyList<string> GameFolders { get; }
}

/// <summary>
/// Translates Kyty's ConfigurationEditDialog for global settings. The folder
/// picker is deliberately supplied by the host, keeping this dialog usable by
/// desktop, Big Picture, tests, or another Avalonia host without knowing any
/// StorageProvider implementation.
/// </summary>
public sealed class GlobalSettingsDialog : Window
{
    /// <summary>
    /// Returns one or more selected directories. The argument is the suggested
    /// starting directory; an empty result means the picker was cancelled.
    /// </summary>
    public delegate Task<IReadOnlyList<string>> GameFolderPickerAsync(string? suggestedStartDirectory);

    private readonly NativeSettingsEditor _editor;
    private readonly AvaloniaList<string> _gameFolders = new();
    private readonly ListBox _gameFolderList;
    private readonly GameFolderPickerAsync? _pickGameFoldersAsync;
    private readonly Action<EmulatorSettings>? _settingsChanged;
    private readonly Action<IReadOnlyList<string>>? _gameFoldersChanged;
    private readonly Border _surface;
    private readonly TextBlock _status;
    private bool _hideStarted;

    public GlobalSettingsDialog(
        EmulatorSettings settings,
        IEnumerable<string>? gameFolders = null,
        GameFolderPickerAsync? pickGameFoldersAsync = null,
        Action<EmulatorSettings>? settingsChanged = null,
        Action<IReadOnlyList<string>>? gameFoldersChanged = null)
    {
        EmulatorSettingsContract.Validate(settings);
        Settings = settings.Copy();
        _editor = new NativeSettingsEditor(settings);
        _pickGameFoldersAsync = pickGameFoldersAsync;
        _settingsChanged = settingsChanged;
        _gameFoldersChanged = gameFoldersChanged;
        AddGameFolders(gameFolders ?? []);

        Title = "Global settings";
        Classes.Add("psDesktopDialog");
        Resources["MutedBrush"] = new SolidColorBrush(Color.Parse("#FF5B6573"));
        Resources["TextBrush"] = new SolidColorBrush(Color.Parse("#FF1F1F1F"));
        Width = 700;
        Height = 650;
        MinWidth = 600;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#FFF5F7FA"));

        _gameFolderList = new ListBox
        {
            Classes = { "psDialogList" },
            ItemsSource = _gameFolders,
            SelectionMode = SelectionMode.Multiple,
            MinHeight = 116,
            MaxHeight = 170,
        };

        var add = Button("Add…", "ghost");
        var remove = Button("Remove", "ghost");
        add.IsEnabled = _pickGameFoldersAsync is not null;
        ToolTip.SetTip(add, _pickGameFoldersAsync is null
            ? "The host did not provide a folder picker."
            : "Add game folders");
        remove.IsEnabled = false;
        _gameFolderList.SelectionChanged += (_, _) =>
            remove.IsEnabled = (_gameFolderList.SelectedItems?.Count ?? 0) > 0;
        add.Click += async (_, _) => await AddGameFoldersFromHostAsync();
        remove.Click += (_, _) => RemoveSelectedGameFolders();

        var folderActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { add, remove },
        };
        var folders = Card(
            "GAME FOLDERS",
            new TextBlock
            {
                Text = "Directories scanned by the launcher for installed games.",
                Foreground = new SolidColorBrush(Color.Parse("#FF5B6573")),
                FontSize = 11,
            },
            _gameFolderList,
            folderActions);

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            Margin = new Thickness(16),
            Children = { folders, _editor.View },
        };

        var save = Button("Save", "accent");
        var clear = Button("Clear", "ghost");
        var cancel = Button("Cancel", "ghost");
        _status = StatusText();
        save.Click += (_, _) => ApplyAndClose();
        clear.Click += (_, _) => ClearSettings();
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { clear, cancel, save },
        };
        var buttonBarLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        buttonBarLayout.Children.Add(_status);
        Grid.SetColumn(actions, 1);
        buttonBarLayout.Children.Add(actions);
        var buttonBar = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#FFD0D7DE")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16),
            Child = buttonBarLayout,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = content });
        Grid.SetRow(buttonBar, 1);
        root.Children.Add(buttonBar);
        _surface = new Border { Classes = { "psDesktopCard" }, Margin = new Thickness(12), Child = root };
        Content = _surface;

        Opened += (_, _) => _ = ShellMotion.ShowSurfaceAsync(_surface);
        Closing += OnSurfaceClosing;
    }

    /// <summary>
    /// Current result, useful when a host chooses not to subscribe to Applied.
    /// </summary>
    public EmulatorSettings Settings { get; private set; } = new();

    /// <summary>
    /// Current normalized, de-duplicated game-folder result.
    /// </summary>
    public IReadOnlyList<string> GameFolders => _gameFolders.ToArray();

    public event EventHandler<GlobalSettingsAppliedEventArgs>? Applied;

    private async Task AddGameFoldersFromHostAsync()
    {
        try
        {
            var start = _gameFolders.LastOrDefault();
            var selected = await _pickGameFoldersAsync!(start);
            AddGameFolders(selected);
            _status.Text = string.Empty;
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not choose game folders: {exception.Message}";
        }
    }

    private void ApplyAndClose()
    {
        try
        {
            Settings = _editor.Capture();
            var folders = GameFolders;
            _settingsChanged?.Invoke(Settings.Copy());
            _gameFoldersChanged?.Invoke(folders);
            Applied?.Invoke(this, new GlobalSettingsAppliedEventArgs(Settings.Copy(), folders));
            Close();
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not save global settings: {exception.Message}";
        }
    }

    private void ClearSettings()
    {
        Settings = new EmulatorSettings();
        _editor.Load(Settings);
        _gameFolders.Clear();
        _status.Text = "Settings and game folders reset; save to apply.";
    }

    private void AddGameFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var normalized = NormalizeGameFolder(folder);
            if (normalized.Length > 0 && !_gameFolders.Contains(normalized, FilePathComparer))
            {
                _gameFolders.Add(normalized);
            }
        }
    }

    private void RemoveSelectedGameFolders()
    {
        var selected = _gameFolderList.SelectedItems?.OfType<string>().ToArray() ?? [];
        foreach (var folder in selected)
        {
            _gameFolders.Remove(folder);
        }
    }

    private static string NormalizeGameFolder(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static StringComparer FilePathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private void OnSurfaceClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_hideStarted || e.CloseReason != WindowCloseReason.WindowClosing)
        {
            return;
        }

        e.Cancel = true;
        _hideStarted = true;
        _ = HideThenCloseAsync();
    }

    private async Task HideThenCloseAsync()
    {
        await ShellMotion.HideSurfaceAsync(_surface);
        Close();
    }

    private static Button Button(string content, string @class) => new()
    {
        Content = content,
        Classes = { @class },
        MinWidth = 74,
    };

    private static TextBlock StatusText() => new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#FFFF8A8A")),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private static SettingRow Row(string label, string description, Control value) => new()
    {
        Label = label,
        Description = description,
        Content = value,
    };

    private static Border Card(string title, params Control[] rows)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 12 };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "sectionTitle" } });
        foreach (var row in rows)
        {
            stack.Children.Add(row);
        }

        return new Border { Classes = { "card" }, Child = stack };
    }
}
