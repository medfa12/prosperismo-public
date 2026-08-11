// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Prosperismo.GUI;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

public sealed record ShellSettingsDetailRow(string ItemId, string Label, string? Value = null);

public sealed record ShellSettingsDetailTab(
    string TabId,
    string Label,
    string TestId,
    bool IsSony,
    IReadOnlyList<ShellSettingsDetailRow> Rows);

internal sealed record ShellSettingsChoiceOption(string Label, object Value);

/// <summary>
/// Stable native-emulator setting identifiers used by both the Big Picture
/// presentation and the settings/launch integration layer.  These are UI
/// identifiers, not command-line switches.
/// </summary>
public static class ShellEmulatorSettingIds
{
    public const string ScreenResolution = "emulator.screen-resolution";
    public const string VblankFrequency = "emulator.vblank-frequency";
    public const string VulkanValidation = "emulator.vulkan-validation";
    public const string ShaderValidation = "emulator.shader-validation";
    public const string ShaderOptimization = "emulator.shader-optimization";
    public const string ShaderLogDirection = "emulator.shader-log-direction";
    public const string ShaderLogFolder = "emulator.shader-log-folder";
    public const string CommandBufferDump = "emulator.command-buffer-dump";
    public const string CommandBufferDumpFolder = "emulator.command-buffer-dump-folder";
    public const string PrintfDirection = "emulator.printf-direction";
    public const string PrintfOutputFile = "emulator.printf-output-file";
    public const string ProfilerDirection = "emulator.profiler-direction";
    public const string RenderDoc = "emulator.renderdoc";
    public const string NggRectlistDraw = "emulator.ngg-rectlist-draw";
}

/// <summary>
/// Prosperismo settings hosted by the recovered NPXS40008 vertical-tab and
/// Native emulator settings deliberately use <see cref="EmulatorSettings"/>,
/// while launcher preferences remain in their own rows.
/// </summary>
public sealed class ShellSettingsDetailList : Panel
{
    // Native TabViewPS owns the "wide" centre gap. The JS exposes 96 as its
    // panel-left constant but not the final absolute placement semantics. Keep
    // this provisional composition seam named honestly until TabViewPS runs.
    public const double ProvisionalContentLeft =
        ShellSettingsMetrics.TabLeft + ShellSettingsMetrics.TabWidth + ShellSettingsMetrics.TabPanelLeft;

    // MenuListItemPS owns this geometry natively. This is only the current
    private const double DiagnosticRowPitch = 112;
    // RCTMenuListDropdown owns this row metric. Six 84-unit choices fill the
    // recovered 504-unit scroll ceiling exactly, but 84 itself remains a
    // diagnostic host value until the native view manager is measured.
    private const double DiagnosticChoiceRowPitch = 84;
    // The dropdown's native width is unresolved. This uses the closest
    // recovered shell popup floor rather than presenting a desktop ComboBox.
    private const double DiagnosticChoicePopupWidth = 652;
    private const double ChoicePopupRightMargin = 48;
    // TabViewPS owns this metric. The 980x552 retail System frame measures
    // adjacent tab baselines 55-56 px apart: 110 design units at 1920x1080.
    public const double CapturedTabPitch = 110;
    private const int VisibleRows = 8;

