// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The two presentation styles <c>DialogPS</c> accepts
/// (EXACT, DIALOG m461 `presentationStyle: "fullScreen"`, m462 `"popup"`).
/// </summary>
public enum ShellDialogPresentation
{
    FullScreen,
    Popup,
}

/// <summary>
/// The action strings a dialog closes with. These are the console's own: the
/// button factory keys its entries <c>positive</c>, <c>neutral</c> and
/// <c>secondNeutral</c>, and every dialog reports a back-out as
/// <c>dismiss</c> (EXACT, DIALOG m185 `DialogButtonFactory`, m461-m463
/// `onClose({ action: ... })`). Note the console renames <c>negativeAction</c>
/// to the <c>neutral</c> key on the way through the factory: there is no
/// "destructive" button kind in the model at all.
/// </summary>
public static class ShellDialogAction
{
    public const string Positive = "positive";
    public const string Neutral = "neutral";
    public const string SecondNeutral = "secondNeutral";
    public const string Dismiss = "dismiss";
}

/// <summary>
/// One entry of the <c>buttons</c> object handed to <c>DialogPS</c>. Field for
/// field the shape the console's factory builds (EXACT, DIALOG m185:76-96).
/// </summary>
public sealed class ShellDialogButton
{
    public ShellDialogButton(string key, string text, Action? onPress = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Text = text ?? string.Empty;
        OnPress = onPress;
    }

    /// <summary>The factory's <c>key</c>, one of <see cref="ShellDialogAction"/>.</summary>
    public string Key { get; }

    /// <summary>The factory's <c>text</c>: the visible label.</summary>
    public string Text { get; }

    /// <summary>The factory's <c>hideOnPress</c>. False keeps the surface up,
    /// which is how the console's "more info" button behaves.</summary>
    public bool HideOnPress { get; init; } = true;

    /// <summary>The factory's <c>soundEffectsEnabled</c>.</summary>
    public bool SoundEffectsEnabled { get; init; } = true;

    /// <summary>The factory's <c>onPress</c>.</summary>
    public Action? OnPress { get; }
}

/// <summary>Carries the closing action out of <see cref="ShellDialog"/>.</summary>
public sealed class ShellDialogClosedEventArgs : EventArgs
{
    public ShellDialogClosedEventArgs(string action)
    {
        Action = action;
    }

    public string Action { get; }
}

/// <summary>
/// Everything a dialog needs, mirroring the props <c>DialogPS</c> is given:
/// a title, a body, an optional short error code, a presentation style and up
/// to three buttons.
/// </summary>
public sealed class ShellDialogRequest
{
    public string? Title { get; init; }

    public string? Body { get; init; }

    /// <summary>The console's <c>shortError</c>: rendered SizeXSmall at opacity
    /// 0.7 beside the title (EXACT, DIALOG m461/m462 `errorCode`).</summary>
    public string? ErrorCode { get; init; }

    public ShellDialogPresentation Presentation { get; init; } = ShellDialogPresentation.Popup;

    /// <summary>The affirming action.</summary>
    public ShellDialogButton? Positive { get; init; }

    /// <summary>What the console builds from <c>negativeAction</c>. It is a
    /// neutral button, not a destructive one.</summary>
    public ShellDialogButton? Neutral { get; init; }

    /// <summary>The console's third slot, used for "more info".</summary>
    public ShellDialogButton? SecondNeutral { get; init; }

    /// <summary>Whether a back-out closes the surface. The power-off warning
    /// blocks it (<c>onBackKeyDown</c> returns false, EXACT, DIALOG
    /// m824:75-77); the resolution confirm routes it to the negative action
    /// (EXACT, DIALOG m673:45).</summary>
    public bool IsDismissable { get; init; } = true;

    /// <summary>Requests the verified <c>psfx_open_error_dialog</c> behavior
    /// instead of the standard <c>psfx_open_dialog</c> behavior.</summary>
    public bool IsError { get; init; }
}

