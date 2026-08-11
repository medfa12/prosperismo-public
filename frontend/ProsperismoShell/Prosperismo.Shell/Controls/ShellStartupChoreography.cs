// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// The home screen's entrance, ported from the home bundle's
/// <c>useStartupAnimation</c> (HOME m843).
///
/// Every driver starts at 1 and springs to 0, so 1 is "not yet arrived" and 0
/// is settled. The source's own schedule:
///
/// <code>
/// Animated.parallel([
///   spring(switcher, {toValue: 0}, SPRING_OPTIONS_SLOWER),
///   Animated.stagger(60, experienceScales.slice(0, min(MAX_TILES, n))
///       .map(v => spring(v, {toValue: 0}, SPRING_OPTIONS_SLOWER))),
///   Animated.sequence([ Animated.delay(1050), Animated.parallel([
///       Animated.parallel([
///         spring(systemOpacity, {toValue: 0}, SPRING_OPTIONS_SLOW),
///         Animated.sequence([ Animated.delay(333),
///           spring(titleOpacity, {toValue: 0}, SPRING_OPTIONS_SLOW) ]) ]),
///       spring(system, {toValue: 0}, SPRING_OPTIONS_SLOW) ]) ]),
///   Animated.sequence([ Animated.delay(1450),
///       Animated.parallel([ spring(hub, {toValue: 0}, SPRING_OPTIONS_SLOW) ]) ])
/// ])
/// </code>
///
/// and what each driver moves:
///
/// <code>
/// switcher       translateX [0, 1920]
/// system         translateY [0, (SCALED_EXP_SIZE - EXPERIENCE_SIZE) / 2]   // on the switcher
/// system         translateY [0, -20]  and  systemOpacity opacity [1, 0]    // on the nav band
/// titleOpacity   opacity    [1, 0]
/// hub            translateY [0, 20]   and  opacity [1, 0]
/// </code>
///
/// So the row arrives from a whole screen to the right while the nav band drops
/// in from 20 px above it a second later, and the hub rises from 20 px below
/// last. The single <c>system</c> driver moves two different things at once,
/// which is why the row settles its last 31 px at the same moment the band
/// appears.
/// </summary>
public sealed class ShellStartupChoreography
{
    /// <summary>Gap between consecutive tiles in the stagger, ms.</summary>
    public const double TileStaggerMs = 60.0;

    /// <summary>Delay before the nav band and the row's vertical settle.</summary>
    public const double SystemDelayMs = 1050.0;

    /// <summary>Further delay after the band before the title fades up.</summary>
    public const double TitleDelayAfterSystemMs = 333.0;

    /// <summary>Absolute delay before the title fades up.</summary>
    public const double TitleDelayMs = SystemDelayMs + TitleDelayAfterSystemMs;

    /// <summary>Delay before the hub rises.</summary>
    public const double HubDelayMs = 1450.0;

    /// <summary>How far right the switcher starts, one authored screen.</summary>
    public const double SwitcherTravelX = 1920.0;

    /// <summary>
    /// The switcher's vertical settle,
    /// <c>(SCALED_EXP_SIZE - EXPERIENCE_SIZE) / 2</c> = 31.
    /// </summary>
    public const double SwitcherTravelY =
        (ShellTileRow.ScaledExperienceSize - ShellTileRow.ExperienceSize) / 2.0;

    /// <summary>How far above its place the nav band starts.</summary>
    public const double SystemTravelY = -20.0;

    /// <summary>How far below its place the hub starts.</summary>
    public const double HubTravelY = 20.0;

    private const double RestDisplacement = 0.0005;
    private const double RestVelocity = 0.005;

    private readonly ShellSpring _switcher = Driver();
    private readonly ShellSpring _system = Driver();
    private readonly ShellSpring _systemOpacity = Driver();
    private readonly ShellSpring _titleOpacity = Driver();
    private readonly ShellSpring _hub = Driver();
    private readonly List<ShellSpring> _tiles = new();

    private double _clockMs;
    private int _tileCount;

    private static ShellSpring Driver()
    {
        var spring = new ShellSpring(RestDisplacement, RestVelocity);
        spring.SnapTo(1.0);
        return spring;
    }

    /// <summary>Time since the entrance began, in milliseconds.</summary>
    public double ElapsedMs => _clockMs;

    /// <summary>True until every driver has settled.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Switcher offset to the right, in authored pixels.</summary>
    public double SwitcherTranslateX => _switcher.Value * SwitcherTravelX;

