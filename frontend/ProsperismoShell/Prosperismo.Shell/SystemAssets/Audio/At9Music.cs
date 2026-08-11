// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using LibAtrac9;

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// without decoding any audio.
/// </summary>
/// <param name="Channels">Source channel count; the shell beds are 5.1.</param>
/// <param name="SampleRate">Source sample rate in Hz.</param>
/// <param name="FrameCount">Decoded frames in the stream, excluding the encoder delay.</param>
/// <param name="LoopStartFrame">First frame of the loop body, or -1 when the file carries no loop.</param>
/// <param name="LoopEndFrame">Frame the loop wraps at, exclusive, or -1 when the file carries no loop.</param>
public sealed record At9StreamInfo(
    int Channels,
    int SampleRate,
    int FrameCount,
    int LoopStartFrame,
    int LoopEndFrame)
{
    /// <summary>True when the file declared a usable forward loop region.</summary>
    public bool HasLoop => LoopStartFrame >= 0 && LoopEndFrame > LoopStartFrame;

    /// <summary>Length of the whole stream.</summary>
    public TimeSpan Duration =>
        SampleRate > 0 ? TimeSpan.FromSeconds((double)FrameCount / SampleRate) : TimeSpan.Zero;
}

/// <summary>
/// A decoded music bed in the mixer's format (<see cref="UiSoundPlayer.MixSampleRate"/> Hz,
/// interleaved stereo PCM16) together with the point it wraps at.
/// </summary>
/// <param name="Samples">Interleaved stereo PCM16.</param>
/// <param name="LoopStartFrame">Frame playback jumps back to.</param>
/// <param name="LoopEndFrame">Frame playback wraps at, exclusive.</param>
/// <param name="Name">Diagnostic label, usually the source file name.</param>
public sealed record MusicClip(short[] Samples, int LoopStartFrame, int LoopEndFrame, string Name = "")
{
    /// <summary>Frames held in <see cref="Samples"/>.</summary>
    public int FrameCount => Samples.Length / UiSoundPlayer.MixChannels;

    /// <summary>Length of the looping body.</summary>
    public TimeSpan LoopDuration =>
        TimeSpan.FromSeconds((double)(LoopEndFrame - LoopStartFrame) / UiSoundPlayer.MixSampleRate);
}

/// <summary>
/// bgm_onboarding.at9) into a form the software mixer can loop forever.
///
/// Two things separate this from the short-cue path in <see cref="VagDecoder"/>
/// and from the snd0.at9 preview player:
/// <list type="bullet">
/// <item>the beds are 5.1, so they are folded down to stereo here rather than
/// having their centre channel dropped;</item>
/// <item>they carry a <c>smpl</c> chunk holding the console's own sample-accurate
/// loop points, so the wrap is exactly where the shell puts it and no seam
/// treatment is needed. Only when that chunk is missing does
/// <see cref="ApplyLoopCrossfade"/> manufacture a seam.</item>
/// </list>
///
/// Nothing here throws: a missing, truncated or unsupported file decodes to
/// null. Decoding a four-minute bed takes a moment and allocates tens of
/// megabytes, so callers must stay off the UI thread.
/// </summary>
public static class At9Music
{
    /// <summary>
    /// Longest bed that will be decoded, as a guard on the memory a malformed
    /// or unexpected header could ask for. The shipping beds are around four
    /// minutes.
    /// </summary>
    public const int MaxDecodeFrames = UiSoundPlayer.MixSampleRate * 360;

    /// <summary>
    /// Seam length used when a file declares no loop points. A quarter second
    /// is long enough to hide a mismatch in an ambient bed and short enough not
    /// to smear a recognisable phrase.
    /// </summary>
    public const int DefaultCrossfadeFrames = UiSoundPlayer.MixSampleRate / 4;

    /// <summary>
    /// Seam length used at a loop point the file did declare, about 25 ms.
    /// Much shorter than <see cref="DefaultCrossfadeFrames"/> on purpose: the
    /// loop points are already musically right, so the seam only has to hide a
    /// sample-level step, and a long blend here would mix the end of the loop
    /// body into the start of it.
    /// </summary>
    public const int LoopSeamFrames = UiSoundPlayer.MixSampleRate / 40;

