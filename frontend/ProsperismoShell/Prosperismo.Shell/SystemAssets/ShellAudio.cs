// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Audio;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>
/// The system-shell audio tracks under filesystems/system_ex/vsh_asset/ in a
/// </summary>
public enum ShellAudioTrack
{
    /// <summary>sfx_coldboot.at9 — the cold-boot chime.</summary>
    BootChime,

    /// <summary>sfx_warmboot.at9 — the resume-from-rest chime.</summary>
    WarmBootChime,

    /// <summary>bgm_home.at9 — the home-screen background music.</summary>
    HomeBgm,

    /// <summary>bgm_onboarding.at9 — the first-boot onboarding music.</summary>
    OnboardingBgm,

    /// <summary>sfx_initialboot.at9 — the distinct initial-boot cue.</summary>
    InitialBootChime,

    /// <summary>sfx_transition.at9 — the boot-to-shell transition cue.</summary>
    TransitionChime,
}

/// <summary>
/// Playback of the PS5 system-shell audio from the committed PCM package, or
/// reader for packaged tracks.
///
/// Playback goes through <see cref="UiSoundPlayer"/>'s mixer rather than
/// winmm's PlaySound, which allows only one sound per process: on PlaySound the
/// boot chime, the home bed and a game's snd0.at9 preview would each cut the
/// others off, and a bed that can be cut cannot be ducked. On the mixer they
/// layer, and the looping bed carries a gain that
/// <see cref="ShellAmbientMusic"/> drives. It uses the platform mixer,
/// including the macOS CoreAudio queue.
///
/// Everything degrades gracefully: when a track is absent the play hooks do
/// nothing, silently. The committed package is provenance-pinned in
/// <c>assets/big-picture/3.00/manifest.json</c>.
/// </summary>
public static class ShellAudio
{
    /// <summary>
    /// Fixed gain applied when a shell track is decoded, for the same reason
    /// <see cref="ShellUiSounds.MakeupGain"/> exists: the vsh_asset audio is
    /// mastered far below full scale and the console's own mixer makes it up
    /// downstream. Measured across the four tracks after the fold-down to
    /// stereo, the peaks sit between roughly -25 and -30 dBFS (972 to 1862 of
    /// 32767), so eight brings the loudest of them to about -7 dBFS and still
    /// leaves every one of them clear of clipping.
    /// </summary>
    public const float MakeupGain = 8.0f;

    private static readonly object Sync = new();
    private static int _generation;
    private static CancellationTokenSource? _decodeCancellation;

