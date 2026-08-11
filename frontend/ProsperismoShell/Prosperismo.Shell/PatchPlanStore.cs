// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI;

/// <summary>
/// Resolves the one patch-plan location retained from the Kyty launcher:
/// <c>_Patches/&lt;TITLE_ID&gt;.json</c> beside the native emulator executable.
/// It deliberately performs no directory search or filename guessing.
/// </summary>
internal static class PatchPlanStore
{
    private const int TitleIdLength = 9;

    public static string? ResolveExistingPlan(string emulatorExecutablePath, string? titleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emulatorExecutablePath);
        if (string.IsNullOrWhiteSpace(titleId))
        {
            return null;
        }

        var normalizedTitleId = NormalizeTitleId(titleId);
        var executableFullPath = Path.GetFullPath(emulatorExecutablePath);
        var executableDirectory = Path.GetDirectoryName(executableFullPath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            throw new ArgumentException(
                "The emulator executable must have a parent directory.",
                nameof(emulatorExecutablePath));
        }

        var patchDirectory = Path.GetFullPath(Path.Combine(executableDirectory, "_Patches"));
        var candidate = Path.GetFullPath(Path.Combine(patchDirectory, normalizedTitleId + ".json"));
        if (!IsWithinDirectory(candidate, patchDirectory))
        {
            throw new InvalidOperationException("The patch plan resolved outside the emulator's _Patches directory.");
        }

        return File.Exists(candidate) ? candidate : null;
    }

    public static string ValidateExistingPlanPath(string patchPlanPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(patchPlanPath);
        var fullPath = Path.GetFullPath(patchPlanPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Patch plans must use the .json extension.", nameof(patchPlanPath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The validated patch plan does not exist.", fullPath);
        }

        return fullPath;
    }

    internal static string NormalizeTitleId(string titleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleId);
        var normalized = titleId.Trim().ToUpperInvariant();
        if (normalized.Length != TitleIdLength ||
            normalized[..4].Any(character => character is < 'A' or > 'Z') ||
            normalized[4..].Any(character => character is < '0' or > '9'))
        {
            throw new ArgumentException(
                "Title IDs must contain four ASCII letters followed by five digits.",
                nameof(titleId));
        }

        return normalized;
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(directoryPrefix, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