    // Folding 5.1 into two speakers sums three sources per side, so the result
    // is attenuated to keep a mastered bed off the clip ceiling.
    private const double CentreCoefficient = 0.7071;
    private const double SurroundCoefficient = 0.7071;
    private const double DownmixHeadroom = 0.5;

    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");

    /// <summary>
    /// Reads channel count, rate, length and loop points without decoding
    /// audio. Returns null when the path is empty, unreadable, or not an
    /// ATRAC9 stream.
    /// </summary>
    public static At9StreamInfo? TryReadInfo(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = TryReadHeader(stream);
            if (header is null)
            {
                return null;
            }

            var config = new Atrac9Config(header.ConfigData);
            return new At9StreamInfo(
                config.ChannelCount,
                config.SampleRate,
                header.SampleCount,
                header.LoopStart,
                header.LoopEnd);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes a bed to an interleaved stereo clip in the mixer's format.
    /// Only the part that actually plays is decoded: when the file declares a
    /// loop, everything past the loop end is skipped, because an endless bed
    /// never reaches its outro.
    /// </summary>
    /// <param name="path">Absolute path to a .at9 file, or null.</param>
    /// <param name="gain">Linear gain applied while folding down; see <c>ShellAudio.MakeupGain</c>.</param>
    /// <param name="forLooping">
    /// True for a bed that will loop forever: everything past the loop end is
    /// skipped and a seam is built if the file declares no loop points. False
    /// for a one-shot, which keeps its whole length and its tail untouched.
    /// </param>
    /// <param name="cancellation">Abandons a decode whose result is no longer wanted.</param>
    public static MusicClip? TryDecode(
        string? path, float gain = 1f, bool forLooping = true, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = TryReadHeader(stream);
            if (header is null || header.SampleCount <= 0)
            {
                return null;
            }

            var decoder = new Atrac9Decoder();
            decoder.Initialize(header.ConfigData);
            var config = decoder.Config;
            if (config.ChannelCount < 1 || config.SampleRate < 1 || config.SuperframeBytes < 1)
            {
                return null;
            }

            // A declared loop end is the last frame that sounds, so the wrap is
            // one past it. A little of what follows is decoded too, as the
            // fade-out half of the seam. A one-shot ignores all of that and
            // keeps its outro.
            bool hasLoop = forLooping && header.LoopStart >= 0 && header.LoopEnd > header.LoopStart;
            long wanted = hasLoop
                ? Math.Min((long)header.LoopEnd + 1 + LoopSeamFrames, header.SampleCount)
                : header.SampleCount;
            wanted = Math.Min(wanted, MaxDecodeFrames);
            if (wanted <= 0)
            {
                return null;
            }

            var samples = DecodeStereo(stream, decoder, header, (int)wanted, gain, cancellation);
            if (samples is null || samples.Length == 0)
            {
                return null;
            }

            int frames = samples.Length / UiSoundPlayer.MixChannels;
            int loopStart = hasLoop ? Math.Min(header.LoopStart, frames - 1) : 0;
            int loopEnd = hasLoop ? Math.Min(header.LoopEnd + 1, frames) : frames;

            if (config.SampleRate != UiSoundPlayer.MixSampleRate)
            {
                samples = UiSoundPlayer.ToMixFormat(samples, UiSoundPlayer.MixChannels, config.SampleRate);
                if (samples.Length == 0)
                {
                    return null;
                }

                int resampled = samples.Length / UiSoundPlayer.MixChannels;
                loopStart = (int)Math.Min(
                    (long)loopStart * UiSoundPlayer.MixSampleRate / config.SampleRate, resampled - 1);
                loopEnd = hasLoop
                    ? (int)Math.Min((long)loopEnd * UiSoundPlayer.MixSampleRate / config.SampleRate, resampled)
                    : resampled;
                frames = resampled;
            }

            if (hasLoop && loopEnd > loopStart)
            {
                // Blend the take's own continuation into the head of the loop
                // body, then drop everything past the wrap.
                samples = ApplyLoopSeam(samples, loopStart, loopEnd, LoopSeamFrames);
                frames = samples.Length / UiSoundPlayer.MixChannels;
                loopEnd = Math.Min(loopEnd, frames);
            }

            if (forLooping && !hasLoop)
            {
                // Nothing told us where the bed wraps, so build a seam. Never
                // done for a one-shot: it would eat the tail of a chime.
                samples = ApplyLoopCrossfade(samples, DefaultCrossfadeFrames);
                frames = samples.Length / UiSoundPlayer.MixChannels;
                loopStart = 0;
                loopEnd = frames;
            }

            if (loopStart < 0 || loopStart >= loopEnd)
            {
                loopStart = 0;
            }

            return new MusicClip(samples, loopStart, loopEnd, Path.GetFileName(path));
        }
        catch (Exception)
        {
            // A missing, truncated or unsupported bed just means no music.
            return null;
        }
    }

