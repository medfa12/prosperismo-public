// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>
/// The system-shell icons the launcher can borrow, named after what they mean
/// here rather than after their container entry. The button glyphs are the
/// DualSense keyguide art the shell draws in its own footer hints; the rest are
/// the shell's pictograms.
///
/// Not every value has bitmap art: the shell keeps most of its pictogram set as
/// SVG. <see cref="ShellIcons.EntryNames"/> lists the bitmap entries and
/// <see cref="ShellIcons.VectorOnlyEntryNames"/> records the vector entries so
/// callers can send those to <c>Ps5BundledIconLibrary</c> without substituting a
/// hand-drawn glyph.
/// </summary>
public enum ShellIcon
{
    /// <summary>Left shoulder button (L1).</summary>
    L1,

    /// <summary>Right shoulder button (R1).</summary>
    R1,

    /// <summary>Left trigger (L2).</summary>
    L2,

    /// <summary>Right trigger (R2).</summary>
    R2,

    /// <summary>Left stick press (L3).</summary>
    L3,

    /// <summary>Right stick press (R3).</summary>
    R3,

    /// <summary>Cross face button.</summary>
    Cross,

    /// <summary>Circle face button.</summary>
    Circle,

    /// <summary>Square face button.</summary>
    Square,

    /// <summary>Triangle face button.</summary>
    Triangle,

    /// <summary>OPTIONS button.</summary>
    OptionsButton,

    /// <summary>CREATE button.</summary>
    CreateButton,

    /// <summary>PS button.</summary>
    PsButton,

    /// <summary>Left stick.</summary>
    LeftStick,

    /// <summary>Right stick.</summary>
    RightStick,

    /// <summary>Gear; the shell's settings pictogram.</summary>
    Settings,

    /// <summary>The shell's "games and apps" pictogram: a pad beside app tiles.</summary>
    Library,

    /// <summary>A bare DualSense silhouette; the shell's "game" pictogram.</summary>
    Controller,

    /// <summary>Storage volume.</summary>
    Storage,

    /// <summary>System / console.</summary>
    System,

    /// <summary>
    /// The art a tile shows when a title has none of its own. This is the
    /// console's own: the home bundle hands its Image
    /// <c>fallbackSource: { uri: "cxml://CommonAssets/iconid_texture_app_fallback" }</c>,
    /// rather than a glyph of ours standing in for one.
    /// </summary>
    AppFallback,

    /// <summary>Magnifier. Vector-only in the shell; no bitmap ships.</summary>
    Search,

    /// <summary>Play triangle. Vector-only in the shell; no bitmap ships.</summary>
    Launch,

    /// <summary>Folder. Vector-only in the shell; no bitmap ships.</summary>
    Folder,

    /// <summary>Duplicate/copy. Vector-only in the shell; no bitmap ships.</summary>
    Copy,

    /// <summary>Delete. Vector-only in the shell; no bitmap ships.</summary>
    Remove,

    /// <summary>Add to a folder. Vector-only in the shell; no bitmap ships.</summary>
    AddFolder,

    /// <summary>Refresh / re-read. Vector-only in the shell; no bitmap ships.</summary>
    Rescan,
}

