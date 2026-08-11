// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// The boot sequence's frames, rendered live.
///
/// This used to decode a movie. It does not any more, and that is the point: the
/// console's cold boot is not a video either. <c>BGLayerNative</c> runs a particle
/// system on a compute shader for exactly <c>ColdBootDurationTick</c>. This
/// visualizer preserves that duration and named-pattern sequence, but its field
/// dynamics are still approximate: native routing proves the formerly cited
/// 30.0 value belongs to a large-particle draw block, not to simulation
/// acceleration. So the emulator ships no boot video, but this class must not be
///
/// The renderer is in two tiers, because one tier cannot be both cheap and sharp:
/// this class owns the diffuse one - a decaying accumulation buffer the motes
/// splat into, with the plate, the haze and the shaft over it - and hands the
/// overlay the mote positions so it can draw the resolved heads at full control
/// resolution on top. See <see cref="BootIntroCompositor"/> for why.
///
/// Everything here runs on the caller's thread inside one animation frame. There
/// is nothing to open, so nothing can fail slowly: <see cref="IsOpen"/> is true
/// from construction and the overlay can cover the shell on its first tick.
/// </summary>
internal sealed class BootIntroFrameSource : IDisposable
{
    private readonly BootIntroField _field;
    private readonly BootIntroCompositor _compositor = new();
    private readonly Stopwatch _profiler = new();

    private BootIntroFrame _frame;
    private double _frameMilliseconds;
    private long _frameCount;
    private bool _disposed;

    /// <summary>Builds the renderer. Cheap: a few arrays, no I/O and no threads.</summary>
    /// <param name="moteCount">Mote population; defaults to the field's own.</param>
    /// <param name="seed">Random seed; the same seed always yields the same sequence.</param>
    internal BootIntroFrameSource(int moteCount = BootIntroField.DefaultCount, int seed = 0x8007)
    {
        _field = new BootIntroField(moteCount, seed);
        _frame = BootIntroTimeline.SampleAt(0.0);
    }

    /// <summary>Diffuse buffer width in texels; zero until <see cref="Resize"/> succeeds.</summary>
    internal int Width => _compositor.Width;

    /// <summary>Diffuse buffer height in texels.</summary>
    internal int Height => _compositor.Height;

    /// <summary>The diffuse buffer's BGRA bytes, row major.</summary>
    internal byte[] PixelBuffer => _compositor.PixelBuffer;

    /// <summary>The live motes, for the resolved tier. Valid until the next <see cref="Advance"/>.</summary>
    internal ReadOnlySpan<BootIntroMote> Motes => _field.Motes;

    /// <summary>The timeline's gains for the frame just composed.</summary>
    internal BootIntroFrame Frame => _frame;

    /// <summary>True once the 6000 ms have run out.</summary>
    internal bool IsComplete => _frame.IsComplete;

    /// <summary>
    /// True as soon as there is something to show. Always true here: unlike a
    /// decoder there is nothing to open, so the overlay never waits on it.
    /// </summary>
    internal bool IsOpen => !_disposed;

    /// <summary>Never true. Kept because the overlay treats a failure as a silent exit.</summary>
    internal bool HasFailed => false;

    /// <summary>True once a frame has been composed.</summary>
    internal bool HasFirstFrame => _frameCount > 0;

    /// <summary>Mean milliseconds a frame has cost, over the last hundred or so. Diagnostics.</summary>
    internal double FrameMilliseconds => _frameMilliseconds;

    /// <summary>
    /// Sizes the diffuse buffer for a control size. Returns false when the control
    /// is too small to compose, which is how the sequence degrades on a tiny
    /// window: it simply draws nothing and ends on its own clock.
    /// </summary>
    /// <param name="width">Control width in device-independent pixels.</param>
    /// <param name="height">Control height in device-independent pixels.</param>
    /// <returns>True when there is a buffer to compose into.</returns>
    internal bool Resize(double width, double height) => _compositor.Resize(width, height);

    /// <summary>
    /// Advances the field and composes one frame.
    /// </summary>
    /// <param name="elapsed">Time since the sequence started; the timeline's clock.</param>
    /// <param name="seconds">Time since the previous frame; drives the motion and the trail decay.</param>
    internal void Advance(TimeSpan elapsed, double seconds)
    {
        if (_disposed)
        {
            return;
        }

        _profiler.Restart();

        _frame = BootIntroTimeline.Sample(elapsed);
        _field.Advance(seconds, _frame);
        _compositor.Compose(_field, _frame, seconds);

        _profiler.Stop();

        _frameCount++;
        _frameMilliseconds +=
            (_profiler.Elapsed.TotalMilliseconds - _frameMilliseconds) / Math.Min(_frameCount, 120);
    }

    /// <summary>Drops the accumulated trails.</summary>
    internal void Reset() => _compositor.Clear();

    /// <summary>Releases the buffers. Safe to call more than once.</summary>
    public void Dispose()
    {
        _disposed = true;
    }
}