    private static readonly IBrush SeparatorBrush =
        new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));

    public static IReadOnlyList<ShellSettingsDetailTab> Tabs { get; } =
    [
        new(
            "id_prosperismo_general",
            "General",
            "tab-general",
            false,
            [
                new("id_language", "Language", "English"),
                new("id_discord_presence", "Discord Presence"),
                new("id_auto_update", "Check for Updates on Startup"),
                new("id_rescan_library", "Rescan Game Library"),
            ]),
        new(
            "id_prosperismo_graphics",
            "Graphics",
            "tab-graphics",
            false,
            [
                new(ShellEmulatorSettingIds.VulkanValidation, "Vulkan Validation"),
                new(ShellEmulatorSettingIds.ShaderValidation, "Shader Validation"),
                new(ShellEmulatorSettingIds.ShaderOptimization, "Shader Optimization"),
                new(ShellEmulatorSettingIds.NggRectlistDraw, "NGG Rect-List Draw"),
            ]),
        new(
            "id_prosperismo_audio_ui",
            "Audio and Interface",
            "tab-audio-ui",
            false,
            [
                new("id_title_music", "Title Music"),
                new("id_shell_motion", "Background Motion"),
                new("id_ui_sounds", "UI Sounds"),
                new("id_shell_music", "Home Music"),
                new("id_boot_intro", "Boot Animation"),
            ]),
        new(
            "id_prosperismo_emulation",
            "Emulation",
            "tab-emulation",
            false,
            [
                new(ShellEmulatorSettingIds.ScreenResolution, "Window Resolution"),
                new(ShellEmulatorSettingIds.VblankFrequency, "Vblank Frequency"),
            ]),
        new(
            "id_prosperismo_debugging",
            "Debugging",
            "tab-debugging",
            false,
            [
                new(ShellEmulatorSettingIds.RenderDoc, "RenderDoc Capture"),
                new(ShellEmulatorSettingIds.CommandBufferDump, "Command Buffer Dump"),
                new(ShellEmulatorSettingIds.CommandBufferDumpFolder, "Command Buffer Folder"),
            ]),
        new(
            "id_prosperismo_output",
            "Output",
            "tab-output",
            false,
            [
                new(ShellEmulatorSettingIds.ShaderLogDirection, "Shader Log Output"),
                new(ShellEmulatorSettingIds.ShaderLogFolder, "Shader Log Folder"),
                new(ShellEmulatorSettingIds.PrintfDirection, "Guest Printf Output"),
                new(ShellEmulatorSettingIds.PrintfOutputFile, "Guest Printf File"),
                new(ShellEmulatorSettingIds.ProfilerDirection, "Network Profiler"),
            ]),
        new(
            "id_prosperismo_about",
            "About Prosperismo",
            "tab-about",
            false,
            [
                new("id_build", "Current Build"),
                new("id_check_updates", "Check for Updates"),
                new("id_github", "GitHub"),
                new("id_discord", "Discord Community"),
            ]),
    ];

    private readonly Canvas _tabs = new();
    private readonly Canvas _rows = new();
    private readonly Canvas _choicePopup = new();
    private readonly Ps5Ui3ChromePlate _choicePopupPlate = new();
    private readonly ShellSettingsRouteTransition? _routeTransition;
    private readonly Dictionary<int, Control> _visibleRows = new();
    private readonly Dictionary<string, Ps5ToggleSwitch> _visibleToggles =
        new(StringComparer.Ordinal);
    private readonly Dictionary<int, Control> _visibleChoiceRows = new();
    private readonly TextBlock _heading;
    private int _selectedTab;
    private int _selectedRow;
    private int _firstVisibleRow;
    private string? _choiceItemId;
    private IReadOnlyList<ShellSettingsChoiceOption> _choiceOptions = [];
    private int _selectedChoiceIndex;
    private bool _rowsHaveFocus;
    private double _renderResolutionScale = 1;
    private bool _playTitleMusic = true;
    private bool _animateShellBackground = true;
    private bool _playUiSounds = true;
    private bool _playShellMusic = true;
    private bool _playBootIntro = true;
    private bool _discordPresence = true;
    private bool _checkUpdates = true;
    private bool _strictDynlib;
    private bool _logToFile;
    private bool _overrideLogFile;
    private readonly HashSet<string> _enabledEnvironmentRows = new(StringComparer.Ordinal);
    private string _languageName = "English";
    private string _logLevel = "Info";
    private int _importTraceLimit;
    private string _logFilePath = "Default";
    private EmulatorResolution _screenResolution = EmulatorResolution.R1280X720;
    private int _vblankFrequency = 60;
    private bool _vulkanValidation = true;
    private bool _shaderValidation = true;
    private ShaderOptimizationMode _shaderOptimization = ShaderOptimizationMode.Performance;
    private EmulatorOutputDirection _shaderLogDirection = EmulatorOutputDirection.Silent;
    private string _shaderLogFolder = "_Shaders";
    private bool _commandBufferDump;
    private string _commandBufferDumpFolder = "_Buffers";
    private EmulatorOutputDirection _printfDirection = EmulatorOutputDirection.Silent;
    private string _printfOutputFile = "_prosperismo.txt";
    private EmulatorProfilerDirection _profilerDirection = EmulatorProfilerDirection.None;
    private bool _renderDoc;
    private bool _nggRectlistDraw = true;

    public ShellSettingsDetailList()
    {
        Width = Ps5DesignSpace.Width;
        Height = Ps5DesignSpace.Height;
        Background = Brushes.Transparent;
        Focusable = true;
        ClipToBounds = true;

        _heading = new TextBlock
        {
            Text = Tabs[0].Label,
            FontSize = Ps5FontScale.SizeLarge,
            Foreground = Brushes.White,
            Margin = new Thickness(96, 82, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Children.Add(_heading);

        _tabs.Width = ShellSettingsMetrics.TabWidth;
        _tabs.Height = ShellSettingsMetrics.TabPanelHeight;
        _tabs.Margin = new Thickness(ShellSettingsMetrics.TabLeft, ShellSettingsMetrics.TabTop, 0, 0);
        _tabs.HorizontalAlignment = HorizontalAlignment.Left;
        _tabs.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_tabs);

        _rows.Width = ShellSettingsMetrics.TabPanelWidth;
        _rows.Height = ShellSettingsMetrics.TabPanelHeight;
        _rows.ClipToBounds = true;
        _rows.Margin = new Thickness(ProvisionalContentLeft, ShellSettingsMetrics.TabTop, 0, 0);
        _rows.HorizontalAlignment = HorizontalAlignment.Left;
        _rows.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_rows);

        _choicePopup.Width = DiagnosticChoicePopupWidth;
        _choicePopup.HorizontalAlignment = HorizontalAlignment.Left;
        _choicePopup.VerticalAlignment = VerticalAlignment.Top;
        _choicePopup.IsVisible = false;
        _choicePopup.ClipToBounds = true;
        _choicePopupPlate.Width = DiagnosticChoicePopupWidth;
        _choicePopupPlate.Asset = Ps5Ui3ChromeAsset.MenuBase;
        _choicePopupPlate.AssetOpacity = .28;
        _choicePopupPlate.SliceCornerRadius = 16;
        _choicePopupPlate.FallbackBrush = new SolidColorBrush(Color.Parse("#080A0F"));
        _choicePopup.Children.Add(_choicePopupPlate);
        Children.Add(_choicePopup);

        _routeTransition = new ShellSettingsRouteTransition(this);

        GotFocus += (_, _) => QueueFocusRect();
        EffectiveViewportChanged += (_, _) => QueueFocusRect();
        LostFocus += (_, _) => ShellFocusRing.For(this)?.Release(this);
        AttachedToVisualTree += (_, _) => QueueFocusRect();
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (change.GetNewValue<bool>())
            {
                _routeTransition?.Enter();
            }
            else
            {
                _routeTransition?.Cancel();
            }
        }
    }

    public event EventHandler? BackRequested;
    public event EventHandler? RenderResolutionScaleChanged;
    /// <summary>
    /// Raised once for every mutable Big Picture settings interaction. Consumers read
    /// <see cref="LastChangedEmulatorSettingId"/> to distinguish native
    /// emulator rows; launcher-preference interactions leave it null.
    /// </summary>
    public event EventHandler? EmulatorSettingChanged;
    public event Action<string>? EmulatorTextSettingRequested;
    public event EventHandler? LanguageCycleRequested;
    // Compatibility seam for the desktop host while its obsolete host-log
    // picker is moved out of the native emulator settings flow.
    public event EventHandler? LogFilePathRequested;
    public event Action<string>? ActionRequested;

    public int SelectedTabIndex
    {
        get => _selectedTab;
        set
        {
            SetSelectedTab(value);
            // TabbedList module 73 sets _isSetFocusOnPanel for an initial tab
            // and calls TabViewPS.setFocusOnPanel() after panel mount.
            _rowsHaveFocus = true;
            QueueFocusRect();
        }
    }

    /// <summary>Whether TabViewPS-equivalent focus is in the content panel.</summary>
    internal bool IsPanelFocused => _rowsHaveFocus;

    /// <summary>The focused content row, exposed for navigation verification.</summary>
    internal int SelectedRowIndex => _selectedRow;

    /// <summary>Whether a native-shaped dropdown currently owns navigation.</summary>
    internal bool IsChoicePopupOpen => _choiceItemId is not null;

    internal string? ActiveChoiceItemId => _choiceItemId;

    internal int SelectedChoiceIndex => _selectedChoiceIndex;

    internal int VisibleToggleTransitionCount =>
        _visibleToggles.Values.Count(static toggle => toggle.IsTransitionRunning);

    internal IReadOnlyList<string> ActiveChoiceLabels =>
        _choiceOptions.Select(option => option.Label).ToArray();

    public double RenderResolutionScale
    {
        get => _renderResolutionScale;
        set
        {
            _renderResolutionScale = NearestScale(value);
            RebuildRows();
        }
    }

    public bool IsShowingSonyTab => Tabs[_selectedTab].IsSony;

    /// <summary>The stable ID most recently changed by a native settings interaction.</summary>
    public string? LastChangedEmulatorSettingId { get; private set; }

    // Native settings accessors. Their defaults mirror EmulatorSettings/Kyty,
    // and are intentionally not mapped to obsolete managed-backend preferences.
    public EmulatorResolution ScreenResolution { get => _screenResolution; set => SetEmulatorOption(ref _screenResolution, value); }
    public int VblankFrequency { get => _vblankFrequency; set => SetEmulatorOption(ref _vblankFrequency, ClampVblank(value)); }
    public bool VulkanValidation { get => _vulkanValidation; set => SetEmulatorOption(ref _vulkanValidation, value); }
    public bool ShaderValidation { get => _shaderValidation; set => SetEmulatorOption(ref _shaderValidation, value); }
    public ShaderOptimizationMode ShaderOptimization { get => _shaderOptimization; set => SetEmulatorOption(ref _shaderOptimization, value); }
    public EmulatorOutputDirection ShaderLogDirection { get => _shaderLogDirection; set => SetEmulatorOption(ref _shaderLogDirection, value); }
    public string ShaderLogFolder { get => _shaderLogFolder; set => SetEmulatorText(ref _shaderLogFolder, value, "_Shaders"); }
    public bool CommandBufferDump { get => _commandBufferDump; set => SetEmulatorOption(ref _commandBufferDump, value); }
    public string CommandBufferDumpFolder { get => _commandBufferDumpFolder; set => SetEmulatorText(ref _commandBufferDumpFolder, value, "_Buffers"); }
    public EmulatorOutputDirection PrintfDirection { get => _printfDirection; set => SetEmulatorOption(ref _printfDirection, value); }
    public string PrintfOutputFile { get => _printfOutputFile; set => SetEmulatorText(ref _printfOutputFile, value, "_prosperismo.txt"); }
    public EmulatorProfilerDirection ProfilerDirection { get => _profilerDirection; set => SetEmulatorOption(ref _profilerDirection, value); }
    public bool RenderDoc { get => _renderDoc; set => SetEmulatorOption(ref _renderDoc, value); }
    public bool NggRectlistDraw { get => _nggRectlistDraw; set => SetEmulatorOption(ref _nggRectlistDraw, value); }

    /// <summary>Snapshots the complete native setting state for a launch request.</summary>
    public EmulatorSettings GetEmulatorSettings() => new()
    {
        ScreenResolution = _screenResolution,
        VblankFrequency = _vblankFrequency,
        VulkanValidation = _vulkanValidation,
        ShaderValidation = _shaderValidation,
        ShaderOptimization = _shaderOptimization,
        ShaderLogDirection = _shaderLogDirection,
        ShaderLogFolder = _shaderLogFolder,
        CommandBufferDump = _commandBufferDump,
        CommandBufferDumpFolder = _commandBufferDumpFolder,
        PrintfDirection = _printfDirection,
        PrintfOutputFile = _printfOutputFile,
        ProfilerDirection = _profilerDirection,
        RenderDoc = _renderDoc,
        NggRectlistDraw = _nggRectlistDraw,
    };

    /// <summary>Loads native setting state without raising an interaction event.</summary>
    public void SetEmulatorSettings(EmulatorSettings settings)
    {
        EmulatorSettingsContract.Validate(settings);
        CloseChoicePopup();
        _screenResolution = settings.ScreenResolution;
        _vblankFrequency = settings.VblankFrequency;
        _vulkanValidation = settings.VulkanValidation;
        _shaderValidation = settings.ShaderValidation;
        _shaderOptimization = settings.ShaderOptimization;
        _shaderLogDirection = settings.ShaderLogDirection;
        _shaderLogFolder = settings.ShaderLogFolder;
        _commandBufferDump = settings.CommandBufferDump;
        _commandBufferDumpFolder = settings.CommandBufferDumpFolder;
        _printfDirection = settings.PrintfDirection;
        _printfOutputFile = settings.PrintfOutputFile;
        _profilerDirection = settings.ProfilerDirection;
        _renderDoc = settings.RenderDoc;
        _nggRectlistDraw = settings.NggRectlistDraw;
        RebuildRows();
    }

    /// <summary>Returns whether a row currently accepts an editable value.</summary>
    public bool IsSettingEnabled(string itemId) => itemId switch
    {
        ShellEmulatorSettingIds.ShaderLogFolder => _shaderLogDirection == EmulatorOutputDirection.File,
        ShellEmulatorSettingIds.CommandBufferDumpFolder => _commandBufferDump,
        ShellEmulatorSettingIds.PrintfOutputFile => _printfDirection == EmulatorOutputDirection.File,
        _ => true,
    };

    /// <summary>Returns the dependency-aware text rendered for a catalog row.</summary>
    public string? GetDisplayValue(string itemId) => ValueFor(new ShellSettingsDetailRow(itemId, itemId));

    public bool PlayTitleMusic { get => _playTitleMusic; set => SetOption(ref _playTitleMusic, value); }
    public bool AnimateShellBackground { get => _animateShellBackground; set => SetOption(ref _animateShellBackground, value); }
    public bool PlayUiSounds { get => _playUiSounds; set => SetOption(ref _playUiSounds, value); }
    public bool PlayShellMusic { get => _playShellMusic; set => SetOption(ref _playShellMusic, value); }
    public bool PlayBootIntro { get => _playBootIntro; set => SetOption(ref _playBootIntro, value); }
    public bool DiscordPresence { get => _discordPresence; set => SetOption(ref _discordPresence, value); }
    public bool CheckUpdates { get => _checkUpdates; set => SetOption(ref _checkUpdates, value); }
    public bool StrictDynlib { get => _strictDynlib; set => SetOption(ref _strictDynlib, value); }
    public bool LogToFile { get => _logToFile; set => SetOption(ref _logToFile, value); }
    public bool OverrideLogFile { get => _overrideLogFile; set => SetOption(ref _overrideLogFile, value); }
    public string LanguageName { get => _languageName; set => SetText(ref _languageName, value); }
    public string LogLevel { get => _logLevel; set => SetText(ref _logLevel, value); }
    public int ImportTraceLimit { get => _importTraceLimit; set { _importTraceLimit = Math.Clamp(value, 0, 4096); RebuildRows(); } }
    public string LogFilePath { get => _logFilePath; set => SetText(ref _logFilePath, value); }

    public bool IsEnvironmentEnabled(string itemId) => _enabledEnvironmentRows.Contains(itemId);

    public void SetEnvironmentEnabled(string itemId, bool enabled)
    {
        if (enabled)
        {
            _enabledEnvironmentRows.Add(itemId);
        }
        else
        {
            _enabledEnvironmentRows.Remove(itemId);
        }
        RebuildRows();
    }

    public static double CycleScale(double scale, int direction)
    {
        double[] scales = [1, .75, .5, .25];
        var current = Array.IndexOf(scales, NearestScale(scale));
        return scales[(current + (direction >= 0 ? 1 : scales.Length - 1)) % scales.Length];
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Back:
                RequestBack();
                e.Handled = true;
                return;
            case Key.Left when _rowsHaveFocus:
                MoveHorizontal(-1);
                e.Handled = true;
                return;
            case Key.Right when !_rowsHaveFocus:
                MoveHorizontal(1);
                e.Handled = true;
                return;
            case Key.Up:
                MoveVertical(-1);
                e.Handled = true;
                return;
            case Key.Down:
                MoveVertical(1);
                e.Handled = true;
                return;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                return;
        }
    }

    /// <summary>Moves within the active TabViewPS column.</summary>
    public void MoveVertical(int delta)
    {
        if (IsChoicePopupOpen)
        {
            MoveChoiceSelection(delta);
        }
        else
        {
            MoveSelection(delta);
        }
    }

    /// <summary>
    /// Moves between TabViewPS's tab column and mounted content panel. Edges
    /// clamp: left in the tab column and right in the panel are no-ops.
    /// </summary>
    public void MoveHorizontal(int direction)
    {
        if (IsChoicePopupOpen)
        {
            return;
        }

        if (direction < 0 && _rowsHaveFocus)
        {
            _rowsHaveFocus = false;
            QueueFocusRect();
        }
        else if (direction > 0 && !_rowsHaveFocus)
        {
            _rowsHaveFocus = true;
            QueueFocusRect();
        }
    }

    /// <summary>Activates the focused row, or enters the mounted panel.</summary>
    public void ActivateSelected()
    {
        if (IsChoicePopupOpen)
        {
            CommitChoiceSelection();
            return;
        }

        if (_rowsHaveFocus)
        {
            ActivateSelectedRow();
        }
        else
        {
            _rowsHaveFocus = true;
            QueueFocusRect();
        }
    }

    /// <summary>Requests the route-stack back action.</summary>
    public void RequestBack()
    {
        if (IsChoicePopupOpen)
        {
            CloseChoicePopup();
            return;
        }

        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Requests the legacy host-log picker. This is intentionally not present
    /// in the native PS5 emulator catalog.
    /// </summary>
    public void RequestLegacyLogFilePath() => LogFilePathRequested?.Invoke(this, EventArgs.Empty);

    private void MoveSelection(int delta)
    {
        if (_rowsHaveFocus)
        {
            SetSelectedRow(_selectedRow + Math.Sign(delta));
        }
        else
        {
            SetSelectedTab(_selectedTab + Math.Sign(delta));
        }
        QueueFocusRect();
    }

    private void SetScale(double scale)
    {
        _renderResolutionScale = scale;
        RebuildRows();
        RenderResolutionScaleChanged?.Invoke(this, EventArgs.Empty);
        QueueFocusRect();
    }

    private void ActivateSelectedRow()
    {
        var itemId = Tabs[_selectedTab].Rows[_selectedRow].ItemId;
        if (!OpenChoiceForSetting(itemId))
        {
            ActivateSetting(itemId);
        }
    }

    /// <summary>
    /// Opens the Settings popup-scroll path for enumerable values. Activating
    /// the row no longer mutates the backend merely because it was opened.
    /// </summary>
    internal bool OpenChoiceForSetting(string itemId)
    {
        var choices = ChoiceOptionsFor(itemId);
        if (choices.Count == 0 || !IsSettingEnabled(itemId))
        {
            return false;
        }

        _choiceItemId = itemId;
        _choiceOptions = choices;
        LastChangedEmulatorSettingId = null;
        var current = CurrentChoiceValue(itemId);
        _selectedChoiceIndex = Math.Max(0,
            choices.ToList().FindIndex(option => Equals(option.Value, current)));
        RebuildChoicePopup();
        _choicePopup.IsVisible = true;
        QueueFocusRect();
        return true;
    }

    internal static IReadOnlyList<ShellSettingsChoiceOption> ChoiceOptionsFor(string itemId) => itemId switch
    {
        ShellEmulatorSettingIds.ScreenResolution =>
        [
            new("1280 × 720", EmulatorResolution.R1280X720),
            new("1920 × 1080", EmulatorResolution.R1920X1080),
        ],
        ShellEmulatorSettingIds.VblankFrequency =>
        [
            new("30 Hz", 30),
            new("60 Hz", 60),
            new("120 Hz", 120),
            new("144 Hz", 144),
            new("240 Hz", 240),
            new("360 Hz", 360),
        ],
        ShellEmulatorSettingIds.ShaderOptimization =>
        [
            new("None", ShaderOptimizationMode.None),
            new("Size", ShaderOptimizationMode.Size),
            new("Performance", ShaderOptimizationMode.Performance),
        ],
        ShellEmulatorSettingIds.ShaderLogDirection or ShellEmulatorSettingIds.PrintfDirection =>
        [
            new("Silent", EmulatorOutputDirection.Silent),
            new("Console", EmulatorOutputDirection.Console),
            new("File", EmulatorOutputDirection.File),
        ],
        ShellEmulatorSettingIds.ProfilerDirection =>
        [
            new("None", EmulatorProfilerDirection.None),
            new("Network", EmulatorProfilerDirection.Network),
        ],
        _ => [],
    };

    private object? CurrentChoiceValue(string itemId) => itemId switch
    {
        ShellEmulatorSettingIds.ScreenResolution => _screenResolution,
        ShellEmulatorSettingIds.VblankFrequency => _vblankFrequency,
        ShellEmulatorSettingIds.ShaderOptimization => _shaderOptimization,
        ShellEmulatorSettingIds.ShaderLogDirection => _shaderLogDirection,
        ShellEmulatorSettingIds.PrintfDirection => _printfDirection,
        ShellEmulatorSettingIds.ProfilerDirection => _profilerDirection,
        _ => null,
    };

    private void MoveChoiceSelection(int delta)
    {
        if (!IsChoicePopupOpen || _choiceOptions.Count == 0 || delta == 0)
        {
            return;
        }

        _selectedChoiceIndex = Math.Clamp(
            _selectedChoiceIndex + Math.Sign(delta), 0, _choiceOptions.Count - 1);
        PositionChoicePopup();
        QueueFocusRect();
    }

    private void CommitChoiceSelection()
    {
        if (_choiceItemId is not { } itemId ||
            _selectedChoiceIndex < 0 || _selectedChoiceIndex >= _choiceOptions.Count)
        {
            return;
        }

        var value = _choiceOptions[_selectedChoiceIndex].Value;
        var changed = !Equals(CurrentChoiceValue(itemId), value);
        if (changed)
        {
            switch (itemId)
            {
                case ShellEmulatorSettingIds.ScreenResolution:
                    _screenResolution = (EmulatorResolution)value;
                    break;
                case ShellEmulatorSettingIds.VblankFrequency:
                    _vblankFrequency = (int)value;
                    break;
                case ShellEmulatorSettingIds.ShaderOptimization:
                    _shaderOptimization = (ShaderOptimizationMode)value;
                    break;
                case ShellEmulatorSettingIds.ShaderLogDirection:
                    _shaderLogDirection = (EmulatorOutputDirection)value;
                    break;
                case ShellEmulatorSettingIds.PrintfDirection:
                    _printfDirection = (EmulatorOutputDirection)value;
                    break;
                case ShellEmulatorSettingIds.ProfilerDirection:
                    _profilerDirection = (EmulatorProfilerDirection)value;
                    break;
            }
        }

        CloseChoicePopup(rebuildRows: false);
        if (changed)
        {
            NotifyNativeSettingChanged(itemId);
        }
        else
        {
            QueueFocusRect();
        }
    }

    private void CloseChoicePopup(bool rebuildRows = false)
    {
        if (!IsChoicePopupOpen)
        {
            return;
        }

        _choiceItemId = null;
        _choiceOptions = [];
        _selectedChoiceIndex = 0;
        _choicePopup.IsVisible = false;
        _choicePopup.Children.Clear();
        _choicePopup.Children.Add(_choicePopupPlate);
        _visibleChoiceRows.Clear();
        if (rebuildRows)
        {
            RebuildRows();
        }
        QueueFocusRect();
    }

    private void RebuildChoicePopup()
    {
        _choicePopup.Children.Clear();
        _visibleChoiceRows.Clear();
        var popupHeight = Math.Min(
            ShellSettingsMetrics.PopupMaximumHeight,
            _choiceOptions.Count * DiagnosticChoiceRowPitch);
        _choicePopup.Width = DiagnosticChoicePopupWidth;
        _choicePopup.Height = popupHeight;
        _choicePopupPlate.Width = DiagnosticChoicePopupWidth;
        _choicePopupPlate.Height = popupHeight;
        _choicePopup.Children.Add(_choicePopupPlate);

        var committedValue = CurrentChoiceValue(_choiceItemId!);
        for (var index = 0; index < _choiceOptions.Count; index++)
        {
            var choice = _choiceOptions[index];
            var capturedIndex = index;
            var row = new Grid
            {
                Width = DiagnosticChoicePopupWidth,
                Height = DiagnosticChoiceRowPitch,
                Background = Brushes.Transparent,
                ColumnDefinitions = new ColumnDefinitions("*,72"),
            };
            row.Children.Add(new TextBlock
            {
                Text = choice.Label,
                FontSize = Ps5FontScale.SizeNormal,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(32, 0, 16, 0),
            });

            var marker = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255)),
                Background = Equals(choice.Value, committedValue)
                    ? Brushes.White
                    : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(marker, 1);
            row.Children.Add(marker);
            row.PointerEntered += (_, _) =>
            {
                _selectedChoiceIndex = capturedIndex;
                QueueFocusRect();
            };
            row.PointerPressed += (_, e) =>
            {
                _selectedChoiceIndex = capturedIndex;
                ShellFocusRing.For(this)?.SetPressed(true);
                QueueFocusRect();
                e.Handled = true;
            };
            row.PointerReleased += (_, e) =>
            {
                ShellFocusRing.For(this)?.SetPressed(false);
                _selectedChoiceIndex = capturedIndex;
                CommitChoiceSelection();
                e.Handled = true;
            };
            Canvas.SetTop(row, index * DiagnosticChoiceRowPitch);
            _choicePopup.Children.Add(row);
            _visibleChoiceRows[index] = row;
        }

        PositionChoicePopup();
    }

    private void PositionChoicePopup()
    {
        if (!IsChoicePopupOpen)
        {
            return;
        }

        var anchorTop = ShellSettingsMetrics.TabTop
            + (_selectedRow - _firstVisibleRow) * DiagnosticRowPitch;
        var desiredTop = anchorTop
            + (DiagnosticRowPitch - DiagnosticChoiceRowPitch) / 2
            - _selectedChoiceIndex * DiagnosticChoiceRowPitch;
        var maximumTop = Ps5DesignSpace.Height
            - ShellSettingsMetrics.PopupBottomMargin
            - _choicePopup.Height;
        var top = Math.Clamp(desiredTop, 0, Math.Max(0, maximumTop));
        var left = Ps5DesignSpace.Width - ChoicePopupRightMargin - DiagnosticChoicePopupWidth;
        _choicePopup.Margin = new Thickness(left, top, 0, 0);
    }

    /// <summary>
    /// Applies the controller interaction associated with a stable setting ID.
    /// Returns false for read-only/dependency-disabled rows and unknown IDs.
    /// </summary>
    public bool ActivateSetting(string itemId)
    {
        LastChangedEmulatorSettingId = null;
        var wasToggle = TryGetToggle(itemId, out var previousToggleValue);
        switch (itemId)
        {
            case "id_internal_resolution":
                SetScale(CycleScale(_renderResolutionScale, 1));
                return true;
            case "id_title_music":
                _playTitleMusic = !_playTitleMusic;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_shell_motion":
                _animateShellBackground = !_animateShellBackground;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_ui_sounds":
                _playUiSounds = !_playUiSounds;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_shell_music":
                _playShellMusic = !_playShellMusic;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_boot_intro":
                _playBootIntro = !_playBootIntro;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_discord_presence":
                _discordPresence = !_discordPresence;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_auto_update":
                _checkUpdates = !_checkUpdates;
                return NotifyLauncherSettingChanged(itemId, previousToggleValue);
            case "id_language":
                LanguageCycleRequested?.Invoke(this, EventArgs.Empty);
                return false;
            case "id_check_updates":
            case "id_github":
            case "id_discord":
            case "id_rescan_library":
                ActionRequested?.Invoke(itemId);
                return false;
            case ShellEmulatorSettingIds.ScreenResolution:
                _screenResolution = CycleScreenResolution(_screenResolution);
                break;
            case ShellEmulatorSettingIds.VblankFrequency:
                _vblankFrequency = CycleVblankFrequency(_vblankFrequency);
                break;
            case ShellEmulatorSettingIds.VulkanValidation:
                _vulkanValidation = !_vulkanValidation;
                break;
            case ShellEmulatorSettingIds.ShaderValidation:
                _shaderValidation = !_shaderValidation;
                break;
            case ShellEmulatorSettingIds.ShaderOptimization:
                _shaderOptimization = CycleShaderOptimization(_shaderOptimization);
                break;
            case ShellEmulatorSettingIds.ShaderLogDirection:
                _shaderLogDirection = CycleOutputDirection(_shaderLogDirection);
                break;
            case ShellEmulatorSettingIds.CommandBufferDump:
                _commandBufferDump = !_commandBufferDump;
                break;
            case ShellEmulatorSettingIds.PrintfDirection:
                _printfDirection = CycleOutputDirection(_printfDirection);
                break;
            case ShellEmulatorSettingIds.ProfilerDirection:
                _profilerDirection = CycleProfilerDirection(_profilerDirection);
                break;
            case ShellEmulatorSettingIds.RenderDoc:
                _renderDoc = !_renderDoc;
                break;
            case ShellEmulatorSettingIds.NggRectlistDraw:
                _nggRectlistDraw = !_nggRectlistDraw;
                break;
            case ShellEmulatorSettingIds.ShaderLogFolder:
            case ShellEmulatorSettingIds.CommandBufferDumpFolder:
            case ShellEmulatorSettingIds.PrintfOutputFile:
                if (!IsSettingEnabled(itemId))
                {
                    return false;
                }
                LastChangedEmulatorSettingId = itemId;
                EmulatorTextSettingRequested?.Invoke(itemId);
                return true;
            default:
                return false;
        };

        return NotifyNativeSettingChanged(
            itemId,
            wasToggle ? previousToggleValue : null);
    }

    private bool NotifyLauncherSettingChanged(string itemId, bool previousToggleValue)
    {
        RebuildRows(itemId, previousToggleValue);
        EmulatorSettingChanged?.Invoke(this, EventArgs.Empty);
        QueueFocusRect();
        return true;
    }

    private bool NotifyNativeSettingChanged(string itemId, bool? previousToggleValue = null)
    {
        LastChangedEmulatorSettingId = itemId;
        RebuildRows(
            previousToggleValue.HasValue ? itemId : null,
            previousToggleValue);
        EmulatorSettingChanged?.Invoke(this, EventArgs.Empty);
        QueueFocusRect();
        return true;
    }

    private void Rebuild()
    {
        _tabs.Children.Clear();
        for (var i = 0; i < Tabs.Count; i++)
        {
            var tab = Tabs[i];
            var capturedIndex = i;
            var text = new TextBlock
            {
                Text = tab.Label,
                FontSize = Ps5FontScale.SizeNormal,
                Foreground = Brushes.White,
                Opacity = i == _selectedTab ? 1 : .65,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Canvas.SetLeft(text, 0);
            Canvas.SetTop(text, i * CapturedTabPitch + 25);
            text.PointerEntered += (_, _) =>
            {
                SetSelectedTab(capturedIndex);
                _rowsHaveFocus = false;
                Focus();
            };
            text.PointerPressed += (_, e) =>
            {
                SetSelectedTab(capturedIndex);
                _rowsHaveFocus = false;
                Focus();
                e.Handled = true;
            };
            _tabs.Children.Add(text);
        }
        RebuildRows();
    }

    private void RebuildRows(
        string? animatedToggleItemId = null,
        bool? previousToggleValue = null)
    {
        _rows.Children.Clear();
        _visibleRows.Clear();
        _visibleToggles.Clear();
        var rows = Tabs[_selectedTab].Rows;
        var last = Math.Min(rows.Count, _firstVisibleRow + VisibleRows);
        for (var i = _firstVisibleRow; i < last; i++)
        {
            var model = rows[i];
            var capturedIndex = i;
            var value = ValueFor(model);
            var row = new Grid
            {
                Width = ShellSettingsMetrics.TabPanelWidth,
                Height = DiagnosticRowPitch,
                Background = Brushes.Transparent,
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            };
            row.PointerEntered += (_, _) =>
            {
                SetSelectedRow(capturedIndex);
                _rowsHaveFocus = true;
                Focus();
            };
            row.PointerPressed += (_, e) =>
            {
                SetSelectedRow(capturedIndex);
                _rowsHaveFocus = true;
                Focus();
                ShellFocusRing.For(this)?.SetPressed(true);
                e.Handled = true;
            };
            row.PointerReleased += (_, e) =>
            {
                ShellFocusRing.For(this)?.SetPressed(false);
                ActivateSelectedRow();
                e.Handled = true;
            };
            row.Children.Add(new TextBlock
            {
                Text = model.Label,
                FontSize = Ps5FontScale.SizeNormal,
                Foreground = Brushes.White,
                Opacity = IsSettingEnabled(model.ItemId) ? 1 : .42,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(
                    ShellSettingsMetrics.LongTextTitleMarginLeft,
                    0,
                    ShellSettingsMetrics.LongTextTitleMarginRight,
                    0),
            });
            if (TryGetToggle(model.ItemId, out var enabled))
            {
                // UI3 owns the switch masks and highlight; this row owns
                // activation so controller and pointer behaviour remain the
                // same across every SettingsList item.
                var toggle = new Ps5ToggleSwitch
                {
                    Width = 96,
                    Height = 48,
                    IsToggleEnabled = false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, ShellSettingsMetrics.LongTextValueMarginRight, 0),
                };
                var animateStateChange = previousToggleValue.HasValue &&
                    string.Equals(
                        animatedToggleItemId,
                        model.ItemId,
                        StringComparison.Ordinal) &&
                    previousToggleValue.Value != enabled;
                toggle.SetState(
                    animateStateChange ? previousToggleValue.GetValueOrDefault() : enabled,
                    animate: false);
                if (animateStateChange)
                {
                    toggle.SetState(enabled, animate: true);
                }
                _visibleToggles[model.ItemId] = toggle;
                Grid.SetColumn(toggle, 1);
                row.Children.Add(toggle);
            }
            else if (value is not null)
            {
                var valueText = new TextBlock
                {
                    Text = value,
                    FontSize = Ps5FontScale.SizeXSmall,
                    Foreground = Brushes.White,
                    Opacity = IsSettingEnabled(model.ItemId)
                        ? ShellSettingsMetrics.LongTextValueOpacity
                        : .35,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, ShellSettingsMetrics.LongTextValueMarginRight, 0),
                };
                Grid.SetColumn(valueText, 1);
                row.Children.Add(valueText);
            }

            var separator = new Border
            {
                Height = ShellSettingsMetrics.SeparatorHeight,
                Background = SeparatorBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetColumn(separator, 0);
            Grid.SetColumnSpan(separator, 2);
            row.Children.Add(separator);
            Canvas.SetTop(row, (i - _firstVisibleRow) * DiagnosticRowPitch);
            _rows.Children.Add(row);
            _visibleRows[i] = row;
        }
    }

    private bool TryGetToggle(string itemId, out bool enabled)
    {
        enabled = itemId switch
        {
            "id_title_music" => _playTitleMusic,
            "id_shell_motion" => _animateShellBackground,
            "id_ui_sounds" => _playUiSounds,
            "id_shell_music" => _playShellMusic,
            "id_boot_intro" => _playBootIntro,
            "id_discord_presence" => _discordPresence,
            "id_auto_update" => _checkUpdates,
            ShellEmulatorSettingIds.VulkanValidation => _vulkanValidation,
            ShellEmulatorSettingIds.ShaderValidation => _shaderValidation,
            ShellEmulatorSettingIds.CommandBufferDump => _commandBufferDump,
            ShellEmulatorSettingIds.RenderDoc => _renderDoc,
            ShellEmulatorSettingIds.NggRectlistDraw => _nggRectlistDraw,
            "id_strict_dynlib" => _strictDynlib,
            "id_log_to_file" => _logToFile,
            "id_override_log_file" => _overrideLogFile,
            _ => false,
        };
        if (itemId.StartsWith("env_", StringComparison.Ordinal))
        {
            enabled = _enabledEnvironmentRows.Contains(itemId);
            return true;
        }
        return itemId is "id_title_music" or "id_shell_motion" or "id_ui_sounds" or
            "id_shell_music" or "id_boot_intro" or "id_discord_presence" or
            "id_auto_update" or ShellEmulatorSettingIds.VulkanValidation or
            ShellEmulatorSettingIds.ShaderValidation or ShellEmulatorSettingIds.CommandBufferDump or
            ShellEmulatorSettingIds.RenderDoc or ShellEmulatorSettingIds.NggRectlistDraw or
            "id_strict_dynlib" or "id_log_to_file" or
            "id_override_log_file";
    }

    private void SetOption(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        RebuildRows();
    }

    private void SetText(ref string field, string? value)
    {
        field = string.IsNullOrWhiteSpace(value) ? "Default" : value;
        RebuildRows();
    }

    private void SetEmulatorOption<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        RebuildRows();
    }

    private void SetEmulatorText(ref string field, string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (string.Equals(field, normalized, StringComparison.Ordinal))
        {
            return;
        }

        field = normalized;
        RebuildRows();
    }

    private string? ValueFor(ShellSettingsDetailRow row) => row.ItemId switch
    {
        "id_internal_resolution" => $"{_renderResolutionScale * 100:0}%",
        "id_language" => _languageName,
        "id_log_level" => _logLevel,
        "id_trace_imports" => _importTraceLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "id_log_file_path" => _logFilePath,
        ShellEmulatorSettingIds.ScreenResolution => ScreenResolutionText(_screenResolution),
        ShellEmulatorSettingIds.VblankFrequency => $"{_vblankFrequency} Hz",
        ShellEmulatorSettingIds.ShaderOptimization => _shaderOptimization.ToString(),
        ShellEmulatorSettingIds.ShaderLogDirection => _shaderLogDirection.ToString(),
        ShellEmulatorSettingIds.ShaderLogFolder => _shaderLogDirection == EmulatorOutputDirection.File
            ? _shaderLogFolder
            : "Disabled (output is not File)",
        ShellEmulatorSettingIds.CommandBufferDumpFolder => _commandBufferDump
            ? _commandBufferDumpFolder
            : "Disabled",
        ShellEmulatorSettingIds.PrintfDirection => _printfDirection.ToString(),
        ShellEmulatorSettingIds.PrintfOutputFile => _printfDirection == EmulatorOutputDirection.File
            ? _printfOutputFile
            : "Disabled (output is not File)",
        ShellEmulatorSettingIds.ProfilerDirection => _profilerDirection.ToString(),
        _ => row.Value,
    };

    public static EmulatorResolution CycleScreenResolution(EmulatorResolution current) => current switch
    {
        EmulatorResolution.R1280X720 => EmulatorResolution.R1920X1080,
        _ => EmulatorResolution.R1280X720,
    };

    public static int CycleVblankFrequency(int current)
    {
        int[] values = [30, 60, 120, 144, 240, 360];
        var index = Array.IndexOf(values, current);
        return values[(Math.Max(index, 0) + 1) % values.Length];
    }

    public static ShaderOptimizationMode CycleShaderOptimization(ShaderOptimizationMode current) => current switch
    {
        ShaderOptimizationMode.None => ShaderOptimizationMode.Size,
        ShaderOptimizationMode.Size => ShaderOptimizationMode.Performance,
        _ => ShaderOptimizationMode.None,
    };

    public static EmulatorOutputDirection CycleOutputDirection(EmulatorOutputDirection current) => current switch
    {
        EmulatorOutputDirection.Silent => EmulatorOutputDirection.Console,
        EmulatorOutputDirection.Console => EmulatorOutputDirection.File,
        _ => EmulatorOutputDirection.Silent,
    };

    public static EmulatorProfilerDirection CycleProfilerDirection(EmulatorProfilerDirection current) => current switch
    {
        EmulatorProfilerDirection.None => EmulatorProfilerDirection.Network,
        _ => EmulatorProfilerDirection.None,
    };

    private static int ClampVblank(int value) => Math.Clamp(
        value,
        EmulatorSettingsContract.MinimumVblankFrequency,
        EmulatorSettingsContract.MaximumVblankFrequency);

    private static string ScreenResolutionText(EmulatorResolution resolution) => resolution switch
    {
        EmulatorResolution.R1280X720 => "1280 × 720",
        EmulatorResolution.R1920X1080 => "1920 × 1080",
        _ => resolution.ToString(),
    };

    public static string CycleLogLevel(string current)
    {
        string[] levels = ["Trace", "Debug", "Info", "Warning", "Error", "Critical"];
        var index = Array.FindIndex(levels, level => string.Equals(level, current, StringComparison.OrdinalIgnoreCase));
        return levels[(Math.Max(index, 0) + 1) % levels.Length];
    }

    public static int CycleImportTraceLimit(int current)
    {
        int[] values = [0, 16, 64, 256, 1024, 4096];
        var index = Array.IndexOf(values, current);
        return values[(Math.Max(index, 0) + 1) % values.Length];
    }

    private void SetSelectedTab(int value)
    {
        CloseChoicePopup();
        var selected = Math.Clamp(value, 0, Tabs.Count - 1);
        if (_selectedTab == selected)
        {
            QueueFocusRect();
            return;
        }

        _selectedTab = selected;
        _heading.Text = Tabs[_selectedTab].Label;
        _selectedRow = 0;
        _firstVisibleRow = 0;
        Rebuild();
        QueueFocusRect();
    }

    private void SetSelectedRow(int value)
    {
        var selected = Math.Clamp(value, 0, Tabs[_selectedTab].Rows.Count - 1);
        if (_selectedRow == selected)
        {
            QueueFocusRect();
            return;
        }

        _selectedRow = selected;
        if (_selectedRow < _firstVisibleRow)
        {
            _firstVisibleRow = _selectedRow;
        }
        else if (_selectedRow >= _firstVisibleRow + VisibleRows)
        {
            _firstVisibleRow = _selectedRow - VisibleRows + 1;
        }
        RebuildRows();
        QueueFocusRect();
    }

    private void QueueFocusRect() =>
        Dispatcher.UIThread.Post(PushFocusRect, DispatcherPriority.Render);

    private void PushFocusRect()
    {
        if (!IsEffectivelyVisible || !IsFocused || ShellFocusRing.For(this) is not { } ring)
        {
            return;
        }

        Rect rect;
        if (IsChoicePopupOpen)
        {
            if (!_visibleChoiceRows.TryGetValue(_selectedChoiceIndex, out var choiceRow) ||
                choiceRow.TransformToVisual(ring) is not { } choiceTransform)
            {
                return;
            }

            rect = new Rect(choiceRow.Bounds.Size).TransformToAABB(choiceTransform);
        }
        else if (_rowsHaveFocus)
        {
            if (!_visibleRows.TryGetValue(_selectedRow, out var row) ||
                row.TransformToVisual(ring) is not { } rowTransform)
            {
                return;
            }

            // Both Settings focus passes follow the arranged item exactly.
            // This prevents the thin focus line from overhanging the captured
            // wash geometry on a wide/scrolled detail row.
            rect = new Rect(row.Bounds.Size).TransformToAABB(rowTransform);
        }
        else
        {
            if (this.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            // TabViewPS focuses the full tab slot rather than the label glyph.
            var tabRect = new Rect(ShellSettingsMetrics.TabLeft,
                ShellSettingsMetrics.TabTop + _selectedTab * CapturedTabPitch,
                ShellSettingsMetrics.TabWidth, CapturedTabPitch);
            rect = tabRect.TransformToAABB(transform);
        }
        ring.Radius = 0;
        ring.LineScale = ShellSettingsMetrics.FocusLineScale;
        // See the category list: the target-resolution evaluator is required
        // here so the capture-approved 1.5 px line is not widened by the
        // generic native FocusRenderWidget path.
        ring.Claim(this, rect, lineMatchesArea: true);
    }

    private static double NearestScale(double value) => value switch
    {
        >= .875 => 1,
        >= .625 => .75,
        >= .375 => .5,
        _ => .25,
    };
}
