// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The shape maths behind the PS5 focus highlight.
/// </summary>
/// <remarks>
/// RECOVERED, not designed. The shell's focus passes are two shaders embedded in
/// libScePsm.sprx - AreaFocus at file offset 0x004F5AE0 (5,472 bytes) and
/// LineFocus at 0x004F7040 (5,840 bytes), with the combined FocusUI3 at
/// 0x004F37D0. They are NOT in the shell eboot and NOT in the managed
/// Sce.PlayStation.PUI assembly, which is why an earlier search of the eboot's
/// 160 embedded shaders came up empty; they were found by scanning for the
/// uniform names (ColorTable, IsLine, StrokePosition, NoiseScale, Shimmer)
/// rather than for shader names.
///
/// the Prospero SDK 10.00 host tools). What the area shader does, read off the
/// instruction stream at 0x0134-0x01b0:
///
///     v5 = halfExtent.x - radius        (s34 - s36)
///     p  = interpolated local position  (attr2.xy)
///     q  = abs(p) - halfExtent + radius
///     sd = length(max(q, 0)) - radius
///
/// i.e. the standard rounded-box signed distance field. The corner term is what
/// a stroked rounded rectangle cannot reproduce: the falloff is a true distance,
/// so it stays uniform around the corners instead of bunching.
///
/// The shaping that follows it, also from the instruction census: a smoothstep
/// (the -2t+3 polynomial appears twice as v_madak_f32 with #0x40400000), a
/// gamma applied as exp2(log2(x) * n) - the pow pairs, of which the alpha gamma
/// is one - and a single sin/cos pair alongside 1/(2*pi) = 0x3e22f983 for the
/// shimmer's angular term. Two image_sample instructions: one single-component
/// fetch (the noise field) and one dmask:0x7 RGB fetch (the seven-entry colour
/// table).
///
/// TRACED SINCE: the noise uv is (p / noiseScale + noiseOffset) * 0.5 + 0.5; the
/// colour table is indexed by the computed intensity itself, so the seven
/// entries are a ramp the alpha walks along rather than a positional gradient;
/// and the shimmer is a rotating diagonal sweep normalised by |cos| + |sin| so
/// its band width stays constant at every angle. See docs/ps5-focus-highlight.md.
///
/// The four constants previously left out as an unidentified group - 2.35,
/// 0.06480824, 0.6398802, 0.01050037 - turned out to be a COLOUR SPACE
/// CONVERSION, not a noise hash: decode with gamma 2.35, a 3x3 matrix whose rows
/// each sum to 1, scale by 0.025, re-encode with gamma 1/2.2, all branch-gated on
/// the display mode. Guessing a noise function out of them would have produced
/// something arbitrary, which is the case for reading rather than approximating.
///
/// The bound noise texture has since been recovered as
/// <c>image_focus_noise</c>, a 64x64 indexed PNG in
/// <c>Sce.PlayStation.PUI_UI3.rco</c>. <see cref="Ps5FocusNoiseTexture"/> reads
/// and samples that user-owned resource in place.
/// </remarks>
internal static class Ps5FocusField
{
    /// <summary>
    /// Applies the focus shader's display-output conversion for the desktop SDR
    /// target: gamma-decode, the recovered wide-gamut matrix, paper-white
    /// normalization, then gamma-encode.
    /// </summary>
    public static (double R, double G, double B) ConvertFocusOutput(
        double red,
        double green,
        double blue)
    {
        red = Math.Pow(Math.Clamp(red, 0.0, 1.0), 2.35);
        green = Math.Pow(Math.Clamp(green, 0.0, 1.0), 2.35);
        blue = Math.Pow(Math.Clamp(blue, 0.0, 1.0), 2.35);

        // The instruction stream emits G, R, B rows in that order. Reordered
        // here to the RGB tuple consumed by the bitmap writers.
        double outRed = (0.6398802 * red) + (0.3273893 * green) + (0.03271094 * blue);
        double outGreen = (0.06480824 * red) + (0.9353735 * green) - (0.0001436224 * blue);
        double outBlue = (0.01050037 * red) + (0.07885341 * green) + (0.9105756 * blue);

        const double paperWhiteScale = 0.025;
        const double encodeGamma = 1.0 / 2.2;
        return (
            Math.Pow(Math.Max(0.0, outRed * paperWhiteScale), encodeGamma),
            Math.Pow(Math.Max(0.0, outGreen * paperWhiteScale), encodeGamma),
            Math.Pow(Math.Max(0.0, outBlue * paperWhiteScale), encodeGamma));
    }

