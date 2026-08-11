// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// The bounded set of UI3 icons that Prosperismo ships for surfaces which must
/// their original SVG paths; extraction provenance and hashes live beside the
/// assets. Control Center entries come from the pristine stock 3.00 UI3
/// container reconstructed from the installable update. The four additional
/// Profile rows and Search utility glyph come from matching 12.40 UI3 entries
/// and are recorded in that asset pack's manifest.
/// </summary>
public static class Ps5BundledIconLibrary
{
    private const string IconIdPrefix = "iconid_";
    private const string ControlCenterRoot =
        "avares://Prosperismo.Shell/Assets/BigPicture/3.00/ControlCenterIcons/";
    private const string ProfileRoot =
        "avares://Prosperismo.Shell/Assets/BigPicture/12.40/ProfileIcons/";
    private const string UtilityRoot =
        "avares://Prosperismo.Shell/Assets/BigPicture/12.40/UtilityIcons/";
    private const string SystemRoot =
        "avares://Prosperismo.Shell/Assets/BigPicture/12.40/SystemIcons/";

    private static readonly IReadOnlyDictionary<string, string> Files =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["iconid_home"] = ControlCenterRoot + "iconid_home.svg",
            ["iconid_switcher"] = ControlCenterRoot + "iconid_switcher.svg",
            ["iconid_notification"] = ControlCenterRoot + "iconid_notification.svg",
            ["iconid_game_base"] = ControlCenterRoot + "iconid_game_base.svg",
            ["iconid_music"] = ControlCenterRoot + "iconid_music.svg",
            ["iconid_sound_speaking"] = ControlCenterRoot + "iconid_sound_speaking.svg",
            ["iconid_mic"] = ControlCenterRoot + "iconid_mic.svg",
            ["iconid_game"] = ControlCenterRoot + "iconid_game.svg",
            ["iconid_ps_user"] = ControlCenterRoot + "iconid_ps_user.svg",
            ["iconid_power"] = ControlCenterRoot + "iconid_power.svg",
            ["iconid_person_online"] = ProfileRoot + "iconid_person_online.svg",
            ["iconid_trophies"] = ProfileRoot + "iconid_trophies.svg",
            ["iconid_person"] = ProfileRoot + "iconid_person.svg",
            ["iconid_logout"] = ProfileRoot + "iconid_logout.svg",
            ["iconid_search"] = UtilityRoot + "iconid_search.svg",
            ["iconid_notification_off"] = UtilityRoot + "iconid_notification_off.svg",
            ["iconid_new"] = UtilityRoot + "iconid_new.svg",
            ["iconid_settings"] = SystemRoot + "iconid_settings.svg",
            ["iconid_system"] = SystemRoot + "iconid_system.svg",
            ["iconid_storage"] = SystemRoot + "iconid_storage.svg",
            ["iconid_control_play"] = SystemRoot + "iconid_control_play.svg",
            ["iconid_folder"] = SystemRoot + "iconid_folder.svg",
            ["iconid_copy"] = SystemRoot + "iconid_copy.svg",
            ["iconid_delete"] = SystemRoot + "iconid_delete.svg",
            ["iconid_add_folder"] = SystemRoot + "iconid_add_folder.svg",
            ["iconid_update"] = SystemRoot + "iconid_update.svg",
            ["iconid_screen_and_video"] = SystemRoot + "iconid_screen_and_video.svg",
            ["iconid_games_and_apps"] = SystemRoot + "iconid_games_and_apps.svg",
            ["iconid_network"] = SystemRoot + "iconid_network.svg",
            ["iconid_information"] = SystemRoot + "iconid_information.svg",
            ["iconid_texture_app_fallback"] = SystemRoot + "iconid_texture_app_fallback.svg",
        };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Ps5VectorIcon?> Vectors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Bitmap?> Rasters = new(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> IconIds { get; } = Files.Keys.ToArray();

    public static Ps5VectorIcon? TryGetVector(string? id)
    {
        var key = Normalize(id);
        if (key is null || !Files.TryGetValue(key, out var uri) || !uri.EndsWith(".svg", StringComparison.Ordinal))
        {
            return null;
        }

        lock (Gate)
        {
            if (Vectors.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Ps5VectorIcon? parsed = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri(uri));
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                parsed = Ps5SvgIconParser.Parse(reader.ReadToEnd(), key, out _);
            }
            catch (Exception)
            {
                // A missing packaged resource remains a visible unresolved icon.
            }

            Vectors[key] = parsed;
            return parsed;
        }
    }

    public static Bitmap? TryGetRaster(string? id)
    {
        var key = Normalize(id);
        if (key is null || !Files.TryGetValue(key, out var uri) || !uri.EndsWith(".png", StringComparison.Ordinal))
        {
            return null;
        }

        lock (Gate)
        {
            if (Rasters.TryGetValue(key, out var cached))
            {
                return cached;
            }

            Bitmap? decoded = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri(uri));
                decoded = new Bitmap(stream);
            }
            catch (Exception)
            {
                // Same fail-visible policy as the vector path.
            }

            Rasters[key] = decoded;
            return decoded;
        }
    }

    private static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var value = id.Trim();
        return value.StartsWith(IconIdPrefix, StringComparison.Ordinal)
            ? value
            : IconIdPrefix + value;
    }
}
