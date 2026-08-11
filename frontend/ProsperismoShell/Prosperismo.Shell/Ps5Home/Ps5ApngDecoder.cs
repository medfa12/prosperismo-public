// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// Minimal APNG reader, producing fully composited RGBA frames.
///
/// <para>The shell's ripple animations are shipped by the console as animated
/// PNGs, and neither Avalonia nor <c>System.Drawing</c> reads that extension -
/// both decode the default image and silently discard every subsequent frame,
/// which looks like a still rather than an error. Decoding here keeps the
/// animation intact and avoids taking an image-codec dependency for one
/// format.</para>
///
/// <para>Frames are returned already composited against the canvas. An APNG
/// frame is often a sub-rectangle that must be blended onto the disposed
/// result of its predecessor, so handing callers the raw sub-images would push
/// the <c>dispose_op</c>/<c>blend_op</c> state machine onto every consumer.</para>
///
/// <para>Only the subset the console's own assets use is implemented: 8-bit
/// RGBA, non-interlaced. Anything else throws rather than rendering something
/// subtly wrong.</para>
/// </summary>
internal static class Ps5ApngDecoder
{
    private static ReadOnlySpan<byte> Signature =>
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    private const byte DisposeNone = 0;
    private const byte DisposeBackground = 1;
    private const byte DisposePrevious = 2;
    private const byte BlendSource = 0;
    private const byte BlendOver = 1;

    /// <summary>One decoded frame: full-canvas BGRA premultiplied-free pixels.</summary>
    internal readonly record struct Frame(byte[] Bgra, TimeSpan Delay);

    /// <summary>The decoded animation.</summary>
    internal readonly record struct Animation(int Width, int Height, IReadOnlyList<Frame> Frames)
    {
        internal TimeSpan Duration
        {
            get
            {
                var total = TimeSpan.Zero;
                foreach (var f in Frames)
                {
                    total += f.Delay;
                }

                return total;
            }
        }
    }

    private readonly record struct Chunk(string Type, byte[] Data);

    private sealed class FrameControl
    {
        internal int Width;
        internal int Height;
        internal int X;
        internal int Y;
        internal ushort DelayNum;
        internal ushort DelayDen;
        internal byte Dispose;
        internal byte Blend;
    }

