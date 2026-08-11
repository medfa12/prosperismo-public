// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The open-font faces used by the shell's typography tokens.
/// </summary>
public enum Ps5FontFace
{
    /// <summary>Fira Sans Light (300).</summary>
    Light,

    /// <summary>Fira Sans Regular (400).</summary>
    Roman,

    /// <summary>Fira Sans Medium (500).</summary>
    Medium,

    /// <summary>Fira Sans SemiBold (600).</summary>
    SemiBold,

    /// <summary>Fira Sans Bold (700).</summary>
    Bold,

    /// <summary>Fira Sans Light italic.</summary>
    LightItalic,

    /// <summary>Fira Sans Regular italic.</summary>
    Italic,

    /// <summary>Fira Sans Medium italic.</summary>
    MediumItalic,

    /// <summary>Fira Sans SemiBold italic.</summary>
    SemiBoldItalic,

    /// <summary>Fira Sans Bold italic.</summary>
    BoldItalic,
}

/// <summary>
/// Single source of truth for the redistributable shell typeface.
///
/// <para>The family is embedded in the Avalonia assembly and is resolved from
/// proprietary font file, or environment variable participates in rendering.
/// This makes Desktop and Big Picture deterministic on a clean machine.</para>
///
/// <para>Fira Sans is distributed under the SIL Open Font License 1.1. The
/// bundled Light, Regular, Medium, SemiBold, and Bold files are selected as
/// the closest open equivalents for the shell's light/regular/medium/strong
/// hierarchy. See <c>assets/fonts/README.md</c> for attribution.</para>
/// </summary>
public static class Ps5FontLibrary
{
    /// <summary>Internal family name used by the bundled font files.</summary>
    public const string OpenFamilyName = "Fira Sans";

    /// <summary>Avalonia resource URI for the bundled family.</summary>
    public const string OpenFamilyResource =
        "avares://Prosperismo.Shell/Assets/Fonts#Fira Sans";

    private static readonly FontFamily OpenFamily = new(OpenFamilyResource);

    /// <summary>
    /// Maps a React Native/CSS-style weight token to the explicit shell face.
    /// Unknown and omitted weights retain the shell's Light default.
    /// </summary>
    public static Ps5FontFace FaceForWeight(string? fontWeight)
    {
        var normalized = fontWeight?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "300" or "light" => Ps5FontFace.Light,
            "400" or "normal" or "regular" => Ps5FontFace.Roman,
            "500" or "medium" => Ps5FontFace.Medium,
            "600" or "semibold" or "semi-bold" => Ps5FontFace.SemiBold,
            "700" or "bold" => Ps5FontFace.Bold,
            _ => Ps5FontFace.Light,
        };
    }

    /// <summary>Returns the Avalonia weight for an explicit open face.</summary>
    public static FontWeight WeightOf(Ps5FontFace face) => face switch
    {
        Ps5FontFace.Light or Ps5FontFace.LightItalic => FontWeight.Light,
        Ps5FontFace.Roman or Ps5FontFace.Italic => FontWeight.Normal,
        Ps5FontFace.Medium or Ps5FontFace.MediumItalic => FontWeight.Medium,
        Ps5FontFace.SemiBold or Ps5FontFace.SemiBoldItalic => FontWeight.SemiBold,
        Ps5FontFace.Bold or Ps5FontFace.BoldItalic => FontWeight.Bold,
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    /// <summary>Returns the Avalonia style for an explicit open face.</summary>
    public static FontStyle StyleOf(Ps5FontFace face) => face switch
    {
        Ps5FontFace.LightItalic or
        Ps5FontFace.Italic or
        Ps5FontFace.MediumItalic or
        Ps5FontFace.SemiBoldItalic or
        Ps5FontFace.BoldItalic => FontStyle.Italic,
        _ => FontStyle.Normal,
    };

    /// <summary>
    /// Returns the bundled family for every shell face. Weight and italic
    /// style are deliberately separate because Avalonia chooses the concrete
    /// bundled file from the control's FontWeight/FontStyle properties.
    /// </summary>
    public static FontFamily? TryGet(Ps5FontFace face = Ps5FontFace.Light) => OpenFamily;
}
