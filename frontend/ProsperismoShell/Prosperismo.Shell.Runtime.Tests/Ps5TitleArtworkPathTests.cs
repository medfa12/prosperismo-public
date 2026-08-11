// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.GUI.Ps5Home;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Ps5TitleArtworkPathTests
{
    [Fact]
    public void MinecraftProfileIsAnIndependentEmbeddedLogoChannel()
    {
        var profile = Ps5TitleArtwork.DescribeEmbeddedTitleLogo("PPSA17221");

        Assert.Equal((0x0E1A7100L, 0x1530C, 1937, 333), profile);
        Assert.Null(Ps5TitleArtwork.DescribeEmbeddedTitleLogo("CUSA00744"));
    }

    [Fact]
    public void AstroProfileUsesItsHashPinnedPackageWordmark()
    {
        var profile = Ps5TitleArtwork.DescribePackagedIconTitleLogo("PPSA01325");

        Assert.Equal(
            ("sce_sys/icon0.png", 255_807, 426, 98),
            profile);
        Assert.Null(Ps5TitleArtwork.DescribePackagedIconTitleLogo("PPSA17221"));
    }

    [Fact]
    public void EmbeddedLogoProfileRequiresWholeExecutableAndPayloadHashes()
    {
        byte[] payload =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        ];
        byte[] executable = Enumerable.Range(0, 96).Select(i => (byte)i).ToArray();
        Array.Copy(payload, 0, executable, 24, payload.Length);
        var profile = new Ps5EmbeddedTitleLogoProfile(
            "TEST00001",
            executable.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(executable)),
            24,
            payload.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            1,
            1);

        using var stream = new MemoryStream(executable, writable: false);
        Assert.True(profile.TryRead(stream, out var recovered));
        Assert.Equal(payload, recovered);

        executable[0] ^= 0xFF;
        using var changed = new MemoryStream(executable, writable: false);
        Assert.False(profile.TryRead(changed, out _));
    }

    [Fact]
    public void ExecutableResolverMatchesSystemDirectoryAndArtworkIgnoringCase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"prosperismo-title-path-{Guid.NewGuid():N}");
        var system = Path.Combine(root, "SCE_SYS");
        var executable = Path.Combine(root, "EBOOT.BIN");
        var backdrop = Path.Combine(system, "PIC1.DDS");

        try
        {
            Directory.CreateDirectory(system);
            File.WriteAllBytes(executable, []);
            File.WriteAllBytes(backdrop, [0x44, 0x44, 0x53, 0x20]);

            Assert.Equal(system, Ps5TitleArtwork.ResolveSystemDirectoryForExecutable(executable));
            Assert.Equal(backdrop, Ps5TitleArtwork.ResolveBackdropForExecutable(executable));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
