// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// An interactive PS5-style switch. Its authored knob, state masks and focus
/// treatment are read from UI3 at render time; the track and all input remain
/// </summary>
public sealed class Ps5ToggleSwitch : Control
{
    private const double DefaultWidth = 96;
    private const double DefaultHeight = 48;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private double _from;
    private double _to;
    private double _progress;

    public static readonly StyledProperty<bool> IsOnProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, bool>(nameof(IsOn));

    /// <summary>
    /// Compatibility surface for the existing settings bindings. Keeping the
    /// nullable shape lets this replace Avalonia's ToggleSwitch without
    /// changing the launcher settings model.
    /// </summary>
    public static readonly StyledProperty<bool?> IsCheckedProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, bool?>(nameof(IsChecked));

    /// <summary>Retained for existing XAML declarations; labels are not drawn inside the PS5 switch.</summary>
    public static readonly StyledProperty<object?> OnContentProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, object?>(nameof(OnContent));

    /// <summary>Retained for existing XAML declarations; labels are not drawn inside the PS5 switch.</summary>
    public static readonly StyledProperty<object?> OffContentProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, object?>(nameof(OffContent));

    /// <summary>Elapsed time for the moving knob, in milliseconds.</summary>
    public static readonly StyledProperty<double> TransitionDurationProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, double>(nameof(TransitionDuration), 180);

    /// <summary>Whether pressing the control can change its state.</summary>
    public static readonly StyledProperty<bool> IsToggleEnabledProperty =
        AvaloniaProperty.Register<Ps5ToggleSwitch, bool>(nameof(IsToggleEnabled), true);

    static Ps5ToggleSwitch()
    {
        IsOnProperty.Changed.AddClassHandler<Ps5ToggleSwitch>((control, change) =>
        {
            control.BeginTransition();
            var value = change.GetNewValue<bool>();
            if (control.IsChecked != value)
            {
                control.SetCurrentValue(IsCheckedProperty, value);
            }
        });
        IsCheckedProperty.Changed.AddClassHandler<Ps5ToggleSwitch>((control, change) =>
        {
            var value = change.GetNewValue<bool?>() == true;
            if (control.IsOn != value)
            {
                control.SetCurrentValue(IsOnProperty, value);
            }

            control.IsCheckedChanged?.Invoke(control, EventArgs.Empty);
        });
    }

