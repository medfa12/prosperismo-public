// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using Prosperismo.Libs.Textures;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class PrismParticlePngAssetTests
{
    [Theory]
    [InlineData(
        "Sce.Vsh.ShellUI.BGLayer.Particle0.png",
        "ff2b9a36d64d4b920e08a6375b766a5c993537984e7eea165c7a8f5e9e0fce05",
        "a3384d30a7ea34745c8edb513a6ecae903e4710cac64837ca050ee73ba7d9e84")]
    [InlineData(
        "Sce.Vsh.ShellUI.BGLayer.Particle1.png",
        "8e92a039b649b91ed2641c462be34273faa90c1870680a8146482d3162e7577b",
        "cc5f7f2389219e475cfbb2ad0b3e87b8dbd615f3987bf5b5d41de662bc5a5937")]
    public void PackagedParticlePngPreservesDecodedPrismPixels(
        string name,
        string expectedFileHash,
        string expectedPixelHash)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..",
            "assets", "big-picture", "3.00", "textures", name));
        var bytes = File.ReadAllBytes(path);
        var rgba = PngRgbaImage.Decode(bytes, out var width, out var height);

        Assert.Equal(expectedFileHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(expectedPixelHash, Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant());
        Assert.Equal(480, width);
        Assert.Equal(270, height);

        var coloured = 0;
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset + 3] > 0 && rgba[offset] != rgba[offset + 1])
            {
                coloured++;
            }
        }
        Assert.True(coloured > 0, "Prism derivative must retain coloured particle pixels.");
    }
}
