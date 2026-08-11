// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.Ps5Home;
using Prosperismo.GUI.SystemAssets;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Host actions that can honestly be offered by the local Game Hub adapter.
/// vocabulary: entitlement, streaming, trial, disc, preorder, and network
/// actions are not capabilities this host currently exposes.
/// </summary>
public enum ShellGameHubActionKind
{
    Play,
    ConfigureGame,
}

/// <summary>One host-backed Game Hub action.</summary>
public sealed record ShellGameHubAction(ShellGameHubActionKind Kind, string Label);

/// <summary>
/// Current host facts used to compose a title's CTA list. This is intentionally
/// a capability record rather than a second library/session model.
/// </summary>
public sealed record ShellGameHubHostCapabilities(
    bool CanLaunch,
    bool CanConfigureGame);

/// <summary>
/// The bounded CTA result rendered by <see cref="ShellGameHubCta"/>.
/// NPXS40033's normal post-purchase <c>GameCTA</c> displays no more than one
/// ordinary action; remaining host actions are represented by the overflow.
/// </summary>
public sealed record ShellGameHubCtaModel(
    ShellGameHubAction? PrimaryAction,
    IReadOnlyList<ShellGameHubAction> OverflowActions)
{
    /// <summary>Number of host actions before NPXS40033's visible-action cap.</summary>
    public int AvailableActionCount => (PrimaryAction is null ? 0 : 1) + OverflowActions.Count;

    /// <summary>
    /// The option button follows the recovered action-count rule rather than
    /// the mere presence of an arbitrary companion control.
    /// </summary>
    public bool HasOverflow => Npxs40087ShellContract.GameHub.HasOverflow(AvailableActionCount);

    public int VisibleOrdinaryActionCount => PrimaryAction is null ? 0 : 1;
}

/// <summary>
/// Creates a minimal Game Hub CTA list from actions the active launcher can
/// really perform. The order intentionally makes a locally installed idle
/// title's primary CTA Play; other current launcher operations remain behind
///
/// Evidence: NPXS40033 <c>GameCTA</c>, <c>maxVisibleCtaCount = 1</c>, and the
/// overflow <c>ButtonWithPopupMenuModel</c> path recorded in
/// overflow". Mapping a local Configure action is an ASSUMED host translation,
/// </summary>
public static class ShellGameHubCtaComposer
{
    /// <summary>
    /// The post-purchase <c>GameCTA</c> limit, rather than a local layout
    /// policy.  Further actions belong in NPXS40033's popup-menu model.
    /// </summary>
    public static int MaxVisibleOrdinaryActions =>
        Npxs40087ShellContract.GameHub.MaximumVisibleOrdinaryActions;

    public static ShellGameHubCtaModel Compose(ShellGameHubHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var actions = new List<ShellGameHubAction>(capacity: 2);
        if (capabilities.CanLaunch)
        {
            actions.Add(new ShellGameHubAction(ShellGameHubActionKind.Play, "Play"));
        }

        if (capabilities.CanConfigureGame)
        {
            actions.Add(new ShellGameHubAction(ShellGameHubActionKind.ConfigureGame, "Game settings"));
        }

        var availableActionCount = actions.Count;
        var visibleActionCount = Math.Min(availableActionCount, MaxVisibleOrdinaryActions);
        var primary = visibleActionCount > 0 ? actions[0] : null;
        var overflow = Npxs40087ShellContract.GameHub.HasOverflow(availableActionCount)
            ? actions.Skip(visibleActionCount).ToArray()
            : Array.Empty<ShellGameHubAction>();
        return new ShellGameHubCtaModel(primary, overflow);
    }
}

/// <summary>Raised when the Hub's single ordinary CTA is activated.</summary>
public sealed class ShellGameHubActionRequestedEventArgs : EventArgs
{
    public ShellGameHubActionRequestedEventArgs(ShellGameHubAction action)
    {
        Action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public ShellGameHubAction Action { get; }
}

/// <summary>
/// Raised for the Hub's additional action list. It is explicitly separate from
/// HOME's tile OPTIONS model: the host may later provide a popup without
/// reusing or coupling to the Home context menu.
/// </summary>
public sealed class ShellGameHubOverflowRequestedEventArgs : EventArgs
{
    public ShellGameHubOverflowRequestedEventArgs(IReadOnlyList<ShellGameHubAction> actions)
    {
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    public IReadOnlyList<ShellGameHubAction> Actions { get; }
}

/// <summary>
/// A focused title's compact Game Hub CTA rail. It consumes horizontal focus,
/// activates the one visible primary action, and publishes a typed overflow
/// request when extra host actions exist. Up returns focus to the Home row;
/// the host owns the corresponding Home-to-Hub transition.
/// </summary>
public sealed class ShellGameHubCta : TemplatedControl
{
    /// <summary>The recovered NPXS40033 CTA allocation.</summary>
    public static Npxs40033GameHubContract Contract => Npxs40087ShellContract.GameHub;

    /// <summary>Outer GameCTA rail width, in its 1920-wide design canvas.</summary>
    public static double ContainerWidth => Contract.CtaContainerWidth;

