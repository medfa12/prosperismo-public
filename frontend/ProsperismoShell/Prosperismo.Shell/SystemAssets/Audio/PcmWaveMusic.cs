// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// Reads the shell's repository-owned PCM-WAVE derivatives. The source beds
/// are folded to stereo during the asset build; this reader only accepts the
/// deliberately boring format used by that build: PCM16, interleaved stereo.
/// Keeping the reader small means the shell no longer needs an ATRAC9 decoder
/// for its own packaged audio, while explicit user <c>.at9</c> files continue
/// to use <see cref="At9Music"/> through <see cref="ShellAudioDecoder"/>.
/// </summary>
internal static class PcmWaveMusic
{
    private const int WaveHeaderMinimum = 12;
    private const string LoopMetadataFileName = "loops.json";

    public static MusicClip? TryDecode(
        string? path,
        float gain = 1f,
        bool forLooping = true,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (!TryReadPcm(bytes, out var sampleRate, out var samples))
            {
                return null;
            }

            cancellation.ThrowIfCancellationRequested();
            if (float.IsFinite(gain) && gain > 0f &&
                Math.Abs(gain - 1f) > float.Epsilon)
            {
                ApplyGain(samples, gain, cancellation);
            }

            var sourceFrames = samples.Length / UiSoundPlayer.MixChannels;
            var loopStart = 0;
            var loopEnd = 0;
            var hasAuthoredLoop = forLooping &&
                TryReadAuthoredLoop(path, sampleRate, sourceFrames, out loopStart, out loopEnd);

            if (sampleRate != UiSoundPlayer.MixSampleRate)
            {
                samples = UiSoundPlayer.ToMixFormat(
                    samples,
                    UiSoundPlayer.MixChannels,
                    sampleRate);

                if (hasAuthoredLoop)
                {
                    var resampledFrames = samples.Length / UiSoundPlayer.MixChannels;
                    loopStart = Math.Min(
                        (int)((long)loopStart * UiSoundPlayer.MixSampleRate / sampleRate),
                        resampledFrames - 1);
                    loopEnd = Math.Min(
                        (int)((long)loopEnd * UiSoundPlayer.MixSampleRate / sampleRate),
                        resampledFrames);
                }
            }

            var frames = samples.Length / UiSoundPlayer.MixChannels;
            if (frames <= 0)
            {
                return null;
            }

            if (hasAuthoredLoop)
            {
                samples = At9Music.ApplyLoopSeam(
                    samples,
                    loopStart,
                    loopEnd,
                    At9Music.LoopSeamFrames);
                frames = samples.Length / UiSoundPlayer.MixChannels;
                loopEnd = Math.Min(loopEnd, frames);
            }
            else if (forLooping)
            {
                samples = At9Music.ApplyLoopCrossfade(samples, At9Music.DefaultCrossfadeFrames);
                frames = samples.Length / UiSoundPlayer.MixChannels;
                loopStart = 0;
                loopEnd = frames;
            }

            return new MusicClip(
                samples,
                hasAuthoredLoop ? loopStart : 0,
                hasAuthoredLoop ? loopEnd : frames,
                Path.GetFileName(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool LooksLikePcmWave(ReadOnlySpan<byte> data)
    {
        return data.Length >= WaveHeaderMinimum &&
            data[..4].SequenceEqual("RIFF"u8) &&
            data[8..12].SequenceEqual("WAVE"u8);
    }

    private static bool TryReadAuthoredLoop(
        string wavePath,
        int sampleRate,
        int frameCount,
        out int loopStart,
        out int loopEnd)
    {
        loopStart = 0;
        loopEnd = 0;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(wavePath));
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var metadataPath = Path.Combine(directory, LoopMetadataFileName);
            if (!File.Exists(metadataPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
            if (!document.RootElement.TryGetProperty(Path.GetFileName(wavePath), out var track) ||
                !track.TryGetProperty("sample_rate", out var encodedRate) ||
                !track.TryGetProperty("start_frame", out var encodedStart) ||
                !track.TryGetProperty("end_frame", out var encodedEnd))
            {
                return false;
            }

            if (!encodedRate.TryGetInt32(out var metadataRate) || metadataRate != sampleRate ||
                !encodedStart.TryGetInt32(out loopStart) ||
                !encodedEnd.TryGetInt32(out loopEnd) ||
                loopStart < 0 || loopEnd <= loopStart || loopEnd > frameCount)
            {
                loopStart = 0;
                loopEnd = 0;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            loopStart = 0;
            loopEnd = 0;
            return false;
        }
    }

    private static bool TryReadPcm(
        ReadOnlySpan<byte> data,
        out int sampleRate,
        out short[] samples)
    {
        sampleRate = 0;
        samples = [];
        if (!LooksLikePcmWave(data))
        {
            return false;
        }

        ushort format = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        int rate = 0;
        ReadOnlySpan<byte> pcm = default;
        int offset = 12;

        while (offset + 8 <= data.Length)
        {
            var chunkId = data.Slice(offset, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            offset += 8;
            if (chunkSize > int.MaxValue || offset > data.Length - (int)chunkSize)
            {
                return false;
            }

            var chunk = data.Slice(offset, (int)chunkSize);
            if (chunkId.SequenceEqual("fmt "u8) && chunk.Length >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                rate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                pcm = chunk;
            }

            offset += (int)chunkSize + ((chunkSize & 1) == 0 ? 0 : 1);
        }

        if (format != 1 || channels != UiSoundPlayer.MixChannels || bitsPerSample != 16 ||
            rate <= 0 || pcm.Length < UiSoundPlayer.MixChannels * sizeof(short) ||
            pcm.Length % (UiSoundPlayer.MixChannels * sizeof(short)) != 0)
        {
            return false;
        }

        samples = new short[pcm.Length / sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(index * 2, 2));
        }

        sampleRate = rate;
        return true;
    }

    private static void ApplyGain(short[] samples, float gain, CancellationToken cancellation)
    {
        for (int index = 0; index < samples.Length; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            samples[index] = (short)Math.Clamp(
                Math.Round(samples[index] * gain),
                short.MinValue,
                short.MaxValue);
        }
    }
}

/// <summary>Chooses the repository's PCM derivative before legacy AT9 input.</summary>
internal static class ShellAudioDecoder
{
    public static MusicClip? TryDecode(
        string? path,
        float gain = 1f,
        bool forLooping = true,
        CancellationToken cancellation = default)
    {
        if (path is not null && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return PcmWaveMusic.TryDecode(path, gain, forLooping, cancellation);
        }

        return At9Music.TryDecode(path, gain, forLooping, cancellation);
    }
}
