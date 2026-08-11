// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Separates the focused title's resting Hub presentation from keyboard,
/// controller, or pointer ownership of its CTA rail.
/// </summary>
public readonly record struct ShellGameHubPresentationState(
    bool IsVisible,
    bool IsInteractive)
{
    public static ShellGameHubPresentationState Resolve(
        bool isSonyPresentation,
        bool hasSelectedTitle,
        int visibleActionCount,
        bool isActive)
    {
        var visible = isSonyPresentation && hasSelectedTitle && visibleActionCount > 0;
        return new ShellGameHubPresentationState(
            visible,
            IsInteractive: visible && isActive);
    }
}

/// <summary>
/// Design-canvas placement for the NPXS40033-owned parts of a focused title
/// Hub.  Values remain authored in 1920x1080 pixels: the root shell Viewbox
/// supplies the uniform window/aspect transform, so this type must not apply
/// a second host-size scale.
/// </summary>
public static class ShellGameHubLayout
{
    private static Npxs40033GameHubContract Contract => Npxs40087ShellContract.GameHub;

    /// <summary>
    /// NPXS40033 renders its icon/title header only as a standalone Hub. HOME
    /// already owns the experience switcher, so repeating the focused title at
    /// the left edge would create a second, false tile.
    /// </summary>
    public static bool ShowsHeaderInEmbeddedHome => false;

    /// <summary>Native Game Hub canvas width.</summary>
    public static double DesignWidth => Contract.DesignWidth;

    /// <summary>CTA origin in the complete 1920x1080 shell canvas.</summary>
    public static Point CtaCanvasOrigin => new(Contract.CtaContainerLeft, Contract.CtaContainerTop);

    /// <summary>
    /// CTA origin relative to the y=128 Game Hub module surface.  Keeping this
    /// relationship explicit prevents the previous accidental y=344 rail.
    /// </summary>
    public static Point CtaOriginInHubSurface => new(
        CtaCanvasOrigin.X,
        CtaCanvasOrigin.Y - ShellHubMetrics.MarginTop);

    /// <summary>
    /// Approximate top-left of Astro's logo slot in the supplied 1920x1080
    /// console capture at 00:36. This is CONSOLE VIDEO MEASURED, not a bundle
    /// </summary>
    public static Point ConsoleMeasuredLogoCanvasOrigin => new(172, 530);

    /// <summary>Logo origin relative to the y=128 Game Hub module surface.</summary>
    public static Point ConsoleMeasuredLogoOriginInHubSurface => new(
        ConsoleMeasuredLogoCanvasOrigin.X,
        ConsoleMeasuredLogoCanvasOrigin.Y - ShellHubMetrics.MarginTop);

    /// <summary>Maximum independent logo extent recovered from Game Hub.</summary>
    public static Size LogoMaximumSize => new(Contract.LogoMaximumWidth, Contract.LogoMaximumHeight);
}
