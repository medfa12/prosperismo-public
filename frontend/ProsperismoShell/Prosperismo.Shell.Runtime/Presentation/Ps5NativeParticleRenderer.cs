// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// particle draw. The shader modules and sprite pixels come from the user's
/// </summary>
public sealed record Ps5NativeParticleResources(
    ReadOnlyMemory<byte> VertexSpirv,
    ReadOnlyMemory<byte> FragmentSpirv,
    Ps5NativeParticleTexture Particle0,
    Ps5NativeParticleTexture Particle1,
    ReadOnlyMemory<byte>? GeometrySpirv = null,
    Ps5NativeVertexStream? VertexStream = null,
    IReadOnlyList<Ps5NativeParticleTexture>? Textures = null,
    IReadOnlyList<int>? TextureAliases = null,
    Gen5NggPrimitiveConnectivity? NggPrimitiveConnectivity = null)
{
    public IReadOnlyList<Ps5NativeParticleTexture> EffectiveTextures =>
        Textures ?? [Particle0, Particle1];

    public bool HasValidTextures =>
        EffectiveTextures.Count > 0 &&
        EffectiveTextures.All(static texture => texture.IsValid) &&
        (TextureAliases is null || IsValidAliases(TextureAliases, EffectiveTextures.Count));

    private static bool IsValidAliases(IReadOnlyList<int> aliases, int count)
    {
        if (aliases.Count != count)
        {
            return false;
        }

        for (var index = 0; index < aliases.Count; index++)
        {
            if (aliases[index] >= index || aliases[index] < -1)
            {
                return false;
            }
        }

        return true;
    }
}

public enum Ps5NativeVertexFormat
{
    Float2,
    Float3,
    Float4,
}

public sealed record Ps5NativeVertexAttribute(
    uint Location,
    uint Offset,
    Ps5NativeVertexFormat Format);

public sealed record Ps5NativeVertexStream(
    ReadOnlyMemory<byte> Data,
    uint Stride,
    IReadOnlyList<Ps5NativeVertexAttribute> Attributes)
{
    public bool IsValid =>
        !Data.IsEmpty && Stride > 0 && (uint)Data.Length % Stride == 0 &&
        Attributes.Count > 0 &&
        Attributes.Select(static attribute => attribute.Location).Distinct().Count() ==
            Attributes.Count;
}

public readonly record struct Ps5NativeViewport(
    float X,
    float Y,
    float Width,
    float Height)
{
    public bool IsValid =>
        float.IsFinite(X) && float.IsFinite(Y) &&
        float.IsFinite(Width) && float.IsFinite(Height) &&
        Width != 0.0f && Height != 0.0f;
}

public sealed record Ps5NativeParticleTexture(
    int Width,
    int Height,
    ReadOnlyMemory<byte> Rgba)
{
    /// <summary>True when the dimensions and tightly packed RGBA payload agree.</summary>
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        Rgba.Length == (long)Width * Height * 4;
}

