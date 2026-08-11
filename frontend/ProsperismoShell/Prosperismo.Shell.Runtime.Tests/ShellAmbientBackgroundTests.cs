// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Prosperismo.GUI;
using Prosperismo.GUI.SystemAssets.Shell;
using Prosperismo.Libs.Presentation;
using Prosperismo.ShaderCompiler.Vulkan;
using Xunit;

namespace Prosperismo.Shell.Runtime.Tests;

public sealed class ShellAmbientBackgroundTests
{
    [Fact]
    public void CompleteLightRoomUsesSourceOverInsteadOfParticleOverlayAddition()
    {
        Assert.Equal(
            BitmapBlendingMode.SourceOver,
            Ps5NativeBackgroundLayer.CompositeBlendingMode);
        Assert.Equal(AlphaFormat.Opaque, Ps5NativeBackgroundLayer.CompositeAlphaFormat);
    }

    [Fact]
    public void AcceleratedPatternClockReplaysTheAcceptedNativeSimulation()
    {
        Assert.Equal(390, Ps5NativeColdBootAmbientTimeline.ResourceStepAtElapsed(4.0));
        Assert.Equal(510, Ps5NativeColdBootAmbientTimeline.ResourceStepAtElapsed(6.0));
        Assert.Equal(240, Ps5NativeColdBootAmbientTimeline.ResourceStepAtNativeSeconds(4.0));
        Assert.Equal(360, Ps5NativeColdBootAmbientTimeline.ResourceStepAtNativeSeconds(6.0));
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.PatternActionSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtResourceStep(390));
        Assert.Equal(
            Ps5NativeColdBootAmbientTimeline.ParticleTransitionSeconds,
            Ps5NativeColdBootAmbientTimeline.NativeSecondsAtResourceStep(510));
    }

    [Fact]
    public void HomeHandoffKeepsTheRetainedNativeSourceWithoutSelectingSpread()
    {
        var background = new ShellBackground
        {
            GlobalState = ShellGlobalBackgroundState.ColdBootAnimation,
        };
        background.ContinueAmbientSequence();

        Assert.Equal(ShellGlobalBackgroundState.NoParticle, background.GlobalState);
        var route = ShellBackgroundComposition.NativeParticleRouteFor(
            ShellGlobalBackgroundState.NoParticle);

        Assert.Null(route.RawState);
        Assert.True(Ps5NativeColdBootAmbientLiveFrameSource.SupportsPersistentState(
            ShellGlobalBackgroundState.NoParticle));
        Assert.True(Ps5NativeColdBootAmbientLiveFrameSource.SupportsPersistentState(
            ShellGlobalBackgroundState.ColdBootAnimation));
        Assert.False(Ps5NativeBackgroundLayer.ShouldCreateLiveSource(
            ShellGlobalBackgroundState.NoParticle));
        Assert.True(Ps5NativeBackgroundLayer.ShouldCreateLiveSource(
            ShellGlobalBackgroundState.ColdBootAnimation));
        Assert.True(Ps5NativeColdBootAmbientLiveFrameSource.StartsFromColdBoot(
            ShellGlobalBackgroundState.NoParticle));
        Assert.False(Ps5NativeColdBootAmbientLiveFrameSource.StartsFromColdBoot(
            ShellGlobalBackgroundState.ParticleSpread));
    }

    [Fact]
    public void AmbientTimelineNeverWrapsOrRestarts()
    {
        var early = Assert.Single(Ps5NativeColdBootAmbientTimeline.Sample(20.0));
        var late = Assert.Single(Ps5NativeColdBootAmbientTimeline.Sample(2020.0));

        Assert.Equal(Ps5NativeColdBootAmbientTimeline.AmbientSelector, early.Selector);
        Assert.Equal(Ps5NativeColdBootAmbientTimeline.AmbientSelector, late.Selector);
        Assert.Equal(2000.0, late.LocalSeconds - early.LocalSeconds, precision: 10);
    }

    [Fact]
    public void PaletteSelectionFollowsBootLoginHomeAndNativeQuarticTransition()
    {
        var initial = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
            0.0,
            startsFromColdBoot: true);
        Assert.Equal(Npxs40087ShellContract.BootPalette, initial.From);
        Assert.Equal(Npxs40087ShellContract.LoginPalette, initial.To);
        Assert.Equal(0.0f, initial.TargetWeight);

        var midpoint = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
            0.15,
            startsFromColdBoot: true);
        Assert.Equal(0.9375f, midpoint.TargetWeight, precision: 6);

        var coldBoot = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
            Ps5NativeColdBootAmbientTimeline.ManagedHomeLightTransitionSeconds - 0.001,
            startsFromColdBoot: true);
        Assert.Equal(Npxs40087ShellContract.LoginPalette, coldBoot.From);
        Assert.Equal(Npxs40087ShellContract.LoginPalette, coldBoot.To);

        var homeStart = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
            Ps5NativeColdBootAmbientTimeline.ManagedHomeLightTransitionSeconds,
            startsFromColdBoot: true);
        Assert.Equal(Npxs40087ShellContract.LoginPalette, homeStart.From);
        Assert.Equal(Npxs40087ShellContract.HomePalette, homeStart.To);
        Assert.Equal(0.0f, homeStart.TargetWeight);

        var home = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
            Ps5NativeColdBootAmbientTimeline.ManagedHomeLightTransitionSeconds + 0.3,
            startsFromColdBoot: true);
        Assert.Equal(Npxs40087ShellContract.HomePalette, home.To);
        Assert.Equal(1.0f, home.TargetWeight);

        // A source entering an already-running HOME field must not replay the
        // cold-boot palette when its retained native clock is near zero.
        Assert.Equal(
            Npxs40087ShellContract.HomePalette,
            Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
                0.0,
                startsFromColdBoot: false).To);
    }

    [Fact]
    public void PlacementContractKeepsAmbientNativeAndNonWrapping()
    {
        var ambient = Npxs40087ShellContract.Ambient;

        Assert.Equal(
            Npxs40087ParticlePlacementModel.NativeThreeDimensionalSimulation,
            ambient.PlacementModel);
        Assert.False(ambient.WrapsOrRestarts);
        Assert.Null(ambient.ConfirmedSmallParticleColorPatternFlag);
    }

    [Theory]
    [InlineData(1920, 1080, 1.7777778f)]
    [InlineData(1080, 1920, 0.5625f)]
    [InlineData(2560, 1440, 1.7777778f)]
    public void LargeParticleProjectionUsesPhysicalOutputAspect(
        int width,
        int height,
        float expectedAspect)
    {
        Assert.Equal(
            expectedAspect,
            Ps5NativeSelector1ParticleDrawProgram.ResolveNativeThreeDimensionalAspect(
                width,
                height),
            precision: 6);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void LargeParticleProjectionRejectsInvalidOutputExtent(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Ps5NativeSelector1ParticleDrawProgram.ResolveNativeThreeDimensionalAspect(
                width,
                height));
    }

    [Fact]
    public void SettingsParticleGateUsesTheShellPointFourSecondPresentationFade()
    {
        var quarter = Ps5NativeBackgroundLayer.AdvanceParticleOverlayOpacity(
            1.0f, visible: false, TimeSpan.FromMilliseconds(100));
        var hidden = Ps5NativeBackgroundLayer.AdvanceParticleOverlayOpacity(
            quarter, visible: false, TimeSpan.FromMilliseconds(300));
        var restored = Ps5NativeBackgroundLayer.AdvanceParticleOverlayOpacity(
            hidden, visible: true, TimeSpan.FromMilliseconds(400));

        Assert.Equal(0.75f, quarter, precision: 5);
        Assert.Equal(0.0f, hidden);
        Assert.Equal(1.0f, restored);
    }

    [Fact]
    public void NativePresentationRequestsFramesAtTheSixtyHertzSimulationCadence()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(1.0 / 60.0),
            Ps5NativeBackgroundLayer.PresentationInterval);
        Assert.True(Ps5NativeBackgroundLayer.ShouldAdvanceSource(
            motionEnabled: true,
            hasSource: true,
            supportsState: true));
        Assert.False(Ps5NativeBackgroundLayer.ShouldAdvanceSource(
            motionEnabled: false,
            hasSource: true,
            supportsState: true));
    }

    [Fact]
    public async Task NativeFrameEvaluationDoesNotBlockTheCallingThread()
    {
        var source = new BlockingParticleSource();
        var layer = new Ps5NativeBackgroundLayer
        {
            GlobalState = ShellGlobalBackgroundState.ColdBootAnimation,
        };

        var assignmentClock = Stopwatch.StartNew();
        layer.LiveSource = source;
        assignmentClock.Stop();

        await source.RenderStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // A synchronous renderer would hold this setter until the deliberately
        // blocked source completed. The product contract is non-blocking UI
        // dispatch, not a particular reusable thread-pool worker ID.
        Assert.True(
            assignmentClock.Elapsed < TimeSpan.FromMilliseconds(500),
            $"LiveSource assignment blocked for {assignmentClock.Elapsed.TotalMilliseconds:0} ms");
        source.Release();
        layer.LiveSource = null;
    }

    [Fact]
    public void AuthoritativeBackgroundPayloadIsVerifiedOnceAndReused()
    {
        Assert.True(Ps5NativeBackgroundAssetPack.TryLoad(out var first));
        Assert.True(Ps5NativeBackgroundAssetPack.TryLoad(out var second));

        Assert.Same(first, second);
        Assert.Same(first.CompatibilityImage, second.CompatibilityImage);
        Assert.Same(first.BootColorCb, second.BootColorCb);
        Assert.Same(first.LoginColorCb, second.LoginColorCb);
        Assert.Same(first.HomeColorCb, second.HomeColorCb);
        Assert.Same(first.Particle0, second.Particle0);
        Assert.Same(first.Particle1, second.Particle1);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(7680, 4320, 3840, 2160)]
    public void LiveRendererUsesPhysicalOutputWithoutALowResolutionIntermediate(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            new PixelSize(expectedWidth, expectedHeight),
            Ps5NativeBackgroundLayer.ResolveRenderExtent(width, height));
    }

    [Fact]
    public void SonyWindowPolicyUsesFullscreenOnlyForCompatibleSixteenByNineDisplays()
    {
        Assert.True(MainWindow.CanSonyDisplayFullscreen(
            new PixelRect(0, 0, 1920, 1080), allowFullscreen: true));
        Assert.False(MainWindow.CanSonyDisplayFullscreen(
            new PixelRect(0, 0, 1080, 1920), allowFullscreen: true));
        Assert.Equal(
            new PixelSize(1080, 607),
            MainWindow.ResolveSonyWindowPixelSize(new PixelRect(0, 0, 1080, 1920)));
    }

    [Fact]
    public void NativeLayerUsesUniformPresentationInsteadOfAspectFillCropping()
    {
        var layer = new Ps5NativeBackgroundLayer();

        Assert.Equal(Stretch.Uniform, layer.Stretch);
    }

    [Fact]
    public void LightShaderReceivesTheParticleOnlyFadeAtItsRecoveredAbiOffset()
    {
        Assert.True(Ps5NativeBackgroundAssetPack.TryLoad(out var assets));
        var program = Ps5NativeLightProgram.Compile(
            assets.CompatibilityImage,
            assets.LightFloorRgba,
            assets.LightVolumeRgba);
        var draw = program.BuildDraw(
            1920,
            1080,
            time: 12.5f,
            assets.HomeColorCb,
            opacity: 1.0f,
            intensity: 1.0f,
            particleAlpha: 0.25f);

        Assert.Contains(draw.VertexBuffers, buffer =>
            buffer.Length >= 0x10 &&
            BitConverter.ToSingle(buffer.Span[0x00..]) == 12.5f &&
            BitConverter.ToSingle(buffer.Span[0x04..]) == 1.0f &&
            BitConverter.ToSingle(buffer.Span[0x08..]) == 1.0f &&
            BitConverter.ToSingle(buffer.Span[0x0C..]) == 0.25f);
    }

    [Fact]
    public void LightRectangleCarriesItsNggConnectivityIntoTheHostLaunchWithoutWarning()
    {
        Assert.True(Ps5NativeBackgroundAssetPack.TryLoad(out var assets));

        var diagnostics = new List<string>();
        var previousSink = Gen5SpirvTranslator.DiagnosticSink;
        Gen5SpirvTranslator.ResetDiagnosticDeduplication();
        try
        {
            Gen5SpirvTranslator.DiagnosticSink = diagnostics.Add;
            var program = Ps5NativeLightProgram.Compile(
                assets.CompatibilityImage,
                assets.LightFloorRgba,
                assets.LightVolumeRgba);
            var resources = program.CreateResources(1920, 1080);
            var draw = program.BuildDraw(1920, 1080, 0.0f, assets.HomeColorCb);

            Assert.Equal(
                new Gen5NggPrimitiveConnectivity(
                    GuestVerticesPerPrimitive: 3,
                    HostVerticesPerPrimitive: 4,
                    HostTopology: Gen5HostPrimitiveTopology.TriangleStrip),
                program.NggPrimitiveConnectivity);
            Assert.Equal(program.NggPrimitiveConnectivity, resources.NggPrimitiveConnectivity);
            Assert.Equal(1u, draw.ParticleCount);
            Assert.DoesNotContain(
                diagnostics,
                message => message.Contains("error=ngg-prim-export-dropped target=20", StringComparison.Ordinal));
        }
        finally
        {
            Gen5SpirvTranslator.DiagnosticSink = previousSink;
            Gen5SpirvTranslator.ResetDiagnosticDeduplication();
        }
    }

    private sealed class BlockingParticleSource : IPs5NativeParticleFrameSource
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        internal TaskCompletionSource RenderStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Release() => _release.Set();

        public bool SupportsState(ShellGlobalBackgroundState state) =>
            state == ShellGlobalBackgroundState.ColdBootAnimation;

        public ValueTask<Ps5NativeParticleFrame?> RenderAsync(
            Ps5NativeParticleFrameRequest request,
            CancellationToken cancellationToken = default)
        {
            RenderStarted.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(2), cancellationToken);
            return ValueTask.FromResult<Ps5NativeParticleFrame?>(null);
        }

        public ValueTask DisposeAsync()
        {
            _release.Set();
            _release.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
