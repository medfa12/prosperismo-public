// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Per-frame values consumed by the original UI3 AreaFocus and LineFocus
/// programs. Coordinates use the 1920x1080 UI reference space used literally
/// by the 4.03 <c>FocusRenderManager</c> implementation.
/// </summary>
public readonly record struct Ps5NativeFocusFrameState(
    float CenterX,
    float CenterY,
    float Width,
    float Height,
    float Radius,
    float Moving,
    float Pressing,
    float Showing,
    float Angle,
    float Distance,
    float Time,
    float PassOpacity = 1.0f,
    float ViewportScale = 1.0f,
    uint DisplayMode = 0,
    float ShowAlpha = 0.0f,
    float InOutScale = 1.0f)
{
    public bool IsValid =>
        float.IsFinite(CenterX) && float.IsFinite(CenterY) &&
        float.IsFinite(Width) && float.IsFinite(Height) &&
        float.IsFinite(Radius) && float.IsFinite(Moving) &&
        float.IsFinite(Pressing) && float.IsFinite(Showing) &&
        float.IsFinite(Angle) && float.IsFinite(Distance) &&
        float.IsFinite(Time) && float.IsFinite(PassOpacity) &&
        float.IsFinite(ViewportScale) && float.IsFinite(ShowAlpha) &&
        float.IsFinite(InOutScale) &&
        Width > 0.0f && Height > 0.0f &&
        ViewportScale > 0.0f;
}

/// <summary>
/// Byte-exact constant-buffer packing recovered from the 4.03 embedded managed
/// UI3 assembly and the reflection metadata in libScePsm's AreaFocus and
/// LineFocus shader ELFs.
/// </summary>
public static class Ps5NativeFocusUniforms
{
    public const int AreaBytes = 128;
    public const int LineBytes = 160;
    public const int DisplayBytes = 8;
    public const int AreaVertexBytes = 112;
    public const int LineVertexBytes = 116;

    private const float ReferenceHalfWidth = 960.0f;
    private const float ReferenceHalfHeight = 540.0f;
    private const float LineThickness = 3.0f;
    private const float LineOffset = 3.0f;
    private const float NoiseScale = 5.0f;
    private const float NoiseMoveFrequency = 0.25f;
    private const float AreaAlphaGammaReciprocal = 1.0f / 0.8f;
    private const float LineAlphaGammaReciprocal = 1.0f;
    private const float LineMinOpacity = 0.065f;
    private const float AreaOpacityDecreaseRate = 30.0f;
    private const float AreaOpacityMinimum = 0.5f;
    private const float AreaWarpFadePixels = 80.0f;
    private const float AreaWarpFadeThresholdRatio = 0.1f;
    private const float WarpGradientRatioIntensity = 0.2f;
    private const float WarpGradientToeValue = 0.3f;

    /// <summary>Packs the original AreaFocus c0 buffer.</summary>
    public static byte[] PackArea(
        Ps5NativeFocusFrameState state,
        bool globalClipCoordinates = false)
    {
        Validate(state);
        var result = new byte[AreaBytes];
        PackShared(result, state, globalClipCoordinates, roundCorner: true);

        var ratio = state.Height / state.Width;
        var shortRatio = ratio <= 1.0f ? ratio : 1.0f / ratio;
        var halfWidth = Math.Max(state.Width * 0.5f, 1.0f);
        var halfHeight = Math.Max(state.Height * 0.5f, 1.0f);
        var area = halfWidth / 1920.0f * (halfHeight / 1080.0f) /
            (state.ViewportScale * state.ViewportScale);
        var areaOpacity = Math.Max(
            1.0f - area * AreaOpacityDecreaseRate,
            AreaOpacityMinimum);
        var warpFade = AreaWarpFadePixels / state.Width * state.Moving;
        warpFade *= 1.0f - SmoothStep(
            AreaWarpFadeThresholdRatio - 0.1f,
            AreaWarpFadeThresholdRatio + 0.1f,
            shortRatio);
        var warpGradient = 1.0f / Lerp(
            1.0f,
            shortRatio,
            WarpGradientRatioIntensity);
        var morph = state.Moving * 0.5f * Lerp(1.0f, ratio, 0.0f);
        var (noiseX, noiseY) = NoiseOrbit(state.Time);
        var (shimmerX, shimmerY) = Shimmer(state.Time);

        WriteSingle(result, 0x40, state.Angle);
        WriteSingle(result, 0x44, state.Pressing);
        WriteSingle(result, 0x48, 3.0f / state.Width);
        WriteSingle(result, 0x4C, state.Moving);
        WriteSingle(result, 0x50, NoiseScale);
        WriteSingle(result, 0x54, AreaAlphaGammaReciprocal);
        WriteSingle(result, 0x58, noiseX);
        WriteSingle(result, 0x5C, noiseY);
        WriteSingle(result, 0x60, state.ShowAlpha);
        WriteSingle(result, 0x64, warpGradient);
        WriteSingle(result, 0x68, WarpGradientToeValue);
        WriteSingle(result, 0x6C, areaOpacity);
        WriteSingle(result, 0x70, warpFade);
        WriteSingle(result, 0x74, shimmerX);
        WriteSingle(result, 0x78, shimmerY);
        // CalcMorphByRatioIntensity still contributes moving*0.5 when the
        // configured ratio interpolation intensity is zero.
        WriteSingle(result, 0x7C, morph);
        return result;
    }