    /// <summary>
    /// Hides the step at a declared loop point by blending the material that
    /// follows the loop end into the material at the loop start.
    ///
    /// A loop point authored in PCM does not survive a lossy MDCT codec as a
    /// sample-exact join: measured on the shipping home bed, wrapping at the
    /// console's own points still steps by around a thousand where the bed's
    /// own sample-to-sample motion is about thirty, which on a quiet ambient
    /// bed is an audible tick every few minutes. The file carries real audio
    /// past the loop end, so the fade-out half of the seam is the take's own
    /// continuation rather than anything invented.
    ///
    /// Requires <paramref name="samples"/> to extend past
    /// <paramref name="loopEnd"/>; the seam is shortened to whatever material
    /// is actually there. The result is truncated to the loop end, since
    /// nothing past it is ever played again.
    /// </summary>
    /// <param name="samples">Interleaved stereo PCM16 covering at least the loop plus the seam.</param>
    /// <param name="loopStart">First frame of the loop body.</param>
    /// <param name="loopEnd">Frame the loop wraps at, exclusive.</param>
    /// <param name="seamFrames">Requested seam length in frames.</param>
    public static short[] ApplyLoopSeam(short[]? samples, int loopStart, int loopEnd, int seamFrames)
    {
        if (samples is null || samples.Length < UiSoundPlayer.MixChannels)
        {
            return Array.Empty<short>();
        }

        int frames = samples.Length / UiSoundPlayer.MixChannels;
        if (loopStart < 0 || loopEnd <= loopStart || loopEnd > frames)
        {
            return samples;
        }

        // Only as much seam as there is material after the loop end, and never
        // more than the loop body itself.
        int seam = Math.Min(seamFrames, frames - loopEnd);
        seam = Math.Min(seam, loopEnd - loopStart);

        var output = new short[(long)loopEnd * UiSoundPlayer.MixChannels];
        Array.Copy(samples, output, output.Length);

        for (int frame = 0; frame < seam; frame++)
        {
            // Equal power, with the continuation at full weight at frame 0 so
            // the wrap lands on material that followed the loop end.
            double phase = (frame + 0.5) / seam * (Math.PI / 2.0);
            double startWeight = Math.Sin(phase);
            double continuationWeight = Math.Cos(phase);

            for (int channel = 0; channel < UiSoundPlayer.MixChannels; channel++)
            {
                int startIndex = ((loopStart + frame) * UiSoundPlayer.MixChannels) + channel;
                int continuationIndex = ((loopEnd + frame) * UiSoundPlayer.MixChannels) + channel;
                double blended = (samples[startIndex] * startWeight) +
                                 (samples[continuationIndex] * continuationWeight);
                output[startIndex] = (short)Math.Clamp(Math.Round(blended), short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    /// <summary>
    /// Makes an arbitrary buffer loop without a click by blending its tail over
    /// its head and dropping the frames that were consumed doing so. The result
    /// wraps onto material that was already consecutive in the source, so the
    /// seam carries no step.
    /// </summary>
    /// <param name="stereo">Interleaved stereo PCM16.</param>
    /// <param name="crossfadeFrames">Seam length in frames; values that do not fit are clamped.</param>
    public static short[] ApplyLoopCrossfade(short[]? stereo, int crossfadeFrames)
    {
        if (stereo is null || stereo.Length < UiSoundPlayer.MixChannels)
        {
            return Array.Empty<short>();
        }

        int frames = stereo.Length / UiSoundPlayer.MixChannels;
        int fade = Math.Min(crossfadeFrames, frames / 3);
        if (fade <= 0)
        {
            return stereo;
        }

        int outFrames = frames - fade;
        var output = new short[(long)outFrames * UiSoundPlayer.MixChannels];

        for (int frame = 0; frame < outFrames; frame++)
        {
            for (int channel = 0; channel < UiSoundPlayer.MixChannels; channel++)
            {
                int index = (frame * UiSoundPlayer.MixChannels) + channel;
                if (frame >= fade)
                {
                    output[index] = stereo[index];
                    continue;
                }

                // Equal power, so a correlated bed does not dip in the middle
                // of the seam. At frame 0 the tail is at full weight, which is
                // what makes the wrap continuous.
                double phase = (frame + 0.5) / fade * (Math.PI / 2.0);
                double headWeight = Math.Sin(phase);
                double tailWeight = Math.Cos(phase);
                int tailIndex = (((frames - fade) + frame) * UiSoundPlayer.MixChannels) + channel;
                double blended = (stereo[index] * headWeight) + (stereo[tailIndex] * tailWeight);
                output[index] = (short)Math.Clamp(Math.Round(blended), short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    /// <summary>
    /// Folds one interleaved frame of any channel layout down to stereo, using
    /// the WAVE channel order the ATRAC9 configurations follow (FL, FR, C, LFE,
    /// SL, SR). LFE is left out: it carries no content a pair of desktop
    /// speakers should reproduce.
    /// </summary>
    /// <param name="frame">One frame, one sample per source channel.</param>
    /// <param name="left">Receives the left result.</param>
    /// <param name="right">Receives the right result.</param>
    public static void DownmixFrame(ReadOnlySpan<double> frame, out double left, out double right)
    {
        switch (frame.Length)
        {
            case 0:
                left = 0;
                right = 0;
                return;
            case 1:
                left = frame[0];
                right = frame[0];
                return;
            case 2:
                left = frame[0];
                right = frame[1];
                return;
        }

        double l = frame[0];
        double r = frame[1];
        double centre = frame[2];
        l += centre * CentreCoefficient;
        r += centre * CentreCoefficient;

        // Channel 3 is LFE; surrounds start at 4 and any height/rear pairs past
        // them fold into the same side.
        for (int channel = 4; channel < frame.Length; channel += 2)
        {
            l += frame[channel] * SurroundCoefficient;
            if (channel + 1 < frame.Length)
            {
                r += frame[channel + 1] * SurroundCoefficient;
            }
        }

        left = l * DownmixHeadroom;
        right = r * DownmixHeadroom;
    }

    private static short[]? DecodeStereo(
        Stream stream,
        Atrac9Decoder decoder,
        At9Header header,
        int wantedFrames,
        float gain,
        CancellationToken cancellation)
    {
        double scale = float.IsFinite(gain) && gain > 0f ? gain : 1.0;

        var config = decoder.Config;
        int channels = config.ChannelCount;
        int superframeSamples = config.SuperframeSamples;
        int superframeBytes = config.SuperframeBytes;

        long available = header.DataSize / superframeBytes;
        long needed = ((long)wantedFrames + header.EncoderDelay + superframeSamples - 1) / superframeSamples;
        long superframes = Math.Min(needed, available);
        if (superframes <= 0)
        {
            return null;
        }

        var planar = new short[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            planar[channel] = new short[superframeSamples];
        }

        var output = new short[(long)wantedFrames * UiSoundPlayer.MixChannels];
        var superframe = new byte[superframeBytes];
        var frameScratch = new double[channels];

        stream.Position = header.DataOffset;
        long decodedIndex = 0; // counts the encoder delay too
        int written = 0;

        for (long block = 0; block < superframes && written < wantedFrames; block++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReadExactly(stream, superframe))
            {
                break;
            }

            decoder.Decode(superframe, planar);

            for (int sample = 0; sample < superframeSamples && written < wantedFrames; sample++)
            {
                if (decodedIndex++ < header.EncoderDelay)
                {
                    continue;
                }

                for (int channel = 0; channel < channels; channel++)
                {
                    frameScratch[channel] = planar[channel][sample];
                }

                DownmixFrame(frameScratch, out double left, out double right);
                int index = written * UiSoundPlayer.MixChannels;
                output[index] = (short)Math.Clamp(Math.Round(left * scale), short.MinValue, short.MaxValue);
                output[index + 1] = (short)Math.Clamp(Math.Round(right * scale), short.MinValue, short.MaxValue);
                written++;
            }
        }

        if (written == 0)
        {
            return null;
        }

        if (written < wantedFrames)
        {
            Array.Resize(ref output, written * UiSoundPlayer.MixChannels);
        }

        return output;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = stream.Read(buffer, read, buffer.Length - read);
            if (got <= 0)
            {
                return false;
            }

            read += got;
        }

        return true;
    }

    /// <summary>
    /// Walks the RIFF chunks for the four pieces a bed needs: the codec config
    /// in the extensible fmt extension, the decoded length and encoder delay in
    /// fact, the loop points in smpl, and where the superframes start.
    /// </summary>
    private static At9Header? TryReadHeader(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 12 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            return null;
        }

        stream.Position += 4; // riff size
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            return null;
        }

        var header = new At9Header();

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            int chunkSize = reader.ReadInt32();
            long chunkStart = stream.Position;
            if (chunkSize < 0 || chunkStart + chunkSize > stream.Length)
            {
                break;
            }

            switch (chunkId)
            {
                case "fmt " when chunkSize >= 44:
                    ushort formatTag = reader.ReadUInt16();
                    stream.Position = chunkStart + 24; // SubFormat GUID
                    var subFormat = new Guid(reader.ReadBytes(16));
                    if (formatTag != 0xFFFE || subFormat != Atrac9SubFormat)
                    {
                        return null;
                    }

                    stream.Position += 4; // version info
                    header.ConfigData = reader.ReadBytes(4);
                    break;

                case "fact" when chunkSize >= 12:
                    header.SampleCount = reader.ReadInt32();
                    stream.Position += 4; // input overlap delay
                    header.EncoderDelay = reader.ReadInt32();
                    break;

                case "smpl" when chunkSize >= 60:
                    ReadSampleLoop(reader, header);
                    break;

                case "data":
                    header.DataOffset = chunkStart;
                    header.DataSize = chunkSize;
                    break;
            }

            long next = chunkStart + chunkSize + (chunkSize & 1);
            if (next <= chunkStart)
            {
                break;
            }

            stream.Position = next;
        }

        if (header.ConfigData is null || header.ConfigData.Length != 4 ||
            header.SampleCount <= 0 || header.DataOffset < 0 || header.DataSize <= 0)
        {
            return null;
        }

        if (header.LoopEnd >= header.SampleCount)
        {
            header.LoopEnd = header.SampleCount - 1;
        }

        if (header.LoopStart < 0 || header.LoopEnd <= header.LoopStart)
        {
            header.LoopStart = -1;
            header.LoopEnd = -1;
        }

        if (header.EncoderDelay < 0)
        {
            header.EncoderDelay = 0;
        }

        return header;
    }

    // The WAVE sampler chunk: 36 bytes of preamble, then 24 bytes per loop. Only
    // the first forward loop is honoured, which is all the shell beds declare.
    private static void ReadSampleLoop(BinaryReader reader, At9Header header)
    {
        long start = reader.BaseStream.Position;
        reader.BaseStream.Position = start + 28;
        int loopCount = reader.ReadInt32();
        if (loopCount < 1)
        {
            return;
        }

        reader.BaseStream.Position = start + 36;
        reader.ReadInt32(); // cue point id
        int loopType = reader.ReadInt32();
        int loopStart = reader.ReadInt32();
        int loopEnd = reader.ReadInt32();

        if (loopType != 0 || loopStart < 0 || loopEnd <= loopStart)
        {
            return;
        }

        header.LoopStart = loopStart;
        header.LoopEnd = loopEnd;
    }

    private sealed class At9Header
    {
        public byte[]? ConfigData { get; set; }

        public int SampleCount { get; set; }

        public int EncoderDelay { get; set; }

        public long DataOffset { get; set; } = -1;

        public int DataSize { get; set; }

        public int LoopStart { get; set; } = -1;

        public int LoopEnd { get; set; } = -1;
    }
}
