// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>One function-control button from NPXS40003's registry.</summary>
public sealed record ShellControlCenterItem(string Id, string Title, string IconId);

public sealed class ShellControlCenterEventArgs : EventArgs
{
    public ShellControlCenterEventArgs(int index, ShellControlCenterItem? item)
    {
        Index = index;
        Item = item;
    }

    public int Index { get; }
    public ShellControlCenterItem? Item { get; }
}

public sealed class ShellControlCenterPanelBackEventArgs : EventArgs
{
    public bool Handled { get; set; }
}

/// <summary>
/// Product host for the PS-button Control Center. It owns the recovered fixed
/// 1920x1080 row/popup geometry and input state; callers supply host-backed
/// </summary>
public sealed class ShellControlCenter : TemplatedControl
{
    public const double DesignWidth = 1920;
    public const double DesignHeight = 1080;
    public const double SystemMargin = 84;
    public const double BarHeight = 147;
    public const double ButtonCellWidth = 112;
    public const double ButtonCellHeight = 147;
    public const double ButtonHitSize = 56;
    public const double IconSize = 48;
    public const double IconTop = 54;
    public const double IconContainerSize = 64;
    public const double SecondaryBadgeSize = 20;
    public const double SecondaryBadgeTop = -7;
    public const double SecondaryBadgeLeft = 35;
    public const double PanelBottom = 190;
    public const double LabelWidth = 368;
    public const double LabelHeight = 34;

    public static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan PanelShowDelay = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan PanelShowDuration = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PanelHideDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Ten controls visible in the supplied 07:03 console-tour segment. The
    /// complete 16-control registry remains documented; state/feature-flag
    /// controls not present in that capture are not silently added here.
    /// </summary>
    public static IReadOnlyList<ShellControlCenterItem> ConsoleTourItems { get; } =
    [
        new("home", "Home", "home"),
        new("apps", "Switcher", "switcher"),
        new("notifications", "Notifications", "notification"),
        new("gaming-lounge", "Game Base", "game_base"),
        new("music", "Music", "music"),
        new("sound", "Sound", "sound_speaking"),
        new("mic", "Mic", "mic"),
        new("controller", "Accessories", "game"),
        new("profile", "Profile", "ps_user"),
        new("power", "Power", "power"),
    ];

    private static readonly IBrush FocusBrush = Brushes.White;
    private static readonly Color LightIcon = Colors.White;
    private static readonly Color DarkIcon = Color.Parse("#292929");

    private readonly List<ButtonVisual> _buttons = new();
    private readonly TranslateTransform _rootTranslate = new(0, 20);
    private readonly TranslateTransform _panelTranslate = new(0, 20);
    private Canvas? _surface;
    private StackPanel? _row;
    private TextBlock? _clock;
    private Border? _panelHost;
    private ShellFunctionPanel? _panel;
    private CancellationTokenSource? _visibilityCancellation;
    private CancellationTokenSource? _panelCancellation;
    private CancellationTokenSource? _notificationBadgeCancellation;
    private IReadOnlyList<ShellControlCenterItem> _items = ConsoleTourItems;
    private int _selectedIndex;
    private bool _panelOpen;
    private string? _panelOwnerId;
    private string _clockText = "00:00";
    private bool _notificationStateInitialized;
    private bool _notificationDoNotDisturb;
    private int _newNotificationCount;

    public ShellControlCenter()
    {
        Focusable = true;
        IsTabStop = true;
        IsVisible = false;
        IsHitTestVisible = false;
        Opacity = 0;
        RenderTransform = _rootTranslate;
        Template = BuildTemplate();
    }

    public event EventHandler<ShellControlCenterEventArgs>? ControlActivated;
    public event EventHandler<ShellFunctionPanelEventArgs>? PanelItemActivated;
    public event EventHandler<ShellFunctionPanelEventArgs>? PanelSelectionChanged;
    public event EventHandler? PanelOptionsRequested;
    public event EventHandler? PanelDeleteRequested;
    public event EventHandler<ShellControlCenterPanelBackEventArgs>? PanelBackRequested;
    public event EventHandler? PanelClosed;
    public event EventHandler? Closed;