    public Ps5ToggleSwitch()
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
        Focusable = true;
        _progress = IsOn ? 1 : 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _timer.Tick += (_, _) => TickTransition();
    }

    /// <summary>Raised only for a successful user-initiated state change.</summary>
    public event EventHandler? Toggled;

    /// <summary>Matches the existing ToggleSwitch settings event contract.</summary>
    public event EventHandler? IsCheckedChanged;

    public bool IsOn
    {
        get => GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public bool? IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public object? OnContent
    {
        get => GetValue(OnContentProperty);
        set => SetValue(OnContentProperty, value);
    }

    public object? OffContent
    {
        get => GetValue(OffContentProperty);
        set => SetValue(OffContentProperty, value);
    }

    public double TransitionDuration
    {
        get => GetValue(TransitionDurationProperty);
        set => SetValue(TransitionDurationProperty, value);
    }

    public bool IsToggleEnabled
    {
        get => GetValue(IsToggleEnabledProperty);
        set => SetValue(IsToggleEnabledProperty, value);
    }

    /// <summary>
    /// Applies an externally owned setting value. Rebuilt Settings rows use a
    /// snap so focus navigation cannot masquerade as a state change; actual
    /// activation requests the authored knob transition.
    /// </summary>
    public void SetState(bool value, bool animate)
    {
        if (!animate)
        {
            _timer.Stop();
            _clock.Reset();
            _progress = value ? 1 : 0;
            _from = _progress;
            _to = _progress;
        }

        SetCurrentValue(IsOnProperty, value);
        InvalidateVisual();
    }

    internal bool IsTransitionRunning => _timer.IsEnabled;

    internal double VisualProgress => _progress;

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var trackHeight = Math.Min(bounds.Height, 34);
        var track = new Rect(
            bounds.X,
            bounds.Y + ((bounds.Height - trackHeight) / 2),
            bounds.Width,
            trackHeight);
        var radius = trackHeight / 2;
        var onColor = Color.Parse("#1687E8");
        var offColor = Color.Parse("#4A4A52");
        var trackColor = Lerp(offColor, onColor, _progress);
        context.DrawRectangle(new SolidColorBrush(trackColor), null, track, radius, radius);

        if (IsFocused && Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.SwitchBaseHighlight) is { } highlight)
        {
            using (context.PushOpacity(0.32))
            {
                context.DrawImage(highlight, new Rect(highlight.Size), track.Inflate(8));
            }
        }

        var knobSide = Math.Min(track.Height + 8, bounds.Height);
        var inset = Math.Max(2, (track.Height - knobSide) / 2);
        var knobX = track.X + inset + ((track.Width - knobSide - (2 * inset)) * _progress);
        var knob = new Rect(knobX, bounds.Y + ((bounds.Height - knobSide) / 2), knobSide, knobSide);
        context.DrawEllipse(Brushes.White, null, knob.Center, knobSide / 2, knobSide / 2);

        if (Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.SwitchBase) is { } baseTexture)
        {
            context.DrawImage(baseTexture, new Rect(baseTexture.Size), knob);
        }

        var stateTexture = Ps5Ui3ControlAssets.TryGet(
            _progress >= 0.5 ? Ps5Ui3ControlAsset.SwitchOn : Ps5Ui3ControlAsset.SwitchOff);
        if (stateTexture is not null)
        {
            using (context.PushOpacity(0.7))
            {
                context.DrawImage(stateTexture, new Rect(stateTexture.Size), knob.Deflate(5));
            }
        }

        if (IsFocused && Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.SwitchControlHighlight) is { } focus)
        {
            using (context.PushOpacity(0.48))
            {
                context.DrawImage(focus, new Rect(focus.Size), knob.Inflate(6));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsToggleEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        IsOn = !IsOn;
        Toggled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsToggleEnabled || e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        IsOn = !IsOn;
        Toggled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        _clock.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void BeginTransition()
    {
        _from = _progress;
        _to = IsOn ? 1 : 0;
        if (Math.Abs(_to - _from) < 0.0001 || TransitionDuration <= 0)
        {
            _progress = _to;
            InvalidateVisual();
            return;
        }

        _clock.Restart();
        _timer.Start();
    }

    private void TickTransition()
    {
        var t = Math.Clamp(_clock.Elapsed.TotalMilliseconds / Math.Max(1, TransitionDuration), 0, 1);
        // Cubic ease-out keeps the switch responsive without an abrupt stop.
        var eased = 1 - Math.Pow(1 - t, 3);
        _progress = _from + ((_to - _from) * eased);
        InvalidateVisual();
        if (t >= 1)
        {
            _clock.Stop();
            _timer.Stop();
        }
    }

    private static Color Lerp(Color from, Color to, double amount) => Color.FromArgb(
        (byte)Math.Round(from.A + ((to.A - from.A) * amount)),
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
}

/// <summary>
/// Progress control which overlays UI3's authored light texture on an
/// independently rendered track. It supports determinate values and a moving
/// indeterminate pass without storing PS5 resources in the app.
/// </summary>
public sealed class Ps5ProgressBar : Control
{
    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _attached;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Ps5ProgressBar, double>(nameof(Value));

    public static readonly StyledProperty<bool> IsIndeterminateProperty =
        AvaloniaProperty.Register<Ps5ProgressBar, bool>(nameof(IsIndeterminate));

    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<Ps5ProgressBar, bool>(nameof(IsAnimationEnabled), true);

    static Ps5ProgressBar()
    {
        ValueProperty.Changed.AddClassHandler<Ps5ProgressBar>((control, _) => control.InvalidateVisual());
        IsIndeterminateProperty.Changed.AddClassHandler<Ps5ProgressBar>((control, _) => control.UpdateClock());
        IsAnimationEnabledProperty.Changed.AddClassHandler<Ps5ProgressBar>((control, _) => control.UpdateClock());
    }

    public Ps5ProgressBar()
    {
        Height = 16;
        MinWidth = 80;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.021) % 1;
            InvalidateVisual();
        };
    }

    /// <summary>Normalised progress; values outside [0, 1] are clamped when drawn.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsIndeterminate
    {
        get => GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Allows the host's reduced-motion policy to stop the moving pass.</summary>
    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var radius = Math.Min(bounds.Height / 2, 8);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#4B4D54")), null, bounds, radius, radius);
        var fraction = IsIndeterminate ? 0.30 : Math.Clamp(Value, 0, 1);
        if (fraction <= 0)
        {
            return;
        }

        var width = Math.Max(Math.Min(bounds.Height, bounds.Width), bounds.Width * fraction);
        var x = IsIndeterminate
            ? bounds.X + ((bounds.Width + width) * _phase) - width
            : bounds.X;
        var fill = new Rect(x, bounds.Y, width, bounds.Height).Intersect(bounds);
        if (fill.Width <= 0)
        {
            return;
        }

        context.DrawRectangle(new SolidColorBrush(Color.Parse("#F6F7F9")), null, fill, radius, radius);
        if (Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.ProgressBarLight) is { } texture)
        {
            using (context.PushOpacity(0.75))
            {
                context.DrawImage(texture, new Rect(texture.Size), fill);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateClock()
    {
        if (_attached && IsIndeterminate && IsAnimationEnabled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            InvalidateVisual();
        }
    }
}

/// <summary>The UI3 busy-indicator texture family.</summary>
public enum Ps5BusyIndicatorKind
{
    Square,
    Horizontal,
}

/// <summary>
/// Animated busy-indicator using UI3's square arc or horizontal pass. Its
/// timer is detached with the visual tree and can be disabled for reduced
/// motion or deterministic captures.
/// </summary>
public sealed class Ps5BusyIndicator : Control
{
    private readonly DispatcherTimer _timer;
    private double _phase;
    private bool _attached;

    public static readonly StyledProperty<Ps5BusyIndicatorKind> KindProperty =
        AvaloniaProperty.Register<Ps5BusyIndicator, Ps5BusyIndicatorKind>(nameof(Kind));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<Ps5BusyIndicator, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<bool> IsAnimationEnabledProperty =
        AvaloniaProperty.Register<Ps5BusyIndicator, bool>(nameof(IsAnimationEnabled), true);

    static Ps5BusyIndicator()
    {
        KindProperty.Changed.AddClassHandler<Ps5BusyIndicator>((control, _) => control.InvalidateVisual());
        IsActiveProperty.Changed.AddClassHandler<Ps5BusyIndicator>((control, _) => control.UpdateClock());
        IsAnimationEnabledProperty.Changed.AddClassHandler<Ps5BusyIndicator>((control, _) => control.UpdateClock());
    }

    public Ps5BusyIndicator()
    {
        Width = 48;
        Height = 48;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60.0) };
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.018) % 1;
            InvalidateVisual();
        };
    }

    public Ps5BusyIndicatorKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsAnimationEnabled
    {
        get => GetValue(IsAnimationEnabledProperty);
        set => SetValue(IsAnimationEnabledProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (!IsActive)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (Kind == Ps5BusyIndicatorKind.Horizontal)
        {
            RenderHorizontal(context, bounds);
            return;
        }

        var texture = Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.BusyIndicatorSquare);
        var center = bounds.Center;
        var angle = _phase * Math.PI * 2;
        var rotation = Matrix.CreateTranslation(-center.X, -center.Y)
            * Matrix.CreateRotation(angle)
            * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(rotation))
        {
            if (texture is not null)
            {
                context.DrawImage(texture, new Rect(texture.Size), bounds);
            }
            else
            {
                var dot = Math.Max(4, Math.Min(bounds.Width, bounds.Height) / 5);
                context.DrawEllipse(Brushes.White, null,
                    new Point(center.X, bounds.Y + dot), dot / 2, dot / 2);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateClock();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void RenderHorizontal(DrawingContext context, Rect bounds)
    {
        var width = Math.Max(bounds.Height, bounds.Width * 0.32);
        var x = bounds.X + ((bounds.Width + width) * _phase) - width;
        var pass = new Rect(x, bounds.Y, width, bounds.Height).Intersect(bounds);
        if (pass.Width <= 0)
        {
            return;
        }

        if (Ps5Ui3ControlAssets.TryGet(Ps5Ui3ControlAsset.BusyIndicatorHorizontal) is { } texture)
        {
            context.DrawImage(texture, new Rect(texture.Size), pass);
            return;
        }

        context.DrawRectangle(Brushes.White, null, pass, pass.Height / 2, pass.Height / 2);
    }

    private void UpdateClock()
    {
        if (_attached && IsActive && IsAnimationEnabled)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            InvalidateVisual();
        }
    }
}
