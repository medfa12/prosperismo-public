// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// </summary>
public enum VagCodec
{
    /// <summary>
    /// Classic PS-ADPCM (SPU-ADPCM): two history samples and five predictor
    /// filters. Used by VAG headers below version 0x20000.
    /// </summary>
    PsAdpcm,

    /// <summary>
    /// HEVAG ("high efficiency VAG"): the same 16-byte frame layout, but four
    /// history samples and a 128-entry predictor table, with the filter index
    /// split across both header bytes. Used by VAG version 0x20001 and later,
    /// which is what the PS5 shell ships.
    /// </summary>
    HighEfficiency,
}

/// <summary>
/// The fixed 0x30-byte "VAGp" file header. All multi-byte fields are
/// big-endian.
/// </summary>
/// <param name="Version">Raw version word at 0x04 (0x20001 for the PS5 shell cues).</param>
/// <param name="SampleRate">Sample rate in Hz from 0x10.</param>
/// <param name="Channels">Channel count from 0x1E; a stored 0 is normalised to 1.</param>
/// <param name="DataLength">ADPCM payload length in bytes from 0x0C.</param>
/// <param name="Name">The 16-byte ASCII stream name at 0x20, trimmed at the first NUL.</param>
/// <param name="Codec">Codec implied by <paramref name="Version"/>.</param>
public sealed record VagHeader(
    uint Version,
    int SampleRate,
    int Channels,
    int DataLength,
    string Name,
    VagCodec Codec);

/// <summary>
/// A decoded VAG stream: interleaved signed 16-bit PCM.
/// </summary>
public sealed class VagClip
{
    /// <summary>Wraps an already-decoded interleaved PCM16 buffer.</summary>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count (at least 1).</param>
    /// <param name="samples">Interleaved PCM16; its length must be a whole number of frames.</param>
    /// <param name="name">Optional stream name carried by the source header.</param>
    public VagClip(int sampleRate, int channels, short[] samples, string name = "")
    {
        ArgumentNullException.ThrowIfNull(samples);
        SampleRate = Math.Max(1, sampleRate);
        Channels = Math.Max(1, channels);
        Samples = samples;
        Name = name;
    }

    /// <summary>Sample rate in Hz.</summary>
    public int SampleRate { get; }

    /// <summary>Channel count.</summary>
    public int Channels { get; }

    /// <summary>Interleaved signed 16-bit PCM, frame-major.</summary>
    public short[] Samples { get; }

    /// <summary>Stream name from the source header, or an empty string.</summary>
    public string Name { get; }

    /// <summary>Number of PCM frames (samples per channel).</summary>
    public int FrameCount => Samples.Length / Channels;

    /// <summary>Playback duration.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    /// <summary>Largest absolute sample value, useful as a "did this decode to silence" check.</summary>
    public int PeakAmplitude
    {
        get
        {
            int peak = 0;
            foreach (var sample in Samples)
            {
                int magnitude = sample == short.MinValue ? -short.MinValue : Math.Abs(sample);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            return peak;
        }
    }
}

/// <summary>
/// evolution) to interleaved PCM16.
///
/// A VAG file is a 0x30-byte big-endian header followed by fixed 16-byte ADPCM
/// frames. Each frame is <c>[shift/filter][flags][14 bytes of nibbles]</c> and
/// expands to 28 samples; the low nibble of each byte comes first. Multi-channel
/// streams interleave whole frames (frame 0 = channel 0, frame 1 = channel 1,
/// ...), so a stereo stream alternates 16-byte frames between the two channels.
///
/// Both codecs use the same frame layout and differ only in how the frame header
/// selects a predictor:
/// <list type="bullet">
/// <item>PS-ADPCM: filter = high nibble of byte 0 (5 filters, 2 taps).</item>
/// <item>HEVAG: filter = high nibble of byte 0 OR'd with the high nibble of
/// byte 1 (128 filters, 4 taps); the flag is the low nibble of byte 1.</item>
/// </list>
/// The predictor coefficients are fixed hardware constants of the SPU/HEVAG
/// decoder; see docs/rco-format.md for how the shell packages these streams.
///
/// Nothing here throws on malformed input: <see cref="TryDecode"/> returns null
/// and out-of-range header fields are clamped.
/// </summary>
public static class VagDecoder
{
    /// <summary>Size of the "VAGp" file header in bytes.</summary>
    public const int HeaderSize = 0x30;

