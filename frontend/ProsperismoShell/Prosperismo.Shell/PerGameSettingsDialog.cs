// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Prosperismo.GUI;

/// <summary>
/// The common native settings editor used by the global and per-game dialogs.
/// Its fields map one-to-one to the typed <see cref="EmulatorSettings"/> contract
/// and follow the four logical groups in Kyty's configuration_edit_dialog.ui.
/// </summary>
internal sealed class NativeSettingsEditor
{
    private readonly EmulatorSettings _fallback;

    private readonly ComboBox _resolution = Combo(Enum.GetValues<EmulatorResolution>());
    private readonly NumericUpDown _vblank = new()
    {
        Width = 180,
        Minimum = EmulatorSettingsContract.MinimumVblankFrequency,
        Maximum = EmulatorSettingsContract.MaximumVblankFrequency,
        Increment = 1,
        FormatString = "0 Hz",
    };
    private readonly ToggleSwitch _vulkanValidation = Toggle();
    private readonly ToggleSwitch _shaderValidation = Toggle();
    private readonly ComboBox _shaderOptimization = Combo(Enum.GetValues<ShaderOptimizationMode>());
    private readonly ToggleSwitch _nggRectlist = Toggle();
    private readonly ToggleSwitch _commandBufferDump = Toggle();
    private readonly TextBox _commandBufferFolder = TextEntry();
    private readonly ToggleSwitch _renderDoc = Toggle();
    private readonly ComboBox _shaderLogDirection = Combo(Enum.GetValues<EmulatorOutputDirection>());
    private readonly TextBox _shaderLogFolder = TextEntry();
    private readonly ComboBox _printfDirection = Combo(Enum.GetValues<EmulatorOutputDirection>());
    private readonly TextBox _printfFile = TextEntry();
    private readonly ComboBox _profilerDirection = Combo(Enum.GetValues<EmulatorProfilerDirection>());
    private readonly TabControl _content;
    private readonly Control[] _editableControls;
    private bool _isEnabled = true;

    public NativeSettingsEditor(EmulatorSettings initial)
    {
        _fallback = initial.Copy();

        _content = new TabControl();
        _content.Items.Add(Tab("Emulation",
            Row("Screen resolution", "Window resolution.", _resolution),
            Row("Vblank frequency", "Virtual display refresh rate used for frame pacing.", _vblank)));
        _content.Items.Add(Tab("Graphics",
            Row("Vulkan validation", "Enable Vulkan validation layers.", _vulkanValidation),
            Row("Shader validation", "Validate SPIR-V binary.", _shaderValidation),
            Row("Shader optimization type", "Optimize shaders for code size or performance.", _shaderOptimization),
            Row("NGG rect-list draw", "Use the NGG four-vertex path for rect-list DrawIndexAuto.", _nggRectlist)));
        _content.Items.Add(Tab("Debugging",
            Row("Command buffer dump", "Dump command buffers.", _commandBufferDump),
            Row("Command buffer dump folder", "Directory used to dump command buffers.", _commandBufferFolder),
            Row("RenderDoc capture", "Enable RenderDoc capture.", _renderDoc)));
        _content.Items.Add(Tab("Output",
            Row("Shader log direction", "Dump shaders to a file or console; may reduce performance.", _shaderLogDirection),
            Row("Shader log folder", "Directory used to dump shaders.", _shaderLogFolder),
            Row("Printf direction", "Print guest logs to a file or console; may reduce performance.", _printfDirection),
            Row("Printf output file", "File used to dump guest logs.", _printfFile),
            Row("Profiler direction", "Enable or disable the profiler.", _profilerDirection)));

        _editableControls =
        [
            _resolution,
            _vblank,
            _vulkanValidation,
            _shaderValidation,
            _shaderOptimization,
            _nggRectlist,
            _commandBufferDump,
            _commandBufferFolder,
            _renderDoc,
            _shaderLogDirection,
            _shaderLogFolder,
            _printfDirection,
            _printfFile,
            _profilerDirection,
        ];

        Load(initial);
        _shaderLogDirection.SelectionChanged += (_, _) => UpdateDependencies();
        _commandBufferDump.IsCheckedChanged += (_, _) => UpdateDependencies();
        _printfDirection.SelectionChanged += (_, _) => UpdateDependencies();
        UpdateDependencies();
    }

    public Control View => _content;

