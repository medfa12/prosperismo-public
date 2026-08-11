// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// Draws one <c>iconid_*</c> pictogram as a vector, at whatever size the layout
/// gives it.
///
/// SVG on a 64x64 grid; resolving one to a bitmap at a fixed size would make
/// every other size a resample of a resample, which is exactly the softness that
/// gives a rebuilt shell away. Here the geometry is scaled and filled at render
/// time, so a 42 px metadata badge and a 56 px system icon are both exact.</para>
///
/// <para>The bundled SVG ids remain vector and the authored PNG ids are drawn
/// from the same package. When an id does not exist, the control draws a hollow marker rather than a substitute glyph.
/// That marker is meant to be noticed: an unfinished icon is information, a
/// plausible stand-in is a lie that survives review.</para>
/// </summary>
public sealed class Ps5IconPresenter : Control
{
    /// <summary>Icon id, with or without the <c>iconid_</c> prefix.</summary>
    public static readonly StyledProperty<string?> IconIdProperty =
        AvaloniaProperty.Register<Ps5IconPresenter, string?>(nameof(IconId));

    /// <summary>
    /// Fill for shapes that declare none. <c>IconPS.ps.js</c> defaults to
    /// <c>#ffffff</c> (<see cref="Ps5HomeMetrics.IconNormal"/>) and swaps in the
    /// positive/negative emphasis colours for single-layer icons.
    /// </summary>
    public static readonly StyledProperty<Color> TintProperty =
        AvaloniaProperty.Register<Ps5IconPresenter, Color>(
            nameof(Tint), defaultValue: Colors.White);

    /// <summary>
    /// Applies <see cref="Tint"/> to every shape, including SVG paths carrying
    /// an authored white fill. IconPS uses this for inverted emphasis.
    /// </summary>
    public static readonly StyledProperty<bool> OverrideDeclaredFillProperty =
        AvaloniaProperty.Register<Ps5IconPresenter, bool>(nameof(OverrideDeclaredFill));

    /// <summary>
    /// True once the id resolved to a real vector icon. Read it to decide
    /// whether a caller's fallback text should be shown alongside.
    /// </summary>
    public static readonly DirectProperty<Ps5IconPresenter, bool> IsResolvedProperty =
        AvaloniaProperty.RegisterDirect<Ps5IconPresenter, bool>(
            nameof(IsResolved), static o => o.IsResolved);

    private Ps5VectorIcon? _icon;
    private Bitmap? _rasterIcon;
    private bool _isResolved;

    static Ps5IconPresenter()
    {
        AffectsRender<Ps5IconPresenter>(TintProperty, OverrideDeclaredFillProperty);
    }

    /// <inheritdoc cref="IconIdProperty"/>
    public string? IconId
    {
        get => GetValue(IconIdProperty);
        set => SetValue(IconIdProperty, value);
    }

    /// <inheritdoc cref="TintProperty"/>
    public Color Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    /// <inheritdoc cref="OverrideDeclaredFillProperty"/>
    public bool OverrideDeclaredFill
    {
        get => GetValue(OverrideDeclaredFillProperty);
        set => SetValue(OverrideDeclaredFillProperty, value);
    }

    /// <inheritdoc cref="IsResolvedProperty"/>
    public bool IsResolved
    {
        get => _isResolved;
        private set => SetAndRaise(IsResolvedProperty, ref _isResolved, value);
    }

    /// <summary>
    /// Test seam: supplies an icon directly, bypassing packaged lookup, so the
    /// </summary>
    /// <param name="icon">Icon to draw, or null to clear.</param>
    internal void SetIcon(Ps5VectorIcon? icon)
    {
        _icon = icon;
        _rasterIcon = null;
        IsResolved = icon is not null;
        InvalidateVisual();
    }

    private void ResolveIcon(string? id)
    {
        _icon = Ps5BundledIconLibrary.TryGetVector(id);
        _rasterIcon = _icon is null
            ? Ps5BundledIconLibrary.TryGetRaster(id)
            : null;
        IsResolved = _icon is not null || _rasterIcon is not null;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // The bundle's own default box. A parent that sets Width/Height wins,
        // which is the normal case for every icon in the home layout.
        const double side = 64;
        return new Size(
            double.IsInfinity(availableSize.Width) ? side : Math.Min(side, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? side : Math.Min(side, availableSize.Height));
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconIdProperty)
        {
            ResolveIcon(IconId);
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (_icon is not null)
        {
            _icon.Render(context, bounds, Tint, OverrideDeclaredFill);
            return;
        }

        if (_rasterIcon is not null)
        {
            context.DrawImage(_rasterIcon, new Rect(_rasterIcon.Size), bounds);
            return;
        }

        DrawUnresolvedMarker(context, bounds, Tint);
    }

    /// <summary>
    /// The "this icon is not recovered" marker: a hairline box with a diagonal.
    /// Deliberately ugly and deliberately not a pictogram, so a screenshot shows
    /// at a glance which cells are real.
    /// </summary>
    /// <param name="context">Target drawing context.</param>
    /// <param name="bounds">Box to mark.</param>
    /// <param name="tint">Colour to draw the marker in.</param>
    internal static void DrawUnresolvedMarker(DrawingContext context, Rect bounds, Color tint)
    {
        ArgumentNullException.ThrowIfNull(context);

        var pen = new Pen(new SolidColorBrush(tint, 0.35), 1);
        var inset = bounds.Deflate(0.5);
        context.DrawRectangle(null, pen, inset);
        context.DrawLine(pen, inset.TopLeft, inset.BottomRight);
    }
}
