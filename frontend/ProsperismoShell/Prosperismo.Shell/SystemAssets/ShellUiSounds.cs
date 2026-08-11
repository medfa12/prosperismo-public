// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets.Audio;

namespace Prosperismo.GUI.SystemAssets;

/// <summary>
/// The system-shell interaction cues, named after the events the shell's own
/// compiled soundscript binds them to (see docs/ps5-shell-motion.md). Each value
/// maps to one <c>snd_*</c> entry in the shell's UI resource container; the
/// mapping is <see cref="ShellUiSounds.EntryNames"/>.
/// </summary>
public enum UiSoundEvent
{
    /// <summary>Focus moved to another item in a list, grid or tile row.</summary>
    FocusMove,

    /// <summary>An item was confirmed / activated.</summary>
    Enter,

    /// <summary>A screen or dialog was backed out of.</summary>
    Cancel,

    /// <summary>An options / context menu opened.</summary>
    OpenOptionMenu,

    /// <summary>An options / context menu closed.</summary>
    CloseOptionMenu,

    /// <summary>A rejected input.</summary>
    Error,

    /// <summary>An informative notification appeared.</summary>
    InformativeToast,

    /// <summary>An interactive notification appeared.</summary>
    InteractiveToast,

    /// <summary>A toggle switched on.</summary>
    SwitchOn,

    /// <summary>A toggle switched off.</summary>
    SwitchOff,

    /// <summary>Focus crossed into a different panel.</summary>
    ChangePanel,

    /// <summary>The shell moved between spaces / hubs.</summary>
    ChangeSpace,

    /// <summary>A modal dialog opened.</summary>
    OpenDialog,

    /// <summary>An error dialog opened.</summary>
    OpenErrorDialog,

    /// <summary>The affirmative button in a dialog was chosen.</summary>
    YesInDialog,

    /// <summary>The negative button in a dialog was chosen.</summary>
    NoInDialog,

    /// <summary>A neutral dialog action was chosen.</summary>
    NeutralInDialog,

    /// <summary>The home screen opened.</summary>
    OpenHome,

    /// <summary>The control centre opened.</summary>
    OpenControlCenter,

    /// <summary>The control centre closed.</summary>
    CloseControlCenter,

    /// <summary>A character was typed.</summary>
    TextInput,

    /// <summary>A character was deleted.</summary>
    Backspace,

    /// <summary>The on-screen keyboard opened.</summary>
    OpenOnScreenKeyboard,

    /// <summary>A slider crossed a level-meter step.</summary>
    SliderLevelMeter,

    /// <summary>A screenshot was captured.</summary>
    TakeScreenshot,

    /// <summary>A trophy notification appeared.</summary>
    TrophyToast,
}

/// <summary>
/// Plays the shell interaction cues from the committed PCM derivative package.
///
/// The cues live inside <c>filesystems/system_ex/vsh_asset/Sce.PlayStation.PUI_UI3.rco</c>
/// as VAG streams named after the soundscript events that trigger them
/// (<c>snd_focus_move</c>, <c>snd_enter</c>, ...). The packaged WAV files are
/// decoded and cached before being handed to
/// <see cref="UiSoundPlayer"/>, which lets blips overlap each other and the
/// background music.
///
/// <see cref="LoadClips"/> for recovery tests and tooling, but normal product
/// startup never needs or searches for one. The first <see cref="Play"/> kicks off the
/// load on a background thread and returns immediately, so the cue that started
/// the load is itself dropped; every later one sounds. Nothing here throws and
/// nothing blocks the UI thread.
/// </summary>
public static class ShellUiSounds
{
    /// <summary>
    /// Fixed gain applied on top of <see cref="Volume"/>. The cues are mastered
    /// very quietly inside the container (the loudest peaks around -15 dBFS);
    /// the shell's own mixer makes that up downstream, so the player does too
    /// rather than normalising each cue, which would flatten their intended
    /// relative loudness.
    /// </summary>
    public const float MakeupGain = 4.0f;