    public void Load(EmulatorSettings settings)
    {
        EmulatorSettingsContract.Validate(settings);
        _resolution.SelectedItem = settings.ScreenResolution;
        _vblank.Value = settings.VblankFrequency;
        _vulkanValidation.IsChecked = settings.VulkanValidation;
        _shaderValidation.IsChecked = settings.ShaderValidation;
        _shaderOptimization.SelectedItem = settings.ShaderOptimization;
        _nggRectlist.IsChecked = settings.NggRectlistDraw;
        _commandBufferDump.IsChecked = settings.CommandBufferDump;
        _commandBufferFolder.Text = settings.CommandBufferDumpFolder;
        _renderDoc.IsChecked = settings.RenderDoc;
        _shaderLogDirection.SelectedItem = settings.ShaderLogDirection;
        _shaderLogFolder.Text = settings.ShaderLogFolder;
        _printfDirection.SelectedItem = settings.PrintfDirection;
        _printfFile.Text = settings.PrintfOutputFile;
        _profilerDirection.SelectedItem = settings.ProfilerDirection;
        UpdateDependencies();
    }

    public EmulatorSettings Capture()
    {
        var settings = new EmulatorSettings
        {
            ScreenResolution = _resolution.SelectedItem is EmulatorResolution resolution
                ? resolution
                : _fallback.ScreenResolution,
            VblankFrequency = Math.Clamp(
                (int)(_vblank.Value ?? _fallback.VblankFrequency),
                EmulatorSettingsContract.MinimumVblankFrequency,
                EmulatorSettingsContract.MaximumVblankFrequency),
            VulkanValidation = _vulkanValidation.IsChecked == true,
            ShaderValidation = _shaderValidation.IsChecked == true,
            ShaderOptimization = _shaderOptimization.SelectedItem is ShaderOptimizationMode optimization
                ? optimization
                : _fallback.ShaderOptimization,
            ShaderLogDirection = _shaderLogDirection.SelectedItem is EmulatorOutputDirection shaderOutput
                ? shaderOutput
                : _fallback.ShaderLogDirection,
            ShaderLogFolder = Required(_shaderLogFolder.Text, _fallback.ShaderLogFolder),
            CommandBufferDump = _commandBufferDump.IsChecked == true,
            CommandBufferDumpFolder = Required(
                _commandBufferFolder.Text,
                _fallback.CommandBufferDumpFolder),
            PrintfDirection = _printfDirection.SelectedItem is EmulatorOutputDirection printfOutput
                ? printfOutput
                : _fallback.PrintfDirection,
            PrintfOutputFile = Required(_printfFile.Text, _fallback.PrintfOutputFile),
            ProfilerDirection = _profilerDirection.SelectedItem is EmulatorProfilerDirection profiler
                ? profiler
                : _fallback.ProfilerDirection,
            RenderDoc = _renderDoc.IsChecked == true,
            NggRectlistDraw = _nggRectlist.IsChecked == true,
        };
        EmulatorSettingsContract.Validate(settings);
        return settings;
    }

    public void SetEnabled(bool enabled)
    {
        // Keep the tabs and effective values readable while an inherited
        // profile is read-only. Fluent's disabled templates replace ComboBox
        // and ToggleSwitch content with dark-theme colors, so interaction is
        // gated explicitly rather than changing the controls' visual state.
        _isEnabled = enabled;
        foreach (var control in _editableControls)
        {
            control.IsHitTestVisible = enabled;
            control.Focusable = enabled;
            control.Opacity = enabled ? 1 : 0.72;
            if (control is TextBox textBox)
            {
                textBox.IsReadOnly = !enabled;
            }
        }
        UpdateDependencies();
    }

    private void UpdateDependencies()
    {
        var enabled = _isEnabled;
        _shaderLogFolder.IsEnabled = enabled &&
            _shaderLogDirection.SelectedItem is EmulatorOutputDirection.File;
        _commandBufferFolder.IsEnabled = enabled && _commandBufferDump.IsChecked == true;
        _printfFile.IsEnabled = enabled &&
            _printfDirection.SelectedItem is EmulatorOutputDirection.File;
    }

