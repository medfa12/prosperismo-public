// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>
/// Locates the small, named UI3 texture derivatives bundled with Prosperismo.
/// SVG icon payloads are deliberately not handled here.
/// </summary>
internal static class Ps5Ui3PackagedTextures
{
    private const string RelativeDirectory = "assets/big-picture/12.40/ui3";

    internal static Bitmap? TryGet(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var root in CandidateDirectories())
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }
            catch (Exception)
            {
                // A single damaged image must not prevent the caller's
                // authored fallback from rendering.
            }
        }

        return null;
    }

    internal static byte[]? TryGetBytes(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var root in CandidateDirectories())
        {
            var path = Path.Combine(root, fileName);
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllBytes(path);
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DirectoryInfo? current = null;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        }
        catch (Exception)
        {
        }

        for (int depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
        {
            var path = Path.Combine(current.FullName, RelativeDirectory);
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }
}
