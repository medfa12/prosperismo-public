// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5VulkanApiTests
{
    [Fact]
    public void PackagedMoltenVkPrecedesDeveloperLoaders()
    {
        var candidates = Ps5VulkanApi.LoaderCandidates("/release");

        Assert.Equal(
            Path.Combine("/release", Ps5VulkanApi.BundledLoaderFileName),
            candidates[0]);
        Assert.Contains("/opt/homebrew/lib/libvulkan.dylib", candidates);
        Assert.Contains("libvulkan.dylib", candidates);
    }

    [Fact]
    public void PackagedMoltenVkDoesNotRequestLoaderOnlyPortabilityEnumeration()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"prosperismo-vulkan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(Ps5VulkanApi.RequiresPortabilityEnumeration(root));
            File.WriteAllBytes(Path.Combine(root, Ps5VulkanApi.BundledLoaderFileName), []);
            Assert.False(Ps5VulkanApi.RequiresPortabilityEnumeration(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
