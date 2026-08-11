// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>Exact Plane2 selection produced by the 4.03 native background owner.</summary>
public readonly record struct Ps5NativePlaneRoute(
    int PresetIndex,
    bool HighContrast,
    int NativeState,
    int RecordIndex);

/// <summary>
/// CPU translation of NPXS40087 4.03 <c>wave_bg_p</c> and its Plane2 uniform
/// writer. This is the full-screen background plate, not the separate light
/// particle simulation.
/// </summary>
public static class Ps5NativeWavePlateEvaluator
{
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;
    public const int NoisePeriod = 256;
    public const int SystemAreaPresetIndex = 2;
    public const int HomePresetIndex = 4;
    public const int HomeRecordIndex = 2;
    public const int HighContrastRecordIndex = 13;
    public const int SteadyNoParticleThemeIndex = 0x41;

    // Background owner constructor 0x70417, slots 0..29. The managed preset
    // selects one native node state while the steady ThemeColourIndex is in the
    // 0x4x family (NoParticle/None). High contrast selects the paired state by
    // adding 26 before Plane2 applies its own record map.
    private static readonly byte[] PresetToNativeState =
    [
        2, 3, 4, 3, 5, 6, 0, 7, 3, 8,
        9, 1, 4, 25, 4, 10, 11, 12, 13, 14,
        15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
    ];

    // Plane2 map at 0xbd1f00, indexed by the 0..51 state delivered to
    // Plane2::Update. Values select 0x70-byte authored records at 0xbd0ed0.
    private static readonly byte[] NativeStateToRecord =
    [
        0, 1, 2, 2, 2, 2, 2, 2, 1, 1,
        6, 8, 3, 4, 9, 5, 7, 21, 22, 23,
        24, 25, 26, 27, 28, 10, 11, 12, 13, 13,
        13, 13, 13, 13, 12, 12, 17, 19, 14, 15,
        20, 16, 18, 29, 30, 31, 32, 33, 34, 35,
        36, 10,
    ];

    // Authored Plane2 record 2 at 0xbd0ed0. This route is now proven end to end:
    // BackgroundLayer.Start assigns WaveColourPreset.HomeScreen (4) to
    // BackgroundLayerState.PresetColourIndex at +0x0c. The native owner maps
    // preset 4 through ownerTable[4] to node state 5, the common node updater
    // forwards state 5 to Plane2::Update, and Plane2Map[5] at 0xbd1f00 is 2.
    private static readonly float[] Record2Fallback =
    [
        0.035f, 0.21f, 0.58f, 0.0f,
        0.0f, 0.15f, 0.50f, 0.5f,
        0.0f, 0.14f, 0.55f, 1.0f,
        0.0f, 0.44f, 0.90f, 0.0f,
        -150.0f, 50.0f, 400.0f, 0.0f,
        -100.0f, 45.0f, 0.2f, 0.15f,
        1.4f, 1.0f, 0.0f, 0.0f,
    ];

    // The embedded 256-float table at shader file offset 0xd85a70 is the
    // canonical Perlin permutation. Byte storage is equivalent because every
    // entry is an integral float in [0,255].
    private static readonly byte[] Permutation =
    [
        151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,
        140,36,103,30,69,142,8,99,37,240,21,10,23,190,6,148,
        247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,
        57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,
        74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,
        60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,
        65,25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,
        200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,
        52,217,226,250,124,123,5,202,38,147,118,126,255,82,85,212,
        207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,
        119,248,152,2,44,154,163,70,221,153,101,155,167,43,172,9,
        129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,
        218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,241,
        81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,
        184,84,204,176,115,121,50,45,127,4,150,254,138,236,205,93,
        222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,
    ];

    public static int NoisePhase(long frame) =>
        (int)(((frame % NoisePeriod) + NoisePeriod) % NoisePeriod);