/// <summary>
/// A PS5 system prompt.
///
/// Every dialog the console owns rides one native primitive, <c>DialogPS</c>,
/// and the JS only ever hands it a title, a body, a presentation style and a
/// button map (EXACT, DIALOG m461 `fullScreenDialog.js`, m462 `popupDialog.js`,
/// m463 `suggestionDialog.js`, all under
/// <c>@rnps-ppr/ui-shared-utilities-error-dialog/src/components/</c>). The panel
/// itself is native, so its chrome does not appear in any stylesheet; what does
/// appear, in two independent confirm screens that agree with each other, is
/// the full 1080-tall stack this control reproduces.
///
/// The stack, at the shell's 1920x1080 design resolution:
///
/// <code>
///   y    0
///      170   body top          marginTop 170
///      865   body bottom       1312 x 694..696
///      919   button row top    gap 54
///      991   button row bottom 1312 x 72
///     1080   screen bottom     marginBottom 90
/// </code>
///
/// It closes on 1080 exactly. Sources, cross-validated:
/// DIALOG m674 (`SelectResolutionDialog/.../ChangeConfirmation`) gives
/// `container { top: 170, left: 304, width: 1312, height: 820 }`,
/// `messageContainer { width: 1312, height: 696 }`,
/// `buttonsBoard { flexDirection: "row", width: 1312, height: 72 }`,
/// `noButton { marginRight: 16, width: 384 }`, `yesButton { width: 384 }`.
/// DIALOG m914 (`MediaEventListenerDialog/.../DeleteConfirmationForStorage`)
/// independently gives `bodyarea { marginTop: 170, marginLeft: 304,
/// marginRight: 304, width: 1312, height: 694 }`, `bodyunder { marginTop: 54,
/// marginBottom: 90, marginLeft: 304, width: 1312 }`, and the same 384 wide
/// buttons (its gap is 18 rather than 16). DIALOG m843 (`VisD`) states the
/// same contract as named design constants: `textAreaMarginTop 170`,
/// `textAreaWidth 1312`, `buttonContainerWidth 1312`, `buttonContainerHeight
/// 72`. DIALOG m463 pins the body box for the suggestion dialog at 1312 x 696
/// fullscreen and 1312 x 594 popup.
///
/// 304 + 1312 + 304 = 1920, so the body is centred and the side margin is not a
/// separate fact.
///
/// What is not mined and is not invented here: the panel's own fill and corner
/// radius are native. We carry the shell's existing surface, and it is at least
/// corroborated rather than guessed — the dialog app paints a dialog header
/// band `#080a0f` on a container with `borderRadius: 16` (EXACT, DIALOG
/// m468:31141-31153, `SystemUpdaterDialog/components/TextViewComponent`), which
/// is the same colour and radius our `OptionSurfaceBrush` and `ps5Card` already
/// use. Button fill and radius stay UNRESOLVED; the fill below is the shell's
/// own neutral BLANK, not an invention, and the radius is the dialog family's.
/// </summary>
public sealed class ShellDialog : TemplatedControl
{
    // ---- Shell geometry constants (1920x1080 design resolution) -----------

    /// <summary>Design width of the shell surface the stack is solved against.</summary>
    public const double DesignWidth = 1920;

    /// <summary>Design height of the shell surface.</summary>
    public const double DesignHeight = 1080;

    /// <summary>Body width. The one number every dialog in the bundle agrees
    /// on, and the same width as the settings list (EXACT, DIALOG m463, m674,
    /// m914, m843).</summary>
    public const double BodyWidth = 1312;

    /// <summary>Air either side of the body, (1920 - 1312) / 2 = 304 (EXACT,
    /// DIALOG m674 `container.left`, m914 `bodyarea.marginLeft`).</summary>
    public const double SideMargin = (DesignWidth - BodyWidth) / 2;

    /// <summary>Top of the body box (EXACT, DIALOG m674, m914, m843).</summary>
    public const double TopMargin = 170;

    /// <summary>Body height in the fullScreen style (EXACT, DIALOG m463
    /// `stylesFull`, m674 `messageContainer`). m914's 694 is the same box two
    /// pixels down; 696 is taken because two sources carry it.</summary>
    public const double FullScreenBodyHeight = 696;

    /// <summary>Body height in the popup style (EXACT, DIALOG m463
    /// `stylesPop`).</summary>
    public const double PopupBodyHeight = 594;

    /// <summary>Air between the body and the button row (EXACT, DIALOG m914
    /// `bodyunder.marginTop`; DIALOG m674 derives 52 from its space-between
    /// 820-tall container, so this is the measured value of the two).</summary>
    public const double ButtonRowGap = 54;

    /// <summary>Button row height (EXACT, DIALOG m674 `buttonsBoard`, m843
    /// `buttonContainerHeight`).</summary>
    public const double ButtonRowHeight = 72;