    private static TabItem Tab(string title, params Control[] rows)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 14,
            Margin = new Thickness(4, 12, 4, 4),
        };
        foreach (var row in rows)
        {
            stack.Children.Add(row);
        }

        return new TabItem
        {
            Header = title,
            Content = new ScrollViewer { Content = stack },
        };
    }

    private static ToggleSwitch Toggle() => new()
    {
        OnContent = "On",
        OffContent = "Off",
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static ComboBox Combo<T>(IEnumerable<T> items) => new()
    {
        ItemsSource = items,
        Width = 180,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox TextEntry() => new()
    {
        Width = 260,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static SettingRow Row(string label, string description, Control value) => new()
    {
        Label = label,
        Description = description,
        Content = value,
    };

    private static string Required(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>
/// Edits the complete native profile for one game installation. This mirrors
/// Kyty's custom-settings switch: disabled means inherit the global profile;
/// enabled stores a complete snapshot for this exact installation path.
/// </summary>
public sealed class PerGameSettingsDialog : Window
{
    private readonly string _gamePath;
    private readonly string? _titleId;
    private readonly EmulatorSettings _globalSettings;
    private readonly PerGameEmulatorSettingsStore _store;
    private readonly NativeSettingsEditor _editor;
    private readonly Border _surface;
    private readonly ToggleSwitch _useCustom;
    private readonly TextBlock _status;
    private bool _hideStarted;

    /// <summary>
    /// Kept source-compatible with the existing MainWindow call site.
    /// </summary>
    public PerGameSettingsDialog(
        string gamePath,
        string? titleId,
        string displayName,
        EmulatorSettings globalSettings,
        PerGameEmulatorSettingsStore store,
        IEnumerable<string>? matchingTitleInstallPaths = null,
        bool useDesktopDesign = false)
    {
        _gamePath = Path.GetFullPath(gamePath);
        _titleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim();
        _globalSettings = globalSettings.Copy();
        _store = store;
        _editor = new NativeSettingsEditor(_globalSettings);
        _useCustom = Toggle();

        Title = $"Game settings — {displayName}";
        if (useDesktopDesign)
        {
            Classes.Add("psDesktopDialog");
            Resources["MutedBrush"] = new SolidColorBrush(Color.Parse("#FF5B6573"));
            Resources["TextBrush"] = new SolidColorBrush(Color.Parse("#FF1F1F1F"));
        }
        Width = 640;
        Height = 560;
        MinWidth = 560;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse(
            useDesktopDesign ? "#FFF5F7FA" : "#020408"));

        var existing = _store.Load(_gamePath, _titleId, matchingTitleInstallPaths);
        _useCustom.IsChecked = existing is not null;
        _editor.Load(existing?.Settings ?? _globalSettings);

        var identity = new TextBlock
        {
            Text = _titleId is null ? _gamePath : $"{_titleId}  ·  {_gamePath}",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse(
                useDesktopDesign ? "#FF5B6573" : "#99FFFFFF")),
            TextWrapping = TextWrapping.Wrap,
        };
        var profile = Card("PROFILE", Row(
            "Custom profile",
            "Off inherits global settings; On stores a complete per-installation profile.",
            _useCustom));

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 10,
            Margin = new Thickness(16),
            Children = { identity, profile, _editor.View },
        };

        var save = Button("Save", "accent");
        var clear = Button("Clear", "ghost");
        var cancel = Button("Cancel", "ghost");
        _status = StatusText();
        save.Click += (_, _) => SaveProfile();
        clear.Click += (_, _) => ClearProfile();
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
            BorderBrush = new SolidColorBrush(Color.Parse(
                useDesktopDesign ? "#FFD0D7DE" : "#1AFFFFFF")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16),
            Child = buttonBarLayout,
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(new ScrollViewer { Content = content });
        Grid.SetRow(buttonBar, 1);
        root.Children.Add(buttonBar);
        _surface = new Border
        {
            Classes = { useDesktopDesign ? "psDesktopCard" : "ps5Card" },
            Margin = new Thickness(12),
            Child = root,
        };
        Content = _surface;

        _useCustom.IsCheckedChanged += (_, _) => _editor.SetEnabled(_useCustom.IsChecked == true);
        _editor.SetEnabled(_useCustom.IsChecked == true);
        Opened += (_, _) => _ = ShellMotion.ShowSurfaceAsync(_surface);
        Closing += OnSurfaceClosing;
    }

    private void SaveProfile()
    {
        try
        {
            if (_useCustom.IsChecked == true)
            {
                _store.Save(_gamePath, _titleId, _editor.Capture());
            }
            else
            {
                _store.Delete(_gamePath);
            }

            Close();
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not save game settings: {exception.Message}";
        }
    }

    private void ClearProfile()
    {
        _useCustom.IsChecked = false;
        _editor.Load(_globalSettings);
        _editor.SetEnabled(false);
        _status.Text = "Custom settings cleared; save to inherit global settings.";
    }

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

    private static ToggleSwitch Toggle() => new()
    {
        OnContent = "On",
        OffContent = "Off",
        VerticalAlignment = VerticalAlignment.Center,
    };

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