    /// <summary>Packs the original LineFocus c0 buffer.</summary>
    public static byte[] PackLine(
        Ps5NativeFocusFrameState state,
        bool globalClipCoordinates = false)
    {
        Validate(state);
        var result = new byte[LineBytes];
        PackShared(result, state, globalClipCoordinates, roundCorner: false);

        var halfWidth = Math.Max(state.Width * 0.5f, 1.0f);
        var thickness = LineThickness / halfWidth * state.ViewportScale *
            (1.0f - state.Moving);
        var offset = LineOffset / halfWidth * state.ViewportScale *
            (1.0f - state.Pressing);
        var (noiseX, noiseY) = NoiseOrbit(state.Time);
        var tone = CalculateLineToneCurve();

        WriteSingle(result, 0x40, thickness);
        WriteSingle(result, 0x44, offset);
        WriteSingle(result, 0x48, 3.0f / state.Width);
        // 0x4c is cbuffer alignment padding for scalar u_Pixel.
        // The line record packs these scalars one dword earlier than AreaFocus.
        // The ISA is authoritative here: loads at PCs 0x16c, 0x2c8 and 0x184
        // read 0x4c, 0x50 and 0x54 respectively. Reflection member order alone
        // obscures this difference because the following tone vectors begin at
        // the same 0x60 boundary in both records.
        WriteSingle(result, 0x4C, NoiseScale);
        WriteSingle(result, 0x50, LineMinOpacity);
        WriteSingle(result, 0x54, noiseX);
        WriteSingle(result, 0x58, noiseY);
        // 0x5c is the alignment padding before u_ToneCurveToe.
        WriteVector4(result, 0x60, tone.Toe);
        WriteVector4(result, 0x70, tone.Mid);
        WriteVector4(result, 0x80, tone.Shoulder);
        WriteSingle(result, 0x90, 0.2f);
        WriteSingle(result, 0x94, 0.9f);
        WriteSingle(result, 0x98, state.ShowAlpha);
        WriteSingle(
            result,
            0x9C,
            (1.0f - state.Moving * 4.0f) * (1.0f - state.Pressing));
        _ = LineAlphaGammaReciprocal; // LineFocus has no AlphaGamma c0 member.
        return result;
    }

    /// <summary>Packs c1: UIRenderer's global alpha and intensity.</summary>
    public static byte[] PackDisplay(Ps5NativeFocusFrameState state)
    {
        Validate(state);
        var result = new byte[DisplayBytes];
        WriteSingle(result, 0x00, Math.Clamp(state.PassOpacity, 0.0f, 1.0f));
        WriteSingle(result, 0x04, 1.0f);
        return result;
    }