    public static bool IsAvailable()
    {
        foreach (var track in Enum.GetValues<ShellAudioTrack>())
        {
            if (GetTrackPath(track) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Absolute path to a shell audio track inside the package or override, or
    /// null when the file is absent.
    /// </summary>
    /// <param name="track">Which vsh_asset track to resolve.</param>
    public static string? GetTrackPath(ShellAudioTrack track)
    {
        return BigPicturePackage.Resolve($"3.00/audio/{TrackFileName(track)}");
    }

    /// <summary>
    /// Returns a packaged PCM-WAVE file directly, or decodes an explicit
    /// ATRAC9 file to a PCM16 WAV image.
    /// </summary>
    public static byte[]? TryDecodeToWav(string? at9Path)
    {
        if (string.IsNullOrEmpty(at9Path))
        {
            return null;
        }

        try
        {
            if (Path.GetExtension(at9Path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadAllBytes(at9Path);
            }

            return SndPreviewPlayer.DecodeAt9ToWav(File.ReadAllBytes(at9Path));
        }
        catch (Exception)
        {
            return null; // absent, corrupt, or unsupported: stay silent
        }
    }

    /// <summary>Plays the cold-boot chime once; no-op when the track is absent.</summary>
    public static void PlayBootChime()
    {
        PlayTrack(ShellAudioTrack.BootChime, loop: false);
    }

    /// <summary>Plays only the confirmed initial-boot cue.</summary>
    public static void PlayInitialBootChime() =>
        PlayTrack(ShellAudioTrack.InitialBootChime, loop: false);

    /// <summary>Plays only the confirmed warm-boot cue.</summary>
    public static void PlayWarmBootChime() =>
        PlayTrack(ShellAudioTrack.WarmBootChime, loop: false);

    /// <summary>Plays only the confirmed boot-to-shell transition cue.</summary>
    public static void PlayTransitionChime() =>
        PlayTrack(ShellAudioTrack.TransitionChime, loop: false);

    /// <summary>
    /// Starts the distinct onboarding music voice. It is cleared by
    /// <see cref="StopOnboardingMusic"/> before Home ambient starts, so the two
    /// </summary>
    public static void PlayOnboardingMusic()
    {
        PlayTrack(ShellAudioTrack.OnboardingBgm, loop: true);
    }

    /// <summary>Stops only onboarding music, leaving Home/title voices intact.</summary>
    public static void StopOnboardingMusic()
    {
        lock (Sync)
        {
            // The decode worker shares the shell transition generation. A
            // stop must invalidate it before clearing the slot, otherwise a
            // late decode could resurrect onboarding after Home owns audio.
            _generation++;
            _decodeCancellation?.Cancel();
            _decodeCancellation?.Dispose();
            _decodeCancellation = null;
        }

        UiSoundPlayer.ClearMusic(MusicVoiceKind.Onboarding);
    }

    /// <summary>
    /// Plays the home-screen background music. Looping it is the ambient bed,
    /// so that path goes through <see cref="ShellAmbientMusic"/> and obeys its
    /// enable and volume preferences. No-op when the track is absent.
    /// </summary>
    /// <param name="loop">True to loop the track like the console home screen.</param>
    public static void PlayHomeBgm(bool loop = true)
    {
        if (loop)
        {
            StopOnboardingMusic();
            ShellAmbientMusic.Start();
            return;
        }

        PlayTrack(ShellAudioTrack.HomeBgm, loop: false);
    }

    /// <summary>
    /// Decodes and plays a shell track on a background task, at full level. A
    /// looping track becomes the mixer's music bed and replaces whatever bed was
    /// there; a one-shot layers over everything already sounding.
    /// </summary>
    public static void PlayTrack(ShellAudioTrack track, bool loop)
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        var path = GetTrackPath(track);
        if (path is null)
        {
            return;
        }

        int generation;
        CancellationToken cancellation;
        lock (Sync)
        {
            generation = ++_generation;
            _decodeCancellation?.Cancel();
            _decodeCancellation?.Dispose();
            _decodeCancellation = new CancellationTokenSource();
            cancellation = _decodeCancellation.Token;
        }

        _ = Task.Run(() =>
        {
            // paths continue through ATRAC9. Decode off the caller's thread.
            var clip = ShellAudioDecoder.TryDecode(
                path,
                MakeupGain,
                forLooping: loop,
                cancellation: cancellation);
            if (clip is null)
            {
                return;
            }

            lock (Sync)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            if (loop)
            {
                var voice = track == ShellAudioTrack.OnboardingBgm
                    ? MusicVoiceKind.Onboarding
                    : MusicVoiceKind.Ambient;
                UiSoundPlayer.SetMusic(voice, clip, 1f);
            }
            else
            {
                UiSoundPlayer.Play(new UiSoundClip(clip.Samples, TrackFileName(track)));
            }
        });
    }

    /// <summary>Stops whatever shell track is playing, bed included.</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            _generation++;
            _decodeCancellation?.Cancel();
            _decodeCancellation?.Dispose();
            _decodeCancellation = null;
        }

        ShellAmbientMusic.Stop();
        UiSoundPlayer.ClearMusic();
    }

    private static string TrackFileName(ShellAudioTrack track)
    {
        return track switch
        {
            ShellAudioTrack.BootChime => "sfx_coldboot.wav",
            ShellAudioTrack.WarmBootChime => "sfx_warmboot.wav",
            ShellAudioTrack.HomeBgm => "bgm_home.wav",
            ShellAudioTrack.OnboardingBgm => "bgm_onboarding.wav",
            ShellAudioTrack.InitialBootChime => "sfx_initialboot.wav",
            ShellAudioTrack.TransitionChime => "sfx_transition.wav",
            _ => throw new ArgumentOutOfRangeException(nameof(track), track, null),
        };
    }
}
