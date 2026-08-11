// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI;
using Prosperismo.GUI.Ps5Home;

namespace Prosperismo.Shell.App;

/// <summary>
/// Entry point for the Prosperismo frontend.
///
/// One executable hosts both routes, exactly as the shell already models them:
/// the compact desktop launcher, and the controller-first Big Picture shell
/// that reproduces the PS5 home screen. <c>--big-picture</c> selects the
/// latter, matching the launch-mode boundary other desktop clients use.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (ShellPresentation.TryParseLaunchArguments(args, out var mode))
        {
            ShellPresentation.SelectForProcess(mode);
        }

        return GuiLauncher.Run();
    }
}
