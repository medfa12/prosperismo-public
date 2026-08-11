// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// A short PCM clip already converted to the mixer's format
/// (<see cref="UiSoundPlayer.MixSampleRate"/> Hz, <see cref="UiSoundPlayer.MixChannels"/>
/// channels, interleaved PCM16). Clips are immutable so the same instance can be
/// cached and triggered from any thread.
/// </summary>
/// <param name="Samples">Interleaved PCM16 in the mixer's format.</param>
/// <param name="Name">Diagnostic label (usually the source cue name).</param>
public sealed record UiSoundClip(short[] Samples, string Name = "")
{
    /// <summary>Number of PCM frames in the clip.</summary>
    public int FrameCount => Samples.Length / UiSoundPlayer.MixChannels;

    /// <summary>Clip duration.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / UiSoundPlayer.MixSampleRate);
}

/// <summary>Independent looping-music slots owned by the shell.</summary>
public enum MusicVoiceKind
{
    Ambient,
    Title,
    /// <summary>Initial-boot onboarding bed, independent from Home ambient.</summary>
    Onboarding,
}

/// <summary>
/// A tiny software mixer for short UI cues.
///
/// winmm's <c>PlaySound</c> (used by <see cref="ShellAudio"/> for the boot chime
/// and background music) allows exactly one active sound per process, which is
/// wrong for menu blips: a focus move would cut the music, and rapid focus
/// changes would cut each other. This player instead keeps a single
/// <c>waveOut</c> stream open, sums every active voice into it, and lets clips
/// overlap freely. Concretely it gives:
/// <list type="bullet">
/// <item>overlap: a blip can start while music and other blips are playing;</item>
/// <item>low latency: 10 ms buffers, so a trigger is audible within ~20-30 ms;</item>
/// <item>bounded cost: one background thread total, no matter how fast triggers
/// arrive, and a hard cap on simultaneous voices;</item>
/// <item>no UI-thread work: <see cref="Play(UiSoundClip, float)"/> only appends to a list.</item>
/// </list>
///
/// The stream is opened lazily (on the first trigger, or up front via
/// <see cref="Warm"/>) and released again after a long stretch of silence, so a
/// session that never plays a cue never touches the audio device and a game
/// session is not left holding it. Everything is Windows-only and a silent no-op
/// elsewhere, and no method throws.
/// </summary>
public static class UiSoundPlayer
{
    /// <summary>Mixer sample rate. Matches the shell's own cue rate, so the PS5 cues need no resampling.</summary>
    public const int MixSampleRate = 48000;

    /// <summary>Mixer channel count.</summary>
    public const int MixChannels = 2;

    /// <summary>Most voices that may sound at once; the oldest is dropped past this.</summary>
    public const int MaxVoices = 24;

    // 10 ms per buffer with six buffers in flight: short enough to feel
    // immediate, long enough that a stalled thread pool cannot underrun it.
    private const int BufferFrames = MixSampleRate / 100;
    private const int BufferCount = 6;

    // Opening the output device costs a few hundred milliseconds, which would be
    // audible as a late blip, so the stream is held well past the end of a burst
    // of interaction. It is still released eventually: during a long game
    // session nothing should be holding the device or waking a thread.
    // <see cref="Warm"/> pays the open cost up front instead.
    private const int IdleShutdownMilliseconds = 20000;

    private const int WaveFormatPcm = 1;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrInQueue = 0x00000010;

    private static readonly object Gate = new();
    private static readonly List<Voice> Voices = new();
    private static MusicVoice? _ambientMusic;
    private static MusicVoice? _titleMusic;
    private static MusicVoice? _onboardingMusic;
    private static bool _pumpRunning;
    private static int _pumpGeneration;

    /// <summary>True when this platform has a usable low-latency output path.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || CoreAudioOutput.IsSupported;

    /// <summary>True while a looping music bed is loaded, whether or not it is audible.</summary>
    public static bool HasMusic
    {
        get
        {
            lock (Gate)
            {
                return _ambientMusic is not null ||
                       _titleMusic is not null ||
                       _onboardingMusic is not null;
            }
        }
    }

