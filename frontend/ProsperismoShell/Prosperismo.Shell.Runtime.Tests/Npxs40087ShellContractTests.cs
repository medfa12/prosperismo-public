// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using Prosperismo.Libs.Presentation;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class Npxs40087ShellContractTests
{
    [Fact]
    public void NativeRectangleRequiresFourCornerTriangleStripExpansion()
    {
        var draw = Npxs40087ShellContract.LightRectangle;

        Assert.Equal(20, Npxs40087ShellContract.NggPrimitiveExportTarget);
        Assert.Equal(3, draw.GuestSubmittedVerticesPerElement);
        Assert.True(draw.RequiresHostVertexExpansion);
        Assert.True(draw.IsCompatibleHostDraw(
            Npxs40087HostTopology.TriangleStrip,
            verticesPerElement: 4,
            indexed: false));
        Assert.Equal(new Npxs40087Triangle(0, 1, 2), draw.FirstTriangle);
        Assert.Equal(new Npxs40087Triangle(2, 1, 3), draw.SecondTriangle);
    }

    [Theory]
    [MemberData(nameof(ParticleDraws))]
    public void ParticleBillboardsRemainSixVertexNonIndexedTriangleLists(
        Npxs40087DrawTopologyContract draw)
    {
        Assert.Null(draw.GuestPrimitiveType);
        Assert.False(draw.RequiresHostVertexExpansion);
        Assert.True(draw.IsCompatibleHostDraw(
            Npxs40087HostTopology.TriangleList,
            verticesPerElement: 6,
            indexed: false));
        Assert.Equal(new Npxs40087Triangle(0, 1, 2), draw.FirstTriangle);
        Assert.Equal(new Npxs40087Triangle(3, 4, 5), draw.SecondTriangle);
    }

    [Fact]
    public void PaletteBytesMatchCommittedFirmwareSeederAssets()
    {
        var colorDirectory = Path.Combine(
            FindRepositoryRoot(),
            "assets",
            "big-picture",
            "12.40",
            "background",
            "colors");
        var bootBytes = Npxs40087ShellContract.BootPalette.ToBytes();
        var loginBytes = Npxs40087ShellContract.LoginPalette.ToBytes();
        var homeBytes = Npxs40087ShellContract.HomePalette.ToBytes();

        Assert.Equal(File.ReadAllBytes(Path.Combine(colorDirectory, "boot-color-cb.bin")), bootBytes);
        Assert.Equal(File.ReadAllBytes(Path.Combine(colorDirectory, "login-color-cb.bin")), loginBytes);
        Assert.Equal(File.ReadAllBytes(Path.Combine(colorDirectory, "home-color-cb.bin")), homeBytes);
        Assert.Equal(
            Npxs40087ShellContract.BootPalette.AssetSha256,
            Sha256(bootBytes));
        Assert.Equal(
            Npxs40087ShellContract.LoginPalette.AssetSha256,
            Sha256(loginBytes));
        Assert.Equal(
            Npxs40087ShellContract.HomePalette.AssetSha256,
            Sha256(homeBytes));
        Assert.Equal(0x7c, homeBytes.Length);
    }

    [Fact]
    public void AmbientContractCannotBeMistakenForAFrameLoop()
    {
        var ambient = Npxs40087ShellContract.Ambient;

        Assert.False(ambient.WrapsOrRestarts);
        Assert.Equal(8, ambient.SmallGroupsInAmbient);
        Assert.Equal(0, ambient.LargeGroupsInAmbient);
        Assert.Equal(2, ambient.LargeGroupsInColdBoot);
        Assert.Equal(6000, ambient.PropertyRecordCount);
        Assert.Equal(0x44, ambient.PropertyRecordStride);
        Assert.Null(ambient.ConfirmedSmallParticleColorPatternFlag);
        Assert.Equal(
            SonyShellEvidenceClass.ConsoleVideoMeasured,
            ambient.ConsoleValidationEvidence.Class);
        Assert.Equal(
            SonyShellEvidenceClass.HostAssumption,
            ambient.ColorPatternAssumptionEvidence.Class);
    }

    [Fact]
    public void TimelineImplementationMatchesTheCentralContract()
    {
        var contract = Npxs40087ShellContract.Ambient;

        Assert.Equal(Ps5NativeColdBootAmbientTimeline.ColdBootSelector, contract.ColdBootSelector);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.AmbientSelector, contract.AmbientSelector);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.ColdBootDurationSeconds, contract.ManagedColdBootSeconds);
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.FirmwareInitialLightClockSeconds,
            contract.FirmwareInitialLightClockSeconds);
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.PresentationLightClockOriginSeconds,
            contract.PresentationLightClockOriginSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.ManagedPatternActionSeconds, contract.ManagedPatternActionSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.PatternActionSeconds, contract.AuthoredPatternActionSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.ManagedHomeLightTransitionSeconds, contract.ManagedHomeLightTransitionSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.PatternActionEndSeconds, contract.AuthoredPatternActionEndSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.ParticleTransitionSeconds, contract.AuthoredSelectorTransitionSeconds);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.PreviousInstanceReleaseSeconds, contract.AuthoredPreviousInstanceReleaseSeconds);
    }

    [Fact]
    public void LightClockKeepsFirmwareObjectSeedSeparateFromPresentationAndParticleTime()
    {
        Assert.Equal(10.0, Ps5NativeColdBootAmbientTimeline.FirmwareInitialLightClockSeconds);
        Assert.Equal(0.0, Ps5NativeColdBootAmbientTimeline.LightSecondsAtElapsed(0.0));
        Assert.Equal(6.5, Ps5NativeColdBootAmbientTimeline.LightSecondsAtElapsed(4.0));
        Assert.Equal(6.5, Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(4.0));
        Assert.Equal(9.0, Ps5NativeColdBootAmbientTimeline.LightSecondsAtElapsed(6.5));
    }

    [Fact]
    public void GameHubOwnershipAndOverflowGeometryRemainExplicit()
    {
        var hub = Npxs40087ShellContract.GameHub;

        Assert.Equal("NPXS40033", hub.BundleEvidence.Owner);
        Assert.Equal(SonyShellEvidenceClass.ConsoleVideoMeasured, hub.PositionEvidence.Class);
        Assert.Equal(334, hub.PrimaryButtonWidthWithOverflow);
        Assert.False(hub.HasOverflow(1));
        Assert.True(hub.HasOverflow(2));
        Assert.Equal(720, hub.LogoMaximumWidth);
        Assert.Equal(148, hub.LogoMaximumHeight);

        var figma = Npxs40087ShellContract.GameHubFigmaReference;
        Assert.Equal(SonyShellEvidenceClass.CommunityFigmaMeasured, figma.Evidence.Class);
        Assert.NotEqual(hub.CtaContainerLeft, figma.PlayLeft);
        Assert.NotEqual(hub.PrimaryButtonWidthWithOverflow, figma.PlayWidth);
    }

    public static TheoryData<Npxs40087DrawTopologyContract> ParticleDraws => new()
    {
        Npxs40087ShellContract.SmallParticle,
        Npxs40087ShellContract.LargeParticle,
    };

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "big-picture")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Prosperismo repository root.");
    }
}