    /// <summary>
    /// Resolves a managed wave preset through the native owner's steady 0x4x
    /// route and Plane2's 52-entry state map. Custom theme-colour families use
    /// additional dispatcher branches and are deliberately not folded into
    /// this method.
    /// </summary>
    public static Ps5NativePlaneRoute ResolveSteadyRoute(int presetIndex, bool highContrast = false)
        => ResolveRoute(presetIndex, SteadyNoParticleThemeIndex, highContrast);

    /// <summary>
    /// Resolves the recovered selector branches for Home and System Area.
    /// <c>0x4x</c> values are light/effect modes and return to the preset's
    /// direct state; they must not be confused with literal theme values
    /// <c>0x01..0x06</c> merely because their low nibbles match.
    /// </summary>
    public static Ps5NativePlaneRoute ResolveRoute(
        int presetIndex,
        int themeColourIndex,
        bool highContrast = false)
    {
        if ((uint)presetIndex >= PresetToNativeState.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(presetIndex));
        }

        int nativeState = PresetToNativeState[presetIndex];
        bool specialPreset = presetIndex is SystemAreaPresetIndex or HomePresetIndex;
        if (specialPreset)
        {
            nativeState = themeColourIndex switch
            {
                >= 0x01 and <= 0x06 => 9 + themeColourIndex,
                >= 0x10 and <= 0x12 => 21 + (themeColourIndex - 0x10),
                >= 0x20 and <= 0x22 => 17 + (themeColourIndex - 0x20),
                0x00 => nativeState,
                _ when (themeColourIndex & 0xf0) == 0x40 => nativeState,
                _ => throw new ArgumentOutOfRangeException(nameof(themeColourIndex)),
            };
        }
        else if (themeColourIndex != 0x00 && (themeColourIndex & 0xf0) != 0x40)
        {
            throw new ArgumentOutOfRangeException(nameof(themeColourIndex));
        }

