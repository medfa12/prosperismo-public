// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using LibAtrac9;
using Prosperismo.GUI.SystemAssets.Audio;

namespace Prosperismo.GUI;

/// <summary>
/// Loops a game's sce_sys/snd0.at9 preview music while the game is selected
/// in the library, like the console home screen. The ATRAC9 stream is decoded
/// on a background task and handed to the same cross-platform mixer as the
/// </summary>
internal sealed class SndPreviewPlayer
{
    private readonly object _sync = new();
    private readonly Func<string, MusicClip?> _decode;
    private readonly bool _resolvePaths;
    private int _generation;
    private bool _playing;
    private bool _paused;
    private string? _cachedPath;
    private MusicClip? _cachedClip;

    public SndPreviewPlayer()
        : this(path => ShellAudioDecoder.TryDecode(path, forLooping: true), resolvePaths: true)
    {
    }

    internal SndPreviewPlayer(Func<string, MusicClip?> decode)
        : this(decode, resolvePaths: false)
    {
    }

    private SndPreviewPlayer(Func<string, MusicClip?> decode, bool resolvePaths)
    {
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        _resolvePaths = resolvePaths;
    }

    /// <summary>
    /// Resolves a title sound path case-insensitively on case-sensitive hosts.
    /// Callers may pass the canonical path or a path whose final component has
    /// the wrong casing; no files are created or copied.
    /// </summary>
    internal static string? ResolveSnd0Path(string? at9Path)
    {
        if (string.IsNullOrWhiteSpace(at9Path))
        {
            return null;
        }

        try
        {
            if (File.Exists(at9Path))
            {
                return at9Path;
            }

            var directory = Path.GetDirectoryName(at9Path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(path), "snd0.at9", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>Starts looping the given snd0.at9 after a short debounce.</summary>
    public void Play(string at9Path)
    {
        var resolvedPath = _resolvePaths ? ResolveSnd0Path(at9Path) : at9Path;
        if (resolvedPath is null)
        {
            Stop();
            return;
        }

        at9Path = resolvedPath;
        int generation;
        lock (_sync)
        {
            generation = ++_generation;
            _playing = false;
            _paused = false;

            if (!string.Equals(_cachedPath, at9Path, StringComparison.OrdinalIgnoreCase))
            {
                // Do not let a failed new title retain a resumable cache entry
                // for the previous title.
                _cachedPath = null;
                _cachedClip = null;
            }

            // Clear the title slot before decoding. This prevents a failed or
            // slow replacement from leaving the previous game's snd0 audible.
            UiSoundPlayer.ClearMusic(MusicVoiceKind.Title);
            // A title-owned bed takes precedence over any onboarding bed that
            // may still be winding down during a surface handoff.
            UiSoundPlayer.ClearMusic(MusicVoiceKind.Onboarding);

            // Until the new title has decoded, ambient owns the audible music.
            ShellAmbientMusic.SetTitleMusicActive(false);
        }

        if (!UiSoundPlayer.IsSupported)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // Debounce so skimming through the library does not decode (or
            // start) a preview per tile.
            await Task.Delay(120).ConfigureAwait(false);

            MusicClip? clip;
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                clip = string.Equals(_cachedPath, at9Path, StringComparison.OrdinalIgnoreCase)
                    ? _cachedClip
                    : null;
            }

            if (clip is null)
            {
                try
                {
                    clip = _decode(at9Path);
                }
                catch (Exception)
                {
                    clip = null;
                }
            }

            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                if (clip is null)
                {
                    // The clear at Play() handles the normal case; repeating
                    // it here makes failure safe even if a caller races a
                    // title-slot operation from another path.
                    UiSoundPlayer.ClearMusic(MusicVoiceKind.Title);
                    _playing = false;
                    ShellAmbientMusic.SetTitleMusicActive(false);
                    return;
                }

                _cachedPath = at9Path;
                _cachedClip = clip;
                UiSoundPlayer.SetMusic(
                    MusicVoiceKind.Title,
                    clip,
                    _paused ? 0.0f : 1.0f);
                _playing = !_paused;

                // Keep the title state transition in the same serialized
                // section as slot installation. An older decode cannot turn
                // ambient back on or re-duck it after a newer title wins.
                ShellAmbientMusic.SetTitleMusicActive(true);
            }
        });
    }

    public void Stop()
    {
        lock (_sync)
        {
            _generation++;
            _playing = false;
            _paused = false;
            UiSoundPlayer.ClearMusic(MusicVoiceKind.Title);
            ShellAmbientMusic.SetTitleMusicActive(false);
        }
        // presentation. Do not start it here: this player is also used by the
    }

    /// <summary>
    /// Silences playback but keeps the decoded track ready, so
    /// <see cref="Resume"/> can raise its mixer gain again.
    /// </summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (!_playing)
            {
                _paused = true;
                return;
            }

            UiSoundPlayer.SetMusicGain(MusicVoiceKind.Title, 0);
            _playing = false;
            _paused = true;
        }
    }

    /// <summary>Restarts the track silenced by <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_sync)
        {
            if (!_paused || _cachedClip is null)
            {
                return;
            }

            _paused = false;
            UiSoundPlayer.SetMusicGain(MusicVoiceKind.Title, 1.0f);
            _playing = true;
            ShellAmbientMusic.SetTitleMusicActive(true);
        }
    }

    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");

    /// <summary>
    /// carries the 4-byte codec config, a fact chunk with the sample count
    /// and encoder delay, and superframes in the data chunk. Internal so the
    /// shell-audio helper (SystemAssets.ShellAudio) shares the same decoder.
    /// </summary>
    internal static byte[] DecodeAt9ToWav(byte[] file)
    {
        using var reader = new BinaryReader(new MemoryStream(file), Encoding.ASCII);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.BaseStream.Position += 4; // riff size
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        byte[]? configData = null;
        var sampleCount = 0;
        var encoderDelay = 0;
        var dataOffset = -1;
        var dataSize = 0;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            var chunkStart = reader.BaseStream.Position;
            switch (chunkId)
            {
                case "fmt ":
                    var formatTag = reader.ReadUInt16();
                    reader.BaseStream.Position = chunkStart + 24; // to SubFormat GUID
                    var subFormat = new Guid(reader.ReadBytes(16));
                    if (formatTag != 0xFFFE || subFormat != Atrac9SubFormat)
                    {
                        throw new InvalidDataException("Not an ATRAC9 stream.");
                    }

                    reader.BaseStream.Position += 4; // version info
                    configData = reader.ReadBytes(4);
                    break;
                case "fact":
                    sampleCount = reader.ReadInt32();
                    reader.BaseStream.Position += 4; // input overlap delay
                    encoderDelay = reader.ReadInt32();
                    break;
                case "data":
                    dataOffset = (int)chunkStart;
                    dataSize = chunkSize;
                    break;
            }

            reader.BaseStream.Position = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (configData is null || sampleCount <= 0 || dataOffset < 0)
        {
            throw new InvalidDataException("Missing fmt, fact, or data chunk.");
        }

        var decoder = new Atrac9Decoder();
        decoder.Initialize(configData);
        var config = decoder.Config;

        var superframeCount = (sampleCount + encoderDelay + config.SuperframeSamples - 1) / config.SuperframeSamples;
        superframeCount = Math.Min(superframeCount, dataSize / config.SuperframeBytes);

        var channels = config.ChannelCount;
        var pcmBuffer = new short[channels][];
        for (var i = 0; i < channels; i++)
        {
            pcmBuffer[i] = new short[config.SuperframeSamples];
        }

        var wav = new byte[44 + (sampleCount * channels * 2)];
        WriteWavHeader(wav, channels, config.SampleRate, sampleCount);

        var superframe = new byte[config.SuperframeBytes];
        var decodedIndex = 0L; // per-channel, includes the encoder delay
        var written = 0;
        for (var f = 0; f < superframeCount && written < sampleCount; f++)
        {
            Buffer.BlockCopy(file, dataOffset + (f * config.SuperframeBytes), superframe, 0, config.SuperframeBytes);
            decoder.Decode(superframe, pcmBuffer);
            for (var s = 0; s < config.SuperframeSamples && written < sampleCount; s++)
            {
                if (decodedIndex++ < encoderDelay)
                {
                    continue;
                }

                var sampleOffset = 44 + ((long)written * channels * 2);
                for (var ch = 0; ch < channels; ch++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        wav.AsSpan((int)(sampleOffset + (ch * 2))),
                        pcmBuffer[ch][s]);
                }

                written++;
            }
        }

        return wav;
    }

    private static void WriteWavHeader(byte[] wav, int channels, int sampleRate, int sampleCount)
    {
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], sampleCount * channels * 2);
    }
}