    /// <summary>
    /// Packs the original Area/Line focus vertex cbuffer. The quad is expressed
    /// in the PUI 1920x1080 design space; a cropped Vulkan viewport maps the
    /// resulting global clip position into the small offscreen target without
    /// </summary>
    public static byte[] PackVertex(
        Ps5NativeFocusFrameState state,
        float quadWidth,
        float quadHeight,
        Ps5NativeFocusShaderKind kind)
    {
        Validate(state);
        if (!float.IsFinite(quadWidth) || !float.IsFinite(quadHeight) ||
            quadWidth <= 0.0f || quadHeight <= 0.0f)
        {
            throw new ArgumentException("focus vertex quad dimensions must be finite and positive");
        }

        var result = new byte[kind == Ps5NativeFocusShaderKind.Area
            ? AreaVertexBytes
            : LineVertexBytes];
        WriteSingle(result, 0x00, (state.CenterX - ReferenceHalfWidth) / ReferenceHalfWidth);
        WriteSingle(result, 0x04, (ReferenceHalfHeight - state.CenterY) / ReferenceHalfWidth);
        WriteSingle(result, 0x08, state.Width * 0.5f / ReferenceHalfWidth);
        WriteSingle(result, 0x0C, state.Height * 0.5f / ReferenceHalfWidth);

        var warp = CalculateWarpDistortion(state.Angle, state.Moving, state.Distance, state.Height / state.Width);
        WriteVector4(result, 0x10, warp);

        var left = state.CenterX - quadWidth * 0.5f;
        var top = state.CenterY - quadHeight * 0.5f;
        // Column-major world matrix, matching the ISA's s20..s35 MAC order.
        WriteSingle(result, 0x20, quadWidth / ReferenceHalfWidth);
        WriteSingle(result, 0x34, -quadHeight / ReferenceHalfHeight);
        WriteSingle(result, 0x48, 1.0f);
        WriteSingle(result, 0x50, (left - ReferenceHalfWidth) / ReferenceHalfWidth);
        WriteSingle(result, 0x54, (ReferenceHalfHeight - top) / ReferenceHalfHeight);
        WriteSingle(result, 0x5C, 1.0f);
        WriteVector4(result, 0x60, (1.0f, 1.0f, 1.0f, 1.0f));
        if (kind == Ps5NativeFocusShaderKind.Line)
        {
            WriteSingle(result, 0x70, state.InOutScale);
        }
        return result;
    }

    public static (float X, float Y) NoiseOrbit(float time)
    {
        var phase = time * NoiseMoveFrequency;
        return (MathF.Sin(phase), MathF.Cos(phase));
    }

    public static (float X, float Y) Shimmer(float time)
    {
        const float speed = 1.0f;
        const float frequency = 5.0f;
        var first = MathF.Max(time * speed % frequency - frequency + 1.0f, -1.0f);
        var second = MathF.Max((time * speed + 0.5f) % frequency - frequency + 1.0f, -1.0f);
        return (MathF.Cos(first * MathF.PI), MathF.Cos(second * MathF.PI));
    }