    /// <summary>
    /// Gain the loaded music bed is moving toward, 0 to 1. Setting it is the
    /// only thing a duck, a fade-in or a mute does; the bed itself keeps
    /// playing and keeps its place.
    /// </summary>
    public static float MusicGain
    {
        get
        {
            lock (Gate)
            {
                return GetMusicGainLocked(MusicVoiceKind.Ambient);
            }
        }

        set
        {
            lock (Gate)
            {
                SetMusicGainLocked(MusicVoiceKind.Ambient, value);
            }
        }
    }

    /// <summary>Returns the target gain for one independent music slot.</summary>
    public static float GetMusicGain(MusicVoiceKind kind)
    {
        lock (Gate)
        {
            return GetMusicGainLocked(kind);
        }
    }

    /// <summary>Diagnostic source label for one loaded music slot.</summary>
    internal static string? GetMusicName(MusicVoiceKind kind)
    {
        lock (Gate)
        {
            return GetMusicLocked(kind)?.Name;
        }
    }

    /// <summary>
    /// Changes one music slot's target gain without replacing or rewinding the
    /// other slot. A missing slot is intentionally a no-op.
    /// </summary>
    public static void SetMusicGain(MusicVoiceKind kind, float gain)
    {
        try
        {
            lock (Gate)
            {
                SetMusicGainLocked(kind, gain);
            }
        }
        catch (Exception)
        {
            // Background music is never worth surfacing an error for.
        }
    }

    /// <summary>Number of clips currently sounding. Diagnostics only.</summary>
    public static int ActiveVoiceCount
    {
        get
        {
            lock (Gate)
            {
                return Voices.Count;
            }
        }
    }

    /// <summary>
    /// Converts a decoded VAG clip to the mixer's format: resampled to
    /// <see cref="MixSampleRate"/> and up/down-mixed to <see cref="MixChannels"/>.
    /// Runs on the caller's thread and is meant to be done once, at cache time.
    /// Returns null for a null or empty clip.
    /// </summary>
    public static UiSoundClip? Prepare(VagClip? clip)
    {
        if (clip is null || clip.Samples.Length == 0)
        {
            return null;
        }

        var samples = ToMixFormat(clip.Samples, clip.Channels, clip.SampleRate);
        return samples.Length == 0 ? null : new UiSoundClip(samples, clip.Name);
    }

    /// <summary>
    /// Converts interleaved PCM16 of any rate/channel count to interleaved
    /// stereo at <see cref="MixSampleRate"/>. Resampling is linear, which is
    /// inaudible for the short cues this player handles.
    /// </summary>
    /// <param name="samples">Interleaved source PCM16.</param>
    /// <param name="channels">Source channel count (at least 1).</param>
    /// <param name="sampleRate">Source sample rate in Hz.</param>
    public static short[] ToMixFormat(short[] samples, int channels, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || channels < 1 || sampleRate < 1)
        {
            return Array.Empty<short>();
        }

        int sourceFrames = samples.Length / channels;
        if (sourceFrames == 0)
        {
            return Array.Empty<short>();
        }

        long targetFrames = sampleRate == MixSampleRate
            ? sourceFrames
            : (long)sourceFrames * MixSampleRate / sampleRate;
        if (targetFrames <= 0)
        {
            return Array.Empty<short>();
        }

        var output = new short[targetFrames * MixChannels];
        double step = (double)sourceFrames / targetFrames;

        for (long frame = 0; frame < targetFrames; frame++)
        {
            double position = frame * step;
            int index = (int)position;
            double fraction = position - index;
            int next = Math.Min(index + 1, sourceFrames - 1);

            for (int channel = 0; channel < MixChannels; channel++)
            {
                // Mono sources feed both outputs; sources with more channels
                // than the mixer are truncated to the first two.
                int sourceChannel = channels == 1 ? 0 : Math.Min(channel, channels - 1);
                double a = samples[(index * channels) + sourceChannel];
                double b = samples[(next * channels) + sourceChannel];
                output[(frame * MixChannels) + channel] = (short)Math.Clamp(
                    Math.Round(a + ((b - a) * fraction)),
                    short.MinValue,
                    short.MaxValue);
            }
        }

