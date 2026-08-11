// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The home screen's focus trap, HOME m805 with its stylesheet m806.
///
/// <code>
/// focusTrap: {
///     position: "absolute",
///     height: EXPERIENCE_SIZE, width: EXPERIENCE_SIZE,
///     top: 157, left: SCALED_EXP_MARGIN_LEFT + 32,
///     opacity: 0, borderRadius: BORDER_RADIUS
/// }
/// </code>
///
/// and the element itself:
///
/// <code>
/// &lt;View focusable name="focus-trap" focusCustomSettings={{
///     canMoveLeft: false, canMoveRight: false,
///     canMoveUp: false,   canMoveDown: false }} /&gt;
/// </code>
///
/// It is invisible on purpose. The home screen needs somewhere to put focus
/// that cannot then be walked out of - while the row is still assembling, or
/// while something modal owns the screen - and a zero-opacity focusable box
/// with every direction refused is how the console does it. It is not dead
/// markup and it is not a placeholder: delete it and directional input escapes
/// to whatever happens to be focusable next.
///
/// Its box is a resting tile's, parked 32 px right of where the focused tile
/// sits, so the trap sits under the row rather than anywhere arbitrary.
/// </summary>
public static class ShellFocusTrapMetrics
{
    /// <summary><c>focusTrap.width</c> and <c>.height</c>: a resting tile.</summary>
    public const double Size = ShellTileRow.ExperienceSize;

    /// <summary><c>focusTrap.top</c>.</summary>
    public const double Top = 157.0;

    /// <summary>
    /// <c>focusTrap.left</c>, <c>SCALED_EXP_MARGIN_LEFT + 32</c>.
    /// </summary>
    public const double Left = ShellTileRow.ScaledExpMarginLeft + 32.0;

    /// <summary>The 32 the trap is offset by from the focused tile's inset.</summary>
    public const double LeftOffsetFromAnchor = 32.0;

    /// <summary><c>focusTrap.borderRadius</c>.</summary>
    public const double CornerRadius = ShellTileRow.BorderRadius;

    /// <summary><c>focusTrap.opacity</c>. It is never drawn.</summary>
    public const double Opacity = 0.0;
}

/// <summary>
/// An invisible focusable box that refuses every direction, so focus put here
/// stays here. See <see cref="ShellFocusTrapMetrics"/> for why the console has
/// one.
/// </summary>
public sealed class ShellFocusTrap : Control
{
    /// <summary>Authored pixels to host pixels.</summary>
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ShellFocusTrap, double>(nameof(Scale), 1.0);

    public ShellFocusTrap()
    {
        Focusable = true;
        Opacity = ShellFocusTrapMetrics.Opacity;
        // Invisible and untouchable: it takes focus programmatically, never a
        // pointer, which is what stops it swallowing clicks meant for the row.
        IsHitTestVisible = false;
        Apply();
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>Where the trap sits, in authored pixels.</summary>
    public static Rect AuthoredBounds => new(
        ShellFocusTrapMetrics.Left,
        ShellFocusTrapMetrics.Top,
        ShellFocusTrapMetrics.Size,
        ShellFocusTrapMetrics.Size);

    /// <summary>Directional movement out of the trap is refused in every
    /// direction, matching the source's focusCustomSettings.</summary>
    public static bool CanMove(ShellFocusDirection direction) => false;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScaleProperty)
        {
            Apply();
        }
    }

    private void Apply()
    {
        double scale = Scale > 0 ? Scale : 1.0;
        Width = ShellFocusTrapMetrics.Size * scale;
        Height = ShellFocusTrapMetrics.Size * scale;
        Margin = new Thickness(
            ShellFocusTrapMetrics.Left * scale,
            ShellFocusTrapMetrics.Top * scale,
            0,
            0);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }
}