    /// <summary>Outer GameCTA rail height, in its 1920-wide design canvas.</summary>
    public static double ContainerHeight => Contract.CtaContainerHeight;

    /// <summary>Shared NPXS40033 CTA button height.</summary>
    public static double ButtonHeight => Contract.ButtonHeight;

    /// <summary>Shared NPXS40033 CTA gap.</summary>
    public static double ButtonGap => Contract.ButtonGap;

    /// <summary>Fixed width of the condensed popup-menu button.</summary>
    public static double OverflowButtonWidth => Contract.CondensedButtonWidth;

    /// <summary>
    /// PUI's general ButtonBase assigns its background and focus radius as
    /// <c>Height / 2</c>; the 72px GameCTA is therefore a 36px capsule.
    /// </summary>
    public static double ButtonCornerRadius => ButtonHeight / 2.0;

    /// <summary>
    /// Regular CTA allocation.  The 334px result when overflow exists is the
    /// contract's exact <c>422 - 16 - 72</c> derivation.
    /// </summary>
    public static double PrimaryButtonWidth(bool hasOverflow) => hasOverflow
        ? Contract.PrimaryButtonWidthWithOverflow
        : Contract.CtaContainerWidth;

    // Console-video inspection establishes that both CTA surfaces are dark,
    // translucent capsules with light foregrounds. The precise compositing
    // alpha is not a recovered NPXS40033 literal, so it stays a presentation
    private static readonly IBrush ButtonBrush =
        new SolidColorBrush(Color.FromArgb(0x5C, 0x01, 0x06, 0x12));
    private static readonly IBrush ButtonForeground = Brushes.White;

    public static readonly StyledProperty<ShellGameHubCtaModel?> ModelProperty =
        AvaloniaProperty.Register<ShellGameHubCta, ShellGameHubCtaModel?>(nameof(Model));

    private readonly List<Control> _buttons = new();
    private StackPanel? _rail;
    private int _selectedIndex = -1;
    private bool _focusPushQueued;

    public ShellGameHubCta()
    {
        Focusable = true;
        FocusAdorner = null;
        Width = ContainerWidth;
        Height = ContainerHeight;
        Template = BuildTemplate();
        GotFocus += (_, _) => SchedulePushFocusRect();
        LostFocus += (_, _) => ShellFocusRing.For(this)?.Release(this);
    }

    /// <summary>Raised for a host-backed ordinary CTA.</summary>
    public event EventHandler<ShellGameHubActionRequestedEventArgs>? PrimaryActionRequested;

    /// <summary>Raised when the ellipsis represents one or more extra actions.</summary>
    public event EventHandler<ShellGameHubOverflowRequestedEventArgs>? OverflowRequested;

    /// <summary>Raised on Up/Escape, returning control to the Home row.</summary>
    public event EventHandler? ExitRequested;

    public ShellGameHubCtaModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>0 is the ordinary CTA, 1 is the overflow button.</summary>
    public int SelectedIndex => _selectedIndex;

    /// <summary>The number of keyboard-focusable CTA widgets in the model.</summary>
    public int VisibleActionCount => Model?.PrimaryAction is null
        ? 0
        : 1 + (Model.HasOverflow ? 1 : 0);

    /// <summary>Whether the ellipsis is actually visible.</summary>
    public bool IsOverflowVisible => Model?.HasOverflow == true;

    /// <summary>The actual ellipsis visual, for the Hub-owned overflow anchor.
    /// It is intentionally not exposed to HOME's tile option menu.</summary>
    public Control? OverflowAnchor => _buttons.Count > 1 ? _buttons[1] : null;

    /// <summary>Moves focus within the CTA rail without wrapping.</summary>
    public void MoveFocus(int delta)
    {
        if (VisibleActionCount == 0)
        {
            return;
        }

        SetSelectedIndex(Math.Clamp(_selectedIndex + delta, 0, _buttons.Count - 1));
    }

    /// <summary>Focuses one visible CTA widget, clamped to the rail.</summary>
    public void SetSelectedIndex(int index)
    {
        int target = VisibleActionCount == 0 ? -1 : Math.Clamp(index, 0, VisibleActionCount - 1);
        if (target == _selectedIndex)
        {
            return;
        }

        _selectedIndex = target;
        ShellUiSounds.Play(UiSoundEvent.FocusMove);
        SchedulePushFocusRect();
    }

    /// <summary>Activates the focused CTA or asks the host for the overflow.</summary>
    public void ActivateSelected()
    {
        var model = Model;
        if (model is null || _selectedIndex < 0)
        {
            return;
        }

        ShellUiSounds.Play(UiSoundEvent.Enter);
        if (_selectedIndex == 0 && model.PrimaryAction is { } primary)
        {
            PrimaryActionRequested?.Invoke(this, new ShellGameHubActionRequestedEventArgs(primary));
        }
        else if (_selectedIndex == 1 && model.HasOverflow)
        {
            OverflowRequested?.Invoke(this, new ShellGameHubOverflowRequestedEventArgs(model.OverflowActions));
        }
    }

