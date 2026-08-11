// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5NativeParticleCompositorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void AdditiveCompositionPreservesSharedClearAndSaturates(int height)
    {
        const int width = 2;
        var basePixels = new byte[width * height * 4];
        var overlayPixels = new byte[basePixels.Length];
        for (var offset = 0; offset < basePixels.Length; offset += 8)
        {
            basePixels[offset] = 1;
            basePixels[offset + 1] = 200;
            basePixels[offset + 2] = 9;
            basePixels[offset + 3] = 40;
            overlayPixels[offset] = 1;
            overlayPixels[offset + 1] = 100;
            overlayPixels[offset + 2] = 9;
            overlayPixels[offset + 3] = 80;

            basePixels[offset + 4] = 255;
            basePixels[offset + 5] = 0;
            basePixels[offset + 6] = 8;
            basePixels[offset + 7] = 255;
            overlayPixels[offset + 4] = 255;
            overlayPixels[offset + 5] = 0;
            overlayPixels[offset + 6] = 0;
            overlayPixels[offset + 7] = 10;
        }

        var result = Ps5NativeParticleCompositor.CompositeAdditive(
            new Ps5NativeParticleFrame(width, height, basePixels),
            new Ps5NativeParticleFrame(width, height, overlayPixels));

        for (var offset = 0; offset < result.Rgba.Length; offset += 8)
        {
            Assert.Equal([1, 255, 9, 80, 255, 0, 0, 255],
                result.Rgba.Span.Slice(offset, 8).ToArray());
        }
    }
}
