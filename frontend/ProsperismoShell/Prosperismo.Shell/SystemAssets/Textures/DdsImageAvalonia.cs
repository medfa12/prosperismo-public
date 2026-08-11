// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Prosperismo.GUI.SystemAssets.Textures;

/// <summary>
/// Bridges decoded RGBA8 pixel data into an Avalonia <see cref="Bitmap"/> so
/// the library UI can bind a BC7 hub background directly. Kept in its own file
/// so the pure decoder stays free of Avalonia dependencies.
/// </summary>
public static class DdsImageAvalonia
{
    /// <summary>
    /// Wraps row-major RGBA8 pixels in a <see cref="WriteableBitmap"/>. The
    /// caller owns the returned bitmap and should dispose it.
    /// </summary>
    /// <param name="rgba">width*height*4 bytes of RGBA8 (R,G,B,A order).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    public static WriteableBitmap CreateBitmap(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
        }

        long expected = (long)width * height * 4;
        if (rgba.Length < expected)
        {
            throw new ArgumentException($"Expected {expected} RGBA bytes, got {rgba.Length}.", nameof(rgba));
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);

        using (var frame = bitmap.Lock())
        {
            int rowBytes = width * 4;
            IntPtr dst = frame.Address;
            for (int y = 0; y < height; y++)
            {
                var row = rgba.Slice(y * rowBytes, rowBytes);
                Marshal.Copy(row.ToArray(), 0, dst + y * frame.RowBytes, rowBytes);
            }
        }

        return bitmap;
    }

    /// <summary>Decodes a BC7 DDS image and returns it as an Avalonia bitmap.</summary>
    public static WriteableBitmap ToBitmap(this DdsImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = image.Decode();
        return CreateBitmap(rgba, image.Width, image.Height);
    }
}