/// <summary>
/// The five entries preserve the native vertex shader's buffer binding order.
/// </summary>
public sealed record Ps5NativeParticleDraw(
    int Width,
    int Height,
    uint ParticleCount,
    IReadOnlyList<ReadOnlyMemory<byte>> VertexBuffers,
    Ps5NativeViewport? Viewport = null,
    IReadOnlyList<int>? BufferAliases = null)
{
    public const int RequiredVertexBufferCount = 1;

    /// <summary>True when the draw has a usable target and a bounded guest-buffer ABI.</summary>
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        ParticleCount > 0 &&
        (Viewport is null || Viewport.Value.IsValid) &&
        VertexBuffers.Count is >= RequiredVertexBufferCount and <= 16 &&
        VertexBuffers.All(static buffer => !buffer.IsEmpty) &&
        (BufferAliases is null || IsValidAliases(BufferAliases, VertexBuffers.Count));

    private static bool IsValidAliases(IReadOnlyList<int> aliases, int count)
    {
        if (aliases.Count != count)
        {
            return false;
        }

        for (var index = 0; index < aliases.Count; index++)
        {
            if (aliases[index] >= index || aliases[index] < -1)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A tightly packed, top-left-origin RGBA8 frame returned by the renderer.</summary>
public sealed record Ps5NativeParticleFrame(
    int Width,
    int Height,
    ReadOnlyMemory<byte> Rgba)
{
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        Rgba.Length == (long)Width * Height * 4;
}

public sealed record Ps5NativeParticleComputeRequest(
    ReadOnlyMemory<byte> ComputeSpirv,
    ReadOnlyMemory<byte> Resources,
    ReadOnlyMemory<byte> ParticleIds,
    float SampleTime,
    float SimulationStart,
    bool PreSimulation,
    float? SpawnEnd = null,
    bool SpawnWindow = false,
    bool ZeroProperties = true,
    ReadOnlyMemory<byte> InitialProperties = default,
    IReadOnlyList<ReadOnlyMemory<byte>>? ResourceFrames = null,
    bool InterleaveSmallDrawHistory = false,
    IReadOnlyList<IReadOnlyList<ReadOnlyMemory<byte>>>? ResourceBankFrames = null,
    uint TransPatternFlag = 0)
{
    public const int ResourceByteCount = 0xF8;
    public const int SmallParticleBankCount = 8;
    public const int ParticleIdByteCount = 6000 * sizeof(uint);
    public const int ParticlePropertyByteCount = 6000 * 0x44;

    public bool IsValid =>
        !ComputeSpirv.IsEmpty &&
        Resources.Length == ResourceByteCount &&
        ParticleIds.Length == ParticleIdByteCount &&
        (InitialProperties.IsEmpty || InitialProperties.Length == ParticlePropertyByteCount) &&
        float.IsFinite(SampleTime) &&
        float.IsFinite(SimulationStart) &&
        SampleTime >= SimulationStart &&
        SimulationStart >= 0.0f &&
        TransPatternFlag <= byte.MaxValue &&
        ResourceSequencesAreValid() &&
        (!SpawnEnd.HasValue || float.IsFinite(SpawnEnd.Value));

    private bool ResourceSequencesAreValid()
    {
        var expectedFrames = checked((int)MathF.Round((SampleTime - SimulationStart) * 60.0f)) + 1;
        var singleValid = ResourceFrames is null ||
            (ResourceFrames.Count == expectedFrames &&
             ResourceFrames.All(static frame => frame.Length == ResourceByteCount));
        var banksValid = ResourceBankFrames is null ||
            (ResourceBankFrames.Count == SmallParticleBankCount &&
             ResourceBankFrames.All(bank =>
                 bank.Count == expectedFrames &&
                 bank.All(static frame => frame.Length == ResourceByteCount)));
        return singleValid && banksValid &&
            (ResourceFrames is null || ResourceBankFrames is null);
    }
}

/// <summary>
/// Backend-neutral boundary for the recovered native BGLayer draw. A backend
/// owns its GPU resources after initialization and accepts only the changing
/// </summary>
public interface IPs5NativeParticleRenderer : IAsyncDisposable
{
    ValueTask InitializeAsync(
        Ps5NativeParticleResources resources,
        CancellationToken cancellationToken = default);

    ValueTask<Ps5NativeParticleFrame> RenderAsync(
        Ps5NativeParticleDraw draw,
        CancellationToken cancellationToken = default);

    ValueTask<Ps5NativeParticleFrame> RenderSequenceAsync(
        IReadOnlyList<Ps5NativeParticleDraw> draws,
        CancellationToken cancellationToken = default);
}

/// <summary>Recovered sequential ONE/ONE/ADD composition for standalone pass readbacks.</summary>
public static class Ps5NativeParticleCompositor
{
    private static readonly Vector<ushort> SharedClear = CreateSharedClear();
    private static readonly Vector<byte> AlphaLaneMask = CreateAlphaLaneMask();

    /// <summary>
    /// Adds <paramref name="overlay"/> over <paramref name="baseFrame"/>,
    /// subtracting the shared clear colour that is present in both standalone
    /// readbacks. This reproduces the two draws occurring in one RGBA8 target.
    /// </summary>
    public static Ps5NativeParticleFrame CompositeAdditive(
        Ps5NativeParticleFrame baseFrame,
        Ps5NativeParticleFrame overlay)
    {
        if (!baseFrame.IsValid || !overlay.IsValid ||
            baseFrame.Width != overlay.Width || baseFrame.Height != overlay.Height)
        {
            throw new ArgumentException("native particle frames must have matching valid extents");
        }

        var rgba = overlay.Rgba.ToArray();
        var baseMemory = baseFrame.Rgba;
        if (baseFrame.Height >= 128)
        {
            var rowBytes = checked(baseFrame.Width * 4);
            var workerCount = Math.Min(Environment.ProcessorCount, baseFrame.Height);
            Parallel.For(0, workerCount, worker =>
            {
                var startRow = baseFrame.Height * worker / workerCount;
                var endRow = baseFrame.Height * (worker + 1) / workerCount;
                var offset = startRow * rowBytes;
                var byteCount = (endRow - startRow) * rowBytes;
                CompositeRow(
                    baseMemory.Span.Slice(offset, byteCount),
                    rgba.AsSpan(offset, byteCount));
            });
        }
        else
        {
            CompositeRow(baseMemory.Span, rgba);
        }

        return new Ps5NativeParticleFrame(baseFrame.Width, baseFrame.Height, rgba);
    }

    private static void CompositeRow(ReadOnlySpan<byte> baseBytes, Span<byte> overlayBytes)
    {
        var offset = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var byteMax = new Vector<ushort>(byte.MaxValue);
            for (; offset <= overlayBytes.Length - Vector<byte>.Count;
                 offset += Vector<byte>.Count)
            {
                var baseVector = new Vector<byte>(
                    baseBytes.Slice(offset, Vector<byte>.Count));
                var overlayVector = new Vector<byte>(
                    overlayBytes.Slice(offset, Vector<byte>.Count));
                Vector.Widen(baseVector, out var baseLow, out var baseHigh);
                Vector.Widen(overlayVector, out var overlayLow, out var overlayHigh);

                var resultLow = AddSaturated(baseLow, overlayLow, SharedClear, byteMax);
                var resultHigh = AddSaturated(baseHigh, overlayHigh, SharedClear, byteMax);
                var rgb = Vector.Narrow(resultLow, resultHigh);
                var alpha = Vector.Max(baseVector, overlayVector);
                Vector.ConditionalSelect(AlphaLaneMask, alpha, rgb)
                    .CopyTo(overlayBytes.Slice(offset, Vector<byte>.Count));
            }
        }

        for (; offset < overlayBytes.Length; offset += 4)
        {
            overlayBytes[offset] = AddSaturated(
                baseBytes[offset], overlayBytes[offset], sharedClear: 1);
            overlayBytes[offset + 1] = AddSaturated(
                baseBytes[offset + 1], overlayBytes[offset + 1], sharedClear: 1);
            overlayBytes[offset + 2] = AddSaturated(
                baseBytes[offset + 2], overlayBytes[offset + 2], sharedClear: 9);
            overlayBytes[offset + 3] = Math.Max(
                baseBytes[offset + 3], overlayBytes[offset + 3]);
        }
    }

    private static Vector<ushort> AddSaturated(
        Vector<ushort> left,
        Vector<ushort> right,
        Vector<ushort> sharedClear,
        Vector<ushort> maximum)
    {
        var sum = left + right;
        var adjusted = Vector.ConditionalSelect(
            Vector.GreaterThan(sum, sharedClear),
            sum - sharedClear,
            Vector<ushort>.Zero);
        return Vector.Min(adjusted, maximum);
    }

    private static Vector<ushort> CreateSharedClear()
    {
        Span<ushort> values = stackalloc ushort[Vector<ushort>.Count];
        ReadOnlySpan<ushort> pixel = [1, 1, 9, 0];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = pixel[index % pixel.Length];
        }

        return new Vector<ushort>(values);
    }

    private static Vector<byte> CreateAlphaLaneMask()
    {
        Span<byte> values = stackalloc byte[Vector<byte>.Count];
        for (var index = 3; index < values.Length; index += 4)
        {
            values[index] = byte.MaxValue;
        }

        return new Vector<byte>(values);
    }

    private static byte AddSaturated(byte left, byte right, int sharedClear)
    {
        var value = left + right - sharedClear;
        return value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)value;
    }
}