    public bool IsOpen { get; private set; }
    public bool IsPanelOpen => _panelOpen;
    public string? PanelOwnerId => _panelOwnerId;
    public int SelectedIndex => _selectedIndex;
    public ShellControlCenterItem? SelectedItem =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;
    public ShellFunctionPanelItem? SelectedPanelItem => _panel?.SelectedItem;
    public int SelectedPanelIndex => _panel?.SelectedIndex ?? -1;
    public Control? SelectedPanelAnchor => _panel?.SelectedRowAnchor;

    public IReadOnlyList<ShellControlCenterItem> Items
    {
        get => _items;
        set
        {
            _items = value ?? Array.Empty<ShellControlCenterItem>();
            _selectedIndex = _items.Count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, _items.Count - 1);
            RebuildButtons();
        }
    }

    /// <summary>
    /// Restores NPXS40003's persisted function-control id. This product host
    /// only exposes visible controls in <see cref="Items"/>, so a missing or
    /// hidden id falls back to Home, matching the bundle's startup rule.
    /// </summary>
    public void RestoreSelectedItem(string? controlId)
    {
        if (_items.Count == 0)
        {
            _selectedIndex = -1;
            UpdateVisuals();
            return;
        }

        var index = string.IsNullOrWhiteSpace(controlId)
            ? -1
            : _items
                .Select((item, itemIndex) => (item, itemIndex))
                .FirstOrDefault(pair => string.Equals(
                    pair.item.Id,
                    controlId,
                    StringComparison.Ordinal))
                .itemIndex;
        if (index < 0 || index >= _items.Count ||
            !string.Equals(_items[index].Id, controlId, StringComparison.Ordinal))
        {
            index = _items
                .Select((item, itemIndex) => (item, itemIndex))
                .FirstOrDefault(pair => string.Equals(
                    pair.item.Id,
                    "home",
                    StringComparison.Ordinal))
                .itemIndex;
            if (index < 0 || index >= _items.Count ||
                !string.Equals(_items[index].Id, "home", StringComparison.Ordinal))
            {
                index = 0;
            }
        }

        _selectedIndex = index;
        UpdateVisuals();
    }

    public void SetClockText(string text)
    {
        _clockText = text ?? string.Empty;
        if (_clock is not null)
        {
            _clock.Text = _clockText;
        }
    }

    /// <summary>
    /// Applies the backend-owned notification state to the function-control
    /// button. NPXS40003 swaps to <c>notification_off</c> in Do Not Disturb and
    /// presents the secondary <c>new</c> badge while unread records remain.
    /// </summary>
    public void SetNotificationState(bool isDoNotDisturb, int newNotificationCount)
    {
        var hadState = _notificationStateInitialized;
        var hadNew = _newNotificationCount > 0;
        _notificationStateInitialized = true;
        _notificationDoNotDisturb = isDoNotDisturb;
        _newNotificationCount = Math.Max(0, newNotificationCount);

        var notification = _buttons.FirstOrDefault(button =>
            string.Equals(button.Model.Id, "notifications", StringComparison.Ordinal));
        if (notification is null)
        {
            return;
        }

        notification.Icon.IconId = NotificationIconId(_notificationDoNotDisturb);
        var hasNew = _newNotificationCount > 0;
        if (!hadState || hadNew == hasNew)
        {
            notification.BadgeHost.Opacity = hasNew ? 1 : 0;
            return;
        }

        _ = AnimateNotificationBadgeAsync(notification.BadgeHost, hasNew);
    }

    public static string NotificationIconId(bool isDoNotDisturb) =>
        isDoNotDisturb ? "notification_off" : "notification";

    public static bool ShouldShowNotificationNewBadge(int newNotificationCount) =>
        newNotificationCount > 0;

    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        CancelVisibility();
        IsOpen = true;
        IsVisible = true;
        IsHitTestVisible = true;
        ConfigureRootTransitions(OpenDuration, ShellMotion.EaseOutBlast);
        Opacity = 1;
        _rootTranslate.Y = 0;
        UpdateVisuals();
        Focus();
        ShellUiSounds.Play(UiSoundEvent.OpenControlCenter);
    }

    public async Task CloseAsync()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        HidePanelForControlCenterClose();
        CancelVisibility();
        _visibilityCancellation = new CancellationTokenSource();
        var token = _visibilityCancellation.Token;
        ConfigureRootTransitions(CloseDuration, new LinearEasing());
        Opacity = 0;
        _rootTranslate.Y = 20;
        ShellUiSounds.Play(UiSoundEvent.CloseControlCenter);

        try
        {
            await Task.Delay(CloseDuration, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && !IsOpen)
            {
                IsVisible = false;
                IsHitTestVisible = false;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void MoveHorizontal(int delta, bool allowEdgeWrap)
    {
        if (_panelOpen || _items.Count == 0 || delta == 0)
        {
            return;
        }

        var next = MoveIndex(_selectedIndex, _items.Count, delta, allowEdgeWrap);
        if (next == _selectedIndex)
        {
            return;
        }

        _selectedIndex = next;
        ShellUiSounds.Play(UiSoundEvent.FocusMove);
        UpdateVisuals();
    }

    public void MovePanelFocus(int delta)
    {
        if (_panelOpen)
        {
            _panel?.MoveFocus(delta);
        }
    }

    public void ActivateSelected()
    {
        if (_panelOpen)
        {
            _panel?.ActivateSelected();
            return;
        }

        ControlActivated?.Invoke(this, new ShellControlCenterEventArgs(_selectedIndex, SelectedItem));
    }

    public async Task BackAsync()
    {
        if (_panelOpen)
        {
            var args = new ShellControlCenterPanelBackEventArgs();
            PanelBackRequested?.Invoke(this, args);
            if (args.Handled)
            {
                return;
            }

            ShellUiSounds.Play(UiSoundEvent.Cancel);
            await HidePanelAsync().ConfigureAwait(true);
            Focus();
            return;
        }

        await CloseAsync().ConfigureAwait(true);
    }

    public async Task ShowPanelAsync(
        string ownerId,
        string header,
        IReadOnlyList<ShellFunctionPanelItem> items,
        int selectedIndex = 0)
    {
        if (!IsOpen || _panel is null || _panelHost is null)
        {
            return;
        }

        var ownerIndex = _items
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair => string.Equals(pair.item.Id, ownerId, StringComparison.Ordinal))
            .index;
        if (ownerIndex < 0 || ownerIndex >= _items.Count ||
            !string.Equals(_items[ownerIndex].Id, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        CancelPanel();
        _panelCancellation = new CancellationTokenSource();
        var token = _panelCancellation.Token;
        _selectedIndex = ownerIndex;
        _panelOwnerId = ownerId;
        _panelOpen = true;
        _panel.Header = header;
        _panel.Items = items ?? Array.Empty<ShellFunctionPanelItem>();
        _panel.SetSelectedIndex(selectedIndex);
        PositionPanel();
        _panelHost.IsVisible = true;
        _panelHost.IsHitTestVisible = true;
        ConfigurePanelTransitions(PanelShowDuration, ShellMotion.EaseOutBlast);
        _panelHost.Opacity = 0;
        _panelTranslate.Y = 20;
        UpdateVisuals();

        try
        {
            await Task.Delay(PanelShowDelay, token).ConfigureAwait(true);
            _panelHost.Opacity = 1;
            _panelTranslate.Y = 0;
            await Task.Delay(PanelShowDuration, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                _panel.Focus();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Applies a backend list update without replaying the popup entrance or
    /// losing the focused row. NotificationDb2 uses this path for state and
    /// database events while its panel is already mounted.
    /// </summary>
    public void UpdateOpenPanelItems(
        string ownerId,
        IReadOnlyList<ShellFunctionPanelItem> items)
    {
        if (!_panelOpen || _panel is null || _panelHost is null ||
            !string.Equals(_panelOwnerId, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        var selectedIndex = _panel.SelectedIndex;
        _panel.Items = items ?? Array.Empty<ShellFunctionPanelItem>();
        _panel.SetSelectedIndex(selectedIndex);
        PositionPanel();
    }

    /// <summary>
    /// Replaces an already-mounted function-control screen without replaying
    /// the panel entrance. NPXS40003 uses a stack navigator inside the same
    /// panel for list, detail and delete-confirm routes.
    /// </summary>
    public void ReplaceOpenPanelScreen(
        string ownerId,
        string header,
        IReadOnlyList<ShellFunctionPanelItem> items,
        int selectedIndex = 0)
    {
        if (!_panelOpen || _panel is null || _panelHost is null ||
            !string.Equals(_panelOwnerId, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        _panel.Header = header;
        _panel.Items = items ?? Array.Empty<ShellFunctionPanelItem>();
        _panel.SetSelectedIndex(selectedIndex);
        PositionPanel();
        _panel.Focus();
    }

    public void RequestPanelOptions()
    {
        if (_panelOpen)
        {
            PanelOptionsRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RequestPanelDelete()
    {
        if (_panelOpen)
        {
            PanelDeleteRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task HidePanelAsync()
    {
        if (!_panelOpen || _panelHost is null)
        {
            return;
        }

        _panelOpen = false;
        _panelOwnerId = null;
        PanelClosed?.Invoke(this, EventArgs.Empty);
        CancelPanel();
        _panelCancellation = new CancellationTokenSource();
        var token = _panelCancellation.Token;
        ConfigurePanelTransitions(PanelHideDuration, new LinearEasing());
        _panelHost.Opacity = 0;
        _panelTranslate.Y = 20;
        UpdateVisuals();

        try
        {
            await Task.Delay(PanelHideDuration, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && !_panelOpen)
            {
                _panelHost.IsVisible = false;
                _panelHost.IsHitTestVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Pure navigation rule used by the controller route and tests.</summary>
    public static int MoveIndex(int current, int count, int delta, bool allowEdgeWrap)
    {
        if (count <= 0)
        {
            return -1;
        }

        current = Math.Clamp(current, 0, count - 1);
        var target = current + Math.Sign(delta);
        if (target < 0)
        {
            return allowEdgeWrap ? count - 1 : 0;
        }
        if (target >= count)
        {
            return allowEdgeWrap ? 0 : count - 1;
        }
        return target;
    }

    public static double PopupLeft(double buttonX, double menuWidth = ShellFunctionPanelMetrics.Width)
    {
        var left = buttonX + (ButtonCellWidth / 2) - (menuWidth / 2);
        return Math.Clamp(left, SystemMargin, DesignWidth - SystemMargin - menuWidth);
    }

    internal void CompletePresentationForCapture(string? panelOwnerId = null)
    {
        IsOpen = true;
        IsVisible = true;
        IsHitTestVisible = true;
        Opacity = 1;
        _rootTranslate.Y = 0;
        if (panelOwnerId is not null && _panelHost is not null)
        {
            _panelOpen = true;
            _panelOwnerId = panelOwnerId;
            _panelHost.IsVisible = true;
            _panelHost.Opacity = 1;
            _panelTranslate.Y = 0;
        }
        UpdateVisuals();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Find<Canvas>("PART_Surface");
        _row = e.NameScope.Find<StackPanel>("PART_Row");
        _clock = e.NameScope.Find<TextBlock>("PART_Clock");
        if (_clock is not null)
        {
            _clock.Text = _clockText;
        }
        _panelHost = e.NameScope.Find<Border>("PART_PanelHost");
        _panel = e.NameScope.Find<ShellFunctionPanel>("PART_Panel");
        if (_panel is not null)
        {
            _panel.ItemActivated += (_, args) => PanelItemActivated?.Invoke(this, args);
            _panel.SelectionChanged += (_, args) => PanelSelectionChanged?.Invoke(this, args);
        }
        RebuildButtons();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left when !_panelOpen:
                MoveHorizontal(-1, allowEdgeWrap: false);
                e.Handled = true;
                break;
            case Key.Right when !_panelOpen:
                MoveHorizontal(1, allowEdgeWrap: false);
                e.Handled = true;
                break;
            case Key.Up when _panelOpen:
                MovePanelFocus(-1);
                e.Handled = true;
                break;
            case Key.Down when _panelOpen:
                MovePanelFocus(1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Back:
                _ = BackAsync();
                e.Handled = true;
                break;
            case Key.F3 when _panelOpen:
                RequestPanelOptions();
                e.Handled = true;
                break;
            case Key.Delete when _panelOpen:
                RequestPanelDelete();
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    private FuncControlTemplate BuildTemplate() => new((_, ns) =>
    {
        var surface = new Canvas
        {
            Name = "PART_Surface",
            Width = DesignWidth,
            Height = DesignHeight,
            ClipToBounds = false,
        }.RegisterInNameScope(ns);

        var clock = new TextBlock
        {
            Name = "PART_Clock",
            Width = 300,
            Height = 126,
            Text = "00:00",
            FontSize = ShellFontSize.Large,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }.RegisterInNameScope(ns);
        Canvas.SetLeft(clock, DesignWidth - SystemMargin - 300);
        Canvas.SetTop(clock, 0);
        surface.Children.Add(clock);

        var row = new StackPanel
        {
            Name = "PART_Row",
            Orientation = Orientation.Horizontal,
            Height = BarHeight,
        }.RegisterInNameScope(ns);
        Canvas.SetTop(row, DesignHeight - BarHeight);
        surface.Children.Add(row);

        var panel = new ShellFunctionPanel { Name = "PART_Panel" }.RegisterInNameScope(ns);
        var panelHost = new Border
        {
            Name = "PART_PanelHost",
            IsVisible = false,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = _panelTranslate,
            Child = panel,
        }.RegisterInNameScope(ns);
        surface.Children.Add(panelHost);

        return surface;
    });

    private void RebuildButtons()
    {
        if (_row is null)
        {
            return;
        }

        _row.Children.Clear();
        _buttons.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var captured = index;
            var model = _items[index];
            var label = new TextBlock
            {
                Text = model.Title,
                Width = LabelWidth,
                Height = LabelHeight,
                FontSize = ShellFontSize.XSmall,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = false,
            };

            var icon = new Ps5IconPresenter
            {
                IconId = string.Equals(model.Id, "notifications", StringComparison.Ordinal)
                    ? NotificationIconId(_notificationDoNotDisturb)
                    : model.IconId,
                Width = IconSize,
                Height = IconSize,
                Tint = LightIcon,
                OverrideDeclaredFill = true,
            };
            var disc = new Border
            {
                Width = ButtonHitSize,
                Height = ButtonHitSize,
                CornerRadius = new CornerRadius(ButtonHitSize / 2),
                Background = Brushes.Transparent,
                Child = icon,
                Padding = new Thickness((ButtonHitSize - IconSize) / 2),
            };

            var badge = new Ps5IconPresenter
            {
                IconId = "new",
                Width = SecondaryBadgeSize,
                Height = SecondaryBadgeSize,
                OverrideDeclaredFill = false,
            };
            var badgeHost = new Border
            {
                Width = SecondaryBadgeSize,
                Height = SecondaryBadgeSize,
                Opacity = string.Equals(model.Id, "notifications", StringComparison.Ordinal) &&
                          ShouldShowNotificationNewBadge(_newNotificationCount)
                    ? 1
                    : 0,
                Child = badge,
            };
            var iconContainer = new Canvas
            {
                Width = IconContainerSize,
                Height = IconContainerSize,
                ClipToBounds = false,
            };
            Canvas.SetLeft(disc, (IconContainerSize - ButtonHitSize) / 2);
            Canvas.SetTop(disc, 0);
            iconContainer.Children.Add(disc);
            Canvas.SetLeft(badgeHost, SecondaryBadgeLeft);
            Canvas.SetTop(badgeHost, SecondaryBadgeTop);
            iconContainer.Children.Add(badgeHost);

            var canvas = new Canvas { Width = ButtonCellWidth, Height = ButtonCellHeight, ClipToBounds = false };
            Canvas.SetLeft(label, (ButtonCellWidth - LabelWidth) / 2);
            Canvas.SetTop(label, 0);
            canvas.Children.Add(label);
            Canvas.SetLeft(iconContainer, (ButtonCellWidth - IconContainerSize) / 2);
            Canvas.SetTop(iconContainer, IconTop - ((ButtonHitSize - IconSize) / 2));
            canvas.Children.Add(iconContainer);

            var cell = new Border { Width = ButtonCellWidth, Height = ButtonCellHeight, Child = canvas };
            cell.PointerEntered += (_, _) => SetSelectedIndex(captured);
            cell.PointerReleased += (_, _) =>
            {
                SetSelectedIndex(captured);
                ActivateSelected();
            };
            _row.Children.Add(cell);
            _buttons.Add(new ButtonVisual(model, cell, disc, icon, badgeHost, label));
        }

        Canvas.SetLeft(_row, DesignWidth - (_items.Count * ButtonCellWidth));
        UpdateVisuals();
    }

    private void SetSelectedIndex(int index)
    {
        if (_items.Count == 0)
        {
            _selectedIndex = -1;
            return;
        }

        var clamped = Math.Clamp(index, 0, _items.Count - 1);
        if (clamped != _selectedIndex)
        {
            _selectedIndex = clamped;
            ShellUiSounds.Play(UiSoundEvent.FocusMove);
        }
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        for (var index = 0; index < _buttons.Count; index++)
        {
            var visual = _buttons[index];
            var focused = !_panelOpen && index == _selectedIndex;
            var selected = _panelOpen && index == _selectedIndex;
            visual.Cell.Opacity = _panelOpen
                ? selected ? 1.0 : 0.16
                : focused ? 1.0 : 0.96;
            visual.Disc.Background = focused ? FocusBrush : Brushes.Transparent;
            visual.Icon.Tint = focused ? DarkIcon : LightIcon;
            visual.Label.IsVisible = focused;
        }
    }

    private void PositionPanel()
    {
        if (_panelHost is null || _panel is null || _selectedIndex < 0)
        {
            return;
        }

        var rowLeft = DesignWidth - (_items.Count * ButtonCellWidth);
        var buttonX = rowLeft + (_selectedIndex * ButtonCellWidth);
        Canvas.SetLeft(_panelHost, PopupLeft(buttonX));
        Canvas.SetTop(_panelHost, DesignHeight - PanelBottom - _panel.PanelHeight);
    }

    private void ConfigureRootTransitions(TimeSpan duration, Easing easing)
    {
        Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = easing },
        ];
        _rootTranslate.Transitions =
        [
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = duration, Easing = easing },
        ];
    }

    private void ConfigurePanelTransitions(TimeSpan duration, Easing easing)
    {
        if (_panelHost is null)
        {
            return;
        }
        _panelHost.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = easing },
        ];
        _panelTranslate.Transitions =
        [
            new DoubleTransition { Property = TranslateTransform.YProperty, Duration = duration, Easing = easing },
        ];
    }

    private void HidePanelForControlCenterClose()
    {
        if (!_panelOpen || _panelHost is null)
        {
            return;
        }

        // Closing the whole Control Center uses the shell's 100 ms app-hide
        // wrapper. The 300 ms modal-hide contract is only for backing out of
        // an otherwise-open function panel.
        _panelOpen = false;
        _panelOwnerId = null;
        PanelClosed?.Invoke(this, EventArgs.Empty);
        CancelPanel();
        ConfigurePanelTransitions(CloseDuration, new LinearEasing());
        _panelHost.Opacity = 0;
        _panelTranslate.Y = 20;
        _panelHost.IsHitTestVisible = false;
        UpdateVisuals();
    }

    private void CancelVisibility()
    {
        _visibilityCancellation?.Cancel();
        _visibilityCancellation?.Dispose();
        _visibilityCancellation = null;
    }

    private void CancelPanel()
    {
        _panelCancellation?.Cancel();
        _panelCancellation?.Dispose();
        _panelCancellation = null;
    }

    private async Task AnimateNotificationBadgeAsync(Border badgeHost, bool show)
    {
        _notificationBadgeCancellation?.Cancel();
        _notificationBadgeCancellation?.Dispose();
        _notificationBadgeCancellation = new CancellationTokenSource();
        var token = _notificationBadgeCancellation.Token;

        try
        {
            if (show)
            {
                badgeHost.Transitions = null;
                badgeHost.Opacity = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(true);
                badgeHost.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(250),
                        Easing = ShellMotion.EaseOutBlast,
                    },
                ];
                badgeHost.Opacity = 1;
            }
            else
            {
                badgeHost.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(100),
                        Easing = new LinearEasing(),
                    },
                ];
                badgeHost.Opacity = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record ButtonVisual(
        ShellControlCenterItem Model,
        Border Cell,
        Border Disc,
        Ps5IconPresenter Icon,
        Border BadgeHost,
        TextBlock Label);
}
