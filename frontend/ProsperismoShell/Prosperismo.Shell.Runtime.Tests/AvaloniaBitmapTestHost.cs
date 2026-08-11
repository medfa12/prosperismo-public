// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Headless;

namespace Prosperismo.Shell.Runtime.Tests;

/// <summary>Initializes the headless render platform for bitmap-backed asset tests.</summary>
internal static class AvaloniaBitmapTestHost
{
    private static readonly object Gate = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            AppBuilder.Configure<Application>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            _initialized = true;
        }
    }
}
