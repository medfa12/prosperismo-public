// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The area pass of the focus highlight: the soft wash that sits under the
/// focused item and is composited beneath the line pass.
/// </summary>
/// <remarks>
/// This was previously not drawn at all, on the grounds that its per-pixel field
/// was unrecovered. It is recovered now. The field is a rounded-box signed
/// distance, read off the AreaFocus shader embedded in libScePsm.sprx at file
/// offset 0x004F5AE0 - see <see cref="Ps5FocusField"/> for the disassembly
/// provenance.
///
/// The field is evaluated on the CPU into a small bitmap and drawn scaled,
/// rather than as a real fragment shader. That is a deliberate tradeoff and the
/// only part of this that is ours: the distance field, the smoothstep, the gamma
/// is a choice. The field is smooth and monotonic away from the edge band, so a
/// modest grid upscales without visible banding, and the cost is paid once per
/// distinct rect size rather than per frame.
///
/// The later shader trace recovered the remaining card composition too. At
/// rest the area source is the diagonal <c>ShimmerParam</c> interpolation; while
/// moving, <c>MorphByRatioIntensity</c> blends it toward the bound
/// <c>image_focus_noise</c> texture. Pressing pulls the result toward 0.15 before
/// area-size falloff, coverage and alpha gamma are applied. HOME cards do not
/// opt into <c>renderFrontOfFocus</c>, so this pass belongs over their artwork.
/// UI3's area edge fade remains disabled in stock 4.03, therefore the bitmap is
/// clipped to the target bounds and never becomes an invented exterior bloom.
/// </remarks>
internal static class ShellFocusWash
{
    /// <summary>
    /// Grid the field is evaluated on before upscaling. The edge band is the
    /// only high-frequency part and it is a smoothstep, so this is ample.
    /// </summary>
    private const int Grid = 96;

    private static WriteableBitmap? _cache;
    private static double _cacheAspect = double.NaN;
    private static double _cacheRadiusRatio = double.NaN;
    private static double _cacheFadeRatio = double.NaN;
    private static double _cacheBodyRatio = double.NaN;
    private static int _cacheAnimationFrame = -1;
    private static double _cacheMoving = double.NaN;
    private static double _cachePressing = double.NaN;

