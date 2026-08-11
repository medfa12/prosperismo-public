// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI;

/// <summary>
/// Locates the native Prosperismo backend for both Avalonia presentation modes.
/// A release package keeps it beside the managed apphost; repository builds are
/// supported as a bounded developer fallback.
/// </summary>
internal static class EmulatorInstallationLocator
{
    public static string? Locate(string baseDirectory, string? configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        foreach (var directory in new[]
        {
            baseDirectory,
            Path.Combine(baseDirectory, "win-x64"),
            Path.Combine(baseDirectory, ".."),
        })
        {
            candidates.Add(Path.Combine(directory, ExecutableName()));
        }

        // A repository build keeps the native backend under _Build while the
        // Avalonia apphost lives several directories below artifacts/. This is
        // deliberately bounded and includes the authoritative build directory
        // names used by scripts/build_release.py and GitHub Actions.
        DirectoryInfo? ancestor = new(baseDirectory);
        for (var depth = 0; depth < 8 && ancestor is not null; depth++, ancestor = ancestor.Parent)
        {
            foreach (var buildDirectory in new[]
            {
                "windows",
                "windows-dbg",
                "macos",
                "macos-dbg",
                "linux",
                "linux-dbg",
                "win64",
                "win64-dbg",
            })
            {
                candidates.Add(Path.Combine(
                    ancestor.FullName,
                    "_Build",
                    buildDirectory,
                    ExecutableName()));
            }
        }

        return candidates.FirstOrDefault(IsNativeEmulatorExecutable) is { } found
            ? Path.GetFullPath(found)
            : null;
    }

    private static bool IsNativeEmulatorExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(path),
            EmulatorLaunchContract.NativeExecutableStem,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ExecutableName() =>
        OperatingSystem.IsWindows()
            ? $"{EmulatorLaunchContract.NativeExecutableStem}.exe"
            : EmulatorLaunchContract.NativeExecutableStem;
}