    /// <summary>Button row width, the body's own (EXACT, same sources).</summary>
    public const double ButtonRowWidth = BodyWidth;

    /// <summary>Air below the button row (EXACT, DIALOG m914
    /// `bodyunder.marginBottom`). 170 + 694 + 54 + 72 + 90 = 1080.</summary>
    public const double BottomMargin = 90;

    /// <summary>
    /// Button width. 384 is what both confirm screens use for a paired button
    /// (EXACT, DIALOG m674 `noButton`/`yesButton`, m914
    /// `buttonstyleno`/`buttonstyleyes`). The 388 that recurs elsewhere in the
    /// bundles is the width of a lone action button parked at the screen edge
    /// (EXACT, DIALOG m864 `okBtn { top: 918, right: 172, width: 388 }`), not
    /// of a button in a centred pair, so the pair value is the one used here.
    /// </summary>
    public const double ButtonWidth = 384;

    /// <summary>Button height (EXACT, same sources as the row).</summary>
    public const double ButtonHeight = ButtonRowHeight;

    /// <summary>Air between two buttons (EXACT, DIALOG m674
    /// `noButton.marginRight`; m914 carries 18).</summary>
    public const double ButtonGap = 16;

    /// <summary>
    /// The error-code strip's height (EXACT, DIALOG m461 `errorCodeContainer`).
    /// It doubles as the title band's height here: the console's own -98 top and
    /// -304 right on that strip are offsets against DialogPS's native content
    /// container, whose origin we do not have, so reproducing them literally
    /// against our body box would be false precision.
    /// </summary>
    public const double HeaderHeight = 64;

    /// <summary>Left padding on the error code, which is what separates it from
    /// the title (EXACT, DIALOG m461 and m462 `errorCodeContainer`).</summary>
    public const double ErrorCodePaddingLeft = 48;

    /// <summary>Opacity of the error code (EXACT, DIALOG m461/m462
    /// `errorCode`).</summary>
    public const double ErrorCodeOpacity = 0.7;

    /// <summary>
    /// Corner radius. UNRESOLVED for the native panel; this is the dialog app's
    /// own container radius (EXACT, DIALOG m468:31147) and the same 16 the rest
    /// of the shell uses.
    /// </summary>
    public const double BorderRadius = 16;

    // ---- Palette ----------------------------------------------------------

    // The modal scrim. EXACT, HOME m632:44435. The dialog app's in-app
    // overlays sit at 0.6 instead (EXACT, DIALOG m914 `container`,
    // DIALOG:71429); 0.8 is the shell's own modal scrim, which is the surface
    // this control dims.
    private static readonly IBrush ScrimBrush = new SolidColorBrush(Color.Parse("#CC000000"));

    // The panel. #080a0f is EXACT in the dialog bundle (DIALOG m468:31150) and
    // is already the shell's OptionSurfaceBrush; whether DialogPS's own native
    // panel is that colour stays UNRESOLVED.
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#080A0F"));
    private static readonly IBrush PanelBorderBrush = new SolidColorBrush(Color.Parse("#1AFFFFFF"));