    private static readonly IReadOnlyDictionary<UiSoundEvent, string> Names =
        new Dictionary<UiSoundEvent, string>
        {
            [UiSoundEvent.FocusMove] = "snd_focus_move",
            [UiSoundEvent.Enter] = "snd_enter",
            [UiSoundEvent.Cancel] = "snd_cancel",
            [UiSoundEvent.OpenOptionMenu] = "snd_open_option_menu",
            [UiSoundEvent.CloseOptionMenu] = "snd_close_option_menu",
            [UiSoundEvent.Error] = "snd_error",
            [UiSoundEvent.InformativeToast] = "snd_informative_toasts_something_to_read",
            [UiSoundEvent.InteractiveToast] = "snd_interactive_toasts_something_to_do",
            [UiSoundEvent.SwitchOn] = "snd_switch_on",
            [UiSoundEvent.SwitchOff] = "snd_switch_off",
            [UiSoundEvent.ChangePanel] = "snd_change_panel",
            [UiSoundEvent.ChangeSpace] = "snd_change_space",
            [UiSoundEvent.OpenDialog] = "snd_open_dialog",
            [UiSoundEvent.OpenErrorDialog] = "snd_open_error_dialog",
            [UiSoundEvent.YesInDialog] = "snd_yes_in_dialog",
            [UiSoundEvent.NoInDialog] = "snd_no_in_dialog",
            [UiSoundEvent.NeutralInDialog] = "snd_neutral_in_dialog",
            [UiSoundEvent.OpenHome] = "snd_open_home",
            [UiSoundEvent.OpenControlCenter] = "snd_open_control_center",
            [UiSoundEvent.CloseControlCenter] = "snd_close_control_center",
            [UiSoundEvent.TextInput] = "snd_text_input",
            [UiSoundEvent.Backspace] = "snd_backspace",
            [UiSoundEvent.OpenOnScreenKeyboard] = "snd_open_osk",
            [UiSoundEvent.SliderLevelMeter] = "snd_slider_level_meter",
            [UiSoundEvent.TakeScreenshot] = "snd_take_screenshot",
            [UiSoundEvent.TrophyToast] = "snd_trophy_toast",
        };

    private static readonly object Gate = new();
    private static IReadOnlyDictionary<UiSoundEvent, UiSoundClip>? _clips;
    private static bool _loadStarted;
    private static double _volume = 1.0;

    /// <summary>
    /// Global mute for the shell UI cues; a settings toggle can bind straight to
    /// this. Default true. Turning it off does not discard the cache, so turning
    /// it back on is instant.
    /// </summary>
    public static bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Playback volume, 0 (silent) to 2 (double). Applied on top of
    /// <see cref="MakeupGain"/>. Values outside the range are clamped.
    /// </summary>
    public static double Volume
    {
        get => _volume;
        set => _volume = double.IsFinite(value) ? Math.Clamp(value, 0.0, 2.0) : 1.0;
    }

    /// <summary>The event to container-entry-name mapping, keyed by event.</summary>
    public static IReadOnlyDictionary<UiSoundEvent, string> EntryNames => Names;

    /// <summary>True once the cues have been extracted and decoded (or found to be unavailable).</summary>
    public static bool IsLoaded => Volatile.Read(ref _clips) is not null;

    /// <summary>Number of cues decoded from the bundled package.</summary>
    public static int LoadedCount => Volatile.Read(ref _clips)?.Count ?? 0;

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

    /// <summary>True when packaged cues or an explicitly selected container are available.</summary>
    public static bool IsAvailable() =>
        Names.Values.All(name => BigPicturePackage.Resolve($"12.40/ui-sounds/{name}.wav") is not null);

    /// <summary>
    /// Starts extracting and decoding the cues on a background thread if that
    /// has not happened yet. Returns immediately and is safe to call repeatedly;
    /// calling it early (for example when the window loads) means the first
    /// focus move already has sound.
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
            var clips = LoadPackagedClips();
            Volatile.Write(ref _clips, clips);

            // Opening the output device costs a few hundred milliseconds; do it
            // now so the first focus move is not late.
            if (clips.Count > 0)
            {
                UiSoundPlayer.Warm();
            }
        });
    }

    /// <summary>
    /// Plays a UI cue. Does nothing when the cues are muted, unavailable, or not
    /// finished loading. Never blocks and never throws; repeated triggers layer
    /// rather than cutting each other off.
    /// </summary>
    /// <param name="soundEvent">Which interaction cue to play.</param>
    public static void Play(UiSoundEvent soundEvent)
    {
        if (!IsEnabled || !UiSoundPlayer.IsSupported)
        {
            return;
        }

        Preload();

        var clips = Volatile.Read(ref _clips);
        if (clips is not null && clips.TryGetValue(soundEvent, out var clip))
        {
            UiSoundPlayer.Play(clip, (float)Volume * MakeupGain);
        }
    }

    /// <summary>Silences any cues still sounding.</summary>
    public static void StopAll() => UiSoundPlayer.StopAll();

    /// <summary>
    /// Drops the decoded cache and the "already loaded" latch so the next
    /// <see cref="Play"/> or <see cref="Preload"/> reloads the bundled cues.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _loadStarted = false;
        }

        Volatile.Write(ref _clips, null);
        UiSoundPlayer.StopAll();
    }

    internal static IReadOnlyDictionary<UiSoundEvent, UiSoundClip> LoadPackagedClips()
    {
        var clips = new Dictionary<UiSoundEvent, UiSoundClip>();
        foreach (var (soundEvent, name) in Names)
        {
            var path = BigPicturePackage.Resolve($"12.40/ui-sounds/{name}.wav");
            var decoded = ShellAudioDecoder.TryDecode(path, gain: 1f, forLooping: false);
            if (decoded is not null)
            {
                clips[soundEvent] = new UiSoundClip(decoded.Samples, name);
            }
        }

        return clips;
    }
}
