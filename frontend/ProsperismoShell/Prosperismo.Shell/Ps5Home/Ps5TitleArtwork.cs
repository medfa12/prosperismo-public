// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// Resolves local package artwork without conflating the package's filenames
///
/// <para>Home/AppBrowse exposes background and logo independently. The locally
/// scanned <c>pic*</c> names are only a resilience fallback while an AppDB or
/// algorithm and none is a title logo. See
///
/// <para>Observed package backdrops are 3840x2160 DDS/PNG. Games commonly use
/// DDS DX10 BC7, while a few system applications use DXT1 or PNG, so callers
/// must retain the existing multi-format decoder.</para>
/// </summary>
public static class Ps5TitleArtwork
{
    // PPSA17221, content UP4433-PPSA17221_00-MINECRAFTPS50000,
    // contentVersion 01.008.000. The package has no sce_sys/logo asset or
    // catalog/AppDB payload. Its executable does contain this self-contained
    // transparent Minecraft wordmark: outer SELF segment 7 / ELF PT_LOAD 7,
    // offset 0xE1A7100, 1937x333 RGBA PNG. Both image and source SELF hashes
    // are checked before the image reaches the UI.
    private static readonly Ps5EmbeddedTitleLogoProfile MinecraftPpsa17221Logo = new(
        TitleId: "PPSA17221",
        ExecutableLength: 254_075_005,
        ExecutableSha256: "c32051395a2ae0c747a0330c82a975404cbe62eaafb4af8abc7a17fca0a467e8",
        Offset: 0x0E1A7100,
        Length: 0x1530C,
        PayloadSha256: "1ab368c3719a0fa0c273a0040bd5d3e8c47a9678b8dff22a09aa1bf570781662",
        PixelWidth: 1937,
        PixelHeight: 333);

    // Astro's Playroom PPSA01325. The installable package has no AppDB/catalog
    // /logo record. Its hash-pinned icon0 does carry the exact English wordmark;
    // these two non-overlapping regions recover only that mark (not Astro or the
    // blue icon field) as a transparent local-package fallback.
    private static readonly Ps5PackagedIconWordmarkProfile AstroPpsa01325Logo = new(
        TitleId: "PPSA01325",
        ExecutableLength: 73_111_358,
        ExecutableSha256: "1bc6e1913426f3249b80b35c42cd9cd8e7ef4fb1c76cea05372fad68a4f535ab",
        RelativeAssetPath: "sce_sys/icon0.png",
        AssetLength: 255_807,
        AssetSha256: "4112b3034903de0c5167f08de22db62628ff45cd2db9e0a9feec0413efe09dfa",
        AssetWidth: 512,
        AssetHeight: 512,
        OutputWidth: 426,
        OutputHeight: 98,
        Regions:
        [
            new(44, 367, 184, 36, 0, 0),
            new(44, 411, 426, 54, 0, 44),
        ]);

    /// <summary>
    /// Local package backdrop fallback names. This is deliberately not called
    /// installable game package.
    /// </summary>
    public static readonly IReadOnlyList<string> BackdropCandidates = new[]
    {
        "pic1.dds",
        "pic1.png",
        "pic0.dds",
        "pic0.png",
        "pic2.dds",
        "pic2.png",
    };

    /// <summary>
    /// The one backdrop-related key that <c>param.json</c> actually carries:
    /// <c>backgroundBasematType</c>. See <see cref="TryReadBasematType"/>.
    /// </summary>
    public const string BasematTypeKey = "backgroundBasematType";

    /// <summary>
    /// Finds a title-logo source proved inside the supplied installable package.
    /// This does not manufacture a logo from a cover/backdrop, and it does not
    /// record. The source is an explicit local fallback for this exact package;
    /// a later catalog field takes normal precedence when it can be recovered.
    /// </summary>
    public static Ps5TitleLogoSource? ResolveEmbeddedTitleLogoForExecutable(
        string? executablePath,
        string? titleId)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(executablePath);
            if (!info.Exists)
            {
                return null;
            }

            if (string.Equals(titleId, MinecraftPpsa17221Logo.TitleId, StringComparison.Ordinal) &&
                info.Length == MinecraftPpsa17221Logo.ExecutableLength)
            {
                return new Ps5TitleLogoSource(executablePath, MinecraftPpsa17221Logo);
            }

