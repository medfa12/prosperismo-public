// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Controls;
using Prosperismo.GUI.SystemAssets;
using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// Owns the packaged host SPIR-V AreaFocus/LineFocus programs and the two
/// </summary>
internal sealed class Ps5NativeFocusRuntime : IAsyncDisposable
{
    private readonly VulkanPs5NativeFocusRenderer _renderer;

    private Ps5NativeFocusRuntime(VulkanPs5NativeFocusRenderer renderer)
    {
        _renderer = renderer;
    }

    internal static Ps5NativeFocusRuntime? TryCreate()
    {
        var areaPixelPath = BigPicturePackage.Resolve("3.20/focus/area-p.spv");
        var linePixelPath = BigPicturePackage.Resolve("3.20/focus/line-p.spv");
        var areaVertexPath = BigPicturePackage.Resolve("3.20/focus/area-vv.spv");
        var lineVertexPath = BigPicturePackage.Resolve("3.20/focus/line-vv.spv");
        if (areaPixelPath is null || linePixelPath is null ||
            areaVertexPath is null || lineVertexPath is null ||
            !Ps5NativeSpirvAsset.TryLoad(areaPixelPath, out var areaPixel, out _) ||
            !Ps5NativeSpirvAsset.TryLoad(linePixelPath, out var linePixel, out _) ||
            !Ps5NativeSpirvAsset.TryLoad(areaVertexPath, out var areaVertex, out _) ||
            !Ps5NativeSpirvAsset.TryLoad(lineVertexPath, out var lineVertex, out _) ||
            !Ps5FocusNoiseTexture.TryGetRgba(
                out var noise,
                out var noiseWidth,
                out var noiseHeight))
        {
            return null;
        }

        var colorTable = new byte[ShellFocusPalette.ColorTable.Count * 4];
        for (var index = 0; index < ShellFocusPalette.ColorTable.Count; index++)
        {
            var color = ShellFocusPalette.ColorTable[index];
            colorTable[index * 4] = color.R;
            colorTable[index * 4 + 1] = color.G;
            colorTable[index * 4 + 2] = color.B;
            colorTable[index * 4 + 3] = color.A;
        }

        var resources = new Ps5NativeFocusResources(
            new Ps5NativeFocusProgram(Ps5NativeFocusShaderKind.Area, areaPixel, 128, 64),
            new Ps5NativeFocusProgram(Ps5NativeFocusShaderKind.Line, linePixel, 160, 64),
            new Ps5NativeParticleTexture(ShellFocusPalette.ColorTable.Count, 1, colorTable),
            new Ps5NativeParticleTexture(noiseWidth, noiseHeight, noise),
            new Ps5NativeFocusVertexProgram(Ps5NativeFocusShaderKind.Area, areaVertex, 112, 64),
            new Ps5NativeFocusVertexProgram(Ps5NativeFocusShaderKind.Line, lineVertex, 116, 64));
        var renderer = new VulkanPs5NativeFocusRenderer();
        renderer.InitializeAsync(resources).AsTask().GetAwaiter().GetResult();
        return new Ps5NativeFocusRuntime(renderer);
    }

    internal ValueTask<Ps5NativeParticleFrame> RenderAsync(
        Ps5NativeFocusRenderRequest request,
        CancellationToken cancellationToken) =>
        _renderer.RenderAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => _renderer.DisposeAsync();
}