    /// <summary>
    /// Signed distance from a point to a rounded rectangle centred on the
    /// origin. Negative inside, zero on the edge, positive outside.
    /// </summary>
    /// <param name="px">Point x, relative to the rect centre.</param>
    /// <param name="py">Point y, relative to the rect centre.</param>
    /// <param name="halfWidth">Half the rect width.</param>
    /// <param name="halfHeight">Half the rect height.</param>
    /// <param name="radius">
    /// Corner radius. The shell never hard-codes this - it is inherited from the
    /// focused widget (FocusRenderWidget.TargetBorderRadius), which is why the
    /// highlight matches whatever it is sitting on.
    /// </param>
    public static double RoundedBoxDistance(
        double px,
        double py,
        double halfWidth,
        double halfHeight,
        double radius)
    {
        // Clamp the radius to what the rect can actually hold, otherwise the
        // corner arcs overlap and the field folds back on itself.
        var r = Math.Max(0.0, Math.Min(radius, Math.Min(halfWidth, halfHeight)));

        var qx = Math.Abs(px) - halfWidth + r;
        var qy = Math.Abs(py) - halfHeight + r;

        var outsideX = Math.Max(qx, 0.0);
        var outsideY = Math.Max(qy, 0.0);
        var outside = Math.Sqrt((outsideX * outsideX) + (outsideY * outsideY));

        // Inside the rect both q components are negative and the length term is
        // zero, so the interior distance comes from the larger (least negative)
        // axis. This is the branch that keeps the field correct in the middle.
        var inside = Math.Min(Math.Max(qx, qy), 0.0);

        return outside + inside - r;
    }

    /// <summary>
    /// The smoothstep the shader applies to the distance field, as the
    /// -2t+3 polynomial that appears in the instruction stream.
    /// </summary>
    public static double SmoothStep(double edge0, double edge1, double x)
    {
        if (edge1 <= edge0)
        {
            return x < edge0 ? 0.0 : 1.0;
        }

        var t = (x - edge0) / (edge1 - edge0);
        t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);
        return t * t * (3.0 - (2.0 * t));
    }

    /// <summary>
    /// Area coverage for a point, before colour, noise and shimmer are applied.
    /// </summary>
    /// <remarks>
    /// The edge fade length is <see cref="Controls.ShellFocusPalette.AreaEdgeFadeLength"/>
    /// (5) and the shell clamps it to at least
    /// <see cref="Controls.ShellFocusPalette.EdgeFadeMinLength"/> (10) when the fade
    /// is enabled - see FocusRenderWidget, which also inflates the area rect by
    /// the fade length on every side and forces its border radius to zero in
    /// that case.
    /// </remarks>
    public static double AreaCoverage(
        double px,
        double py,
        double halfWidth,
        double halfHeight,
        double radius,
        double edgeFadeLength)
    {
        var sd = RoundedBoxDistance(px, py, halfWidth, halfHeight, radius);
        var fade = Math.Max(edgeFadeLength, 0.0001);

        // The shader saturates sd/range before smoothstep, so coverage is one
        // throughout the interior and falls outside the rounded boundary. When
        // EnableAreaEdgeFade is true FocusRenderWidget inflates the quad to give
        // that falloff room. Stock 4.03 leaves it false, so HOME clips this same
        // field to the card bounds and the area reads as an interior shimmer.
        var t = Math.Clamp(sd / fade, 0.0, 1.0);
        return 1.0 - (t * t * (3.0 - (2.0 * t)));
    }

    /// <summary>
    /// Applies the recovered alpha gamma. The shell stores
    /// AreaAlphaGamma = 0.8 and LineAlphaGamma = 1.0 but passes the RECIPROCAL
    /// to the shader (1/0.8 = 1.25), so the exponent here is 1/gamma.
    /// </summary>
    public static double ApplyAlphaGamma(double alpha, double gamma)
    {
        if (alpha <= 0.0)
        {
            return 0.0;
        }

        if (gamma <= 0.0 || Math.Abs(gamma - 1.0) < 1e-6)
        {
            return alpha > 1.0 ? 1.0 : alpha;
        }

        var shaped = Math.Pow(alpha > 1.0 ? 1.0 : alpha, 1.0 / gamma);
        return shaped > 1.0 ? 1.0 : shaped;
    }

    /// <summary>
    /// The size gate. The area pass is skipped entirely when the focused target
    /// covers 40% or more of the screen - AreaRenderingThrethold in
    /// FocusRenderWidget, spelling and all. A large focused item therefore gets
    /// the line pass only, with no glow behind it.
    /// </summary>
    public static bool AreaPassApplies(
        double targetWidth,
        double targetHeight,
        double screenWidth,
        double screenHeight)
    {
        if (screenWidth <= 0.0 || screenHeight <= 0.0)
        {
            return false;
        }

        var coverage = (targetWidth / screenWidth) * (targetHeight / screenHeight);
        return coverage < Controls.ShellFocusPalette.AreaRenderingThreshold;
    }

    /// <summary>
    /// Area opacity falls off as the focused target grows:
    /// AreaOpacityDecreaseRateBySize = 30 with a floor of
    /// AreaOpacityMinimumDecreaseValueBySize = 0.5.
    /// </summary>
    public static double AreaOpacityScaleForSize(
        double targetWidth,
        double targetHeight,
        double screenWidth,
        double screenHeight)
    {
        if (screenWidth <= 0.0 || screenHeight <= 0.0)
        {
            return 1.0;
        }

        var coverage = (targetWidth / screenWidth) * (targetHeight / screenHeight);
        var scale = 1.0 - (coverage * Controls.ShellFocusPalette.AreaOpacityDecreaseRateBySize);
        var floor = Controls.ShellFocusPalette.AreaOpacityMinimumDecreaseValueBySize;
        return scale < floor ? floor : (scale > 1.0 ? 1.0 : scale);
    }
}