/// <summary>
/// Serves the bounded set of shell bitmap art bundled with the application.
/// The packaged PNG subset is decoded to Avalonia bitmaps and cached. The first
/// <see cref="Preload"/> kicks the extraction off on a background thread and
/// returns immediately; <see cref="Loaded"/> fires when it finishes so a host can
/// swap its glyphs for the real art. Nothing here throws and nothing blocks the
/// UI thread.
///
/// This loader serves non-<c>iconid_*</c> PNG entries, including keyguides and
/// inline emoji. SVG and <c>iconid_*</c> PNG nodes are rendered directly by
/// <c>Ps5BundledIconLibrary</c>; their ids are exposed through
/// <see cref="TryGetRcoIconId"/>.
/// </summary>
public static class ShellIcons
{
    // The container entries backing each icon. Keyguide glyphs are the shell's
    // own footer button art; the emoji_* entries are the pictograms it inlines
    // into text runs, and are the only bitmap form of those symbols in the package.
    private static readonly IReadOnlyDictionary<ShellIcon, string> Names =
        new Dictionary<ShellIcon, string>
        {
            [ShellIcon.L1] = "image_keyguide_l1",
            [ShellIcon.R1] = "image_keyguide_r1",
            [ShellIcon.L2] = "image_keyguide_l2",
            [ShellIcon.R2] = "image_keyguide_r2",
            [ShellIcon.L3] = "image_keyguide_l3",
            [ShellIcon.R3] = "image_keyguide_r3",
            [ShellIcon.Cross] = "image_keyguide_cross",
            [ShellIcon.Circle] = "image_keyguide_circle",
            [ShellIcon.Square] = "image_keyguide_square",
            [ShellIcon.Triangle] = "image_keyguide_triangle",
            [ShellIcon.OptionsButton] = "image_keyguide_options",
            [ShellIcon.CreateButton] = "image_keyguide_create",
            [ShellIcon.PsButton] = "image_keyguide_ps",
            [ShellIcon.LeftStick] = "image_keyguide_left_stick",
            [ShellIcon.RightStick] = "image_keyguide_right_stick",
            [ShellIcon.Settings] = "emoji_settings",
            [ShellIcon.Library] = "emoji_game_and_apps",
            [ShellIcon.Controller] = "emoji_game",
            [ShellIcon.Storage] = "emoji_storage",
            [ShellIcon.System] = "emoji_system",

            // Not an emoji or a keyguide glyph: this is the 512 square texture
            // the shell itself uses for a title with no art, and it lives in
            // the same PUI_UI3 container as the rest.
            [ShellIcon.AppFallback] = "iconid_texture_app_fallback",
        };

    // Icons the shell ships as SVG. These are drawn as vectors by
    // Ps5BundledIconLibrary; keeping their exact ids here lets shell surfaces make
    // that choice without carrying their own parallel lookup tables.
    private static readonly IReadOnlyDictionary<ShellIcon, string> VectorNames =
        new Dictionary<ShellIcon, string>
        {
            [ShellIcon.Settings] = "iconid_settings",
            [ShellIcon.Controller] = "iconid_game",
            [ShellIcon.Storage] = "iconid_storage",
            [ShellIcon.System] = "iconid_system",
            [ShellIcon.Search] = "iconid_search",
            [ShellIcon.Launch] = "iconid_control_play",
            [ShellIcon.Folder] = "iconid_folder",
            [ShellIcon.Copy] = "iconid_copy",
            [ShellIcon.Remove] = "iconid_delete",
            [ShellIcon.AddFolder] = "iconid_add_folder",
            [ShellIcon.Rescan] = "iconid_update",
        };

    private static readonly object Gate = new();
    private static readonly Dictionary<ShellIcon, Bitmap> Decoded = new();

    private static IReadOnlyDictionary<ShellIcon, byte[]>? _payloads;
    private static bool _loadStarted;

    /// <summary>
    /// Raised once, on a background thread, after a load finishes — whether or
    /// not it found anything. A host subscribes to swap its fallback glyphs for
    /// the real art, and must marshal to the UI thread itself.
    /// </summary>
    public static event EventHandler? Loaded;

    /// <summary>The icon to container-entry-name mapping for the icons that have bitmap art.</summary>
    public static IReadOnlyDictionary<ShellIcon, string> EntryNames => Names;

    /// <summary>
    /// The icons the shell ships in vector form, mapped to their UI3 entries.
    /// These are rendered through <c>Ps5BundledIconLibrary</c>, not <see cref="TryGet"/>.
    /// </summary>
    public static IReadOnlyDictionary<ShellIcon, string> VectorOnlyEntryNames => VectorNames;

    /// <summary>
    /// Returns the UI3 <c>iconid_*</c> node for an icon that should be rendered
    /// by <c>Ps5BundledIconLibrary</c>. This includes the shell's raster app fallback,
    /// which also lives in the <c>iconid_*</c> namespace.
    /// </summary>
    public static string? TryGetRcoIconId(ShellIcon icon)
    {
        if (VectorNames.TryGetValue(icon, out var vectorId))
        {
            return vectorId;
        }

        return Names.TryGetValue(icon, out var bitmapId) &&
               bitmapId.StartsWith("iconid_", StringComparison.Ordinal)
            ? bitmapId
            : null;
    }

    /// <summary>True once the icons have been extracted (or found to be unavailable).</summary>
    public static bool IsLoaded => Volatile.Read(ref _payloads) is not null;

    /// <summary>Number of icons decoded from the bundled package.</summary>
    public static int LoadedCount => Volatile.Read(ref _payloads)?.Count ?? 0;

    /// <summary>True once a load has been kicked off and not since <see cref="Reset"/>.</summary>
    internal static bool LoadStarted
    {
        get
        {
            lock (Gate)
            {
                return _loadStarted;
            }
        }
    }

