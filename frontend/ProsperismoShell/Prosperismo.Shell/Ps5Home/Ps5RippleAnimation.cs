// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// Loads the console's own ripple animations and presents them as playable
/// frame sequences.
///
/// <para>These are recovered assets rather than a reimplementation: the shell's
/// ripple is shipped as an animated PNG authored at 60 Hz, and the frames here
/// are the console's, decoded by <see cref="Ps5ApngDecoder"/>. Playback is
/// therefore driven by the file's own per-frame delays instead of an assumed
/// frame rate - the assets are not uniformly timed and resampling them to a
/// fixed step visibly changes the motion.</para>
///
/// the normal case and is reported rather than thrown - callers fall back to
/// </summary>
internal sealed class Ps5RippleAnimation : IDisposable
{
    private readonly Bitmap[] _frames;
    private readonly TimeSpan[] _cumulative;

    private Ps5RippleAnimation(
        string name, int width, int height, Bitmap[] frames, TimeSpan[] cumulative)
    {
        Name = name;
        PixelWidth = width;
        PixelHeight = height;
        _frames = frames;
        _cumulative = cumulative;
    }

    internal string Name { get; }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal int FrameCount => _frames.Length;

    internal TimeSpan Duration =>
        _cumulative.Length == 0 ? TimeSpan.Zero : _cumulative[^1];

    /// <summary>
    /// The directory holding recovered ripple assets, relative to the oracle
    /// root. Kept as one constant because the resolver and the documentation
    /// both refer to it.
    /// </summary>
    internal const string RelativeAssetDirectory = "shell_ui/ripple_apng";

    /// <summary>
    /// Loads every ripple animation found under <paramref name="directory"/>,
    /// longest first. Returns an empty list when the directory is absent.
    /// </summary>
    internal static IReadOnlyList<Ps5RippleAnimation> LoadAll(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return Array.Empty<Ps5RippleAnimation>();
        }

        var loaded = new List<Ps5RippleAnimation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.png"))
        {
            var animation = TryLoad(path);

            // Stills decode to a single frame; they are icons that happen to
            // share the directory, not ripples.
            if (animation is { FrameCount: > 1 })
            {
                loaded.Add(animation);
            }
            else
            {
                animation?.Dispose();
            }
        }

        return loaded.OrderByDescending(a => a.FrameCount).ToList();
    }

    /// <summary>Loads one animation, or null when it cannot be decoded.</summary>
    internal static Ps5RippleAnimation? TryLoad(string path)
    {
        var decoded = Ps5ApngDecoder.TryDecode(path);
        if (decoded is not { } animation || animation.Frames.Count == 0)
        {
            return null;
        }

        var frames = new Bitmap[animation.Frames.Count];
        var cumulative = new TimeSpan[animation.Frames.Count];
        var elapsed = TimeSpan.Zero;

        try
        {
            for (var i = 0; i < animation.Frames.Count; i++)
            {
                var frame = animation.Frames[i];
                frames[i] = ToBitmap(frame.Bgra, animation.Width, animation.Height);
                elapsed += frame.Delay;
                cumulative[i] = elapsed;
            }
        }
        catch (Exception)
        {
            foreach (var bitmap in frames)
            {
                bitmap?.Dispose();
            }

            return null;
        }

        return new Ps5RippleAnimation(
            Path.GetFileNameWithoutExtension(path),
            animation.Width,
            animation.Height,
            frames,
            cumulative);
    }

    /// <summary>
    /// The frame showing at <paramref name="elapsed"/>, looping. Selection walks
    /// the animation's own cumulative delays rather than dividing by a nominal
    /// frame rate, so unevenly timed frames land where the author put them.
    /// </summary>
    internal Bitmap FrameAt(TimeSpan elapsed)
    {
        if (_frames.Length == 1 || Duration <= TimeSpan.Zero)
        {
            return _frames[0];
        }

        var ticks = elapsed.Ticks % Duration.Ticks;
        if (ticks < 0)
        {
            ticks += Duration.Ticks;
        }

        var position = TimeSpan.FromTicks(ticks);

        // Binary search: these run to 185 frames and are sampled every vsync.
        var lo = 0;
        var hi = _cumulative.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_cumulative[mid] <= position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return _frames[lo];
    }

    /// <summary>The frame at a normalised 0..1 position through the animation.</summary>
    internal Bitmap FrameAtProgress(double progress)
    {
        if (Duration <= TimeSpan.Zero)
        {
            return _frames[0];
        }

        var clamped = Math.Clamp(progress, 0.0, 1.0);
        return FrameAt(TimeSpan.FromTicks((long)(Duration.Ticks * clamped)));
    }

    private static Bitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (var buffer = bitmap.Lock())
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    bgra,
                    y * stride,
                    buffer.Address + (y * buffer.RowBytes),
                    stride);
            }
        }

        return bitmap;
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }
    }
}
