// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets;

/// <summary>
/// Resolves the committed, publish-copied Big Picture asset package. The
/// bounded parent probe keeps development and test outputs usable; a published
/// build resolves the first candidate beside the apphost.
/// </summary>
internal static class BigPicturePackage
{
    internal const string PackageRelativePath = "assets/big-picture";

    internal static string? Resolve(string relativePath) =>
        ResolveFrom(AppContext.BaseDirectory, relativePath);

    internal static byte[]? TryReadAllBytes(string relativePath)
    {
        try
        {
            return Resolve(relativePath) is { } path ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? ResolveFrom(string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory) ||
            string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var normalized = relativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (normalized.Split(Path.DirectorySeparatorChar)
                .Any(static segment => segment == ".."))
            {
                return null;
            }

            var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(
                    current.FullName,
                    PackageRelativePath,
                    normalized);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception)
        {
            // Missing or inaccessible package entries are handled by callers.
        }

        return null;
    }
}
