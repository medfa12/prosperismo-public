// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Audio;

namespace Prosperismo.GUI.BootAnimation;

/// <summary>
/// The boot sequence's sound: a shell cue decoded to stereo PCM and
/// handed to <see cref="UiSoundPlayer"/>.
///
/// The mixer is used rather than winmm's PlaySound (which the title-music
/// preview uses) for two reasons the intro needs: it stops on the next 10 ms
/// buffer, so skipping silences the sound instead of letting it ring on over the
/// shell, and it takes raw PCM, so the clip can be trimmed to the movie and
/// given a fade rather than being cut off mid-note.
///
/// The cue is 5.1 at 48 kHz. It is folded to stereo here because the mixer is
/// stereo and its own conversion would simply drop the centre channel, which on
/// this cue carries most of the sound.
///
/// Windows-only, like the rest of the audio path, and a silent no-op everywhere
/// else. Nothing here throws: a missing, corrupt or unsupported file just means
/// the movie plays without sound.
/// </summary>
public static class BootIntroAudio
{
    /// <summary>Length of the ramp applied to the tail of the trimmed clip.</summary>
    public static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Decodes, folds, trims and fades a cue, ready for <see cref="Play"/>.
    /// Returns null when the path is null, the file is missing, or it is not a
    /// decodable ATRAC9 stream. Costs a few hundred milliseconds, so callers run
    /// it on a background thread.
    /// </summary>
    /// <param name="at9Path">The cue to decode, or null.</param>
    /// <param name="duration">Trim length; the movie's duration. Zero keeps the whole cue.</param>
    public static short[]? Prepare(string? at9Path, TimeSpan duration)
    {
        if (string.IsNullOrEmpty(at9Path))
        {
            return null;
        }

        // Use the same cancellation-safe shell path as Home and title music.
        // still use the original 5.1 fold-down path.
        var decoded = ShellAudioDecoder.TryDecode(
            at9Path,
            ShellAudio.MakeupGain,
            forLooping: false);
        if (decoded is null)
        {
            return null;
        }

        return TrimAndFade(decoded.Samples, duration, FadeOut);
    }

    /// <summary>
    /// Cuts interleaved stereo PCM to <paramref name="duration"/> and ramps the
    /// last <paramref name="fade"/> down to silence, so a cue longer than the
    /// movie ends with it instead of being chopped. A zero or negative duration,
    /// or one past the end of the clip, leaves the length alone and still fades.
    /// </summary>
    internal static short[] TrimAndFade(short[] samples, TimeSpan duration, TimeSpan fade)
    {
        if (samples.Length < UiSoundPlayer.MixChannels)
        {
            return samples;
        }

        int frames = samples.Length / UiSoundPlayer.MixChannels;
        int keep = frames;
        if (duration > TimeSpan.Zero)
        {
            var wanted = (long)Math.Round(duration.TotalSeconds * UiSoundPlayer.MixSampleRate);
            keep = (int)Math.Clamp(wanted, 1, frames);
        }

        var result = keep == frames
            ? samples
            : samples[..(keep * UiSoundPlayer.MixChannels)];

        int fadeFrames = fade > TimeSpan.Zero
            ? (int)Math.Min(keep, Math.Round(fade.TotalSeconds * UiSoundPlayer.MixSampleRate))
            : 0;
        if (fadeFrames <= 1)
        {
            return result;
        }

        int start = keep - fadeFrames;
        for (int frame = start; frame < keep; frame++)
        {
            double gain = (double)(keep - 1 - frame) / (fadeFrames - 1);
            for (int channel = 0; channel < UiSoundPlayer.MixChannels; channel++)
            {
                int index = (frame * UiSoundPlayer.MixChannels) + channel;
                result[index] = Clamp(result[index] * gain);
            }
        }

        return result;
    }

    /// <summary>
    /// Starts a prepared clip. Returns immediately; null or an unsupported
    /// platform does nothing.
    /// </summary>
    public static void Play(short[]? samples) => UiSoundPlayer.Play(samples);

    /// <summary>
    /// Silences the intro. This clears every mixer voice, which is what the intro
    /// wants: it owns the window while it runs, so nothing else is sounding.
    /// </summary>
    public static void Stop() => UiSoundPlayer.StopAll();

    private static short Clamp(double value) =>
        (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

}
