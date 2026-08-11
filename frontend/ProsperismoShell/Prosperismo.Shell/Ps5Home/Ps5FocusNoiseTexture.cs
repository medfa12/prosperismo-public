// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prosperismo.GUI.SystemAssets;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// <c>Sce.PlayStation.PUI_UI3.rco</c>. The authoritative 12.40 asset is a
/// 64x64 indexed PNG. Nothing is extracted or persisted by this loader.
/// </summary>
internal static class Ps5FocusNoiseTexture
{
    public const string ResourceId = "image_focus_noise";

    private const int ExpectedWidth = 64;
    private const int ExpectedHeight = 64;
    private static readonly object Gate = new();
    private static byte[]? _payload;
    private static byte[]? _rgba;
    private static double[]? _samples;
    private static int _width;
    private static int _height;
    private static bool _probed;

    public static byte[]? TryGetPayload()
    {
        EnsureLoaded();
        return _payload;
    }

    /// <summary>
    /// Resolves the texture before a focus transition starts. This keeps both
    /// the native and CPU focus paths on the RCO field from their first frame
    /// when the authoritative shell asset is present; it remains a no-op when
    /// the user has not supplied that asset.
    /// </summary>
    internal static void Preload() => EnsureLoaded();

    internal static bool TryGetRgba(out ReadOnlyMemory<byte> rgba, out int width, out int height)
    {
        EnsureLoaded();
        lock (Gate)
        {
            rgba = _rgba;
            width = _width;
            height = _height;
            return _rgba is { Length: > 0 } && width > 0 && height > 0;
        }
    }

    /// <summary>
    /// Drops both a successful decode and a failed path probe. Call this when
    /// does not remain on the constant fallback for its whole lifetime.
    /// </summary>
    internal static void Invalidate()
    {
        lock (Gate)
        {
            _payload = null;
            _rgba = null;
            _samples = null;
            _width = 0;
            _height = 0;
            _probed = false;
        }
    }

    /// <summary>
    /// Linearly samples the single-channel field with PSM's texture state.
    /// FocusRenderManager changes the filter to Linear but never changes the
    /// default ClampToEdge wrap mode.
    /// </summary>
    public static double Sample(double u, double v)
    {
        EnsureLoaded();
        if (_samples is not { Length: > 0 } samples || _width <= 0 || _height <= 0)
        {
            return 0.5;
        }

        return SampleClampLinear(samples, _width, _height, u, v);
    }

    /// <summary>
    /// PSM's normalized Linear + ClampToEdge sample. Normalized texel centres
    /// are at <c>(n + 0.5) / size</c>, hence the half-texel shift before the
    /// bilinear footprint is clamped to the edge.
    /// </summary>
    internal static double SampleClampLinear(
        IReadOnlyList<double> samples,
        int width,
        int height,
        double u,
        double v)
    {
        if (width <= 0 || height <= 0 || samples.Count < width * height)
        {
            return 0.5;
        }

        u = Math.Clamp(u, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        double fx = (u * width) - 0.5;
        double fy = (v * height) - 0.5;
        int floorX = (int)Math.Floor(fx);
        int floorY = (int)Math.Floor(fy);
        int x0 = Math.Clamp(floorX, 0, width - 1);
        int y0 = Math.Clamp(floorY, 0, height - 1);
        int x1 = Math.Clamp(floorX + 1, 0, width - 1);
        int y1 = Math.Clamp(floorY + 1, 0, height - 1);
        double tx = fx - Math.Floor(fx);
        double ty = fy - Math.Floor(fy);

        double a = Lerp(samples[(y0 * width) + x0], samples[(y0 * width) + x1], tx);
        double b = Lerp(samples[(y1 * width) + x0], samples[(y1 * width) + x1], tx);
        return Lerp(a, b, ty);
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_probed)
            {
                return;
            }

            _probed = true;
            _ = TryLoadPackagedFallback();
        }
    }

    private static bool TryLoadPackagedFallback()
    {
        var payload = Ps5Ui3PackagedTextures.TryGetBytes("focus-noise.png");
        if (payload is null)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var source = new Bitmap(stream);
            var size = source.PixelSize;
            if (size.Width != ExpectedWidth || size.Height != ExpectedHeight)
            {
                return false;
            }

            using var copy = new WriteableBitmap(
                size,
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            var field = new double[size.Width * size.Height];
            var rgba = new byte[size.Width * size.Height * 4];
            using (var framebuffer = copy.Lock())
            {
                source.CopyPixels(framebuffer, AlphaFormat.Unpremul);
                unsafe
                {
                    for (int y = 0; y < size.Height; y++)
                    {
                        byte* row = (byte*)framebuffer.Address + (y * framebuffer.RowBytes);
                        for (int x = 0; x < size.Width; x++)
                        {
                            byte* pixel = row + (x * 4);
                            var index = (y * size.Width) + x;
                            field[index] = pixel[2] / 255.0;
                            var offset = index * 4;
                            rgba[offset] = pixel[2];
                            rgba[offset + 1] = pixel[1];
                            rgba[offset + 2] = pixel[0];
                            rgba[offset + 3] = pixel[3];
                        }
                    }
                }
            }

            _payload = payload;
            _rgba = rgba;
            _samples = field;
            _width = size.Width;
            _height = size.Height;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
