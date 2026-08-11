// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.ShaderCompiler.Vulkan;
using System.Runtime.InteropServices;

namespace Prosperismo.Libs.Presentation;

public sealed record Ps5NativeFocusResources(
    Ps5NativeFocusProgram Area,
    Ps5NativeFocusProgram Line,
    Ps5NativeParticleTexture ColorTable,
    Ps5NativeParticleTexture Noise,
    Ps5NativeFocusVertexProgram? AreaVertex = null,
    Ps5NativeFocusVertexProgram? LineVertex = null)
{
    public bool HasNativeVertices =>
        AreaVertex is { IsValid: true, Kind: Ps5NativeFocusShaderKind.Area } &&
        LineVertex is { IsValid: true, Kind: Ps5NativeFocusShaderKind.Line };

    public bool IsValid =>
        Area.IsValid && Area.Kind == Ps5NativeFocusShaderKind.Area &&
        Line.IsValid && Line.Kind == Ps5NativeFocusShaderKind.Line &&
        Area.HostSubgroupSize == Line.HostSubgroupSize &&
        (!HasNativeVertices ||
            (AreaVertex!.HostSubgroupSize == Area.HostSubgroupSize &&
             LineVertex!.HostSubgroupSize == Line.HostSubgroupSize)) &&
        ColorTable.IsValid && ColorTable.Width == 7 && ColorTable.Height == 1 &&
        Noise.IsValid && Noise.Width == 64 && Noise.Height == 64;
}

public readonly record struct Ps5NativeFocusRenderRequest(
    int Width,
    int Height,
    Ps5NativeFocusFrameState State,
    float AreaOpacity,
    float LineOpacity,
    bool RenderArea = true)
{
    public bool IsValid =>
        Width > 0 && Height > 0 && State.IsValid &&
        float.IsFinite(AreaOpacity) && float.IsFinite(LineOpacity);
}

public interface IPs5NativeFocusRenderer : IAsyncDisposable
{
    ValueTask InitializeAsync(
        Ps5NativeFocusResources resources,
        CancellationToken cancellationToken = default);

