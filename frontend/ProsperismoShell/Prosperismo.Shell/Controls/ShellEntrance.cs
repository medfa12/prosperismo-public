// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Drives <see cref="ShellStartupChoreography"/> onto the three parts of the
/// home screen that the console moves on the way in: the experience switcher,
/// the nav band and the hub.
///
/// The parts are moved from outside rather than each growing its own entrance,
/// because the source's schedule is one <c>Animated.parallel</c> over shared
/// drivers - the same <c>system</c> value moves the band and finishes the row's
/// vertical settle - and three independent animations cannot stay in step by
/// construction.
///
/// The title strip is deliberately not driven here. The row already fades its
/// caption on the same 1050 + 333 ms beat through its own transition, and two
/// owners writing one opacity is how that ends up flickering.
/// </summary>
public sealed class ShellEntrance
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / 60.0);

    private readonly ShellStartupChoreography _choreography = new();
    private readonly Stopwatch _stopwatch = new();

    private readonly TranslateTransform _switcherTransform = new();
    private readonly TranslateTransform _bandTransform = new();
    private readonly TranslateTransform _hubTransform = new();

    private DispatcherTimer? _timer;
    private Control? _switcher;
    private Control? _band;
    private Control? _hub;

    /// <summary>
    /// Authored pixels to host pixels. Every travel in the bundle is quoted
    /// against the 1920 x 1080 canvas, so a host that scales the shell has to
    /// scale the entrance with it or the row will slide in from the wrong
    /// place.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// When set, no timer of its own runs and the host drives
    /// <see cref="Advance"/>. Without it a headless host advances the schedule
    /// twice, once itself and once off the wall clock, and stages arrive early.
    /// </summary>
    public bool ManualClock { get; set; }

    /// <summary>The schedule this is playing.</summary>
    public ShellStartupChoreography Choreography => _choreography;

    /// <summary>True while the entrance is still running.</summary>
    public bool IsRunning => _choreography.IsRunning;

    /// <summary>Raised once every part has arrived.</summary>
    public event EventHandler? Finished;

    /// <summary>
    /// Raised every time the entrance writes a new pose, i.e. once per frame
    /// while it runs and once more when it settles.
    ///
    /// <para>The entrance moves the switcher with a <see cref="RenderTransform"/>,
    /// which repaints without re-arranging anything. Anything holding a rect
    /// that was measured from the switcher - the travelling focus ring, which
    /// lives on the window's overlay and is handed a rect in the overlay's
    /// coordinates - is stale from the first frame of the entrance and stays
    /// stale, because no layout pass ever happens to correct it. That is exactly
    /// how the ring came to sit a whole screen away from the tile it framed. The
    /// host subscribes here and re-publishes.</para>
    /// </summary>
    public event EventHandler? Moved;

    /// <summary>
    /// Points the entrance at the parts it moves. Any of them may be null, so a
    /// host without a hub still gets the row and the band.
    /// </summary>
    public void Attach(Control? switcher, Control? band, Control? hub)
    {
        _switcher = switcher;
        _band = band;
        _hub = hub;

        if (_switcher is not null)
        {
            _switcher.RenderTransform = _switcherTransform;
            _switcher.RenderTransformOrigin = RelativePoint.TopLeft;
        }

        if (_band is not null)
        {
            _band.RenderTransform = _bandTransform;
            _band.RenderTransformOrigin = RelativePoint.TopLeft;
        }

        if (_hub is not null)
        {
            _hub.RenderTransform = _hubTransform;
            _hub.RenderTransformOrigin = RelativePoint.TopLeft;
        }
    }

    /// <summary>Starts the entrance for a row of <paramref name="tileCount"/> tiles.</summary>
    public void Begin(int tileCount)
    {
        _choreography.Begin(tileCount);
        Apply();
        Wake();
    }

    /// <summary>Puts every part in its settled pose at once.</summary>
    public void SettleNow()
    {
        _choreography.SettleNow();
        Apply();
        StopTimer();
    }

    /// <summary>Advances the entrance. The host may drive this instead of a timer.</summary>
    public void Advance(TimeSpan delta)
    {
        bool running = _choreography.Advance(delta);
        Apply();
        if (!running)
        {
            StopTimer();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Apply()
    {
        _switcherTransform.X = _choreography.SwitcherTranslateX * Scale;
        _switcherTransform.Y = _choreography.SwitcherTranslateY * Scale;

        _bandTransform.Y = _choreography.SystemTranslateY * Scale;
        if (_band is not null)
        {
            _band.Opacity = _choreography.SystemOpacity;
        }

        _hubTransform.Y = _choreography.HubTranslateY * Scale;
        if (_hub is not null)
        {
            _hub.Opacity = _choreography.HubOpacity;
        }

        Moved?.Invoke(this, EventArgs.Empty);
    }

    private void Wake()
    {
        if (ManualClock || !_choreography.IsRunning)
        {
            return;
        }

        try
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
                _timer.Tick += OnTick;
            }

            if (!_timer.IsEnabled)
            {
                _stopwatch.Restart();
                _timer.Start();
            }
        }
        catch
        {
            // No dispatcher, so there are no frames to play the entrance on and
            // the shell simply starts in its settled pose.
            SettleNow();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var delta = _stopwatch.Elapsed;
        _stopwatch.Restart();
        Advance(delta);
    }

    private void StopTimer()
    {
        try
        {
            _timer?.Stop();
            _stopwatch.Reset();
        }
        catch
        {
            // Nothing to unwind.
        }
    }
}