    /// <summary>Size of one ADPCM frame in bytes.</summary>
    public const int FrameSize = 16;

    /// <summary>Samples one ADPCM frame expands to.</summary>
    public const int SamplesPerFrame = 28;

    /// <summary>First version word that selects HEVAG rather than classic PS-ADPCM.</summary>
    public const uint HighEfficiencyVersion = 0x00020000;

    /// <summary>Flag value that forces a frame to decode to silence.</summary>
    private const int MuteFlag = 0x07;

    // Classic PS-ADPCM predictor filters, expressed as the spec's rationals
    // (60/64, 115/64, ...).
    private static readonly float[] PsAdpcmCoefficients =
    {
        0.0f, 0.0f,
        0.9375f, 0.0f,
        1.796875f, -0.8125f,
        1.53125f, -0.859375f,
        1.90625f, -0.9375f,
    };

    // HEVAG predictor filters: 128 entries of 4 taps, flattened. Entries 0..4
    // reproduce the classic PS-ADPCM filters above (with the third and fourth
    // taps zeroed), which is what keeps HEVAG backward compatible.
    //
    // These are fixed constants of the console's HEVAG decoder, not a tuning
    // choice: every value here was checked bit-exactly against the coefficient
    // stores each filter pre-expanded into an 8x4 SIMD matrix whose rows 4..7
    // carry these four taps in column 0.
    private static readonly float[] HevagCoefficients = BuildHevagCoefficients();

    /// <summary>Number of HEVAG predictor filters.</summary>
    public const int HevagFilterCount = 128;

    /// <summary>History taps each HEVAG filter uses.</summary>
    public const int HevagFilterTaps = 4;

    /// <summary>True when the buffer starts with the "VAGp" magic.</summary>
    public static bool LooksLikeVag(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 &&
               data[0] == (byte)'V' && data[1] == (byte)'A' && data[2] == (byte)'G' && data[3] == (byte)'p';
    }

    /// <summary>
    /// Parses the 0x30-byte "VAGp" header, or returns null when the buffer is
    /// too short or does not start with the magic.
    /// </summary>
    public static VagHeader? TryReadHeader(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeVag(data) || data.Length < HeaderSize)
        {
            return null;
        }

        uint version = ReadUInt32BigEndian(data, 0x04);
        long dataLength = ReadUInt32BigEndian(data, 0x0C);
        long sampleRate = ReadUInt32BigEndian(data, 0x10);
        int channels = data[0x1E];

        // A stored 0 means mono on every observed file; anything absurd is
        // clamped so a corrupt header cannot drive a huge allocation.
        if (channels < 1)
        {
            channels = 1;
        }
        else if (channels > 8)
        {
            channels = 8;
        }

        if (sampleRate is < 1 or > 384000)
        {
            sampleRate = 48000;
        }

        long available = data.Length - HeaderSize;
        if (dataLength <= 0 || dataLength > available)
        {
            dataLength = available;
        }

        return new VagHeader(
            version,
            (int)sampleRate,
            channels,
            (int)dataLength,
            ReadName(data.Slice(0x20, 0x10)),
            version >= HighEfficiencyVersion ? VagCodec.HighEfficiency : VagCodec.PsAdpcm);
    }

    /// <summary>
    /// Decodes a complete VAG file (header plus ADPCM payload) to interleaved
    /// PCM16, or returns null when the buffer is not a usable VAG.
    /// </summary>
    public static VagClip? TryDecode(ReadOnlySpan<byte> data)
    {
        var header = TryReadHeader(data);
        if (header is null)
        {
            return null;
        }

        var payload = data.Slice(HeaderSize, header.DataLength);
        var samples = DecodeFrames(payload, header.Channels, header.Codec);
        return samples.Length == 0
            ? null
            : new VagClip(header.SampleRate, header.Channels, samples, header.Name);
    }

