// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The shell's design space, measured from <c>Sce.PlayStation.PUI.UISystem</c>
/// and <c>Sce.PlayStation.PUI.LayoutSettings</c>.
///
/// Every constant here is a measurement, not a choice. See
/// <c>docs/ps5-shell-recovery-audit.md</c> §2.2, which supersedes the earlier
/// layout spec's "safe area UNRECOVERED" verdict: the safe area was found, and
/// it is a <em>rectangle</em>, not a per-edge inset. There is nothing left to
/// invent here, so nothing in this file may be tuned.
/// </summary>
public static class Ps5DesignSpace
{
    /// <summary><c>UIValues.ScreenWidth</c>. The whole shell is authored here.</summary>
    public const double Width = 1920.0;

    /// <summary><c>UIValues.ScreenHeight</c>.</summary>
    public const double Height = 1080.0;

    /// <summary>
    /// <c>LayoutSettings.SafetyArea = new Rectangle(W*0.05, H*0.05, W*0.9, H*0.9)</c>
    /// = (96, 54, 1728, 972). The classic 5 % title-safe rect. It is a rect in
    /// design coordinates; do not re-derive it as four insets, and do not apply
    /// it as a margin to the root.
    /// </summary>
    public static Rect SafetyArea { get; } = new(
        Width * 0.05, Height * 0.05, Width * 0.9, Height * 0.9);

    /// <summary>
    /// <c>LayoutSettings.EnabledSafeAreaScaling</c>. False on retail, so
    /// <see cref="SafeAreaScale"/> is pinned at 1.0 and a build that ignores it
    /// is byte-correct for the shipped configuration.
    /// </summary>
    public const bool EnabledSafeAreaScaling = false;

    /// <summary><c>LayoutSettings.ThresholdSafeAreaScale</c>; any scale at or above snaps to 1.</summary>
    public const float ThresholdSafeAreaScale = 0.9994792f;

    /// <summary><c>UIValues.SafeAreaScale</c> at init.</summary>
    public const double DefaultSafeAreaScale = 1.0;

    /// <summary><c>UISystem.MinimumLoopTime</c>, normal refresh rates.</summary>
    public const double MinimumLoopTimeMilliseconds = 16.683;

    /// <summary><c>UISystem.MinimumLoopTime</c> when VideoOutRefreshRate is in (89, 91).</summary>
    public const double MinimumLoopTimeMilliseconds90Hz = 33.366;

    /// <summary>
    /// <c>UpdateSafeArea()</c>, verbatim: scaling is skipped entirely when
    /// disabled, and any value at or above the threshold snaps to exactly 1.
    /// </summary>
    /// <param name="displaySafeArea"><c>SystemParameters.DisplaySafeArea</c>.</param>
    /// <param name="enabled"><c>EnabledSafeAreaScaling</c>.</param>
    public static double ResolveSafeAreaScale(double displaySafeArea, bool enabled = EnabledSafeAreaScaling)
    {
        if (!enabled)
        {
            return 1.0;
        }

        return displaySafeArea >= ThresholdSafeAreaScale ? 1.0 : displaySafeArea;
    }

    /// <summary>True when a resolved scale counts as scaled (<c>UIValues.IsScaledSafeArea</c>).</summary>
    /// <param name="scale">A scale from <see cref="ResolveSafeAreaScale"/>.</param>
    public static bool IsScaledSafeArea(double scale) => scale < ThresholdSafeAreaScale;

    /// <summary>
    /// Render-target size for a pixel density:
    /// <c>ceil(1920 * d) x ceil(1080 * d)</c>.
    /// </summary>
    /// <param name="pixelDensity">Pixel density; 1.0 on a 1080p output.</param>
    public static PixelSize RenderTargetSize(double pixelDensity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelDensity);
        return new PixelSize(
            (int)Math.Ceiling(Width * pixelDensity),
            (int)Math.Ceiling(Height * pixelDensity));
    }

    /// <summary>
    /// The uniform scale that fits the 1920x1080 canvas inside a viewport,
    /// letterboxing rather than cropping. This is the only correct way to put
    /// the design space on a window: any non-uniform fit breaks every recovered
    /// proportion at once.
    /// </summary>
    /// <param name="viewport">Available size in device-independent pixels.</param>
    public static double FitScale(Size viewport)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return 1.0;
        }

        return Math.Min(viewport.Width / Width, viewport.Height / Height);
    }

    /// <summary>
    /// Places the 1920x1080 canvas centred inside a viewport at
    /// <see cref="FitScale"/>. Returns the canvas rect in viewport coordinates.
    /// </summary>
    /// <param name="viewport">Available size in device-independent pixels.</param>
    public static Rect FitCanvas(Size viewport)
    {
        var scale = FitScale(viewport);
        var w = Width * scale;
        var h = Height * scale;
        return new Rect((viewport.Width - w) / 2.0, (viewport.Height - h) / 2.0, w, h);
    }
}
