// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Textures;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// Non-visual half of <see cref="ShellBackground"/>: resolves which hub
/// prepares decoded, display-sized pixels. Kept free of control state so all
/// of it is unit-testable without a rendering surface.
/// </summary>
public static class ShellBackgroundSource
{
    /// <summary>
    /// can present on a 4K display, so retain the authored source resolution.
    /// Downscaling here created a 1080p intermediate that the fixed design
    /// canvas then enlarged back to 4K.
    /// </summary>
    public const int TargetDecodeWidth = 3840;

    /// <summary>
    /// Loads a BC7 DDS background and returns RGBA8 pixels no wider than
    /// <paramref name="maxWidth"/>, or null when the file is missing, is not a
    /// decodable DDS, or decoding fails for any other reason. Never throws;
    /// the shell backdrop must degrade, not crash.
    /// </summary>
    public static byte[]? TryLoadRgba(string? path, int maxWidth, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var image = DdsImage.LoadFile(path);
            if (!image.IsSupported)
            {
                return null;
            }

            var rgba = image.Decode();
            return DownscaleRgba(rgba, image.Width, image.Height, maxWidth, out width, out height);
        }
        catch (Exception)
        {
            // Anything a hostile or truncated file can provoke (bad header,
            // short pixel data, IO errors) ends in the gradient fallback.
            return null;
        }
    }

    /// <summary>
    /// Box-filters RGBA8 pixels down by the largest integer factor that keeps
    /// the result at least <paramref name="maxWidth"/> wide. Images already at
    /// or below the limit are returned unchanged.
    /// </summary>
    /// <param name="rgba">width*height*4 bytes of RGBA8.</param>
    /// <param name="width">Source width in pixels.</param>
    /// <param name="height">Source height in pixels.</param>
    /// <param name="maxWidth">Widest acceptable result.</param>
    /// <param name="scaledWidth">Width of the returned image.</param>
    /// <param name="scaledHeight">Height of the returned image.</param>
    public static byte[] DownscaleRgba(
        byte[] rgba, int width, int height, int maxWidth, out int scaledWidth, out int scaledHeight)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || maxWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                width <= 0 ? nameof(width) : height <= 0 ? nameof(height) : nameof(maxWidth));
        }

        if (rgba.Length < (long)width * height * 4)
        {
            throw new ArgumentException(
                $"Expected {(long)width * height * 4} RGBA bytes, got {rgba.Length}.", nameof(rgba));
        }

        var factor = 1;
        while (width / (factor + 1) >= maxWidth)
        {
            factor++;
        }

        if (factor == 1)
        {
            scaledWidth = width;
            scaledHeight = height;
            return rgba;
        }

        scaledWidth = width / factor;
        scaledHeight = Math.Max(1, height / factor);
        var result = new byte[scaledWidth * scaledHeight * 4];
        var samples = factor * factor;

        for (var y = 0; y < scaledHeight; y++)
        {
            for (var x = 0; x < scaledWidth; x++)
            {
                var r = 0;
                var g = 0;
                var b = 0;
                var a = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = ((y * factor + dy) * width + x * factor) * 4;
                    for (var dx = 0; dx < factor; dx++)
                    {
                        var src = row + dx * 4;
                        r += rgba[src];
                        g += rgba[src + 1];
                        b += rgba[src + 2];
                        a += rgba[src + 3];
                    }
                }

                var dst = (y * scaledWidth + x) * 4;
                result[dst] = (byte)(r / samples);
                result[dst + 1] = (byte)(g / samples);
                result[dst + 2] = (byte)(b / samples);
                result[dst + 3] = (byte)(a / samples);
            }
        }

        return result;
    }
}
