// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Threading;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.GUI.Controls;

/// <summary>
/// Applies the recovered default PUI screen transition to a Settings route.
/// NPXS40008 delegates ordinary route motion to native <c>NavigatorPS</c> as
/// <c>"default"</c>; the only authored properties reproduced here are its
/// measured opacity duration and curve. No unverified slide distance is added.
/// </summary>
internal sealed class ShellSettingsRouteTransition
{
    private readonly Control _route;
    private int _generation;

    public ShellSettingsRouteTransition(Control route) => _route = route;

    /// <summary>
    /// Cancels any previous entrance and starts the latest route from its
    /// initial opacity. The generation check is the equivalent of RN's stopped
    /// animation reporting <c>finished: false</c>: a queued completion from an
    /// older route selection cannot revive or modify the new page.
    /// </summary>
    internal void Enter()
    {
        int generation = ++_generation;
        _route.Transitions = null;
        _route.Opacity = 0.0;

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _generation || !_route.IsVisible)
            {
                return;
            }

            _route.Transitions =
            [
                new DoubleTransition
                {
                    Property = Control.OpacityProperty,
                    Duration = Ps5Transitions.SettingsRoute,
                    Easing = Ps5Transitions.SettingsRouteCurve,
                },
            ];
            _route.Opacity = 1.0;
        }, DispatcherPriority.Render);
    }

    /// <summary>Invalidates queued work and leaves the hidden route ready.</summary>
    internal void Cancel()
    {
        _generation++;
        _route.Transitions = null;
        _route.Opacity = 1.0;
    }
}