    /// <summary>Switcher offset downwards, in authored pixels.</summary>
    public double SwitcherTranslateY => _system.Value * SwitcherTravelY;

    /// <summary>Nav band offset upwards, in authored pixels.</summary>
    public double SystemTranslateY => _system.Value * SystemTravelY;

    /// <summary>Nav band opacity, <c>[1, 0]</c> against the driver.</summary>
    public double SystemOpacity => 1.0 - _systemOpacity.Value;

    /// <summary>Title strip opacity.</summary>
    public double TitleOpacity => 1.0 - _titleOpacity.Value;

    /// <summary>Hub offset downwards, in authored pixels.</summary>
    public double HubTranslateY => _hub.Value * HubTravelY;

    /// <summary>Hub opacity.</summary>
    public double HubOpacity => 1.0 - _hub.Value;

    /// <summary>Scale progress of one tile, 1 at the start and 0 once arrived.</summary>
    public double TileProgress(int index) =>
        index >= 0 && index < _tiles.Count ? _tiles[index].Value : 0.0;

    /// <summary>
    /// Arms the entrance for <paramref name="tileCount"/> tiles. The source
    /// staggers only the first <c>MAX_TILES</c>, which is all the row shows.
    /// </summary>
    public void Begin(int tileCount)
    {
        _tileCount = Math.Clamp(tileCount, 0, ShellTileRow.MaxTiles);
        _clockMs = 0.0;
        _tiles.Clear();
        for (int i = 0; i < _tileCount; i++)
        {
            _tiles.Add(Driver());
        }

        _switcher.SnapTo(1.0);
        _system.SnapTo(1.0);
        _systemOpacity.SnapTo(1.0);
        _titleOpacity.SnapTo(1.0);
        _hub.SnapTo(1.0);

        // The switcher and the tile stagger start immediately; everything else
        // waits on its delay and is retargeted in Advance.
        _switcher.SpringTo(0.0, ShellSpringConfig.Slower);
        IsRunning = true;
    }

    /// <summary>Drops every driver onto its settled value.</summary>
    public void SettleNow()
    {
        _switcher.SnapTo(0.0);
        _system.SnapTo(0.0);
        _systemOpacity.SnapTo(0.0);
        _titleOpacity.SnapTo(0.0);
        _hub.SnapTo(0.0);
        foreach (var tile in _tiles)
        {
            tile.SnapTo(0.0);
        }

        IsRunning = false;
    }

    /// <summary>
    /// Advances the entrance. Returns true while it still needs frames.
    /// </summary>
    public bool Advance(TimeSpan delta)
    {
        if (!IsRunning)
        {
            return false;
        }

        double ms = delta.TotalMilliseconds;
        if (!(ms > 0.0) || double.IsNaN(ms))
        {
            return true;
        }

        _clockMs += ms;

        // Release each driver as its delay expires. Retargeting a spring that
        // is already heading somewhere is harmless, so this needs no edge
        // tracking beyond the settled check.
        for (int i = 0; i < _tiles.Count; i++)
        {
            if (_clockMs >= i * TileStaggerMs && _tiles[i].Target != 0.0)
            {
                _tiles[i].SpringTo(0.0, ShellSpringConfig.Slower);
            }
        }

        if (_clockMs >= SystemDelayMs)
        {
            if (_system.Target != 0.0)
            {
                _system.SpringTo(0.0, ShellSpringConfig.Slow);
            }

            if (_systemOpacity.Target != 0.0)
            {
                _systemOpacity.SpringTo(0.0, ShellSpringConfig.Slow);
            }
        }

        if (_clockMs >= TitleDelayMs && _titleOpacity.Target != 0.0)
        {
            _titleOpacity.SpringTo(0.0, ShellSpringConfig.Slow);
        }

        if (_clockMs >= HubDelayMs && _hub.Target != 0.0)
        {
            _hub.SpringTo(0.0, ShellSpringConfig.Slow);
        }

        double seconds = ms / 1000.0;
        bool busy = false;
        busy |= _switcher.Advance(seconds);
        busy |= _system.Advance(seconds);
        busy |= _systemOpacity.Advance(seconds);
        busy |= _titleOpacity.Advance(seconds);
        busy |= _hub.Advance(seconds);
        foreach (var tile in _tiles)
        {
            busy |= tile.Advance(seconds);
        }

        // A driver still waiting on its delay is settled but not finished.
        bool waiting = _clockMs < HubDelayMs;
        IsRunning = busy || waiting;
        return IsRunning;
    }
}