    /// <summary>
    /// Decodes a headerless ADPCM frame stream (no "VAGp" header) to interleaved
    /// PCM16. Use this when the container already knows the rate, channel count
    /// and codec.
    /// </summary>
    /// <param name="adpcm">Raw 16-byte frames, channel-interleaved.</param>
    /// <param name="sampleRate">Sample rate in Hz.</param>
    /// <param name="channels">Channel count (at least 1).</param>
    /// <param name="codec">Which predictor set the frames use.</param>
    public static VagClip DecodeRaw(ReadOnlySpan<byte> adpcm, int sampleRate, int channels, VagCodec codec)
    {
        int safeChannels = Math.Clamp(channels, 1, 8);
        return new VagClip(sampleRate, safeChannels, DecodeFrames(adpcm, safeChannels, codec));
    }

    /// <summary>
    /// Returns one HEVAG predictor coefficient. Exposed so the table can be
    /// checked against the console's own constants.
    /// </summary>
    /// <param name="filter">Filter index, 0..127.</param>
    /// <param name="tap">History tap, 0..3.</param>
    public static float GetHevagCoefficient(int filter, int tap)
    {
        if ((uint)filter >= HevagFilterCount || (uint)tap >= HevagFilterTaps)
        {
            return 0f;
        }

        return HevagCoefficients[(filter * HevagFilterTaps) + tap];
    }

    private static short[] DecodeFrames(ReadOnlySpan<byte> adpcm, int channels, VagCodec codec)
    {
        int frames = adpcm.Length / FrameSize;

        // A trailing partial frame is unusable, and so is a frame count that
        // does not divide evenly between the channels.
        int framesPerChannel = frames / channels;
        if (framesPerChannel <= 0)
        {
            return Array.Empty<short>();
        }

        int frameCount = framesPerChannel * SamplesPerFrame;
        var output = new short[frameCount * channels];

        for (int channel = 0; channel < channels; channel++)
        {
            DecodeChannel(adpcm, channel, channels, framesPerChannel, codec, output);
        }

        return output;
    }

    private static void DecodeChannel(
        ReadOnlySpan<byte> adpcm,
        int channel,
        int channels,
        int framesPerChannel,
        VagCodec codec,
        short[] output)
    {
        int history1 = 0;
        int history2 = 0;
        int history3 = 0;
        int history4 = 0;
        int write = channel;

        for (int frame = 0; frame < framesPerChannel; frame++)
        {
            var block = adpcm.Slice(((frame * channels) + channel) * FrameSize, FrameSize);
            byte control = block[0];
            byte flagByte = block[1];
            int shift = control & 0x0F;
            int flag = flagByte & 0x0F;

            int filter;
            float coefficient0;
            float coefficient1;
            float coefficient2 = 0f;
            float coefficient3 = 0f;

            if (codec == VagCodec.HighEfficiency)
            {
                filter = ((control >> 4) & 0x0F) | (flagByte & 0xF0);
                int coefficientBase = filter * HevagFilterTaps;
                coefficient0 = HevagCoefficients[coefficientBase];
                coefficient1 = HevagCoefficients[coefficientBase + 1];
                coefficient2 = HevagCoefficients[coefficientBase + 2];
                coefficient3 = HevagCoefficients[coefficientBase + 3];
            }
            else
            {
                // Only five filters are defined; anything else is a corrupt
                // frame and decodes flat rather than exploding.
                filter = (control >> 4) & 0x0F;
                if (filter > 4)
                {
                    filter = 0;
                }

                if (shift > 12)
                {
                    shift = 9;
                }

                coefficient0 = PsAdpcmCoefficients[filter * 2];
                coefficient1 = PsAdpcmCoefficients[(filter * 2) + 1];
            }

            for (int i = 0; i < SamplesPerFrame; i++)
            {
                int sample = 0;
                if (flag < MuteFlag)
                {
                    byte pair = block[2 + (i / 2)];
                    int code = SignedNibble((i & 1) == 0 ? pair & 0x0F : (pair >> 4) & 0x0F);

                    if (codec == VagCodec.HighEfficiency)
                    {
                        int scaled = (code << 12) >> shift;
                        float predicted = (history1 * coefficient0) +
                                          (history2 * coefficient1) +
                                          (history3 * coefficient2) +
                                          (history4 * coefficient3);
                        sample = scaled + (int)predicted;
                    }
                    else
                    {
                        int scaled = code << (20 - shift);
                        float predicted = ((history1 * coefficient0) + (history2 * coefficient1)) * 256.0f;
                        sample = (scaled + (int)predicted) >> 8;
                    }
                }

                output[write] = Clamp16(sample);
                write += channels;

                // The predictor state keeps the unclamped sample, matching the
                // hardware decoder.
                history4 = history3;
                history3 = history2;
                history2 = history1;
                history1 = sample;
            }
        }
    }

