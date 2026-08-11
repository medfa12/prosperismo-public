// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The console's scrolling title. The cycle itself is
/// <see cref="ShellMarqueeCycle"/>, taken from the managed shell's
/// <c>MarqueeLabelElement</c>; this draws it.
///
/// The JavaScript side only declares the intent. <c>ScrollTextPS.ps.js</c> in
/// react-native-playstation is a wrapper over the native <c>RCTScrollText</c>
/// view, and the home bundle's TitleContainer picks
/// <c>ellipsizeMode: "marquee"</c> for the focused title and <c>"tail"</c> for
/// every other one, so a title only scrolls while it is the selection.
///
/// A label whose text fits never animates, which is why most of the home screen
/// is still.
/// </summary>
public sealed class ShellMarqueeText : Control
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ShellMarqueeText, string?>(nameof(Text));

    /// <summary>
    /// Whether this label is the one that scrolls. Only the focused title is
    /// marked <c>marquee</c> by the shell.
    /// </summary>
    public static readonly StyledProperty<bool> IsMarqueeProperty =
        AvaloniaProperty.Register<ShellMarqueeText, bool>(nameof(IsMarquee));

    public static readonly StyledProperty<ShellMarqueeSpeed> SpeedProperty =
        AvaloniaProperty.Register<ShellMarqueeText, ShellMarqueeSpeed>(nameof(Speed));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ShellMarqueeText, IBrush?>(nameof(Foreground), Brushes.White);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<ShellMarqueeText, double>(nameof(FontSize), 26.0);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<ShellMarqueeText, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<ShellMarqueeText, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    /// <summary>Do not run frames off a dispatcher; the host drives Advance.</summary>
    public static readonly StyledProperty<bool> ManualClockProperty =
        AvaloniaProperty.Register<ShellMarqueeText, bool>(nameof(ManualClock));

    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer? _timer;
    private FormattedText? _formatted;
    private double _textWidth;

    static ShellMarqueeText()
    {
        AffectsRender<ShellMarqueeText>(ForegroundProperty);
        AffectsMeasure<ShellMarqueeText>(
            TextProperty,
            FontSizeProperty,
            FontFamilyProperty,
            FontWeightProperty);
    }

    /// <summary>The scroll cycle this label is running.</summary>
    public ShellMarqueeCycle Cycle { get; } = new();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsMarquee
    {
        get => GetValue(IsMarqueeProperty);
        set => SetValue(IsMarqueeProperty, value);
    }

    public ShellMarqueeSpeed Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public bool ManualClock
    {
        get => GetValue(ManualClockProperty);
        set => SetValue(ManualClockProperty, value);
    }

    /// <summary>Rendered width of the whole string.</summary>
    public double TextWidth => _textWidth;

    /// <summary>How far the text can travel before its end is flush right.</summary>
    public double ScrollDistance => Math.Max(0.0, _textWidth - Bounds.Width);

    /// <summary>Advances the cycle. The host may drive this instead of a timer.</summary>
    public void Advance(TimeSpan delta)
    {
        if (Cycle.Advance(delta.TotalMilliseconds, ScrollDistance))
        {
            InvalidateVisual();
        }
        else
        {
            StopTimer();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty
            || change.Property == FontSizeProperty
            || change.Property == FontFamilyProperty
            || change.Property == FontWeightProperty)
        {
            _formatted = null;
            _textWidth = 0.0;
        }

        if (change.Property == TextProperty
            || change.Property == IsMarqueeProperty
            || change.Property == FontSizeProperty
            || change.Property == FontFamilyProperty
            || change.Property == FontWeightProperty
            || change.Property == SpeedProperty)
        {
            Cycle.Speed = Speed;
            Cycle.Reset();
            UpdateStatus();
            Wake();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = BuildFormattedText();
        double height = text?.Height ?? FontSize;
        // The label never asks for the full text width: it is meant to be
        // constrained and to scroll inside that constraint.
        double width = double.IsInfinity(availableSize.Width)
            ? (text?.Width ?? 0.0)
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateStatus();
        Wake();
        return size;
    }

    private FormattedText? BuildFormattedText()
    {
        if (string.IsNullOrEmpty(Text))
        {
            _formatted = null;
            _textWidth = 0.0;
            return null;
        }

        if (_formatted is not null)
        {
            return _formatted;
        }

        try
        {
            _formatted = new FormattedText(
                Text!,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle.Normal, FontWeight),
                FontSize,
                Foreground ?? Brushes.White);
            _textWidth = _formatted.Width;
        }
        catch (InvalidOperationException)
        {
            // No font manager, so there is nothing to measure and nothing to
            // draw. The cycle still behaves; it simply has no distance to run.
            _formatted = null;
            _textWidth = 0.0;
        }

        return _formatted;
    }

    private void UpdateStatus()
    {
        BuildFormattedText();
        bool fits = _textWidth <= Bounds.Width + 0.5;
        if (!IsMarquee || fits || string.IsNullOrEmpty(Text))
        {
            Cycle.SetShort();
            StopTimer();
        }
        else if (Cycle.Status == ShellMarqueeStatus.NoMoveByShortText)
        {
            Cycle.Reset();
        }
    }

    public override void Render(DrawingContext context)
    {
        var text = BuildFormattedText();
        if (text is null)
        {
            return;
        }

        text.SetForegroundBrush(Foreground ?? Brushes.White);

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            if (Cycle.Status == ShellMarqueeStatus.NoMoveByShortText)
            {
                // A label that fits is drawn plainly and trimmed, which is the
                // console's `ellipsizeMode: "tail"` path.
                text.MaxTextWidth = Bounds.Width;
                text.Trimming = TextTrimming.CharacterEllipsis;
                context.DrawText(text, new Point(0, 0));
                return;
            }

            text.MaxTextWidth = double.PositiveInfinity;
            text.Trimming = TextTrimming.None;
            using (context.PushOpacity(Cycle.Opacity))
            {
                context.DrawText(text, new Point(-Cycle.Offset, 0));
            }
        }
    }

    private void Wake()
    {
        if (ManualClock || Cycle.Status == ShellMarqueeStatus.NoMoveByShortText)
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                _timer.Tick += OnTick;
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            // No dispatcher: the label simply does not scroll.
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _stopwatch.Elapsed;
        _stopwatch.Restart();
        Advance(delta);
    }

    private void StopTimer()
    {
        try
        {
            _timer?.Stop();
            _stopwatch.Reset();
        }
        catch
        {
            // Nothing to unwind.
        }
    }
}
