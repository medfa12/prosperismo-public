// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>Small control textures authored in the PS5 UI3 resource package.</summary>
public enum Ps5Ui3ControlAsset
{
    SwitchBase,
    SwitchOff,
    SwitchOn,
    SwitchBaseHighlight,
    SwitchControlHighlight,
    ProgressBarLight,
    BusyIndicatorSquare,
    BusyIndicatorHorizontal,
}

/// <summary>
/// Immutable provenance for one UI3 control texture. The dimensions and hash
/// describe the preferred 4K source when one exists, otherwise its only
/// payloads in this repository.
/// </summary>
public sealed record Ps5Ui3ControlAssetDefinition(
    string EntryName,
    int Width,
    int Height,
    string Sha256,
    string SourceLabel);

/// <summary>
/// Runtime reader for the PS5 shell's switch, progress and busy-indicator
/// textures. It prefers the user's authoritative
/// <c>PS5_12.40/Sce.PlayStation.PUI_UI3.rco</c> and falls back to the named
/// packaged PNG derivatives when that RCO is unavailable.
/// </summary>
public static class Ps5Ui3ControlAssets
{
    /// <summary>
    /// Audited PS5 12.40 entries. The busy indicators only provide <c>src</c>;
    /// the other controls use their <c>src_4k</c> payload.
    /// </summary>
    public static IReadOnlyDictionary<Ps5Ui3ControlAsset, Ps5Ui3ControlAssetDefinition> Definitions { get; } =
        new Dictionary<Ps5Ui3ControlAsset, Ps5Ui3ControlAssetDefinition>
        {
            [Ps5Ui3ControlAsset.SwitchBase] = new(
                "image_switch_base", 70, 68,
                "54cc345b1465f75c1c36e7ad07201d922e09e3c9aa09bd9eb0438a6471c1c330", "src_4k"),
            [Ps5Ui3ControlAsset.SwitchOff] = new(
                "image_switch_off", 56, 56,
                "9dcf7d302486bfce3e856f4d3faf20a05f407f833a252c246fea0be09c14c60a", "src_4k"),
            [Ps5Ui3ControlAsset.SwitchOn] = new(
                "image_switch_on", 56, 56,
                "31928ad932711f428ce9233ae238c86877f69a8d40ae5b41910453c6972911e0", "src_4k"),
            [Ps5Ui3ControlAsset.SwitchBaseHighlight] = new(
                "image_switch_base_highlight", 256, 64,
                "e01802948b9a097713e64d1f31749ed0e1fae055adfd07e953ff6dfa5ec21d98", "src_4k"),
            [Ps5Ui3ControlAsset.SwitchControlHighlight] = new(
                "image_switch_control_highlight", 96, 96,
                "6d3950b6c13eb0cc1ac58662f2b1972075bc440c9f9df1791bf19de07d98738f", "src_4k"),
            [Ps5Ui3ControlAsset.ProgressBarLight] = new(
                "image_progressbar_light", 80, 40,
                "43cb8657cd06675990d5de703e0c1b2b2acd955d763936f1925e505443e93ce2", "src_4k"),
            [Ps5Ui3ControlAsset.BusyIndicatorSquare] = new(
                "image_busy_indicator_square", 96, 96,
                "a867a973e672d523f9e68ffe2fb718e206c4cbef92a17643c1a1fc8c7496b87e", "src"),
            [Ps5Ui3ControlAsset.BusyIndicatorHorizontal] = new(
                "image_busy_indicator_horizontal", 256, 32,
                "0c8b7ef4b437d3e375773a7a588db324807b3a663427f1ab77a603d5a044d3f2", "src"),
        };

    public static Bitmap? TryGet(Ps5Ui3ControlAsset asset)
    {
        if (Ps5Ui3PackagedTextures.TryGet(PackagedFileName(asset)) is not { } bitmap)
        {
            return null;
        }

        var definition = Definitions[asset];
        if (bitmap.PixelSize.Width == definition.Width && bitmap.PixelSize.Height == definition.Height)
        {
            return bitmap;
        }

        bitmap.Dispose();
        return null;
    }

    public static bool IsAvailable() =>
        Enum.GetValues<Ps5Ui3ControlAsset>().All(asset => TryGet(asset) is not null);

    private static string PackagedFileName(Ps5Ui3ControlAsset asset) => asset switch
    {
        Ps5Ui3ControlAsset.SwitchBase => "switch-base.png",
        Ps5Ui3ControlAsset.SwitchOff => "switch-off.png",
        Ps5Ui3ControlAsset.SwitchOn => "switch-on.png",
        Ps5Ui3ControlAsset.SwitchBaseHighlight => "switch-base-highlight.png",
        Ps5Ui3ControlAsset.SwitchControlHighlight => "switch-control-highlight.png",
        Ps5Ui3ControlAsset.ProgressBarLight => "progressbar-light.png",
        Ps5Ui3ControlAsset.BusyIndicatorSquare => "busy-indicator-square.png",
        Ps5Ui3ControlAsset.BusyIndicatorHorizontal => "busy-indicator-horizontal.png",
        _ => throw new ArgumentOutOfRangeException(nameof(asset)),
    };

}