    /// <summary>True when all packaged bitmap art is available.</summary>
    public static bool IsAvailable() =>
        Names.All(pair => ResolvePackagedPath(pair.Key, pair.Value) is not null);

    /// <summary>
    /// Starts extracting and decoding the icons on a background thread if that
    /// has not happened yet. Returns immediately and is safe to call repeatedly.
    /// <see cref="Loaded"/> fires when the work is done.
    /// </summary>
    public static void Preload()
    {
        lock (Gate)
        {
            if (_loadStarted)
            {
                return;
            }

            _loadStarted = true;
        }

        _ = Task.Run(() =>
        {
            var payloads = LoadPackagedPayloads();

            // Decoding here keeps the first paint off the PNG decoder. It needs
            // a live rendering backend, so a very early preload can fail; the
            // payloads stay cached and TryGet retries lazily.
            foreach (var pair in payloads)
            {
                TryDecode(pair.Key, pair.Value);
            }

            Volatile.Write(ref _payloads, payloads);

            try
            {
                Loaded?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception)
            {
                // A host that throws while refreshing does not take the loader
                // down with it.
            }
        });
    }

    /// <summary>
    /// The decoded art for an icon, or null when it has no bitmap entry or the
    /// load has not finished. Never blocks on the
    /// load and never throws, so a caller can ask on every layout pass and fall
    /// back to its own glyph whenever the answer is null.
    /// </summary>
    /// <param name="icon">Which icon to fetch.</param>
    public static IImage? TryGet(ShellIcon icon)
    {
        lock (Gate)
        {
            if (Decoded.TryGetValue(icon, out var cached))
            {
                return cached;
            }
        }

        var payloads = Volatile.Read(ref _payloads);
        return payloads is not null && payloads.TryGetValue(icon, out var bytes)
            ? TryDecode(icon, bytes)
            : null;
    }

    /// <summary>
    /// Drops the extracted payloads, the decoded bitmaps and the "already
    /// loaded" latch so the next <see cref="Preload"/> reloads the package.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _loadStarted = false;
            foreach (var bitmap in Decoded.Values)
            {
                try
                {
                    bitmap.Dispose();
                }
                catch (Exception)
                {
                    // A bitmap still referenced by a live visual is left alone.
                }
            }

            Decoded.Clear();
        }

        Volatile.Write(ref _payloads, null);
    }

    internal static IReadOnlyDictionary<ShellIcon, byte[]> LoadPackagedPayloads()
    {
        var payloads = new Dictionary<ShellIcon, byte[]>();
        foreach (var (icon, name) in Names)
        {
            var path = ResolvePackagedPath(icon, name);
            if (path is null)
            {
                continue;
            }

            try
            {
                var bytes = LooksLikePng(File.ReadAllBytes(path));
                if (bytes is not null)
                {
                    payloads[icon] = bytes;
                }
            }
            catch (Exception)
            {
                // A partial package remains fail-visible through caller glyphs.
            }
        }

        return payloads;
    }

    private static string? ResolvePackagedPath(ShellIcon icon, string name) =>
        icon == ShellIcon.AppFallback
            ? BigPicturePackage.Resolve("3.00/textures/tex_default_game.png")
            : BigPicturePackage.Resolve($"12.40/ui3-raster/{name}.png");

    /// <summary>
    /// Returns <paramref name="payload"/> when it starts with the PNG signature,
    /// else null. The container also carries SVG, DDS and audio payloads under
    /// entry names that look alike, and only PNG is decodable here.
    /// </summary>
    internal static byte[]? LooksLikePng(byte[]? payload)
    {
        if (payload is null || payload.Length < 8)
        {
            return null;
        }

        return payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47 &&
               payload[4] == 0x0D && payload[5] == 0x0A && payload[6] == 0x1A && payload[7] == 0x0A
            ? payload
            : null;
    }

    private static Bitmap? TryDecode(ShellIcon icon, byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            var bitmap = new Bitmap(stream);

            lock (Gate)
            {
                if (Decoded.TryGetValue(icon, out var existing))
                {
                    bitmap.Dispose();
                    return existing;
                }

                Decoded[icon] = bitmap;
            }

            return bitmap;
        }
        catch (Exception)
        {
            // No rendering backend yet, or a payload the decoder rejects: the
            // caller falls back to its own glyph and a later call may succeed.
            return null;
        }
    }
}