        nativeState += highContrast ? 26 : 0;
        return new Ps5NativePlaneRoute(
            presetIndex,
            highContrast,
            nativeState,
            NativeStateToRecord[nativeState]);
    }

    /// <summary>
    /// Renders record 2 to row-major RGBA8. Coordinates are evaluated on the
    /// console's 1920x1080 design surface even when the bitmap is smaller.
    /// </summary>
    public static void RenderRecord2(int width, int height, long frame, Span<byte> rgba)
    {
        new FrameRenderer(width, height, HomeRecordIndex).Render(frame, rgba);
    }

    /// <summary>
    /// Renders high-contrast record 13. It shares record 2's ramp, light,
    /// projection and specular values, but uses authored light-colour scale
    /// 0.3 and dither value 0.55.
    /// </summary>
    public static void RenderRecord13(int width, int height, long frame, Span<byte> rgba)
    {
        new FrameRenderer(width, height, HighContrastRecordIndex).Render(frame, rgba);
    }

    /// <summary>
    /// Renders any authored Plane2 record. Records other than the two embedded
    /// steady-shell fallbacks are read directly from the user's NPXS40087 ELF.
    /// </summary>
    public static void RenderRecord(int width, int height, int recordIndex, long frame, Span<byte> rgba)
    {
        new FrameRenderer(width, height, recordIndex).Render(frame, rgba);
    }

    /// <summary>
    /// Reusable renderer for a fixed surface. Plane2's ramp, projection,
    /// lighting and radial lookup are invariant; only the integral 0..255
    /// permutation phase changes each draw. Precomputing those invariant terms
    /// is algebraically identical to the shader and keeps the live 60 Hz path
    /// from repeating its expensive normalisation and specular work.
    /// </summary>
    public sealed class FrameRenderer
    {
        private readonly int _width;
        private readonly int _height;
        private readonly RampStop[] _ramp;
        private readonly float _lightR;
        private readonly float _lightG;
        private readonly float _lightB;
        private readonly float _lightX;
        private readonly float _lightY;
        private readonly float _lightZ;
        private readonly float _planeZ;
        private readonly float _angle;
        private readonly float _exponentControl;
        private readonly float _specularIntensity;
        private readonly float _lightColourScale;
        private readonly float _ditherAmplitude;
        private readonly float[] _baseRgb;
        private readonly byte[] _noiseIndex0;
        private readonly byte[] _noiseIndex1;

        public FrameRenderer(int width, int height, int recordIndex = HomeRecordIndex)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
            }

            _width = width;
            _height = height;
            var record = ResolveRecord(recordIndex);
            _ramp =
            [
                new(record[0], record[1], record[2], record[3]),
                new(record[4], record[5], record[6], record[7]),
                new(record[8], record[9], record[10], record[11]),
            ];
            _lightR = record[12];
            _lightG = record[13];
            _lightB = record[14];
            _lightX = record[16];
            _lightY = record[17];
            _lightZ = record[18];
            _planeZ = record[20];
            _angle = record[21];
            _exponentControl = record[22];
            _specularIntensity = record[23];
            var ditherRecordValue = record[24];
            _lightColourScale = record[25];

            // Plane2 draw 0xa2c8e..0xa2cc7 divides record.dither/255 by the
            // live node opacity at object +0x58. The pixel shader applies that
            // same opacity at the end, preserving authored grain strength.
            // A settled plate has opacity 1, which is what this renderer emits.
            _ditherAmplitude = ditherRecordValue / 255.0f;
            int pixels = checked(width * height);
            _baseRgb = new float[pixels * 3];
            _noiseIndex0 = new byte[pixels];
            _noiseIndex1 = new byte[pixels];
            BuildInvariantTerms();
        }

        public void Render(long frame, Span<byte> rgba)
        {
            if (rgba.Length < checked(_width * _height * 4))
            {
                throw new ArgumentException(
                    "The destination is smaller than width*height*4.", nameof(rgba));
            }

            int phase = NoisePhase(frame);
            int pixels = _width * _height;
            for (int pixel = 0; pixel < pixels; pixel++)
            {
                int i0 = (_noiseIndex0[pixel] + phase) & 0xff;
                int i1 = (_noiseIndex1[pixel] + phase) & 0xff;
                float grain = _ditherAmplitude *
                    (Permutation[i0] + Permutation[i1]) / 510.0f;
                int source = pixel * 3;
                int target = pixel * 4;
                rgba[target] = ToUnormByte(_baseRgb[source] + grain);
                rgba[target + 1] = ToUnormByte(_baseRgb[source + 1] + grain);
                rgba[target + 2] = ToUnormByte(_baseRgb[source + 2] + grain);
                rgba[target + 3] = 255;
            }
        }

        private void BuildInvariantTerms()
        {
            float axis = MathF.Cos(_angle * (MathF.PI / 360.0f));
            float radius = MathF.Abs(_planeZ);
            float extentX = 2.0f * radius * axis;
            float extentY = -2.0f * axis * radius * ReferenceHeight / ReferenceWidth;
            float lightRatio = radius / (radius + _lightZ);
            float centerX = lightRatio * _lightX * 0.5f;
            float centerY = lightRatio * _lightY * 0.5f;
            float exponent = MathF.Pow(2.0f, (10.0f * _exponentControl) + 2.0f);

            for (int y = 0; y < _height; y++)
            {
                float py = (y + 0.5f) * ReferenceHeight / _height;
                float v = Math.Clamp(py / ReferenceHeight, 0.0f, 1.0f);
                var baseColour = SampleRamp(v, _ramp);
                for (int x = 0; x < _width; x++)
                {
                    float px = (x + 0.5f) * ReferenceWidth / _width;
                    float u = Math.Clamp(px / ReferenceWidth, 0.0f, 1.0f);
                    float worldX = extentX * (u - 0.5f);
                    float worldY = extentY * (v - 0.5f);
                    float lx = worldX - _lightX;
                    float ly = worldY - _lightY;
                    float lz = _planeZ - _lightZ;
                    Normalize(ref lx, ref ly, ref lz);
                    float vx = worldX;
                    float vy = worldY;
                    float vz = _planeZ;
                    Normalize(ref vx, ref vy, ref vz);
                    float specular = MathF.Pow(
                        MathF.Max(0.0f, -((vx * lx) + (vy * ly) + (vz * lz))), exponent) *
                        _specularIntensity;

                    int pixel = (y * _width) + x;
                    int rgb = pixel * 3;
                    _baseRgb[rgb] = baseColour.R +
                        (_lightR * _lightColourScale * specular);
                    _baseRgb[rgb + 1] = baseColour.G +
                        (_lightG * _lightColourScale * specular);
                    _baseRgb[rgb + 2] = baseColour.B +
                        (_lightB * _lightColourScale * specular);

                    float dx = worldX - centerX;
                    float dy = worldY - centerY;
                    int first = ((int)(extentX * MathF.Sqrt((dx * dx) + (dy * dy)))) & 0xff;
                    int n = Permutation[first];
                    _noiseIndex0[pixel] = (byte)(((int)(py + (px * n))) & 0xff);
                    _noiseIndex1[pixel] = (byte)(((int)(px + (py * n))) & 0xff);
                }
            }
        }
    }

    private static Ps5NativeWaveRecord ResolveRecord(int recordIndex)
    {
        if (Ps5NativeWaveRecordSource.TryLoad(recordIndex, out var packagedRecord) &&
            packagedRecord is not null)
        {
            return packagedRecord;
        }

        if (recordIndex is not HomeRecordIndex and not HighContrastRecordIndex)
        {
            throw new InvalidOperationException(
                $"Plane2 record {recordIndex} is unavailable from the bundled asset package.");
        }

        var values = (float[])Record2Fallback.Clone();
        if (recordIndex == HighContrastRecordIndex)
        {
            values[24] = 0.55f;
            values[25] = 0.30f;
        }

        return new Ps5NativeWaveRecord(values);
    }

    private static Rgb SampleRamp(float t, RampStop[] ramp)
    {
        int segment = t < ramp[1].Position ? 0 : 1;
        var p0 = ramp[segment];
        var p1 = ramp[segment + 1];
        float local = (t - p0.Position) / (p1.Position - p0.Position);

        var before = segment == 0 ? p0 : ramp[segment - 1];
        var after = segment + 2 >= ramp.Length ? p1 : ramp[segment + 2];
        var m0 = segment == 0 ? p1 - p0 : (p1 - before) * 0.5f;
        var m1 = segment + 1 == ramp.Length - 1 ? p1 - p0 : (after - p0) * 0.5f;
        float t2 = local * local;
        float t3 = t2 * local;
        return new Rgb(
            Hermite(p0.R, p1.R, m0.R, m1.R, local, t2, t3),
            Hermite(p0.G, p1.G, m0.G, m1.G, local, t2, t3),
            Hermite(p0.B, p1.B, m0.B, m1.B, local, t2, t3));
    }

    private static float Hermite(float p0, float p1, float m0, float m1, float t, float t2, float t3) =>
        Math.Clamp(p0 + (t * m0) + (t2 * ((3 * (p1 - p0)) - (2 * m0) - m1))
            + (t3 * ((2 * (p0 - p1)) + m0 + m1)), 0.0f, 1.0f);

    private static void Normalize(ref float x, ref float y, ref float z)
    {
        float inverse = 1.0f / MathF.Sqrt((x * x) + (y * y) + (z * z));
        x *= inverse;
        y *= inverse;
        z *= inverse;
    }

    // wave_bg_p writes packed colour to a UNORM intermediate. Applying an
    // extra linear-to-sRGB transform here double-encodes it and produces the
    // visibly incorrect electric-blue plate.
    private static byte ToUnormByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0f), 0, 255);

    private readonly record struct Rgb(float R, float G, float B);

    private readonly record struct RampStop(float R, float G, float B, float Position)
    {
        public static RampStop operator -(RampStop left, RampStop right) =>
            new(left.R - right.R, left.G - right.G, left.B - right.B, left.Position - right.Position);

        public static RampStop operator *(RampStop value, float scale) =>
            new(value.R * scale, value.G * scale, value.B * scale, value.Position * scale);
    }
}