            if (string.Equals(titleId, AstroPpsa01325Logo.TitleId, StringComparison.Ordinal) &&
                info.Length == AstroPpsa01325Logo.ExecutableLength)
            {
                return new Ps5TitleLogoSource(executablePath, AstroPpsa01325Logo);
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Test/documentation seam for the only currently verified embedded title
    /// logo profile. It exposes provenance dimensions, never the game bytes.
    /// </summary>
    internal static (long Offset, int Length, int PixelWidth, int PixelHeight)?
        DescribeEmbeddedTitleLogo(string? titleId) =>
        string.Equals(titleId, MinecraftPpsa17221Logo.TitleId, StringComparison.Ordinal)
            ? (MinecraftPpsa17221Logo.Offset,
                MinecraftPpsa17221Logo.Length,
                MinecraftPpsa17221Logo.PixelWidth,
                MinecraftPpsa17221Logo.PixelHeight)
            : null;

    internal static (string RelativePath, int Length, int PixelWidth, int PixelHeight)?
        DescribePackagedIconTitleLogo(string? titleId) =>
        string.Equals(titleId, AstroPpsa01325Logo.TitleId, StringComparison.Ordinal)
            ? (AstroPpsa01325Logo.RelativeAssetPath,
                AstroPpsa01325Logo.AssetLength,
                AstroPpsa01325Logo.OutputWidth,
                AstroPpsa01325Logo.OutputHeight)
            : null;

    /// <summary>
    /// Resolves the home backdrop inside a title's <c>sce_sys</c> directory, or
    /// null when the title ships none. Probing is case-insensitive by way of
    /// enumerating the directory once: the dump really does contain
    /// <c>pic1.DDS</c> in upper case (NPXS40140), which a plain
    /// <see cref="File.Exists"/> would still find on Windows but would miss on
    /// a case-sensitive filesystem.
    /// </summary>
    /// <param name="sceSysDirectory">A title's <c>sce_sys</c> folder.</param>
    public static string? ResolveBackdrop(string? sceSysDirectory)
    {
        if (string.IsNullOrWhiteSpace(sceSysDirectory) || !Directory.Exists(sceSysDirectory))
        {
            return null;
        }

        Dictionary<string, string> present;
        try
        {
            present = Directory
                .EnumerateFiles(sceSysDirectory)
                .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // An unreadable or vanished directory is a title without artwork,
            // not a crash.
            return null;
        }

        foreach (var candidate in BackdropCandidates)
        {
            if (present.TryGetValue(candidate, out var path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the home backdrop for a title given the path to its executable,
    /// which is what the library scan records. Looks in <c>sce_sys</c> beside
    /// the executable, the same place the title's <c>snd0.at9</c> preview and
    /// <c>param.json</c> live.
    /// </summary>
    /// <param name="executablePath">Path to the title's eboot.</param>
    public static string? ResolveBackdropForExecutable(string? executablePath)
    {
        var sceSys = ResolveSystemDirectoryForExecutable(executablePath);
        return sceSys is null ? null : ResolveBackdrop(sceSys);
    }

    /// <summary>
    /// Resolves the executable-adjacent <c>sce_sys</c> directory without
    /// assuming host file-name casing. Copied package trees may contain
    /// <c>SCE_SYS</c>; the console namespace is case-insensitive even when the
    /// macOS/Linux filesystem hosting the dump is not.
    /// </summary>
    public static string? ResolveSystemDirectoryForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(executablePath);
        }
        catch (Exception)
        {
            return null;
        }

        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateDirectories(directory)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    "sce_sys",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>backgroundBasematType</c> out of a <c>param.json</c> payload, or
    /// null when the key is absent or the JSON will not parse.
    ///
    /// <para><b>What the survey found.</b> This is the <em>only</em> key in any
    /// <c>param.json</c> on the dump that says anything about the backdrop.
    /// Across 41 system <c>param.json</c> files exactly three carry it —
    /// Explore (NPXS40063) with <c>EllipseNarrow</c>, Game Library (NPXS40071)
    /// and App Library (NPXS40139) with <c>Linear</c> — and no shipped game
    /// carries it at all. There is no background colour, no blur variant and no
    /// "suppress the background" flag in title metadata.</para>
    ///
    /// <para><b>What it controls, and what it does not.</b> It names the
    /// <em>basemat</em> — the gradient laid over the backdrop — and not the
    /// image. <c>Sce.Vsh.ShellUI.BGLayer</c> keeps the two on separate calls:
    /// <c>SetBackgroundBasemat(BasematType, Color, Duration)</c> against
    /// <c>SetBackgroundTransition(… NextImageUri, NextBlurImageUri,
    /// NextFallbackImageUri, OverlayImageUri, BasematType …)</c>. The three
    /// enum values seen so far are <c>Linear</c>, <c>EllipseNarrow</c> and
    /// <c>EllipseWide</c>; the full member list has not been recovered, so this
    /// returns the raw string and does not pretend to be an enum.</para>
    ///
    /// <para><b>Not yet honoured by the renderer.</b> The plate draws no
    /// basemat at all today — the only mat in the shell is the tile row's
    /// per-tile darkening, which is a different thing that happens to share the
    /// word. Wiring the Linear and Ellipse variants needs their geometry, which
    /// is unrecovered, and inventing an ellipse would be worse than leaving
    /// this parsed, documented and unused.</para>
    /// </summary>
    /// <param name="paramJson">Raw <c>param.json</c> bytes.</param>
    public static string? TryReadBasematType(ReadOnlySpan<byte> paramJson)
    {
        if (paramJson.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(paramJson.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty(BasematTypeKey, out var value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
