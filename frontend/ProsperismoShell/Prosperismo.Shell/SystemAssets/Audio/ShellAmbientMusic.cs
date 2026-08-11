// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// bundled audio package, looping under the library the way the console loops it under its own
/// home screen.
///
/// The bed is one voice in <see cref="UiSoundPlayer"/>'s mixer, not a second
/// audio stack, so navigation blips layer over it instead of cutting it. It
/// wraps at the loop points the file itself declares (see <see cref="At9Music"/>),
///
/// Four things decide whether it is audible, and all of them are just a gain:
/// <list type="bullet">
/// <item><see cref="IsEnabled"/> is its own preference, separate from the
/// navigation blips, because wanting clicks and wanting music are different
/// wants;</item>
/// <item><see cref="Volume"/> is the single persisted level;</item>
/// <item>a game's own snd0.at9 preview ducks it via
/// <see cref="SetTitleMusicActive"/>, so the two never play over each other;</item>
/// <item>launching a game or minimising the window silences it via
/// <see cref="SetSuspended"/>.</item>
/// </list>
///
/// Everything degrades silently. With a partial package the bed never loads,
/// nothing is logged, and every call here is a cheap no-op. The decode happens
/// on a background task, so it never delays startup.
/// </summary>
public static class ShellAmbientMusic
{
    /// <summary>
    /// Default level for the bed. It sits under the interaction cues rather
    /// than competing with them, matching how quietly the console mixes it.
    /// </summary>
    public const double DefaultVolume = 0.65;

    /// <summary>
    /// What the bed is multiplied by while a game's own title music plays.
    /// Zero by default: two pieces of music at once is worse than either, so
    /// the bed steps aside completely and fades back when the preview stops.
    /// Raise it to keep the bed audible underneath.
    /// </summary>
    public const float DefaultDuckLevel = 0f;

    private static readonly object Gate = new();

    private static bool _enabled = true;
    private static double _volume = DefaultVolume;
    private static float _duckLevel = DefaultDuckLevel;
    private static bool _titleMusicActive;
    private static bool _suspended;
    private static bool _sonyPresentationActive = true;
    private static bool _loadStarted;
    private static bool _started;
    private static int _loadGeneration;
    private static CancellationTokenSource? _loadCancellation;
    private static MusicClip? _clip;

    /// <summary>
    /// Whether the ambient bed may sound at all. Its own preference, separate
    /// from the navigation cues. Turning it off fades the bed out but keeps the
    /// decoded audio, so turning it back on is instant.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }

        set
        {
            lock (Gate)
            {
                _enabled = value;
                ApplyGainLocked();
            }
        }
    }

    /// <summary>Persisted ambient level, 0 (silent) to 1. Values outside the range are clamped.</summary>
    public static double Volume
    {
        get
        {
            lock (Gate)
            {
                return _volume;
            }
        }

        set
        {
            lock (Gate)
            {
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : DefaultVolume;
                ApplyGainLocked();
            }
        }
    }

    /// <summary>
    /// Multiplier applied while a title preview is playing. See
    /// <see cref="DefaultDuckLevel"/>.
    /// </summary>
    public static float DuckLevel
    {
        get
        {
            lock (Gate)
            {
                return _duckLevel;
            }
        }

        set
        {
            lock (Gate)
            {
                _duckLevel = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : DefaultDuckLevel;
                ApplyGainLocked();
            }
        }
    }

    /// <summary>True while a game's own snd0.at9 preview is playing.</summary>
    public static bool IsTitleMusicActive
    {
        get
        {
            lock (Gate)
            {
                return _titleMusicActive;
            }
        }
    }

    /// <summary>True while the bed is held silent for a running game or a minimised window.</summary>
    public static bool IsSuspended
    {
        get
        {
            lock (Gate)
            {
                return _suspended;
            }
        }
    }

    /// <summary>
    /// launcher can still use the shared title-preview component, but it cannot
    /// </summary>
    public static bool IsSonyPresentationActive
    {
        get
        {
            lock (Gate)
            {
                return _sonyPresentationActive;
            }
        }
    }

    public static void SetSonyPresentationActive(bool active)
    {
        lock (Gate)
        {
            _sonyPresentationActive = active;
            if (!active)
            {
                _started = false;
                _loadCancellation?.Cancel();
                ++_loadGeneration;
                UiSoundPlayer.ClearMusic(MusicVoiceKind.Ambient);
            }
        }
    }

    /// <summary>True once the bed has been decoded and handed to the mixer.</summary>
    public static bool IsLoaded
    {
        get
        {
            lock (Gate)
            {
                return _clip is not null;
            }
        }
    }

    /// <summary>The current effective gain, after enable, volume, duck and suspend.</summary>
    public static float CurrentGain => ComputeGain();

    /// <summary>True when the bundled home-music track is available.</summary>
    public static bool IsAvailable() =>
        ShellAudio.GetTrackPath(ShellAudioTrack.HomeBgm) is not null;

    /// <summary>
    /// Ducks the bed under a game's own title music, or lifts it again. The two
    /// are never both at full level, which is what stops the home screen playing
    /// two pieces of music at once.
    /// </summary>
    /// <param name="active">True while a snd0.at9 preview is playing.</param>
    public static void SetTitleMusicActive(bool active)
    {
        lock (Gate)
        {
            if (_titleMusicActive == active)
            {
                return;
            }

            _titleMusicActive = active;
            ApplyGainLocked();
        }
    }

    /// <summary>
    /// Silences the bed while a game runs or the window is minimised, and
    /// restores it afterwards. The bed keeps its place, so it resumes rather
    /// than restarting.
    /// </summary>
    /// <param name="suspended">True to hold the bed silent.</param>
    public static void SetSuspended(bool suspended)
    {
        lock (Gate)
        {
            if (_suspended == suspended)
            {
                return;
            }

            _suspended = suspended;
            ApplyGainLocked();
        }
    }

    /// <summary>
    /// Starts the bed. Returns immediately: the first call kicks the decode off
    /// on a background task and later calls are cheap. Safe to call whenever the
    /// home screen becomes visible, and a no-op when the track is unavailable.
    /// </summary>
    public static void Start()
    {
        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        MusicClip? ready;
        int loadGeneration;
        lock (Gate)
        {
            if (!_sonyPresentationActive)
            {
                return;
            }

            _started = true;
            ready = _clip;

            if (ready is not null)
            {
                UiSoundPlayer.SetMusic(MusicVoiceKind.Ambient, ready, ComputeGainLocked());
                return;
            }

            if (_loadStarted)
            {
                return; // a decode is already in flight
            }

            _loadStarted = true;
            loadGeneration = ++_loadGeneration;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();
        }

        _ = Task.Run(() =>
        {
            var path = ShellAudio.GetTrackPath(ShellAudioTrack.HomeBgm);
            CancellationToken cancellation;
            lock (Gate)
            {
                cancellation = _loadCancellation?.Token ?? CancellationToken.None;
            }

            var clip = At9Music.TryDecode(
                path,
                ShellAudio.MakeupGain,
                forLooping: true,
                cancellation: cancellation);

            lock (Gate)
            {
                if (loadGeneration != _loadGeneration)
                {
                    return;
                }

                _loadStarted = false;
                if (_loadCancellation?.Token == cancellation)
                {
                    _loadCancellation.Dispose();
                    _loadCancellation = null;
                }
                _clip = clip;
                if (clip is not null && _started)
                {
                    // Keep the ambient state lock through installation. A
                    // title transition cannot change the duck decision between
                    // computing the gain and replacing the ambient slot.
                    UiSoundPlayer.SetMusic(MusicVoiceKind.Ambient, clip, ComputeGainLocked());
                }
            }
        });
    }

    /// <summary>
    /// Stops the bed. The decoded audio stays cached, so returning to the home
    /// screen starts it again without another decode.
    /// </summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _started = false;
            _loadStarted = false;
            ++_loadGeneration;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        }

        UiSoundPlayer.ClearMusic(MusicVoiceKind.Ambient);
    }

    /// <summary>
    /// Drops the cached audio and every latch, so the next <see cref="Start"/>
    /// reloads the packaged track.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _clip = null;
            _loadStarted = false;
            _started = false;
            ++_loadGeneration;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
            _titleMusicActive = false;
            _suspended = false;
            _sonyPresentationActive = true;
        }

        UiSoundPlayer.ClearMusic();
    }

    /// <summary>
    /// The whole audibility rule in one place, as a pure function of the four
    /// inputs, so it can be checked without an audio device.
    /// </summary>
    /// <param name="enabled">The ambient-music preference.</param>
    /// <param name="volume">The persisted level, 0 to 1.</param>
    /// <param name="titleMusicActive">True while a game's own preview plays.</param>
    /// <param name="suspended">True while a game runs or the window is minimised.</param>
    /// <param name="duckLevel">Multiplier to apply when ducking.</param>
    public static float ComputeGain(
        bool enabled, double volume, bool titleMusicActive, bool suspended, float duckLevel)
    {
        if (!enabled || suspended)
        {
            return 0f;
        }

        double level = double.IsFinite(volume) ? Math.Clamp(volume, 0.0, 1.0) : 0.0;
        if (titleMusicActive)
        {
            level *= float.IsFinite(duckLevel) ? Math.Clamp(duckLevel, 0f, 1f) : 0f;
        }

        return (float)level;
    }

    private static float ComputeGain()
    {
        lock (Gate)
        {
            return ComputeGain(_enabled, _volume, _titleMusicActive, _suspended, _duckLevel);
        }
    }

    private static float ComputeGainLocked() =>
        ComputeGain(_enabled, _volume, _titleMusicActive, _suspended, _duckLevel);

    private static void ApplyGainLocked()
    {
        UiSoundPlayer.SetMusicGain(
            MusicVoiceKind.Ambient,
            _started ? ComputeGainLocked() : 0f);
    }
}