    /// <summary>
    /// Draws the wash for <paramref name="body"/>, or does nothing when the
    /// size gate rejects it.
    /// </summary>
    /// <param name="context">Target.</param>
    /// <param name="body">The focus rect, already inflated by the caller.</param>
    /// <param name="radius">Corner radius, inherited from the focused widget.</param>
    /// <param name="alpha">Area opacity from the timeline, before the size falloff.</param>
    /// <param name="screenWidth">Canvas width, for the size gate.</param>
    /// <param name="screenHeight">Canvas height, for the size gate.</param>
    public static void Render(
        DrawingContext context,
        Rect body,
        double radius,
        double alpha,
        double screenWidth,
        double screenHeight,
        double clock,
        double moving,
        double pressing)
    {
        if (alpha <= 0.004 || body.Width <= 1.0 || body.Height <= 1.0)
        {
            return;
        }

        // A large focused target gets no wash at all - only the line.
        if (!Ps5FocusField.AreaPassApplies(body.Width, body.Height, screenWidth, screenHeight))
        {
            return;
        }

        var sizeScale = Ps5FocusField.AreaOpacityScaleForSize(
            body.Width,
            body.Height,
            screenWidth,
            screenHeight);

        var effective = alpha * sizeScale;
        if (effective <= 0.004)
        {
            return;
        }

        // EnableAreaEdgeFade defaults false in 4.03 and HOME never changes it.
        // The area quad is therefore the target itself, not an inflated bloom.
        var fade = Math.Max(
            ShellFocusPalette.AreaEdgeFadeLength,
            ShellFocusPalette.EdgeFadeMinLength);

        var target = body;

        var bitmap = GetOrBuild(target, body, radius, fade, clock, moving, pressing);
        if (bitmap is null)
        {
            return;
        }

        using (context.PushOpacity(Math.Clamp(effective, 0.0, 1.0)))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
        }
    }

    /// <summary>
    /// Builds the field bitmap, reusing the last one when the shape matches.
    /// Keyed on ratios rather than absolute size because the bitmap is drawn
    /// scaled - two rects with the same proportions share a field.
    /// </summary>
    /// <param name="target">The inflated surface the field is drawn into.</param>
    /// <param name="body">The tile rect itself, which the field is measured against.</param>
    private static WriteableBitmap? GetOrBuild(
        Rect target,
        Rect body,
        double radius,
        double fade,
        double clock,
        double moving,
        double pressing)
    {
        var aspect = target.Width / target.Height;

        // Ratios are taken against the TARGET, because that is the space the
        // field is evaluated in, but the shape is the BODY - so the tile
        // occupies the middle of the surface and the fade rings it.
        var targetMinHalf = Math.Min(target.Width, target.Height) / 2.0;
        var bodyRatio = targetMinHalf > 0.0
            ? Math.Min(body.Width, body.Height) / 2.0 / targetMinHalf
            : 1.0;
        var radiusRatio = targetMinHalf > 0.0 ? radius / targetMinHalf : 0.0;
        var fadeRatio = targetMinHalf > 0.0 ? fade / targetMinHalf : 0.0;
        // Keep the recovered noise/shimmer uniforms on the renderer's native
        // SecPerFrame cadence. The old 30 Hz cache was an implementation
        // shortcut and visibly stepped the otherwise continuous card wash.
        int animationFrame = ShellFocusPalette.AnimationFrame(clock);

        if (_cache is not null
            && Math.Abs(aspect - _cacheAspect) < 0.01
            && Math.Abs(radiusRatio - _cacheRadiusRatio) < 0.01
            && Math.Abs(fadeRatio - _cacheFadeRatio) < 0.01
            && Math.Abs(bodyRatio - _cacheBodyRatio) < 0.01
            && animationFrame == _cacheAnimationFrame
            && Math.Abs(moving - _cacheMoving) < 0.005
            && Math.Abs(pressing - _cachePressing) < 0.005)
        {
            return _cache;
        }

        var built = Build(aspect, bodyRatio, radiusRatio, fadeRatio, clock, moving, pressing);
        if (built is null)
        {
            return null;
        }

        _cache = built;
        _cacheAspect = aspect;
        _cacheRadiusRatio = radiusRatio;
        _cacheFadeRatio = fadeRatio;
        _cacheBodyRatio = bodyRatio;
        _cacheAnimationFrame = animationFrame;
        _cacheMoving = moving;
        _cachePressing = pressing;
        return built;
    }

    private static WriteableBitmap? Build(
        double aspect,
        double bodyRatio,
        double radiusRatio,
        double fadeRatio,
        double clock,
        double moving,
        double pressing)
    {
        int w = Grid;
        int h = Grid;
        if (aspect > 1.0)
        {
            h = Math.Max(8, (int)Math.Round(Grid / aspect));
        }
        else if (aspect > 0.0)
        {
            w = Math.Max(8, (int)Math.Round(Grid * aspect));
        }

        WriteableBitmap bitmap;
        try
        {
            bitmap = new WriteableBitmap(
                new PixelSize(w, h),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }
        catch (Exception)
        {
            // A headless or surfaceless host may refuse to allocate. The wash is
            // decoration; the line pass still carries the highlight.
            return null;
        }

        // Work in the bitmap's own space: half extents of w/2 by h/2, with the
        // radius and fade scaled by the same ratio they had on the real rect.
        double halfW = (w / 2.0) * bodyRatio;
        double halfH = (h / 2.0) * bodyRatio;
        double targetMinHalf = Math.Min(w, h) / 2.0;
        double radius = radiusRatio * targetMinHalf;
        double fade = Math.Max(fadeRatio * targetMinHalf, 0.5);

        // The colour table is sampled by intensity. Without the recovered uv
        // construction the wash takes a single table entry rather than a
        // per-pixel fetch - the midpoint, which is the neutral lavender.
        // Tint per pixel from the intensity, not one flat entry. The shader
        // feeds that scalar directly into the colour-table lookup.

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    var row = (byte*)buffer.Address + (y * buffer.RowBytes);
                    double py = (y + 0.5) - (h / 2.0);

                    for (int x = 0; x < w; x++)
                    {
                        var o = x * 4;
                        row[o + 0] = 0;
                        row[o + 1] = 0;
                        row[o + 2] = 0;
                        row[o + 3] = 0;

                        double px = (x + 0.5) - (w / 2.0);

                        var signedDistance = Ps5FocusField.RoundedBoxDistance(
                            px,
                            py,
                            halfW,
                            halfH,
                            radius);
                        if (signedDistance > fade)
                        {
                            continue;
                        }

                        var coverage = Ps5FocusField.AreaCoverage(
                            px,
                            py,
                            halfW,
                            halfH,
                            radius,
                            fade);

                        double stX = halfW > 0.0 ? px / halfW : 0.0;
                        double stY = halfH > 0.0 ? py / halfH : 0.0;
                        double diagonal = ShellFocusPalette.DiagonalRamp(stX, stY);

                        // AreaFocus's resting source is the diagonal
                        // ShimmerParam interpolation. During travel the shader
                        // blends toward image_focus_noise by
                        // MorphByRatioIntensity; with the stock ratio intensity
                        // this is Moving * .5. Pressing then pulls the result
                        // toward the shader's literal .15.
                        double shimmer = Math.Max(
                            ShellFocusPalette.ShimmerAcross(clock, diagonal),
                            0.0);
                        var (u, v) = ShellFocusPalette.NoiseUv(stX, stY, clock);
                        double noise = Ps5FocusNoiseTexture.Sample(u, v);
                        double morph = Math.Clamp(moving * 0.5, 0.0, 1.0);
                        double intensity = shimmer + ((noise - shimmer) * morph);
                        intensity += Math.Clamp(pressing, 0.0, 1.0)
                            * (ShellFocusPalette.PressingIntensity - intensity);
                        intensity *= coverage;

                        var shaped = Ps5FocusField.ApplyAlphaGamma(
                            intensity,
                            ShellFocusPalette.AreaAlphaGamma);

                        if (shaped < ShellFocusPalette.AreaMinOpacity)
                        {
                            shaped = ShellFocusPalette.AreaMinOpacity;
                        }

                        var tint = ShellFocusPalette.AreaColorFor(intensity);
                        byte a = (byte)Math.Clamp(shaped * 255.0, 0.0, 255.0);

                        // Premultiplied, as the surface format demands.
                        row[o + 0] = (byte)(tint.B * a / 255);
                        row[o + 1] = (byte)(tint.G * a / 255);
                        row[o + 2] = (byte)(tint.R * a / 255);
                        row[o + 3] = a;
                    }
                }
            }
        }

        return bitmap;
    }
}