    // Button plate: BLANK, rgba(255,255,255,0.05), from the shell's own tile
    // surface palette (EXACT, HOME m19:2858-2863). Focus is the travelling
    // ring and nothing else, so the plate does not change on focus.
    private static readonly IBrush ButtonBrush = new SolidColorBrush(Color.Parse("#0DFFFFFF"));

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));

    private readonly List<ShellDialogButton> _buttons = new();
    private readonly List<Control> _buttonVisuals = new();

    private Border? _scrim;
    private Canvas? _surface;
    private Border? _body;
    private TextBlock? _title;
    private TextBlock? _errorCode;
    private TextBlock? _bodyText;
    private StackPanel? _buttonRow;
    private bool _closed;

    public ShellDialog()
    {
        Focusable = true;
        ZIndex = 20_000;
        // The scrim dims the whole shell surface, so the control always takes
        // its host's full extent rather than sizing to the panel it draws.
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Template = BuildTemplate();
        GotFocus += (_, _) => PushFocusRect();
        EffectiveViewportChanged += (_, _) => PushFocusRect();
    }

    /// <summary>Raised once, with the action the surface closed on.</summary>
    public event EventHandler<ShellDialogClosedEventArgs>? Closed;

    public static readonly StyledProperty<ShellDialogPresentation> PresentationProperty =
        AvaloniaProperty.Register<ShellDialog, ShellDialogPresentation>(
            nameof(Presentation), ShellDialogPresentation.Popup);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ShellDialog, string?>(nameof(Title));

    public static readonly StyledProperty<string?> BodyProperty =
        AvaloniaProperty.Register<ShellDialog, string?>(nameof(Body));

    public static readonly StyledProperty<string?> ErrorCodeProperty =
        AvaloniaProperty.Register<ShellDialog, string?>(nameof(ErrorCode));

    public static readonly StyledProperty<int> FocusedButtonIndexProperty =
        AvaloniaProperty.Register<ShellDialog, int>(nameof(FocusedButtonIndex), -1);

    public ShellDialogPresentation Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string? ErrorCode
    {
        get => GetValue(ErrorCodeProperty);
        set => SetValue(ErrorCodeProperty, value);
    }

    /// <summary>Which button holds focus, or -1 when the dialog has none.</summary>
    public int FocusedButtonIndex
    {
        get => GetValue(FocusedButtonIndexProperty);
        set => SetFocusedButtonIndex(value);
    }

    /// <summary>Re-publishes the current button geometry to the shared ring.</summary>
    internal void RefreshFocusRect() => PushFocusRect();

    /// <summary>Whether a back-out closes the surface.</summary>
    public bool IsDismissable { get; set; } = true;

    /// <summary>When true, the owning SystemModal manager dims BGLayer through
    /// its flat basemat and this RN-style surface does not add a second scrim.</summary>
    public bool UsesBackgroundBasemat { get; set; }

    /// <summary>The buttons, in the order the row draws them.</summary>
    public IReadOnlyList<ShellDialogButton> Buttons => _buttons;

    /// <summary>Height of the body box for the current presentation style.</summary>
    public double BodyHeight => Presentation == ShellDialogPresentation.FullScreen
        ? FullScreenBodyHeight
        : PopupBodyHeight;

    /// <summary>
    /// Y of the body box's top edge. The fullScreen style is anchored at the
    /// console's 170 and closes on 1080 with the 90 bottom margin. The popup
    /// style's shorter 594 body has no anchor of its own in the bundle, so its
    /// whole stack is centred; INFERRED, and it lands at 180 on a 1080 surface,
    /// ten pixels off the fullScreen anchor.
    /// </summary>
    public double BodyTop => Presentation == ShellDialogPresentation.FullScreen
        ? TopMargin
        : (DesignHeight - StackHeight) / 2;

    /// <summary>Total height of body + gap + button row.</summary>
    public double StackHeight => BodyHeight + ButtonRowGap + ButtonRowHeight;

    /// <summary>Y of the button row's top edge.</summary>
    public double ButtonRowTop => BodyTop + BodyHeight + ButtonRowGap;

    /// <summary>
    /// Width of a centred row of <paramref name="count"/> buttons:
    /// n * 384 + (n - 1) * 16.
    /// </summary>
    public static double ButtonRowContentWidth(int count) => count <= 0
        ? 0
        : (count * ButtonWidth) + ((count - 1) * ButtonGap);

    /// <summary>
    /// Fills the surface from a request. The button order is the console's:
    /// the "more info" slot first, then the neutral (what the factory builds
    /// out of <c>negativeAction</c>), then the affirming one, which is also the
    /// one focus opens on — every confirm screen in the dialog app names the
    /// affirming button as its <c>initialFocusItem</c> (EXACT, DIALOG m673:48
    /// `yesButton`, m913:87 `nextButton`, m824:96 `okButton`).
    /// </summary>
    public void Apply(ShellDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Presentation = request.Presentation;
        Title = request.Title;
        Body = request.Body;
        ErrorCode = request.ErrorCode;
        IsDismissable = request.IsDismissable;

        _buttons.Clear();
        if (request.SecondNeutral is { } second)
        {
            _buttons.Add(second);
        }

        if (request.Neutral is { } neutral)
        {
            _buttons.Add(neutral);
        }

        if (request.Positive is { } positive)
        {
            _buttons.Add(positive);
        }

        RebuildButtons();
        SetFocusedButtonIndex(_buttons.Count - 1);
        UpdateText();
        InvalidateArrange();
    }

    /// <summary>Walks focus along the button row. Clamped at both ends: the
    /// shell has no focus wrap.</summary>
    public void MoveFocus(int delta)
    {
        if (_buttons.Count == 0)
        {
            return;
        }

        var start = FocusedButtonIndex < 0 ? 0 : FocusedButtonIndex;
        var next = Math.Clamp(start + delta, 0, _buttons.Count - 1);
        if (next != FocusedButtonIndex)
        {
            ShellUiSounds.Play(UiSoundEvent.FocusMove);
            SetFocusedButtonIndex(next);
        }
    }

    /// <summary>Presses the focused button.</summary>
    public void ActivateFocused()
    {
        if (FocusedButtonIndex < 0 || FocusedButtonIndex >= _buttons.Count)
        {
            return;
        }

        Press(_buttons[FocusedButtonIndex]);
    }

    /// <summary>Backs out, reporting <c>dismiss</c>. A dialog that blocks the
    /// back key ignores this.</summary>
    public void Dismiss()
    {
        if (IsDismissable)
        {
            Close(ShellDialogAction.Dismiss);
        }
    }

    /// <summary>
    /// Puts a dialog up over <paramref name="host"/>, on the shell's modal
    /// motion (show 250 ms on easeOutBlast after a 50 ms lead-in, hide 300 ms
    /// linear; EXACT, HOME m677:48013-48021), and hands back the action it
    /// closed on.
    /// </summary>
    public static async Task<string> ShowAsync(
        Panel host,
        ShellDialogRequest request,
        bool usesBackgroundBasemat = false,
        Action<ShellDialog>? onCreated = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new ShellDialog();
        dialog.UsesBackgroundBasemat = usesBackgroundBasemat;
        dialog.Apply(request);
        onCreated?.Invoke(dialog);

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, e) => completion.TrySetResult(e.Action);

        host.Children.Add(dialog);
        dialog.Focus();

        ShellUiSounds.Play(request.IsError
            ? UiSoundEvent.OpenErrorDialog
            : UiSoundEvent.OpenDialog);

        await ShellMotion.ShowSurfaceAsync(dialog).ConfigureAwait(true);
        var action = await completion.Task.ConfigureAwait(true);
        await ShellMotion.HideSurfaceAsync(dialog).ConfigureAwait(true);

        ShellFocusRing.For(dialog)?.Release(dialog);
        host.Children.Remove(dialog);
        return action;
    }

    /// <summary>
    /// The confirm the console puts behind <c>MENU_ID_APPLICATION_CLOSE</c>
    /// (EXACT id, HOME m525:37923). The wording is ours: the console's copy is
    /// a localisation id resolved by a native module and is not in the bundles
    /// we hold.
    /// </summary>
    public static ShellDialogRequest CloseApplication(string? titleName, Action? onConfirm = null) =>
        new()
        {
            Presentation = ShellDialogPresentation.Popup,
            Title = "Close application",
            Body = string.IsNullOrWhiteSpace(titleName)
                ? "The application will close. Unsaved progress will be lost."
                : $"{titleName} will close. Unsaved progress will be lost.",
            Neutral = new ShellDialogButton(ShellDialogAction.Neutral, "Cancel"),
            Positive = new ShellDialogButton(ShellDialogAction.Positive, "Close application", onConfirm),
        };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                ActivateFocused();
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Back:
                Dismiss();
                e.Handled = true;
                break;
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _scrim = e.NameScope.Find<Border>("PART_Scrim");
        _surface = e.NameScope.Find<Canvas>("PART_Surface");
        _body = e.NameScope.Find<Border>("PART_Body");
        _title = e.NameScope.Find<TextBlock>("PART_Title");
        _errorCode = e.NameScope.Find<TextBlock>("PART_ErrorCode");
        _bodyText = e.NameScope.Find<TextBlock>("PART_BodyText");
        _buttonRow = e.NameScope.Find<StackPanel>("PART_ButtonRow");

        if (_scrim is { })
        {
            _scrim.Background = UsesBackgroundBasemat ? Brushes.Transparent : ScrimBrush;
        }

        RebuildButtons();
        UpdateText();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // The shared focus-ring adorner can be attached after this control's
        // first arrange pass. Re-publish once the render queue has built the
        // complete visual tree so a dialog opened without an explicit keyboard
        // Focus() call still receives its initial affirmative-button ring.
        Dispatcher.UIThread.Post(PushFocusRect, DispatcherPriority.Render);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TitleProperty
            || change.Property == BodyProperty
            || change.Property == ErrorCodeProperty)
        {
            UpdateText();
        }
        else if (change.Property == PresentationProperty)
        {
            InvalidateArrange();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // Canvas attached positions must be set before its arrange pass. Doing
        // this after base.ArrangeOverride left the button visuals at their old
        // origin for the focus publication below, so the travelling highlight
        // appeared at the top edge until another unrelated layout happened.
        LayoutSurface(finalSize);
        var size = base.ArrangeOverride(finalSize);
        PushFocusRect();
        return size;
    }

    /// <summary>
    /// Places the body box and the button row. Both are centred horizontally,
    /// which at the design width reproduces the console's 304 side margin
    /// exactly; the vertical anchor is the console's own.
    /// </summary>
    private void LayoutSurface(Size size)
    {
        if (_body is null || _buttonRow is null)
        {
            return;
        }

        var left = Math.Max(0, (size.Width - BodyWidth) / 2);

        _body.Width = BodyWidth;
        _body.Height = BodyHeight;
        Canvas.SetLeft(_body, left);
        Canvas.SetTop(_body, BodyTop);

        _buttonRow.Height = ButtonRowHeight;
        var rowWidth = ButtonRowContentWidth(_buttons.Count);
        Canvas.SetLeft(_buttonRow, Math.Max(0, (size.Width - rowWidth) / 2));
        Canvas.SetTop(_buttonRow, ButtonRowTop);
    }

    private void UpdateText()
    {
        if (_title is { })
        {
            _title.Text = Title ?? string.Empty;
            _title.IsVisible = !string.IsNullOrEmpty(Title);
        }

        if (_errorCode is { })
        {
            _errorCode.Text = ErrorCode ?? string.Empty;
            _errorCode.IsVisible = !string.IsNullOrEmpty(ErrorCode);
        }

        if (_bodyText is { })
        {
            _bodyText.Text = Body ?? string.Empty;
        }
    }

    private void RebuildButtons()
    {
        if (_buttonRow is null)
        {
            return;
        }

        _buttonRow.Children.Clear();
        _buttonVisuals.Clear();

        for (int i = 0; i < _buttons.Count; i++)
        {
            var index = i;
            var model = _buttons[i];

            var label = new TextBlock
            {
                Text = model.Text,
                FontSize = ShellFontSize.Normal,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // image_button_base is rendered as a nine-patch at the existing
            // allocation. The local plate remains underneath when the PS5 RCO
            // cannot be reached, so dialog geometry and input never depend on
            var plate = new Ps5Ui3ChromePlate
            {
                Width = ButtonWidth,
                Height = ButtonHeight,
                Asset = Ps5Ui3ChromeAsset.ButtonBase,
                FallbackBrush = ButtonBrush,
                SliceCornerRadius = BorderRadius,
                AssetOpacity = 0.05,
                Margin = new Thickness(0, 0, i < _buttons.Count - 1 ? ButtonGap : 0, 0),
                Child = label,
            };

            // Pointer entry moves focus rather than lighting a second highlight:
            // one travelling ring per scene is the shell's whole focus story.
            plate.PointerEntered += (_, _) => SetFocusedButtonIndex(index);
            plate.PointerPressed += (_, _) => SetRingPressed(true);
            plate.PointerReleased += (_, _) =>
            {
                SetRingPressed(false);
                Press(model);
            };

            _buttonRow.Children.Add(plate);
            _buttonVisuals.Add(plate);
        }

        InvalidateArrange();
    }

    private void SetFocusedButtonIndex(int index)
    {
        var clamped = _buttons.Count == 0 ? -1 : Math.Clamp(index, 0, _buttons.Count - 1);
        if (clamped == FocusedButtonIndex)
        {
            return;
        }

        SetValue(FocusedButtonIndexProperty, clamped);
        PushFocusRect();
    }

    private void Press(ShellDialogButton button)
    {
        try
        {
            if (button.SoundEffectsEnabled)
            {
                ShellUiSounds.Play(button.Key switch
                {
                    ShellDialogAction.Positive => UiSoundEvent.YesInDialog,
                    ShellDialogAction.Neutral or ShellDialogAction.SecondNeutral =>
                        UiSoundEvent.NeutralInDialog,
                    _ => UiSoundEvent.NoInDialog,
                });
            }
            button.OnPress?.Invoke();
        }
        catch (Exception)
        {
            // Dialog callbacks are application work; a failure must not leave
            // the modal/input lock alive or escape through the UI event loop.
        }
        finally
        {
            if (button.HideOnPress)
            {
                Close(button.Key);
            }
        }
    }

    private void Close(string action)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (action == ShellDialogAction.Dismiss)
        {
            ShellUiSounds.Play(UiSoundEvent.Cancel);
        }
        Closed?.Invoke(this, new ShellDialogClosedEventArgs(action));
    }

    /// <summary>Retargets the scene's single focus ring onto the focused
    /// button. The dialog publishes a rect and nothing more.</summary>
    private void PushFocusRect()
    {
        try
        {
            if (ShellFocusRing.For(this) is not { } ring)
            {
                return;
            }

            if (FocusedButtonIndex < 0 || FocusedButtonIndex >= _buttonVisuals.Count)
            {
                ring.Release(this);
                return;
            }

            var plate = _buttonVisuals[FocusedButtonIndex];
            if (plate.Bounds.Width <= 0 || plate.TransformToVisual(ring) is not { } transform)
            {
                return;
            }

            ring.Radius = BorderRadius;
            ring.Claim(this, new Rect(plate.Bounds.Size).TransformToAABB(transform));
        }
        catch
        {
            // A detached or half-built tree just leaves the ring where it is.
        }
    }

    private void SetRingPressed(bool pressed)
    {
        try
        {
            if (ShellFocusRing.For(this) is { } ring && ReferenceEquals(ring.Owner, this))
            {
                ring.SetPressed(pressed);
            }
        }
        catch
        {
            // Press feedback is decoration.
        }
    }

    /// <summary>
    /// The surface: a scrim over everything, then the body box and the button
    /// row on a canvas so both can be placed at the console's own coordinates.
    /// The body carries a header row (title left, error code right, the
    /// space-between row of DIALOG m462 `headerContainer`) above copy that is
    /// centred in what is left, matching the error dialogs' own
    /// `contentContainer { justifyContent: "center", alignItems: "center" }`.
    /// </summary>
    private static FuncControlTemplate BuildTemplate()
    {
        return new FuncControlTemplate((_, ns) =>
        {
            var scrim = new Border { Name = "PART_Scrim", Background = ScrimBrush }.RegisterInNameScope(ns);

            var title = new TextBlock
            {
                Name = "PART_Title",
                FontSize = ShellFontSize.Large,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }.RegisterInNameScope(ns);

            var errorCode = new TextBlock
            {
                Name = "PART_ErrorCode",
                FontSize = ShellFontSize.XSmall,
                Foreground = TextBrush,
                Opacity = ErrorCodeOpacity,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(ErrorCodePaddingLeft, 0, 0, 0),
            }.RegisterInNameScope(ns);

            var header = new Grid
            {
                Height = HeaderHeight,
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            };
            header.Children.Add(title);
            Grid.SetColumn(errorCode, 1);
            header.Children.Add(errorCode);

            var bodyText = new TextBlock
            {
                Name = "PART_BodyText",
                FontSize = ShellFontSize.Normal,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }.RegisterInNameScope(ns);

            var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            content.Children.Add(header);
            Grid.SetRow(bodyText, 1);
            content.Children.Add(bodyText);

            var bodyContent = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(48, 24, 48, 24),
                Child = content,
            };

            // image_popup_dialog_base is an 18px source texture. Preserve its
            // corner pixels through the same scale-nine-patch route rather
            // than stretching that square across the large dialog body.
            var bodyChrome = new Ps5Ui3ChromePlate
            {
                Asset = Ps5Ui3ChromeAsset.PopupDialogBase,
                FallbackBrush = PanelBrush,
                SliceCornerRadius = BorderRadius,
                AssetOpacity = 0.28,
                Child = bodyContent,
            };

            var body = new Border
            {
                Name = "PART_Body",
                Background = Brushes.Transparent,
                BorderBrush = PanelBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(BorderRadius),
                Child = bodyChrome,
            }.RegisterInNameScope(ns);

            var buttonRow = new StackPanel
            {
                Name = "PART_ButtonRow",
                Orientation = Orientation.Horizontal,
                Height = ButtonRowHeight,
            }.RegisterInNameScope(ns);

            var surface = new Canvas { Name = "PART_Surface" }.RegisterInNameScope(ns);
            surface.Children.Add(body);
            surface.Children.Add(buttonRow);

            return new Panel { Children = { scrim, surface } };
        });
    }
}