    /// <summary>Publishes the Home-row return route.</summary>
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Re-publishes the focused button rect after a host scale/move.</summary>
    public void RefreshFocusRect() => SchedulePushFocusRect();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            Rebuild();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _rail = e.NameScope.Find<StackPanel>("PART_Rail");
        Rebuild();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                return;
            case Key.Right:
                MoveFocus(1);
                e.Handled = true;
                return;
            case Key.Enter:
            case Key.Space:
                ActivateSelected();
                e.Handled = true;
                return;
            case Key.Up:
            case Key.Escape:
                RequestExit();
                e.Handled = true;
                return;
            default:
                base.OnKeyDown(e);
                return;
        }
    }

    private static FuncControlTemplate BuildTemplate() => new((_, scope) =>
    {
        var rail = new StackPanel
        {
            Name = "PART_Rail",
            Orientation = Orientation.Horizontal,
            Spacing = ButtonGap,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        rail.RegisterInNameScope(scope);
        return rail;
    });

    private void Rebuild()
    {
        if (_rail is null)
        {
            return;
        }

        _rail.Children.Clear();
        _buttons.Clear();
        var model = Model;
        if (model?.PrimaryAction is { } primary)
        {
            AddButton(primary.Label, primary: true, index: 0);
            if (model.HasOverflow)
            {
                AddOverflowButton(index: 1);
            }
        }

        _selectedIndex = VisibleActionCount == 0
            ? -1
            : Math.Clamp(_selectedIndex, 0, VisibleActionCount - 1);
        IsVisible = VisibleActionCount > 0;
        SchedulePushFocusRect();
    }

    private void AddButton(string label, bool primary, int index)
    {
        var text = new TextBlock
        {
            Text = label,
            Foreground = ButtonForeground,
            FontSize = ShellFontSize.Large,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Keep the CTA on the concrete bundled Bold cut. The family and weight
        // are independent in Avalonia, so this avoids synthetic emboldening.
        if (Ps5FontLibrary.TryGet(Ps5FontFace.Bold) is { } buttonFont)
        {
            text.FontFamily = buttonFont;
        }

        AddButtonVisual(
            primary
                ? PrimaryButtonWidth(Model?.HasOverflow == true)
                : OverflowButtonWidth,
            text,
            index);
    }

    /// <summary>
    /// The recovered overflow model uses the semantic <c>option</c> icon.
    /// Render its three marks geometrically rather than depending on a font's
    /// ellipsis glyph and its platform-specific advance/weight.
    /// </summary>
    private void AddOverflowButton(int index)
    {
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var dot = 0; dot < 3; dot++)
        {
            dots.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = ButtonForeground,
            });
        }

        AddButtonVisual(OverflowButtonWidth, dots, index);
    }

    private void AddButtonVisual(double width, Control child, int index)
    {
        // image_button_base is a neutral source texture, not a pre-sized CTA.
        // Ps5Ui3ChromePlate retains its authored rounded ends with nine-patch
        // drawing while the existing brush remains the fallback.
        var button = new Ps5Ui3ChromePlate
        {
            Height = ButtonHeight,
            Width = width,
            Asset = Ps5Ui3ChromeAsset.ButtonBase,
            FallbackBrush = ButtonBrush,
            SliceCornerRadius = ButtonCornerRadius,
            AssetOpacity = 0.055,
            Child = child,
        };

        button.PointerEntered += (_, _) => SetSelectedIndex(index);
        button.PointerPressed += (_, _) =>
        {
            Focus();
            SetSelectedIndex(index);
            SetRingPressed(true);
        };
        button.PointerReleased += (_, _) =>
        {
            SetRingPressed(false);
            ActivateSelected();
        };
        button.PointerExited += (_, _) => SetRingPressed(false);

        _buttons.Add(button);
        _rail!.Children.Add(button);
    }

    private void SchedulePushFocusRect()
    {
        if (_focusPushQueued)
        {
            return;
        }

        _focusPushQueued = true;
        try
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    _focusPushQueued = false;
                    PushFocusRect();
                },
                DispatcherPriority.Render);
        }
        catch
        {
            _focusPushQueued = false;
        }
    }

    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (!IsFocused || !IsEffectivelyVisible ||
                _selectedIndex < 0 || _selectedIndex >= _buttons.Count)
            {
                ring.Release(this);
                return;
            }

            var button = _buttons[_selectedIndex];
            if (button.TransformToVisual(ring) is not { } transform)
            {
                ring.Release(this);
                return;
            }

            ring.Radius = ButtonCornerRadius;
            ring.LineScale = 1.0;
            ring.Claim(this, new Rect(button.Bounds.Size).TransformToAABB(transform));
        }
        catch
        {
            // A detached/half-built visual has no focus plane to update.
        }
    }

    private void SetRingPressed(bool pressed)
    {
        try
        {
            if (IsFocused && ShellFocusRing.For(this) is { } ring && ReferenceEquals(ring.Owner, this))
            {
                ring.SetPressed(pressed);
            }
        }
        catch
        {
            // Decoration only.
        }
    }
}
