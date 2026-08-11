// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.SystemAssets;
using Prosperismo.GUI.SystemAssets.Shell;
using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class PackagedShellDependencyTests
{
    [Fact]
    public void EveryConsumedUiCueIsPackagedAndDecodable()
    {
        var clips = ShellUiSounds.LoadPackagedClips();

        Assert.Equal(ShellUiSounds.EntryNames.Count, clips.Count);
        Assert.All(clips.Values, clip => Assert.True(clip.FrameCount > 0));
    }

    [Fact]
    public void EveryConsumedRasterIconIsPackaged()
    {
        var payloads = ShellIcons.LoadPackagedPayloads();

        Assert.Equal(ShellIcons.EntryNames.Count, payloads.Count);
        Assert.All(payloads.Values, payload => Assert.NotNull(ShellIcons.LooksLikePng(payload)));
    }

    [Fact]
    public void CompletePlane2RecordTableIsPackaged()
    {
        Ps5NativeWaveRecordSource.Invalidate();
        for (var index = 0; index < Ps5NativeWaveRecordSource.RecordCount; index++)
        {
            Assert.True(Ps5NativeWaveRecordSource.TryLoad(index, out var record));
            Assert.NotNull(record);
        }
    }

    [Theory]
    [InlineData("area-p.spv")]
    [InlineData("line-p.spv")]
    public void PackagedFocusPixelProgramsArePortableSpirv(string fileName)
    {
        var path = BigPicturePackage.Resolve($"3.20/focus/{fileName}");
        Assert.True(Ps5NativeSpirvAsset.TryLoad(path, out var spirv, out var error), error);
        Assert.NotEmpty(spirv.ToArray());
    }

    [Theory]
    [InlineData("area-vv.spv")]
    [InlineData("line-vv.spv")]
    public void PackagedFocusVertexProgramsArePortableSpirv(string fileName)
    {
        var path = BigPicturePackage.Resolve($"3.20/focus/{fileName}");
        Assert.True(Ps5NativeSpirvAsset.TryLoad(path, out var spirv, out var error), error);
        Assert.NotEmpty(spirv.ToArray());
    }

    [Fact]
    public void PackagedRippleProgramIsPortableSpirv()
    {
        var path = BigPicturePackage.Resolve("3.00/transitions/ripple-p.spv");
        Assert.True(Ps5NativeSpirvAsset.TryLoad(path, out var spirv, out var error), error);
        Assert.NotEmpty(spirv.ToArray());
    }

    [Fact]
    public void PackageLocatorRejectsTraversal()
    {
        Assert.Null(BigPicturePackage.ResolveFrom(AppContext.BaseDirectory, "../manifest.json"));
    }
}