    ValueTask<Ps5NativeParticleFrame> RenderAsync(
        Ps5NativeFocusRenderRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistent Vulkan host for libScePsm's original AreaFocus and LineFocus
/// remain resident; each frame changes only the two recovered constant buffers.
/// </summary>
public sealed class VulkanPs5NativeFocusRenderer : IPs5NativeFocusRenderer
{
    private readonly object _gate = new();
    private Ps5NativeFocusResources? _resources;
    private Ps5ParticleVulkanSession? _areaSession;
    private Ps5ParticleVulkanSession? _lineSession;
    private int _areaWidth;
    private int _areaHeight;
    private int _lineWidth;
    private int _lineHeight;

    public ValueTask InitializeAsync(
        Ps5NativeFocusResources resources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!resources.IsValid)
        {
            throw new ArgumentException("invalid native focus resources", nameof(resources));
        }

        lock (_gate)
        {
            DisposeSessions();
            _resources = resources;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<Ps5NativeParticleFrame> RenderAsync(
        Ps5NativeFocusRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsValid)
        {
            throw new ArgumentException("invalid native focus request", nameof(request));
        }

        lock (_gate)
        {
            var resources = _resources ??
                throw new InvalidOperationException("native focus renderer has not been initialized");
            EnsureSessions(resources, request);

            var areaState = request.State with
            {
                PassOpacity = Math.Clamp(request.AreaOpacity, 0.0f, 1.0f),
            };
            var lineState = request.State with
            {
                PassOpacity = Math.Clamp(request.LineOpacity, 0.0f, 1.0f),
            };
            var areaWidth = Math.Max(2, (int)MathF.Ceiling(request.State.Width));
            var areaHeight = Math.Max(2, (int)MathF.Ceiling(request.State.Height));
            var nativeVertices = resources.HasNativeVertices;
            var area = request.RenderArea && request.AreaOpacity > 0.0001f
                ? _areaSession!.Render([CreateDraw(
                    areaWidth,
                    areaHeight,
                    areaState,
                    Ps5NativeFocusUniforms.PackArea(areaState, nativeVertices),
                    Ps5NativeFocusUniforms.PackDisplay(areaState),
                    nativeVertices)])
                : Transparent(areaWidth, areaHeight);
            cancellationToken.ThrowIfCancellationRequested();
            var line = request.LineOpacity > 0.0001f
                ? _lineSession!.Render([CreateDraw(
                    request.Width,
                    request.Height,
                    lineState,
                    Ps5NativeFocusUniforms.PackLine(lineState, nativeVertices),
                    Ps5NativeFocusUniforms.PackDisplay(lineState),
                    nativeVertices)])
                : Transparent(request.Width, request.Height);
            return ValueTask.FromResult(CompositeCentered(area, line));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            DisposeSessions();
            _resources = null;
        }
        return ValueTask.CompletedTask;
    }

    private void EnsureSessions(
        Ps5NativeFocusResources resources,
        Ps5NativeFocusRenderRequest request)
    {
        var areaWidth = Math.Max(2, (int)MathF.Ceiling(request.State.Width));
        var areaHeight = Math.Max(2, (int)MathF.Ceiling(request.State.Height));
        if (_areaSession is not null && _lineSession is not null &&
            _areaWidth == areaWidth && _areaHeight == areaHeight &&
            _lineWidth == request.Width && _lineHeight == request.Height)
        {
            return;
        }

        DisposeSessions();
        var areaState = request.State with { PassOpacity = 1.0f };
        var lineState = request.State with { PassOpacity = 1.0f };
        var nativeVertices = resources.HasNativeVertices;
        var areaDraw = CreateDraw(
            areaWidth,
            areaHeight,
            areaState,
            Ps5NativeFocusUniforms.PackArea(areaState, nativeVertices),
            Ps5NativeFocusUniforms.PackDisplay(areaState),
            nativeVertices);
        var lineDraw = CreateDraw(
            request.Width,
            request.Height,
            lineState,
            Ps5NativeFocusUniforms.PackLine(lineState, nativeVertices),
            Ps5NativeFocusUniforms.PackDisplay(lineState),
            nativeVertices);
        var fallbackVertex = nativeVertices
            ? ReadOnlyMemory<byte>.Empty
            : SpirvFixedShaders.CreateFocusFullscreenVertex();
        var vertexStream = nativeVertices ? CreateFocusVertexStream() : null;
        var areaResources = new Ps5NativeParticleResources(
            nativeVertices ? resources.AreaVertex!.VertexSpirv : fallbackVertex,
            resources.Area.FragmentSpirv,
            resources.ColorTable,
            resources.Noise,
            VertexStream: vertexStream);
        var lineResources = new Ps5NativeParticleResources(
            nativeVertices ? resources.LineVertex!.VertexSpirv : fallbackVertex,
            resources.Line.FragmentSpirv,
            resources.ColorTable,
            resources.Noise,
            VertexStream: vertexStream);
        var transparent = (R: 0.0f, G: 0.0f, B: 0.0f, A: 0.0f);
        _areaSession = new Ps5ParticleVulkanSession(
            areaResources,
            areaDraw,
            drawCapacity: 1,
            verticesPerDrawUnit: nativeVertices ? 6u : 3u,
            additiveBlend: false,
            clearColor: transparent);
        try
        {
            _lineSession = new Ps5ParticleVulkanSession(
                lineResources,
                lineDraw,
                drawCapacity: 1,
                verticesPerDrawUnit: nativeVertices ? 6u : 3u,
                additiveBlend: false,
                clearColor: transparent);
        }
        catch
        {
            _areaSession.Dispose();
            _areaSession = null;
            throw;
        }
        _areaWidth = areaWidth;
        _areaHeight = areaHeight;
        _lineWidth = request.Width;
        _lineHeight = request.Height;
    }

    private static Ps5NativeParticleDraw CreateDraw(
        int width,
        int height,
        Ps5NativeFocusFrameState state,
        byte[] focusConstants,
        byte[] displayConstants,
        bool nativeVertices)
    {
        var buffers = new List<ReadOnlyMemory<byte>>
        {
            focusConstants,
            displayConstants,
            new byte[4],
            new byte[4],
            new byte[4],
        };
        Ps5NativeViewport? viewport = null;
        if (nativeVertices)
        {
            var kind = focusConstants.Length == Ps5NativeFocusUniforms.AreaBytes
                ? Ps5NativeFocusShaderKind.Area
                : Ps5NativeFocusShaderKind.Line;
            buffers.Add(Ps5NativeFocusUniforms.PackVertex(state, width, height, kind));
            var cropLeft = state.CenterX - width * 0.5f;
            var cropTop = state.CenterY - height * 0.5f;
            viewport = new Ps5NativeViewport(
                -cropLeft,
                1080.0f - cropTop,
                1920.0f,
                -1080.0f);
        }
        return new Ps5NativeParticleDraw(
            width,
            height,
            1,
            buffers,
            viewport);
    }

    internal static Ps5NativeVertexStream CreateFocusVertexStream()
    {
        ReadOnlySpan<float> vertices =
        [
            0, 0, 0, 1, 1, 1, 1, 0, 0,
            1, 0, 0, 1, 1, 1, 1, 1, 0,
            0, 1, 0, 1, 1, 1, 1, 0, 1,
            0, 1, 0, 1, 1, 1, 1, 0, 1,
            1, 0, 0, 1, 1, 1, 1, 1, 0,
            1, 1, 0, 1, 1, 1, 1, 1, 1,
        ];
        return new Ps5NativeVertexStream(
            MemoryMarshal.AsBytes(vertices).ToArray(),
            36,
            [
                new Ps5NativeVertexAttribute(0, 0, Ps5NativeVertexFormat.Float3),
                new Ps5NativeVertexAttribute(1, 12, Ps5NativeVertexFormat.Float4),
                new Ps5NativeVertexAttribute(2, 28, Ps5NativeVertexFormat.Float2),
            ]);
    }

    private static Ps5NativeParticleFrame Transparent(int width, int height) =>
        new(width, height, new byte[checked(width * height * 4)]);

    internal static Ps5NativeParticleFrame CompositeNormal(
        Ps5NativeParticleFrame area,
        Ps5NativeParticleFrame line)
    {
        if (!area.IsValid || !line.IsValid ||
            area.Width != line.Width || area.Height != line.Height)
        {
            throw new ArgumentException("focus passes must be matching RGBA frames");
        }

        var bottom = area.Rgba.Span;
        var top = line.Rgba.Span;
        var output = new byte[bottom.Length];
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            var sourceAlpha = top[offset + 3] / 255.0f;
            var destinationAlpha = bottom[offset + 3] / 255.0f;
            var alpha = sourceAlpha + destinationAlpha * (1.0f - sourceAlpha);
            for (var channel = 0; channel < 3; channel++)
            {
                var premultiplied = top[offset + channel] / 255.0f * sourceAlpha +
                    bottom[offset + channel] / 255.0f * destinationAlpha *
                    (1.0f - sourceAlpha);
                output[offset + channel] = alpha > 0.0f
                    ? (byte)Math.Clamp(MathF.Round(premultiplied / alpha * 255.0f), 0.0f, 255.0f)
                    : (byte)0;
            }
            output[offset + 3] =
                (byte)Math.Clamp(MathF.Round(alpha * 255.0f), 0.0f, 255.0f);
        }
        return new Ps5NativeParticleFrame(area.Width, area.Height, output);
    }

    internal static Ps5NativeParticleFrame CompositeCentered(
        Ps5NativeParticleFrame area,
        Ps5NativeParticleFrame line)
    {
        if (!area.IsValid || !line.IsValid ||
            area.Width > line.Width || area.Height > line.Height)
        {
            throw new ArgumentException("area focus must fit inside the line focus plane");
        }

        var paddedArea = new byte[checked(line.Width * line.Height * 4)];
        var offsetX = (line.Width - area.Width) / 2;
        var offsetY = (line.Height - area.Height) / 2;
        for (var y = 0; y < area.Height; y++)
        {
            area.Rgba.Span.Slice(y * area.Width * 4, area.Width * 4).CopyTo(
                paddedArea.AsSpan(((y + offsetY) * line.Width + offsetX) * 4));
        }

        return CompositeNormal(
            new Ps5NativeParticleFrame(line.Width, line.Height, paddedArea),
            line);
    }

    private void DisposeSessions()
    {
        _lineSession?.Dispose();
        _lineSession = null;
        _areaSession?.Dispose();
        _areaSession = null;
        _areaWidth = 0;
        _areaHeight = 0;
        _lineWidth = 0;
        _lineHeight = 0;
    }
}
