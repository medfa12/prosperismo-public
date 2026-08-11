// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Audio;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// Decides whether the first-launch boot sequence runs, and finds the one asset it
/// can still use.
///
/// there is no movie to find any more and the sequence cannot be missing. The
/// sound is the committed vsh_asset cue with an explicit-file override, so a
/// missing asset simply means the sequence plays silently. Nothing in this
/// class throws, logs, or searches outside those bounded locations.
/// </summary>
public static class BootIntroPolicy
{
    /// <summary>Environment variable naming the boot sound (file or directory).</summary>
    public const string AudioEnvironmentVariable = "PROSPERISMO_BOOT_INTRO_AUDIO";

    /// <summary>Subdirectory probed under each search base before the base itself.</summary>
    public const string AssetSubdirectory = "boot";

    /// <summary>
    /// The initial-boot picture has its own cue. Do not silently substitute the
    /// </summary>
    public static readonly IReadOnlyList<string> AudioFileNames =
    [
        "sfx_initialboot.at9",
    ];

    /// <summary>True when the intro is due on this launch: enabled and not yet seen.</summary>
    public static bool ShouldPlay(GuiSettings? settings) =>
        settings is not null && settings.PlayBootIntro && !settings.HasPlayedBootIntro;

    /// <summary>
    /// What a settings toggle shows: "the intro will run next launch". Same
    /// condition as <see cref="ShouldPlay"/>, named for the UI that binds to it.
    /// </summary>
    public static bool IsArmed(GuiSettings? settings) => ShouldPlay(settings);

    /// <summary>
    /// Arms or disarms the intro from a settings toggle. Arming clears the
    /// once-only latch so it runs again on the next launch. Does not persist; the
    /// settings screen saves once for the whole page.
    ///
    /// A request that matches the current state does nothing at all. That matters
    /// because the settings screen pushes loaded values into its toggles, which
    /// raises the same change event a user click does: without this, loading a
    /// profile that has already seen the intro would read back as "turn it off"
    /// and disable the feature for good.
    /// </summary>
    public static void SetArmed(GuiSettings? settings, bool armed)
    {
        if (settings is null || IsArmed(settings) == armed)
        {
            return;
        }

        settings.PlayBootIntro = armed;
        if (armed)
        {
            settings.HasPlayedBootIntro = false;
        }
    }

    /// <summary>
    /// Latches the intro as seen and persists it. Called when playback starts, not
    /// when it ends, so a crash or a kill during the movie cannot make it repeat
    /// on every launch.
    /// </summary>
    public static void MarkPlayed(GuiSettings? settings)
    {
        if (settings is null || settings.HasPlayedBootIntro)
        {
            return;
        }

        settings.HasPlayedBootIntro = true;
        settings.Save();
    }

    /// <summary>Absolute path to the boot sound, or null when there is none.</summary>
    /// <param name="settings">Settings carrying the configured override, if any.</param>
    /// <returns>The sound's path, or null when the sequence plays silently.</returns>
    public static string? ResolveAudioPath(GuiSettings? settings = null)
    {
        // A user-selected file remains an explicit override. The default is
        // directory or the ignored oracle just because the current directory
        // happens to contain a similarly named file.
        var configured = ResolveAsset(
            settings?.BootIntroAudioPath,
            SafeGetEnvironment(AudioEnvironmentVariable),
            Array.Empty<string?>(),
            AudioFileNames);

        return configured ?? ShellAudio.GetTrackPath(ShellAudioTrack.InitialBootChime);
    }

    /// <summary>
    /// The bounded development search bases retained for direct policy tests.
    /// Runtime resolution uses the committed package through
    /// <see cref="ShellAudio.GetTrackPath"/>.
    /// </summary>
    internal static IReadOnlyList<string?> DefaultSearchDirectories()
    {
        return [AppContext.BaseDirectory];
    }

    /// <summary>
    /// Testable core. <paramref name="configured"/> and
    /// <paramref name="environmentValue"/> win in that order and may name either a
    /// file or a directory to search; after them each of
    /// <paramref name="searchDirectories"/> is tried, its <c>boot</c>
    /// subdirectory first. Returns the first existing file, or null.
    /// </summary>
    internal static string? ResolveAsset(
        string? configured,
        string? environmentValue,
        IEnumerable<string?> searchDirectories,
        IReadOnlyList<string> fileNames)
    {
        foreach (var candidate in new[] { configured, environmentValue })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (SafeFullPath(candidate) is not { } full)
            {
                continue;
            }

            if (SafeFileExists(full))
            {
                return full;
            }

            if (SafeDirectoryExists(full) && FindIn(full, fileNames) is { } fromDirectory)
            {
                return fromDirectory;
            }
        }

        foreach (var directory in searchDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory) || SafeFullPath(directory) is not { } root)
            {
                continue;
            }

            if (FindIn(Path.Combine(root, AssetSubdirectory), fileNames) is { } nested)
            {
                return nested;
            }

            if (FindIn(root, fileNames) is { } direct)
            {
                return direct;
            }
        }

        return null;
    }

    private static string? FindIn(string directory, IReadOnlyList<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var candidate = Path.Combine(directory, fileName);
            if (SafeFileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? SafeFullPath(string candidate)
    {
        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception)
        {
            return null; // a malformed override is just a miss
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeGetEnvironment(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (Exception)
        {
            return null;
        }
    }

}
