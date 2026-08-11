// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Prosperismo.GUI.Ps5Home;

public enum ShellPresentationMode
{
    Sony,
    Desktop,
}

/// <summary>Startup selection between the console surface and desktop chrome.</summary>
public static class ShellPresentation
{
    public const string EnvironmentVariable = "PROSPERISMO_UI_MODE";

    /// <summary>
    /// Parses frontend-only launch forms so a shortcut can start the console
    /// surface like Steam Big Picture without relying on environment setup.
    /// Emulator CLI arguments are deliberately left untouched.
    /// </summary>
    public static bool TryParseLaunchArguments(
        IReadOnlyList<string> args,
        out ShellPresentationMode mode)
    {
        // The normal application entry point is the compact launcher. Big
        // Picture is explicit, just as it is in other desktop clients; making
        // the console route the accidental default would strand mouse and
        // keyboard users outside the launcher operations they need first.
        mode = ShellPresentationMode.Desktop;
        if (args.Count != 1)
        {
            return false;
        }

        var value = args[0].Trim();
        if (value.Equals("--sony-ui", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--big-picture", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--ui=sony", StringComparison.OrdinalIgnoreCase))
        {
            mode = ShellPresentationMode.Sony;
            return true;
        }

        if (value.Equals("--desktop-ui", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("--ui=desktop", StringComparison.OrdinalIgnoreCase))
        {
            mode = ShellPresentationMode.Desktop;
            return true;
        }

        return false;
    }

    /// <summary>Applies an explicit frontend choice for this process only.</summary>
    public static void SelectForProcess(ShellPresentationMode mode) =>
        Environment.SetEnvironmentVariable(
            EnvironmentVariable,
            mode == ShellPresentationMode.Desktop ? "desktop" : "sony");

    public static ShellPresentationMode Current =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static ShellPresentationMode Parse(string? value) =>
        string.Equals(value?.Trim(), "sony", StringComparison.OrdinalIgnoreCase)
            ? ShellPresentationMode.Sony
            : ShellPresentationMode.Desktop;

    /// <summary>
    /// True only for the conventional launcher surface. Keeping this decision
    /// at the presentation boundary prevents desktop styling from leaking into
    /// </summary>
    public static bool UsesDesktopLauncherVisuals(ShellPresentationMode mode) =>
        mode == ShellPresentationMode.Desktop;

    /// <summary>
    /// Creates the explicit process hand-off to Big Picture. Presentation is
    /// chosen before Avalonia creates MainWindow; changing this environment
    /// value in an already-running window would leave two incompatible visual
    /// trees attached to one state model, so it is intentionally a restart.
    /// </summary>
    public static ProcessStartInfo CreateBigPictureRestartStartInfo(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The current executable path is required.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("--big-picture");
        return startInfo;
    }

    /// <summary>Creates the inverse hand-off to the conventional launcher.</summary>
    public static ProcessStartInfo CreateDesktopRestartStartInfo(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The current executable path is required.", nameof(executablePath));
        }

        var fullPath = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("--desktop-ui");
        return startInfo;
    }
}