    private static int SignedNibble(int nibble) => nibble >= 8 ? nibble - 16 : nibble;

    private static short Clamp16(int value)
    {
        if (value > short.MaxValue)
        {
            return short.MaxValue;
        }

        return value < short.MinValue ? short.MinValue : (short)value;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        if (end < 0)
        {
            end = field.Length;
        }

        for (int i = 0; i < end; i++)
        {
            if (field[i] < 0x20 || field[i] > 0x7E)
            {
                return string.Empty;
            }
        }

        return end == 0 ? string.Empty : Encoding.ASCII.GetString(field[..end]);
    }

    private static float[] BuildHevagCoefficients()
    {
        return new float[]
        {
0f, 0f, 0f, 0f,  // 0
            0.9375f, 0f, 0f, 0f,
            1.796875f, -0.8125f, 0f, 0f,
            1.53125f, -0.859375f, 0f, 0f,
            1.90625f, -0.9375f, 0f, 0f,
            1.7982178f, -0.86169434f, 0f, 0f,
            1.770874f, -0.8991699f, 0f, 0f,
            1.6992188f, -0.9182129f, 0f, 0f,
            1.6031494f, -0.9375f, 0f, 0f,
            1.4682617f, -0.9375f, 0f, 0f,
            1.3139648f, -0.9375f, 0f, 0f,
            1.142456f, -0.9375f, 0f, 0f,
            0.9560547f, -0.9375f, 0f, 0f,
            0.756958f, -0.9375f, 0f, 0f,
            0.54785156f, -0.9375f, 0f, 0f,
            0.33166504f, -0.9375f, 0f, 0f,
            0.11108398f, -0.9375f, 0f, 0f,  // 16
            -0.11108398f, -0.9375f, 0f, 0f,
            -0.33166504f, -0.9375f, 0f, 0f,
            -0.54785156f, -0.9375f, 0f, 0f,
            -0.756958f, -0.9375f, 0f, 0f,
            -0.9560547f, -0.9375f, 0f, 0f,
            -1.142456f, -0.9375f, 0f, 0f,
            -1.3139648f, -0.9375f, 0f, 0f,
            -1.4682617f, -0.9375f, 0f, 0f,
            -1.6031494f, -0.9375f, 0f, 0f,
            -1.6992188f, -0.9182129f, 0f, 0f,
            -1.770874f, -0.8991699f, 0f, 0f,
            -1.7982178f, -0.86169434f, 0f, 0f,
            0.65625f, -1.125f, 0.40625f, -0.375f,
            -0.78125f, -0.875f, -0.40625f, -0.28125f,
            -1.28125f, -0.90625f, -0.4375f, -0.125f,
            -0.020385742f, -0.3322754f, -0.060302734f, -0.06604004f,  // 32
            -0.9069824f, -0.27111816f, -0.28051758f, 0.051757812f,
            -0.9766846f, -0.3864746f, -0.34350586f, 0.03527832f,
            0.73461914f, -0.579834f, 0.32336426f, -0.15844727f,
            0.46362305f, -0.8479004f, 0.47302246f, -0.1484375f,
            -1.0054932f, -0.31689453f, -0.25280762f, 0.027709961f,
            1.1229248f, 0.24194336f, -0.16870117f, -0.28271484f,
            1.5894775f, -0.37158203f, -0.46289062f, 0.15466309f,
            1.6005859f, -0.5477295f, -0.2746582f, 0.20324707f,
            -0.20361328f, -0.45703125f, -0.78808594f, 0.10253906f,
            0.9544678f, -0.5283203f, 0.25769043f, -0.061767578f,
            1.168335f, -0.16308594f, -0.09240723f, 0.059448242f,
            1.2246094f, -0.31274414f, 0.036621094f, 0.024291992f,
            -0.57922363f, -0.5031738f, -0.66967773f, -0.18225098f,
            -0.71972656f, 0.2902832f, -0.5843506f, -0.84802246f,
            -0.14562988f, -1.112915f, -0.15100098f, -0.38012695f,
            0.33972168f, -0.8676758f, -0.19226074f, -0.17663574f,  // 48
            -0.8952637f, -0.25170898f, -0.27001953f, 0.05444336f,
            0.7479248f, -0.3145752f, -0.03845215f, -0.0021972656f,
            1.154419f, -0.22680664f, 0.012451172f, 0.03149414f,
            0.9614258f, -0.5472412f, 0.25952148f, -0.06567383f,
            -0.8754883f, -0.21911621f, -0.25256348f, 0.05883789f,
            -0.89819336f, -0.2565918f, -0.272583f, 0.053710938f,
            -1.1193848f, -0.42834473f, -0.32641602f, -0.047729492f,
            -0.32202148f, -0.32312012f, -0.23547363f, -0.1998291f,
            0.2286377f, 1.1209717f, 0.22705078f, -0.701416f,
            1.1247559f, 0.22692871f, -0.13720703f, -0.29626465f,
            1.6118164f, -0.36767578f, -0.505249f, 0.16723633f,
            1.5181885f, -0.58496094f, -0.03125f, 0.075927734f,
            -0.32385254f, -0.13964844f, -0.38842773f, -0.8395996f,
            1.1390381f, -0.12792969f, -0.10107422f, 0.06188965f,
            0.20043945f, -0.075683594f, -0.11547852f, -0.51623535f,
            0.51831055f, -0.9259033f, -0.06506348f, -0.27575684f,  // 64
            -1.097168f, -0.4749756f, -0.34265137f, 0.0053710938f,
            -0.31274414f, -0.3338623f, -0.21118164f, -0.23181152f,
            0.38842773f, -0.05895996f, -0.0871582f, -0.17346191f,
            0.9688721f, -0.46923828f, 0.34436035f, -0.12438965f,
            1.229126f, -0.31848145f, 0.038330078f, 0.023803711f,
            1.0253906f, -0.40246582f, 0.18933105f, -0.018920898f,
            -1.0411377f, -0.33874512f, -0.296875f, -0.041015625f,
            1.1568604f, -0.22973633f, 0.013183594f, 0.03125f,
            0.009155273f, -0.27355957f, -0.036376953f, -0.84680176f,
            -1.1160889f, -0.5078125f, -0.36169434f, 0.00061035156f,
            -0.8874512f, -0.23901367f, -0.2631836f, 0.056152344f,
            -0.33447266f, 0.45715332f, 0.7246094f, -0.13293457f,
            1.0977783f, 0.23779297f, -0.08337402f, -0.33007812f,
            1.5992432f, -0.34606934f, -0.47045898f, 0.12878418f,
            1.164917f, -0.23937988f, 0.01586914f, 0.030517578f,
            0.6435547f, -0.52124023f, 0.38134766f, -0.38537598f,  // 80
            -0.9394531f, -0.41296387f, -0.3548584f, -0.055664062f,
            0.8922119f, 0.3079834f, 0.052978516f, -0.30041504f,
            1.2542725f, -0.3499756f, 0.047729492f, 0.020996094f,
            1.3354492f, -0.45422363f, 0.08117676f, 0.01184082f,
            0.0029296875f, -0.037841797f, -0.15405273f, 0.0390625f,
            -0.9914551f, -0.29431152f, -0.2821045f, -0.033081055f,
            -1.0389404f, -0.37438965f, -0.28527832f, 0.019897461f,
            0.039794922f, -0.46948242f, 0.05114746f, -0.1138916f,
            1.0858154f, 0.26782227f, -0.06604004f, -0.3515625f,
            1.4737549f, -0.2290039f, -0.24621582f, -0.07336426f,
            1.0655518f, -0.41784668f, 0.2043457f, -0.020629883f,
            1.5808105f, -0.4696045f, -0.36706543f, 0.23754883f,
            1.2253418f, -0.3137207f, 0.036865234f, 0.024169922f,
            1.1456299f, -0.33654785f, 0.12304688f, 0.005004883f,
            -0.5761719f, -0.611084f, -0.34814453f, -0.14172363f,
            0.9605713f, -0.5280762f, 0.26062012f, -0.061157227f,  // 96
            0.29907227f, -1.0494385f, 0.15856934f, -0.33935547f,
            1.2441406f, -0.33728027f, 0.043945312f, 0.022094727f,
            1.3809814f, -0.5142822f, 0.10168457f, 0.0064697266f,
            1.239502f, -0.33154297f, 0.042114258f, 0.022583008f,
            1.1765137f, -0.17297363f, -0.08996582f, 0.05883789f,
            0.47045898f, -0.5559082f, 0.3470459f, -0.41467285f,
            0.817749f, -0.6907959f, 0.27453613f, -0.13110352f,
            1.3527832f, -0.47705078f, 0.08886719f, 0.009765625f,
            -0.12524414f, -1.1975098f, -0.0982666f, -0.42260742f,
            1.269043f, -0.4572754f, 0.16687012f, -0.01171875f,
            1.2557373f, 0.12060547f, -0.23376465f, -0.17541504f,
            0.9708252f, 0.47338867f, -0.09326172f, -0.39831543f,
            1.5489502f, -0.4119873f, -0.40942383f, 0.25378418f,
            0.81066895f, 0.3864746f, 0.028198242f, -0.25500488f,
            -0.2866211f, -0.8977051f, -0.23730469f, -0.5031738f,
            1.1340332f, -0.493042f, 0.23010254f, -0.030029297f,  // 112
            0.56555176f, -0.7816162f, 0.2133789f, -0.19763184f,
            1.3729248f, -0.50354004f, 0.09790039f, 0.007446289f,
            1.1971436f, -0.2788086f, 0.026733398f, 0.02709961f,
            1.1884766f, -0.1875f, -0.08618164f, 0.057739258f,
            1.0302734f, -0.4194336f, 0.19067383f, -0.021484375f,
            1.1361084f, -0.12463379f, -0.10192871f, 0.06213379f,
            0.20727539f, -1.1016846f, 0.083984375f, -0.37072754f,
            1.2468262f, -0.34069824f, 0.044921875f, 0.021850586f,
            1.0241699f, 0.39648438f, -0.0925293f, -0.36486816f,
            0.8790283f, 0.40478516f, 0.0056152344f, -0.3190918f,
            -0.010742188f, -0.9532471f, -0.06567383f, -0.5579834f,
            0.75598145f, -0.63342285f, 0.33691406f, -0.15197754f,
            1.5045166f, -0.1574707f, -0.4008789f, 0.030883789f,
            1.5947266f, -0.49743652f, -0.34472656f, 0.22912598f,
            0.651001f, 0.36608887f, 0.09460449f, -0.1381836f,
        };
    }
}