        return output;
    }

    /// <summary>
    /// Starts a clip. Returns immediately; the clip is mixed on the player's own
    /// thread. Triggering the same clip again while it sounds layers a second
    /// voice rather than restarting it.
    /// </summary>
    /// <param name="clip">The prepared clip, or null to do nothing.</param>
    /// <param name="gain">Linear gain applied to the voice.</param>
    public static void Play(UiSoundClip? clip, float gain = 1.0f)
    {
        if (clip is not null)
        {
            Play(clip.Samples, gain);
        }
    }

    /// <summary>
    /// Starts a raw interleaved stereo PCM16 buffer already in the mixer's
    /// format. Silently ignores null, empty or non-finite input.
    /// </summary>
    /// <param name="samples">Interleaved stereo PCM16 at <see cref="MixSampleRate"/>.</param>
    /// <param name="gain">Linear gain applied to the voice.</param>
    public static void Play(short[]? samples, float gain = 1.0f)
    {
        if (!IsSupported || samples is null || samples.Length < MixChannels ||
            !float.IsFinite(gain) || gain <= 0f)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (Voices.Count >= MaxVoices)
                {
                    Voices.RemoveAt(0);
                }

                Voices.Add(new Voice(samples, gain));
                EnsurePumpLocked();
            }
        }
        catch (Exception)
        {
            // A UI blip is never worth surfacing an error for.
        }
    }

    /// <summary>
    /// Loads a looping bed and starts it fading in to <paramref name="gain"/>.
    /// Replacing a bed that is already loaded restarts from its beginning;
    /// passing null clears the slot. Safe on any platform and never throws.
    /// </summary>
    /// <param name="clip">The decoded bed, or null to clear.</param>
    /// <param name="gain">Gain to fade up to, 0 to 1.</param>
    public static void SetMusic(MusicClip? clip, float gain)
    {
        SetMusic(MusicVoiceKind.Ambient, clip, gain);
    }

    /// <summary>
    /// Loads a looping bed into one independent music slot. Replacing a slot
    /// restarts only that slot; ambient and title music cannot overwrite one
    /// another while either decode is completing on a background thread.
    /// </summary>
    public static void SetMusic(MusicVoiceKind kind, MusicClip? clip, float gain)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (clip is null || clip.Samples.Length < MixChannels)
                {
                    SetMusicLocked(kind, null, gain);
                    return;
                }

                SetMusicLocked(kind, clip, gain);
            }
        }
        catch (Exception)
        {
            // Background music is never worth surfacing an error for.
        }
    }

    /// <summary>Drops the music bed immediately. One-shot cues keep sounding.</summary>
    public static void ClearMusic()
    {
        lock (Gate)
        {
            _ambientMusic = null;
            _titleMusic = null;
            _onboardingMusic = null;
        }
    }

    /// <summary>Drops one music slot immediately while leaving the other intact.</summary>
    public static void ClearMusic(MusicVoiceKind kind)
    {
        lock (Gate)
        {
            SetMusicLocked(kind, null, 0f);
        }
    }

    /// <summary>
    /// Opens the output device ahead of the first cue so that cue is not delayed
    /// by device setup. Safe to call repeatedly and from any thread; returns
    /// immediately and does nothing on unsupported platforms.
    /// </summary>
    public static void Warm()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                EnsurePumpLocked();
            }
        }
        catch (Exception)
        {
            // Warming is best effort; the first cue will open the device instead.
        }
    }

    private static void EnsurePumpLocked()
    {
        if (_pumpRunning)
        {
            return;
        }

        _pumpRunning = true;
        int generation = ++_pumpGeneration;
        var thread = new Thread(() => Pump(generation))
        {
            IsBackground = true,
            Name = "shell-ui-sound",
        };
        thread.Start();
    }

    /// <summary>Silences every active voice. The device closes on its own shortly after.</summary>
    public static void StopAll()
    {
        lock (Gate)
        {
            Voices.Clear();
        }
    }

    private static void Pump(int generation)
    {
        try
        {
            RunDevice(generation);
        }
        catch (Exception)
        {
            // Losing the audio device must not take the process with it.
        }
        finally
        {
            lock (Gate)
            {
                if (_pumpGeneration == generation)
                {
                    _pumpRunning = false;
                }
            }
        }
    }

    private static void RunDevice(int generation)
    {
        if (CoreAudioOutput.IsSupported)
        {
            RunCoreAudioDevice(generation);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = MixChannels,
            SamplesPerSecond = MixSampleRate,
            AverageBytesPerSecond = MixSampleRate * MixChannels * sizeof(short),
            BlockAlign = MixChannels * sizeof(short),
            BitsPerSample = 16,
            Size = 0,
        };

        using var ready = new AutoResetEvent(false);
        if (waveOutOpen(
                out nint device,
                WaveMapper,
                ref format,
                ready.SafeWaitHandle.DangerousGetHandle(),
                nint.Zero,
                CallbackEvent) != 0)
        {
            return;
        }

        int headerSize = Marshal.SizeOf<WaveHdr>();
        int bufferBytes = BufferFrames * MixChannels * sizeof(short);
        var headers = new nint[BufferCount];
        var buffers = new nint[BufferCount];
        var scratch = new short[BufferFrames * MixChannels];
        var accumulator = new int[scratch.Length];

        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bufferBytes);
                headers[i] = Marshal.AllocHGlobal(headerSize);
                Marshal.StructureToPtr(
                    new WaveHdr { Data = buffers[i], BufferLength = (uint)bufferBytes },
                    headers[i],
                    fDeleteOld: false);
                if (waveOutPrepareHeader(device, headers[i], (uint)headerSize) != 0)
                {
                    return;
                }
            }

            long lastActive = Environment.TickCount64;

            while (true)
            {
                bool queuedAny = false;
                for (int i = 0; i < BufferCount; i++)
                {
                    var header = Marshal.PtrToStructure<WaveHdr>(headers[i]);
                    if ((header.Flags & WhdrInQueue) != 0)
                    {
                        continue;
                    }

                    if (Mix(scratch, accumulator))
                    {
                        lastActive = Environment.TickCount64;
                    }

                    Marshal.Copy(scratch, 0, buffers[i], scratch.Length);
                    if (waveOutWrite(device, headers[i], (uint)headerSize) == 0)
                    {
                        queuedAny = true;
                    }
                }

                if (!queuedAny)
                {
                    ready.WaitOne(BufferFrames * 1000 / MixSampleRate * 2);
                }

                if (Environment.TickCount64 - lastActive < IdleShutdownMilliseconds)
                {
                    continue;
                }

                // Idle long enough to release the device. Decided under the
                // lock so a trigger racing this either sees the pump still
                // running or starts a fresh one.
                lock (Gate)
                {
                    if (Voices.Count > 0 || HasAudibleMusicLocked())
                    {
                        lastActive = Environment.TickCount64;
                        continue;
                    }

                    if (_pumpGeneration == generation)
                    {
                        _pumpRunning = false;
                    }
                }

                break;
            }
        }
        finally
        {
            _ = waveOutReset(device);
            for (int i = 0; i < BufferCount; i++)
            {
                if (headers[i] != nint.Zero)
                {
                    _ = waveOutUnprepareHeader(device, headers[i], (uint)headerSize);
                    Marshal.FreeHGlobal(headers[i]);
                }

                if (buffers[i] != nint.Zero)
                {
                    Marshal.FreeHGlobal(buffers[i]);
                }
            }

            _ = waveOutClose(device);
        }
    }

    /// <summary>
    /// Sums the music bed and the active one-shot voices into
    /// <paramref name="scratch"/>, advancing and retiring each one. Returns true
    /// when anything contributed, which keeps the device open.
    /// </summary>
    /// <summary>
    /// macOS pump. Shares the mixer, voice limit and idle-shutdown policy with
    /// the Win32 path; only the output transport differs.
    /// </summary>
    private static void RunCoreAudioDevice(int generation)
    {
        var accumulator = new int[BufferFrames * MixChannels];
        CoreAudioOutput.Run(
            BufferFrames,
            BufferCount,
            scratch => Mix(scratch, accumulator),
            () =>
            {
                lock (Gate)
                {
                    if (_pumpGeneration != generation)
                    {
                        return true;
                    }

                    if (Voices.Count == 0 && !HasAudibleMusicLocked())
                    {
                        _pumpRunning = false;
                        return true;
                    }
                }

                return false;
            },
            IdleShutdownMilliseconds);

        lock (Gate)
        {
            if (_pumpGeneration == generation)
            {
                _pumpRunning = false;
            }
        }
    }

    private static bool Mix(short[] scratch, int[] accumulator)
    {
        Array.Clear(accumulator);
        bool sounded = false;

        lock (Gate)
        {
            if (_ambientMusic is not null && _ambientMusic.Mix(accumulator))
            {
                sounded = true;
            }

            if (_titleMusic is not null && _titleMusic.Mix(accumulator))
            {
                sounded = true;
            }

            if (_onboardingMusic is not null && _onboardingMusic.Mix(accumulator))
            {
                sounded = true;
            }

            for (int v = Voices.Count - 1; v >= 0; v--)
            {
                var voice = Voices[v];
                int remaining = voice.Samples.Length - voice.Position;
                int count = Math.Min(accumulator.Length, remaining);
                for (int i = 0; i < count; i++)
                {
                    accumulator[i] += (int)(voice.Samples[voice.Position + i] * voice.Gain);
                }

                voice.Position += count;
                sounded = true;

                if (voice.Position >= voice.Samples.Length)
                {
                    Voices.RemoveAt(v);
                }
            }
        }

        for (int i = 0; i < scratch.Length; i++)
        {
            scratch[i] = (short)Math.Clamp(accumulator[i], short.MinValue, short.MaxValue);
        }

        return sounded;
    }

    private static void SetMusicLocked(MusicVoiceKind kind, MusicClip? clip, float gain)
    {
        if (clip is null || clip.Samples.Length < MixChannels)
        {
            SetMusicSlotLocked(kind, null);
            return;
        }

        var voice = new MusicVoice(clip.Samples, clip.LoopStartFrame, clip.LoopEndFrame, name: clip.Name)
        {
            TargetGain = gain,
        };
        SetMusicSlotLocked(kind, voice);

        if (gain > 0f)
        {
            EnsurePumpLocked();
        }
    }

    private static void SetMusicGainLocked(MusicVoiceKind kind, float gain)
    {
        var voice = GetMusicLocked(kind);
        if (voice is null)
        {
            return;
        }

        voice.TargetGain = gain;
        if (gain > 0f)
        {
            // Coming back from silence may have let the device close.
            EnsurePumpLocked();
        }
    }

    private static float GetMusicGainLocked(MusicVoiceKind kind) => GetMusicLocked(kind)?.TargetGain ?? 0f;

    private static MusicVoice? GetMusicLocked(MusicVoiceKind kind) => kind switch
    {
        MusicVoiceKind.Ambient => _ambientMusic,
        MusicVoiceKind.Title => _titleMusic,
        MusicVoiceKind.Onboarding => _onboardingMusic,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void SetMusicSlotLocked(MusicVoiceKind kind, MusicVoice? voice)
    {
        switch (kind)
        {
            case MusicVoiceKind.Ambient:
                _ambientMusic = voice;
                break;
            case MusicVoiceKind.Title:
                _titleMusic = voice;
                break;
            case MusicVoiceKind.Onboarding:
                _onboardingMusic = voice;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static bool HasAudibleMusicLocked() =>
        (_ambientMusic is { IsSilent: false }) ||
        (_titleMusic is { IsSilent: false }) ||
        (_onboardingMusic is { IsSilent: false });

    private sealed class Voice
    {
        public Voice(short[] samples, float gain)
        {
            Samples = samples;
            Gain = gain;
        }

        public short[] Samples { get; }

        public float Gain { get; }

        public int Position { get; set; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out nint device, uint deviceId, ref WaveFormatEx format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(nint device, nint header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(nint device, nint header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(nint device, nint header, uint headerSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(nint device);
}