    /// <summary>
    /// Decodes <paramref name="path"/>, or returns null when the file is not an
    /// APNG this reader supports. A still PNG yields a single frame.
    /// </summary>
    internal static Animation? TryDecode(string path)
    {
        try
        {
            return Decode(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static Animation Decode(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }

        var chunks = ReadChunks(data);

        var ihdr = FindFirst(chunks, "IHDR")
            ?? throw new InvalidDataException("missing IHDR");
        var canvasWidth = ReadInt32(ihdr, 0);
        var canvasHeight = ReadInt32(ihdr, 4);
        var bitDepth = ihdr[8];
        var colourType = ihdr[9];
        var interlace = ihdr[12];

        if (bitDepth != 8 || colourType != 6 || interlace != 0)
        {
            throw new NotSupportedException(
                $"only 8-bit RGBA non-interlaced APNG is supported "
                + $"(depth={bitDepth} colour={colourType} interlace={interlace})");
        }

        // Walk chunks in order, grouping the data that belongs to each frame.
        // The default image participates in the animation only when an fcTL
        // precedes IDAT; otherwise it is a poster frame and is skipped.
        var frames = new List<Frame>();
        var canvas = new byte[canvasWidth * canvasHeight * 4];
        byte[]? previousCanvas = null;

        FrameControl? pending = null;
        var payload = new MemoryStream();
        var defaultImageIsFrame = false;
        var sawIdat = false;

        foreach (var chunk in chunks)
        {
            switch (chunk.Type)
            {
                case "fcTL":
                {
                    if (pending is not null && payload.Length > 0)
                    {
                        EmitFrame(frames, canvas, ref previousCanvas,
                            canvasWidth, canvasHeight, pending, payload.ToArray());
                        payload.SetLength(0);
                    }

                    pending = new FrameControl
                    {
                        Width = ReadInt32(chunk.Data, 4),
                        Height = ReadInt32(chunk.Data, 8),
                        X = ReadInt32(chunk.Data, 12),
                        Y = ReadInt32(chunk.Data, 16),
                        DelayNum = BinaryPrimitives.ReadUInt16BigEndian(chunk.Data.AsSpan(20)),
                        DelayDen = BinaryPrimitives.ReadUInt16BigEndian(chunk.Data.AsSpan(22)),
                        Dispose = chunk.Data[24],
                        Blend = chunk.Data[25],
                    };
                    if (!sawIdat)
                    {
                        defaultImageIsFrame = true;
                    }

                    break;
                }

                case "IDAT":
                    sawIdat = true;
                    if (defaultImageIsFrame)
                    {
                        payload.Write(chunk.Data, 0, chunk.Data.Length);
                    }

                    break;

                case "fdAT":
                    // fdAT carries a 4-byte sequence number ahead of the data.
                    if (chunk.Data.Length > 4)
                    {
                        payload.Write(chunk.Data, 4, chunk.Data.Length - 4);
                    }

                    break;
            }
        }

        if (pending is not null && payload.Length > 0)
        {
            EmitFrame(frames, canvas, ref previousCanvas,
                canvasWidth, canvasHeight, pending, payload.ToArray());
        }

        if (frames.Count == 0)
        {
            // A still PNG: decode the default image as the only frame.
            var idat = Concat(chunks, "IDAT");
            var pixels = Inflate(idat, canvasWidth, canvasHeight);
            RgbaToBgraInPlace(pixels);
            frames.Add(new Frame(pixels, TimeSpan.Zero));
        }

        return new Animation(canvasWidth, canvasHeight, frames);
    }

    private static void EmitFrame(
        List<Frame> frames,
        byte[] canvas,
        ref byte[]? previousCanvas,
        int canvasWidth,
        int canvasHeight,
        FrameControl fc,
        byte[] compressed)
    {
        if (fc.Dispose == DisposePrevious)
        {
            previousCanvas = (byte[])canvas.Clone();
        }

        var sub = Inflate(compressed, fc.Width, fc.Height);
        RgbaToBgraInPlace(sub);
        Composite(canvas, canvasWidth, canvasHeight, sub, fc);

        frames.Add(new Frame((byte[])canvas.Clone(), FrameDelay(fc)));

        switch (fc.Dispose)
        {
            case DisposeBackground:
                ClearRegion(canvas, canvasWidth, fc);
                break;
            case DisposePrevious when previousCanvas is not null:
                Array.Copy(previousCanvas, canvas, canvas.Length);
                break;
            case DisposeNone:
            default:
                break;
        }
    }

    private static TimeSpan FrameDelay(FrameControl fc)
    {
        // Per spec a zero denominator means 100, and zero numerator means
        // "as fast as possible" - clamped so a consumer's timer cannot spin.
        var den = fc.DelayDen == 0 ? 100 : fc.DelayDen;
        var seconds = fc.DelayNum / (double)den;
        return seconds <= 0 ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromSeconds(seconds);
    }

    private static void Composite(
        byte[] canvas, int canvasWidth, int canvasHeight, byte[] sub, FrameControl fc)
    {
        for (var row = 0; row < fc.Height; row++)
        {
            var dstY = fc.Y + row;
            if (dstY < 0 || dstY >= canvasHeight)
            {
                continue;
            }

            for (var col = 0; col < fc.Width; col++)
            {
                var dstX = fc.X + col;
                if (dstX < 0 || dstX >= canvasWidth)
                {
                    continue;
                }

                var s = ((row * fc.Width) + col) * 4;
                var d = ((dstY * canvasWidth) + dstX) * 4;

                if (fc.Blend == BlendSource)
                {
                    canvas[d] = sub[s];
                    canvas[d + 1] = sub[s + 1];
                    canvas[d + 2] = sub[s + 2];
                    canvas[d + 3] = sub[s + 3];
                    continue;
                }

                // BlendOver: standard source-over in straight alpha.
                var sa = sub[s + 3];
                if (sa == 255)
                {
                    canvas[d] = sub[s];
                    canvas[d + 1] = sub[s + 1];
                    canvas[d + 2] = sub[s + 2];
                    canvas[d + 3] = 255;
                    continue;
                }

                if (sa == 0)
                {
                    continue;
                }

                var da = canvas[d + 3];
                var outA = sa + (da * (255 - sa) / 255);
                if (outA == 0)
                {
                    canvas[d] = canvas[d + 1] = canvas[d + 2] = canvas[d + 3] = 0;
                    continue;
                }

                for (var c = 0; c < 3; c++)
                {
                    var sc = sub[s + c] * sa;
                    var dc = canvas[d + c] * da * (255 - sa) / 255;
                    canvas[d + c] = (byte)((sc + dc) / outA);
                }

                canvas[d + 3] = (byte)outA;
            }
        }
    }

    private static void ClearRegion(byte[] canvas, int canvasWidth, FrameControl fc)
    {
        for (var row = 0; row < fc.Height; row++)
        {
            var start = (((fc.Y + row) * canvasWidth) + fc.X) * 4;
            if (start < 0 || start + (fc.Width * 4) > canvas.Length)
            {
                continue;
            }

            Array.Clear(canvas, start, fc.Width * 4);
        }
    }

    /// <summary>Inflates zlib-compressed scanlines and reverses PNG filtering.</summary>
    private static byte[] Inflate(byte[] compressed, int width, int height)
    {
        const int bpp = 4;
        var stride = width * bpp;
        var raw = new byte[(stride + 1) * height];

        using (var input = new MemoryStream(compressed))
        using (var zlib = new ZLibStream(input, CompressionMode.Decompress))
        {
            var read = 0;
            while (read < raw.Length)
            {
                var n = zlib.Read(raw, read, raw.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }
        }

        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = (y * (stride + 1)) + 1;
            var dst = y * stride;
            var prev = dst - stride;

            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? pixels[dst + x - bpp] : 0;
                int b = y > 0 ? pixels[prev + x] : 0;
                int c = (x >= bpp && y > 0) ? pixels[prev + x - bpp] : 0;
                int cur = raw[src + x];

                pixels[dst + x] = filter switch
                {
                    0 => (byte)cur,
                    1 => (byte)(cur + a),
                    2 => (byte)(cur + b),
                    3 => (byte)(cur + ((a + b) >> 1)),
                    4 => (byte)(cur + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"bad PNG filter {filter}"),
                };
            }
        }

        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
    }

    /// <summary>PNG stores RGBA; Avalonia's Bgra8888 wants the bytes swapped.</summary>
    private static void RgbaToBgraInPlace(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
    }

    private static List<Chunk> ReadChunks(byte[] data)
    {
        var chunks = new List<Chunk>();
        var p = 8;
        while (p + 8 <= data.Length)
        {
            var length = ReadInt32(data, p);
            if (length < 0 || p + 12 + length > data.Length)
            {
                break;
            }

            var type = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
            var body = new byte[length];
            Array.Copy(data, p + 8, body, 0, length);
            chunks.Add(new Chunk(type, body));
            p += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    private static byte[]? FindFirst(List<Chunk> chunks, string type)
    {
        foreach (var c in chunks)
        {
            if (c.Type == type)
            {
                return c.Data;
            }
        }

        return null;
    }

    private static byte[] Concat(List<Chunk> chunks, string type)
    {
        using var ms = new MemoryStream();
        foreach (var c in chunks)
        {
            if (c.Type == type)
            {
                ms.Write(c.Data, 0, c.Data.Length);
            }
        }

        return ms.ToArray();
    }

    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
}