    private static void PackShared(
        byte[] destination,
        Ps5NativeFocusFrameState state,
        bool globalClipCoordinates,
        bool roundCorner)
    {
        // With no ancestor EdgeFadeEffect, UIRenderer supplies identity fade
        // planes. This is the normal HOME card path.
        for (var offset = 0; offset < 0x20; offset += sizeof(float))
        {
            WriteSingle(destination, offset, 1.0f);
        }

        var focusRadius = state.Radius / Math.Max(state.Width * 0.5f, 1.0f) *
            state.ViewportScale;
        if (globalClipCoordinates)
        {
            var globalRadius = state.Radius / ReferenceHalfWidth * state.ViewportScale;
            WriteSingle(destination, 0x20,
                (state.CenterX - ReferenceHalfWidth) / ReferenceHalfWidth);
            WriteSingle(destination, 0x24,
                (ReferenceHalfHeight - state.CenterY) / ReferenceHalfWidth);
            WriteSingle(destination, 0x28,
                state.Width * 0.5f / ReferenceHalfWidth - globalRadius);
            WriteSingle(destination, 0x2C,
                state.Height * 0.5f / ReferenceHalfWidth - globalRadius);
            WriteSingle(destination, 0x30, globalRadius);
            var flags = state.DisplayMode | 0x08u | (roundCorner ? 0x04u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(0x34), flags);
        }
        else
        {
            // Local stand-in path retained for missing/unsupported vertex
            // programs. Its ClipPos is not global and must not set bit 0x08.
            WriteSingle(destination, 0x20, 0.0f);
            WriteSingle(destination, 0x24, 0.0f);
            WriteSingle(destination, 0x28, 1.0f);
            WriteSingle(destination, 0x2C, 1.0f);
            WriteSingle(destination, 0x30, focusRadius);
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(0x34), state.DisplayMode);
        }
        WriteSingle(destination, 0x38, focusRadius);
        WriteSingle(destination, 0x3C, state.Height / state.Width);
    }

    private static ToneCurve CalculateLineToneCurve()
    {
        ReadOnlySpan<(float X, float Y)> points =
        [
            (0.0f, 0.0f),
            (0.2f, 0.25f),
            (0.9f, 0.9f),
            (1.0f, 1.0f),
        ];
        var x = new float[points.Length + 2];
        var y = new float[points.Length + 2];
        x[0] = points[0].X - 0.01f;
        y[0] = points[0].Y;
        x[^1] = points[^1].X + 0.01f;
        y[^1] = points[^1].Y;
        for (var index = 1; index < x.Length - 1; index++)
        {
            x[index] = points[index - 1].X;
            y[index] = points[index - 1].Y;
        }

        var h = new float[x.Length - 1];
        for (var index = 0; index < h.Length; index++)
        {
            h[index] = x[index + 1] - x[index];
        }

        var matrix = new float[y.Length, y.Length];
        var rhs = new float[y.Length];
        for (var row = 0; row < y.Length; row++)
        {
            if (row == 0 || row == y.Length - 1)
            {
                matrix[row, row] = 1.0f;
            }
            else
            {
                matrix[row, row - 1] = h[row - 1];
                matrix[row, row] = 2.0f * (h[row - 1] + h[row]);
                matrix[row, row + 1] = h[row];
                rhs[row] = h[row] != 0.0f && h[row - 1] != 0.0f
                    ? 3.0f * (y[row + 1] - y[row]) / h[row] -
                        3.0f * (y[row] - y[row - 1]) / h[row - 1]
                    : 0.0f;
            }
        }

        var curvature = Solve(matrix, rhs);
        var linear = new float[h.Length];
        var cubic = new float[h.Length];
        for (var index = 0; index < h.Length; index++)
        {
            if (h[index] == 0.0f)
            {
                continue;
            }

            linear[index] = (y[index + 1] - y[index]) / h[index] -
                h[index] * (curvature[index + 1] + 2.0f * curvature[index]) / 3.0f;
            cubic[index] = (curvature[index + 1] - curvature[index]) /
                (3.0f * h[index]);
        }

        return new ToneCurve(
            [y[1], linear[1], curvature[1], cubic[1]],
            [y[2], linear[2], curvature[2], cubic[2]],
            [y[3], linear[3], curvature[3], cubic[3]]);
    }

    private static (float X, float Y, float Z, float W) CalculateWarpDistortion(
        float angle,
        float moving,
        float distance,
        float ratio)
    {
        var strain = ratio < 0.25f ? 0.0f : 0.75f;
        var amount = MathF.Min(0.2f, strain * moving * distance);
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var sineSquared = sine * sine;
        var cosineSquared = cosine * cosine;
        var stretched = 1.0f / (1.0f - amount);
        var offDiagonal = sine * cosine * (1.0f - stretched);
        return (
            cosineSquared + sineSquared * stretched,
            offDiagonal,
            offDiagonal,
            sineSquared + cosineSquared * stretched);
    }

    private static float[] Solve(float[,] matrix, float[] rhs)
    {
        var count = rhs.Length;
        var augmented = new float[count, count + 1];
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < count; column++)
            {
                augmented[row, column] = matrix[row, column];
            }
            augmented[row, count] = rhs[row];
        }

        for (var pivot = 0; pivot < count; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < count; row++)
            {
                if (MathF.Abs(augmented[row, pivot]) > MathF.Abs(augmented[best, pivot]))
                {
                    best = row;
                }
            }
            for (var column = pivot; column <= count; column++)
            {
                (augmented[pivot, column], augmented[best, column]) =
                    (augmented[best, column], augmented[pivot, column]);
            }
            var divisor = augmented[pivot, pivot];
            if (MathF.Abs(divisor) < float.Epsilon)
            {
                return new float[count];
            }
            for (var column = pivot; column <= count; column++)
            {
                augmented[pivot, column] /= divisor;
            }
            for (var row = 0; row < count; row++)
            {
                if (row == pivot)
                {
                    continue;
                }
                var factor = augmented[row, pivot];
                for (var column = pivot; column <= count; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        var result = new float[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = augmented[index, count];
        }
        return result;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge0 == edge1)
        {
            return value < edge0 ? 0.0f : 1.0f;
        }
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static void Validate(Ps5NativeFocusFrameState state)
    {
        if (!state.IsValid)
        {
            throw new ArgumentException("native focus state is not finite or has empty geometry", nameof(state));
        }
    }

    private static void WriteSingle(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset),
            BitConverter.SingleToInt32Bits(value));

    private static void WriteVector4(byte[] destination, int offset, ReadOnlySpan<float> values)
    {
        for (var index = 0; index < 4; index++)
        {
            WriteSingle(destination, offset + index * sizeof(float), values[index]);
        }
    }

    private static void WriteVector4(
        byte[] destination,
        int offset,
        (float X, float Y, float Z, float W) values)
    {
        WriteSingle(destination, offset, values.X);
        WriteSingle(destination, offset + 4, values.Y);
        WriteSingle(destination, offset + 8, values.Z);
        WriteSingle(destination, offset + 12, values.W);
    }

    private sealed record ToneCurve(float[] Toe, float[] Mid, float[] Shoulder);
}
