// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5VulkanPipelineCacheTests
{
    [Fact]
    public void CacheKeyIsStableForTheSamePipelineContract()
    {
        ReadOnlyMemory<byte>[] programs =
        [
            new byte[] { 0x03, 0x02, 0x23, 0x07 },
            new byte[] { 0x11, 0x22, 0x33, 0x44 },
        ];

        var first = Ps5VulkanPipelineCache.BuildCacheFileName("graphics-a", programs);
        var second = Ps5VulkanPipelineCache.BuildCacheFileName("graphics-a", programs);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}\\.vkpc$", first);
    }

    [Fact]
    public void CacheKeySeparatesStateAndProgramBoundaries()
    {
        var left = Ps5VulkanPipelineCache.BuildCacheFileName(
            "graphics-a",
            [new byte[] { 1, 2 }, new byte[] { 3 }]);
        var changedState = Ps5VulkanPipelineCache.BuildCacheFileName(
            "graphics-b",
            [new byte[] { 1, 2 }, new byte[] { 3 }]);
        var changedBoundary = Ps5VulkanPipelineCache.BuildCacheFileName(
            "graphics-a",
            [new byte[] { 1 }, new byte[] { 2, 3 }]);

        Assert.NotEqual(left, changedState);
        Assert.NotEqual(left, changedBoundary);
    }
}
