// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using Prosperismo.Libs.Presentation;
using Prosperismo.Libs.Textures;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// particle renderer. Two native pattern instances share one 6000-record
/// property allocation during the accepted hand-off interval; elapsed time is
/// monotonic and never wrapped into a rendered or authored frame loop.
/// </summary>
internal sealed class Ps5NativeColdBootAmbientLiveFrameSource : IPs5NativeParticleFrameSource
{
    private const string DisableEnvironmentVariable =
        "PROSPERISMO_PS5_DISABLE_NATIVE_COLD_BOOT";
    private const int SystemsPerInstance = 10;
    private const int LogicalBankCount = SystemsPerInstance * 2;

    private readonly Ps5NativeSelector1PatternMaterializer _coldBoot;
    private readonly Ps5NativeSelector1PatternMaterializer _ambient;
    private readonly Ps5NativeSelector1ParticleDrawProgram _smallProgram;
    private readonly Ps5NativeSelector1ParticleDrawProgram _largeProgram;
    private readonly Ps5NativeParticleComputeBackend _compute;
    private readonly VulkanPs5NativeParticleRenderer _smallRenderer;
    private readonly VulkanPs5NativeParticleRenderer _largeRenderer;
    private readonly Ps5NativeLightProgram _lightProgram;
    private readonly byte[] _bootColorCb;
    private readonly byte[] _loginColorCb;
    private readonly byte[] _ambientColorCb;
    private readonly byte[] _blendedColorCb = new byte[Npxs40087ColorCbContract.ByteCount];
    private readonly byte[] _ids;
    private readonly Ps5NativeFrameTimeStatistics? _frameTimeStatistics =
        Ps5NativeFrameTimeStatistics.TryCreateFromEnvironment();
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private IReadOnlyList<Ps5NativeParticleDrawHistory> _previousDrawHistory = [];
    private IReadOnlyList<ActiveInstance> _currentInstances = [];
    private long _lastSimulationFrame = -1;
    private double _lastElapsedSeconds;
    private bool? _coldBootSequence;
    private Ps5NativeLightRenderer? _lightRenderer;
    private int _lightWidth;
    private int _lightHeight;
    private bool _hasRendered;
    private bool _disposed;

    private static readonly Lazy<Task<PreparedAssets?>> PreparedAssetTask = new(
        static () => Task.Run(PrepareAssets),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private sealed record PreparedAssets(
        Ps5NativeSelector1PatternMaterializer ColdBoot,
        Ps5NativeSelector1PatternMaterializer Ambient,
        Ps5NativeLightProgram LightProgram,
        Ps5NativePatternResourceFrame ColdInitial,
        Ps5NativeSelector1ParticleDrawProgram SmallProgram,
        Ps5NativeSelector1ParticleDrawProgram LargeProgram,
        byte[] ComputeSpirv,
        byte[] ZeroProperties,
        byte[] Ids,
        Ps5NativeParticleTexture Texture0,
        Ps5NativeParticleTexture Texture1,
        byte[] BootColorCb,
        byte[] LoginColorCb,
        byte[] AmbientColorCb,
        TimeSpan PreparationDuration);

    private sealed record ActiveInstance(
        Ps5NativePatternInstanceState State,
        Ps5NativePatternResourceFrame Resources);

    private Ps5NativeColdBootAmbientLiveFrameSource(
        Ps5NativeSelector1PatternMaterializer coldBoot,
        Ps5NativeSelector1PatternMaterializer ambient,
        Ps5NativeSelector1ParticleDrawProgram smallProgram,
        Ps5NativeSelector1ParticleDrawProgram largeProgram,
        Ps5NativeParticleComputeBackend compute,
        VulkanPs5NativeParticleRenderer smallRenderer,
        VulkanPs5NativeParticleRenderer largeRenderer,
        Ps5NativeLightProgram lightProgram,
        byte[] bootColorCb,
        byte[] loginColorCb,
        byte[] ambientColorCb,
        byte[] ids)
    {
        _coldBoot = coldBoot;
        _ambient = ambient;
        _smallProgram = smallProgram;
        _largeProgram = largeProgram;
        _compute = compute;
        _smallRenderer = smallRenderer;
        _largeRenderer = largeRenderer;
        _lightProgram = lightProgram;
        _bootColorCb = bootColorCb;
        _loginColorCb = loginColorCb;
        _ambientColorCb = ambientColorCb;
        _ids = ids;
    }

    /// <summary>
    /// Starts immutable asset verification, PNG decode and shader
    /// translation on a worker before the cold-boot gate asks for the source.
    /// Vulkan state remains per-source and is created by <see cref="TryCreate"/>.
    /// </summary>
    internal static void StartPrewarming()
    {
        if (!IsDisabled())
        {
            _ = PreparedAssetTask.Value;
        }
    }

    internal static async Task<bool> PrewarmAsync()
    {
        if (IsDisabled())
        {
            return false;
        }

        return await PreparedAssetTask.Value.ConfigureAwait(false) is not null;
    }

    internal static Ps5NativeColdBootAmbientLiveFrameSource? TryCreate()
    {
        if (IsDisabled())
        {
            return null;
        }

        var createClock = Stopwatch.StartNew();
        try
        {
            var prepared = PreparedAssetTask.Value.GetAwaiter().GetResult();
            if (prepared is null)
            {
                return null;
            }

            var initialSrt = Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeSrt();
            var zeroResource = new byte[Ps5NativeParticleComputeRequest.ResourceByteCount];
            var computeBanks = new Ps5NativeParticleComputeBank[LogicalBankCount];
            for (var logicalIndex = 0; logicalIndex < LogicalBankCount; logicalIndex++)
            {
                ReadOnlyMemory<byte> resource = zeroResource;
                if (logicalIndex < SystemsPerInstance)
                {
                    resource = logicalIndex < Ps5NativePatternResourceFrame.SmallBankCount
                        ? prepared.ColdInitial.SmallBanks[logicalIndex].ResourcesCs
                        : prepared.ColdInitial.LargeBanks[
                            logicalIndex - Ps5NativePatternResourceFrame.SmallBankCount].ResourcesCs;
                }

                computeBanks[logicalIndex] = new Ps5NativeParticleComputeBank(
                    initialSrt,
                    resource,
                    prepared.Ids);
            }

            Ps5NativeParticleComputeBackend? compute = null;
            VulkanPs5NativeParticleRenderer? smallRenderer = null;
            VulkanPs5NativeParticleRenderer? largeRenderer = null;
            try
            {
                var stageClock = Stopwatch.StartNew();
                compute = new Ps5NativeParticleComputeBackend(
                    prepared.ComputeSpirv,
                    computeBanks,
                    prepared.ZeroProperties);
                TraceTiming(
                    $"native background create: compute={stageClock.Elapsed.TotalMilliseconds:0.0}ms");
                stageClock.Restart();
                smallRenderer = new VulkanPs5NativeParticleRenderer();
                smallRenderer.InitializeAsync(new Ps5NativeParticleResources(
                    prepared.SmallProgram.VertexSpirv,
                    prepared.SmallProgram.FragmentSpirv,
                    prepared.Texture0,
                    prepared.Texture1,
                    NggPrimitiveConnectivity: prepared.SmallProgram.NggPrimitiveConnectivity))
                    .GetAwaiter().GetResult();
                TraceTiming(
                    $"native background create: small-config={stageClock.Elapsed.TotalMilliseconds:0.0}ms");
                stageClock.Restart();
                largeRenderer = new VulkanPs5NativeParticleRenderer();
                largeRenderer.InitializeAsync(new Ps5NativeParticleResources(
                    prepared.LargeProgram.VertexSpirv,
                    prepared.LargeProgram.FragmentSpirv,
                    prepared.Texture0,
                    prepared.Texture1,
                    NggPrimitiveConnectivity: prepared.LargeProgram.NggPrimitiveConnectivity))
                    .GetAwaiter().GetResult();
                TraceTiming(
                    $"native background create: large-config={stageClock.Elapsed.TotalMilliseconds:0.0}ms");

                TraceTiming(
                    $"native background ready: prepare={prepared.PreparationDuration.TotalMilliseconds:0.0}ms " +
                    $"source={createClock.Elapsed.TotalMilliseconds:0.0}ms");
                return new Ps5NativeColdBootAmbientLiveFrameSource(
                    prepared.ColdBoot,
                    prepared.Ambient,
                    prepared.SmallProgram,
                    prepared.LargeProgram,
                    compute,
                    smallRenderer,
                    largeRenderer,
                    prepared.LightProgram,
                    prepared.BootColorCb,
                    prepared.LoginColorCb,
                    prepared.AmbientColorCb,
                    prepared.Ids);
            }
            catch
            {
                largeRenderer?.DisposeAsync().GetAwaiter().GetResult();
                smallRenderer?.DisposeAsync().GetAwaiter().GetResult();
                compute?.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("PROSPERISMO_PS5_NATIVE_TRACE"),
                    "1",
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"coldboot/ambient native source initialization failed: {exception}");
            }
            return null;
        }
    }

    private static PreparedAssets? PrepareAssets()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!Ps5NativeBackgroundAssetPack.TryLoad(out var assets))
            {
                return null;
            }

            var coldBoot = Ps5NativeSelector1PatternMaterializer.FromEboot(
                assets.CompatibilityImage,
                Ps5NativeColdBootAmbientTimeline.ColdBootSelector);
            var ambient = Ps5NativeSelector1PatternMaterializer.FromEboot(
                assets.CompatibilityImage,
                Ps5NativeColdBootAmbientTimeline.AmbientSelector);
            var lightProgram = Ps5NativeLightProgram.Compile(
                assets.CompatibilityImage,
                assets.LightFloorRgba,
                assets.LightVolumeRgba);
            var coldInitial = coldBoot.MaterializeResources(0.0);
            var coldLargeCompile = coldBoot.MaterializeResources(
                Ps5NativeColdBootAmbientTimeline.PatternActionSeconds);
            var ids = Ps5NativeSelector1ParticleDrawProgram.BuildParticleIds();
            var zeroProperties = new byte[Ps5NativeParticleComputeRequest.ParticlePropertyByteCount];
            var computeSpirv = Ps5NativeParticleProgramCompiler.CompileSmallParticleCompute(
                assets.CompatibilityImage);

            var smallProgram = Ps5NativeSelector1ParticleDrawProgram.Compile(
                assets.CompatibilityImage,
                coldBoot.Materialize(0.0),
                zeroProperties,
                ids);
            var largeCompileBank = coldLargeCompile.LargeBanks.First(
                static bank => IsActiveDrawResource(bank.ResourcesVsPs.Span, isLarge: true));
            var largeProgram = Ps5NativeSelector1ParticleDrawProgram.CompileLarge(
                assets.CompatibilityImage,
                largeCompileBank,
                zeroProperties,
                ids,
                assets.ParticleDescriptor,
                assets.ParticleDescriptor);

            return new PreparedAssets(
                coldBoot,
                ambient,
                lightProgram,
                coldInitial,
                smallProgram,
                largeProgram,
                computeSpirv,
                zeroProperties,
                ids,
                assets.Particle0,
                assets.Particle1,
                assets.BootColorCb,
                assets.LoginColorCb,
                assets.HomeColorCb,
                clock.Elapsed);
        }
        catch (Exception exception)
        {
            TraceFailure(exception);
            return null;
        }
    }

    private static bool IsDisabled() => string.Equals(
        Environment.GetEnvironmentVariable(DisableEnvironmentVariable),
        "1",
        StringComparison.Ordinal);

    private static bool IsTraceEnabled() => string.Equals(
        Environment.GetEnvironmentVariable("PROSPERISMO_PS5_NATIVE_TRACE"),
        "1",
        StringComparison.Ordinal);

    private static void TraceTiming(string message)
    {
        if (IsTraceEnabled())
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void TraceFailure(Exception exception)
    {
        if (IsTraceEnabled())
        {
            Console.Error.WriteLine(
                $"coldboot/ambient native source initialization failed: {exception}");
        }
    }

    public bool SupportsState(ShellGlobalBackgroundState state)
        => SupportsPersistentState(state);

    internal static bool SupportsPersistentState(ShellGlobalBackgroundState state)
    {
        var rawState = ShellBackgroundComposition.NativeParticleRouteFor(state).RawState;
        // NoParticle has no native setter call. After cold boot it therefore
        // leaves the retained selector-1 allocation and light_p room alive.
        return rawState is 2 or 3 || state == ShellGlobalBackgroundState.NoParticle;
    }

    /// <summary>
    /// Creates the extent-dependent Vulkan pipelines before the cold-boot
    /// simulation state and authored time remain at zero.
    /// </summary>
    internal async ValueTask PrimeRenderersAsync(
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primeClock = Stopwatch.StartNew();
            var resources = _coldBoot.MaterializeResources(
                Ps5NativeColdBootAmbientTimeline.PatternActionSeconds);
            var properties = _compute.CopyProperties();
            var smallDraws = resources.SmallBanks
                .Where(static bank =>
                    IsActiveDrawResource(bank.ResourcesVsPs.Span, isLarge: false))
                .Select(bank => _smallProgram.BuildDraw(
                    new Ps5NativeSelector1ResourceBank(
                        bank.Index,
                        bank.ResourcesCs,
                        bank.ResourcesVsPs),
                    properties,
                    _ids,
                    width,
                    height))
                .ToList();
            // The selector hand-off can expose nine simultaneous small draws.
            // Prime that capacity and every cold-boot draw size now. Repeating
            // only the first 200-particle bank left the 1,800-particle bank to
            // trigger host-driver allocation on its first visible frame.
            while (smallDraws.Count < 9)
            {
                smallDraws.Add(smallDraws[0]);
            }
            _ = await _smallRenderer.RenderSequenceAsync(smallDraws, cancellationToken)
                .ConfigureAwait(false);
            TraceTiming(
                $"native background prime: small={primeClock.Elapsed.TotalMilliseconds:0.0}ms");
            primeClock.Restart();

            var largeBank = resources.LargeBanks.First(
                static bank => IsActiveDrawResource(bank.ResourcesVsPs.Span, isLarge: true));
            var largeDraw = _largeProgram.BuildLargeDraw(
                largeBank,
                properties,
                _ids,
                width,
                height,
                time: 0.0f,
                timeStep: 1.0f / 60.0f);
            var particleFrame = await _largeRenderer.RenderSequenceAsync(
                [largeDraw],
                cancellationToken).ConfigureAwait(false);
            TraceTiming(
                $"native background prime: large={primeClock.Elapsed.TotalMilliseconds:0.0}ms");
            primeClock.Restart();

            EnsureLightRenderer(width, height);
            TraceTiming(
                $"native background prime: light-create={primeClock.Elapsed.TotalMilliseconds:0.0}ms");
            primeClock.Restart();
            _ = _lightRenderer!.Render(
                particleFrame,
                time: 0.0f,
                _bootColorCb,
                particleAlpha: 1.0f);
            TraceTiming(
                $"native background prime: light-render={primeClock.Elapsed.TotalMilliseconds:0.0}ms");
        }
        finally
        {
            _renderGate.Release();
        }
    }

    public async ValueTask<Ps5NativeParticleFrame?> RenderAsync(
        Ps5NativeParticleFrameRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SupportsState(request.State) || request.Width <= 0 || request.Height <= 0 ||
            !double.IsFinite(request.Elapsed.TotalSeconds) || request.Elapsed < TimeSpan.Zero)
        {
            return null;
        }

        var particleAlpha = Math.Clamp(request.ParticleAlpha, 0.0f, 1.0f);
        if (!float.IsFinite(particleAlpha))
        {
            return null;
        }

        await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var renderClock = Stopwatch.StartNew();
            var nativeSeconds = AdvanceSimulation(
                request.State,
                request.Elapsed,
                cancellationToken);
            var simulationElapsed = renderClock.Elapsed;

            var properties = _compute.CopyProperties();
            var smallDraws = new List<Ps5NativeParticleDraw>(9);
            var largeDraws = new List<Ps5NativeParticleDraw>(2);
            foreach (var active in _currentInstances)
            {
                foreach (var bank in active.Resources.SmallBanks)
                {
                    if (!IsActiveDrawResource(bank.ResourcesVsPs.Span, isLarge: false))
                    {
                        continue;
                    }

                    smallDraws.Add(_smallProgram.BuildDraw(
                        new Ps5NativeSelector1ResourceBank(
                            bank.Index,
                            bank.ResourcesCs,
                            bank.ResourcesVsPs),
                        properties,
                        _ids,
                        request.Width,
                        request.Height,
                        (float)active.State.LocalSeconds,
                        1.0f / 60.0f,
                        active.State.Instance,
                        active.State.CurrentInstance));
                }

                foreach (var bank in active.Resources.LargeBanks)
                {
                    if (!IsActiveDrawResource(bank.ResourcesVsPs.Span, isLarge: true))
                    {
                        continue;
                    }

                    largeDraws.Add(_largeProgram.BuildLargeDraw(
                        bank,
                        properties,
                        _ids,
                        request.Width,
                        request.Height,
                        (float)active.State.LocalSeconds,
                        1.0f / 60.0f,
                        active.State.Instance,
                        active.State.CurrentInstance));
                }
            }
            var drawBuildElapsed = renderClock.Elapsed;

            if (string.Equals(
                    Environment.GetEnvironmentVariable("PROSPERISMO_PS5_NATIVE_TRACE"),
                    "1",
                    StringComparison.Ordinal))
            {
                var live = 0;
                var latched = 0;
                var shadeable = 0;
                for (var record = 0; record < 6000; record++)
                {
                    var offset = record * 0x44;
                    var currentLife = BitConverter.ToSingle(properties, offset + 0x38);
                    var renderLife = BitConverter.ToSingle(properties, offset + 0x40);
                    if (currentLife == 0.0f)
                    {
                        continue;
                    }
                    live++;
                    if (renderLife >= 0.0f) latched++;
                    if (currentLife > 0.0f && renderLife > currentLife) shadeable++;
                }
                Console.Error.WriteLine(
                    $"native particle elapsed={request.Elapsed.TotalSeconds:0.###} " +
                    $"pattern={nativeSeconds:0.###} instances={_currentInstances.Count} " +
                    $"small={smallDraws.Count}[{string.Join(',', smallDraws.Select(static draw => draw.ParticleCount))}] " +
                    $"large={largeDraws.Count}[{string.Join(',', largeDraws.Select(static draw => draw.ParticleCount))}] " +
                    $"live={live} latched={latched} shadeable={shadeable}");
            }

            Ps5NativeParticleFrame? smallFrame = smallDraws.Count == 0
                ? null
                : await _smallRenderer.RenderSequenceAsync(smallDraws, cancellationToken)
                    .ConfigureAwait(false);
            var smallRenderElapsed = renderClock.Elapsed;
            Ps5NativeParticleFrame? largeFrame = largeDraws.Count == 0
                ? null
                : await _largeRenderer.RenderSequenceAsync(largeDraws, cancellationToken)
                    .ConfigureAwait(false);
            var largeRenderElapsed = renderClock.Elapsed;
            var particleFrame = (smallFrame, largeFrame) switch
            {
                ({ } small, { } large) =>
                    Ps5NativeParticleCompositor.CompositeAdditive(small, large),
                ({ } small, null) => small,
                (null, { } large) => large,
                _ => new Ps5NativeParticleFrame(
                    request.Width,
                    request.Height,
                    new byte[checked(request.Width * request.Height * 4)]),
            };
            var compositeElapsed = renderClock.Elapsed;
            EnsureLightRenderer(request.Width, request.Height);
            var palette = Ps5NativeColdBootAmbientTimeline.PaletteBlendAtElapsed(
                request.Elapsed.TotalSeconds,
                startsFromColdBoot: _coldBootSequence == true);
            var colorCb = BlendColorCb(palette);
            var result = _lightRenderer!.Render(
                particleFrame,
                // The accepted POC submits the same authored frame time to
                // light_p and the particle path. Keep its zero origin separate
                // from the native object's independently recovered +0xCC seed.
                (float)Ps5NativeColdBootAmbientTimeline.LightSecondsAtElapsed(
                    request.Elapsed.TotalSeconds),
                colorCb,
                particleAlpha: particleAlpha);
            var lightElapsed = renderClock.Elapsed;
            _frameTimeStatistics?.Record(
                request.Elapsed.TotalSeconds,
                lightElapsed.TotalMilliseconds,
                request.Width,
                request.Height);
            if (IsTraceEnabled())
            {
                static double StageMilliseconds(TimeSpan end, TimeSpan start) =>
                    (end - start).TotalMilliseconds;

                Console.Error.WriteLine(
                    $"native frame timing {request.Width}x{request.Height}: " +
                    $"simulate={simulationElapsed.TotalMilliseconds:0.0}ms " +
                    $"build={StageMilliseconds(drawBuildElapsed, simulationElapsed):0.0}ms " +
                    $"small={StageMilliseconds(smallRenderElapsed, drawBuildElapsed):0.0}ms " +
                    $"large={StageMilliseconds(largeRenderElapsed, smallRenderElapsed):0.0}ms " +
                    $"composite={StageMilliseconds(compositeElapsed, largeRenderElapsed):0.0}ms " +
                    $"light={StageMilliseconds(lightElapsed, compositeElapsed):0.0}ms " +
                    $"total={lightElapsed.TotalMilliseconds:0.0}ms");
            }
            return result;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    private byte[] BlendColorCb(Ps5NativeColorCbBlend blend)
    {
        var from = ColorCbFor(blend.From.SeederPreset);
        var to = ColorCbFor(blend.To.SeederPreset);
        if (blend.From.SeederPreset == blend.To.SeederPreset || blend.TargetWeight >= 1.0f)
        {
            return to;
        }

        if (blend.TargetWeight <= 0.0f)
        {
            return from;
        }

        for (var offset = 0; offset < _blendedColorCb.Length; offset += sizeof(float))
        {
            var fromValue = BitConverter.ToSingle(from, offset);
            var toValue = BitConverter.ToSingle(to, offset);
            var value = fromValue + ((toValue - fromValue) * blend.TargetWeight);
            BinaryPrimitives.WriteInt32LittleEndian(
                _blendedColorCb.AsSpan(offset, sizeof(float)),
                BitConverter.SingleToInt32Bits(value));
        }
        return _blendedColorCb;
    }

    private byte[] ColorCbFor(int seederPreset) => seederPreset switch
    {
        11 => _bootColorCb,
        9 => _loginColorCb,
        4 => _ambientColorCb,
        _ => throw new ArgumentOutOfRangeException(nameof(seederPreset)),
    };

    /// <summary>
    /// Advances the same retained 60 Hz compute allocation without issuing
    /// particle, light, or readback draws. Title artwork can cover the room for
    /// any duration without freezing it or forcing a catch-up burst when focus
    /// returns to a shell card.
    /// </summary>
    internal async ValueTask AdvanceSimulationAsync(
        ShellGlobalBackgroundState state,
        TimeSpan elapsed,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SupportsState(state) ||
            !double.IsFinite(elapsed.TotalSeconds) ||
            elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        await _renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = AdvanceSimulation(state, elapsed, cancellationToken);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    internal double LastNativeElapsedSeconds => _lastElapsedSeconds;

    private double AdvanceSimulation(
        ShellGlobalBackgroundState state,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var elapsedSeconds = elapsed.TotalSeconds;
        _coldBootSequence ??= StartsFromColdBoot(state);
        var nativeSeconds = _coldBootSequence == true
            ? Ps5NativeColdBootAmbientTimeline.NativeSecondsAtElapsed(elapsedSeconds)
            : elapsedSeconds;
        if (_hasRendered && nativeSeconds < _lastElapsedSeconds)
        {
            throw new InvalidOperationException(
                "coldboot/ambient live source received a non-monotonic elapsed time");
        }

        // The particle shader derives its spawn seed from round(time/timeStep).
        // Preserve the accepted POC's authored 60 Hz sequence while the managed
        // presentation maps 6.5 authored seconds onto four visible seconds.
        // Collapsing this to 240 wall ticks changes both births and positions;
        // rendering remains off Avalonia's UI thread so replay does not block
        // input or audio.
        // Retained ambient sources already receive authored/native time and
        // must not run through the cold-boot wall-clock mapping a second time.
        var targetFrame = Ps5NativeColdBootAmbientTimeline.ResourceStepAtNativeSeconds(
            nativeSeconds);
        if (_lastSimulationFrame < 0)
        {
            // The ordinary coldboot path begins with an actual t=0 integration
            // step. isPreSimulation is reserved for entering an already-running
            // ambient field; applying it to cold boot drops authored particles.
            AdvanceSimulationFrame(
                nativeSeconds: Ps5NativeColdBootAmbientTimeline.NativeSecondsAtResourceStep(0),
                preSimulation: _coldBootSequence != true);
            _lastSimulationFrame = 0;
        }

        for (var frame = _lastSimulationFrame + 1; frame <= targetFrame; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _compute.ApplyDrawHistory(_previousDrawHistory);
            AdvanceSimulationFrame(
                Ps5NativeColdBootAmbientTimeline.NativeSecondsAtResourceStep(frame),
                preSimulation: false);
            _lastSimulationFrame = frame;
        }

        _lastElapsedSeconds = nativeSeconds;
        _hasRendered = true;
        return nativeSeconds;
    }

    internal static bool StartsFromColdBoot(ShellGlobalBackgroundState state)
    {
        var rawState = ShellBackgroundComposition.NativeParticleRouteFor(state).RawState;
        // A source may finish compiling after HOME has already published
        // NoParticle. The layer only starts this source from raw state 2/3, and
        // product HOME reaches NoParticle from raw state 3, so preserve the
        // cold-boot initialization instead of treating the retained state as a
        // fresh pre-simulated ambient allocation.
        return rawState == 3 || state == ShellGlobalBackgroundState.NoParticle;
    }

    private void AdvanceSimulationFrame(
        double nativeSeconds,
        bool preSimulation)
    {
        _currentInstances = CreateActiveInstances(nativeSeconds);
        var zeroResource = new byte[Ps5NativeParticleComputeRequest.ResourceByteCount];
        var bankFrames = new Ps5NativeParticleComputeBankFrame[LogicalBankCount];
        for (var index = 0; index < bankFrames.Length; index++)
        {
            bankFrames[index] = new Ps5NativeParticleComputeBankFrame(
                Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeSrt(
                    (float)nativeSeconds,
                    preSimulation),
                zeroResource);
        }

        foreach (var active in _currentInstances)
        {
            var baseIndex = active.State.Instance * SystemsPerInstance;
            foreach (var bank in active.Resources.SmallBanks)
            {
                bankFrames[baseIndex + bank.Index] = CreateComputeBankFrame(
                    active.State,
                    bank.ResourcesCs,
                    preSimulation);
            }
            foreach (var bank in active.Resources.LargeBanks)
            {
                bankFrames[
                    baseIndex + Ps5NativePatternResourceFrame.SmallBankCount + bank.Index] =
                    CreateComputeBankFrame(active.State, bank.ResourcesCs, preSimulation);
            }
        }

        _compute.Dispatch(new Ps5NativeParticleComputeFrame(bankFrames));
        _previousDrawHistory = _currentInstances
            .SelectMany(static active =>
                active.Resources.SmallBanks
                    .Where(static bank => IsActiveDrawResource(bank.ResourcesVsPs.Span, false))
                    .Select(static bank => new Ps5NativeParticleDrawHistory(
                        bank.ResourcesVsPs,
                        IsLarge: false))
                    .Concat(active.Resources.LargeBanks
                        .Where(static bank => IsActiveDrawResource(bank.ResourcesVsPs.Span, true))
                        .Select(static bank => new Ps5NativeParticleDrawHistory(
                            bank.ResourcesVsPs,
                            IsLarge: true))))
            .ToArray();
    }

    private Ps5NativeParticleComputeBankFrame CreateComputeBankFrame(
        Ps5NativePatternInstanceState state,
        ReadOnlyMemory<byte> resources,
        bool preSimulation)
    {
        var timeRate = state.Instance == state.CurrentInstance
            ? 1.0f
            : BitConverter.Int32BitsToSingle(unchecked((int)
                BinaryPrimitives.ReadUInt32LittleEndian(resources.Span[0x3C..]))) *
              (1.0f / 6.5f);
        return new Ps5NativeParticleComputeBankFrame(
            Ps5NativeParticleProgramCompiler.CreateSmallParticleComputeSrt(
                (float)state.LocalSeconds,
                preSimulation,
                state.TransitionPatternFlag,
                1.0f / 60.0f,
                timeRate),
            resources);
    }

    private IReadOnlyList<ActiveInstance> CreateActiveInstances(double globalSeconds)
    {
        if (_coldBootSequence == true)
        {
            return Ps5NativeColdBootAmbientTimeline.Sample(globalSeconds)
                .Select(state => new ActiveInstance(
                    state,
                    MaterializerFor(state.Selector).MaterializeResources(state.LocalSeconds)))
                .ToArray();
        }

        var state = new Ps5NativePatternInstanceState(
            Instance: 0,
            CurrentInstance: 0,
            Selector: Ps5NativeColdBootAmbientTimeline.AmbientSelector,
            LocalSeconds: globalSeconds);
        return [new ActiveInstance(state, _ambient.MaterializeResources(globalSeconds))];
    }

    private Ps5NativeSelector1PatternMaterializer MaterializerFor(int selector) => selector switch
    {
        Ps5NativeColdBootAmbientTimeline.ColdBootSelector => _coldBoot,
        Ps5NativeColdBootAmbientTimeline.AmbientSelector => _ambient,
        _ => throw new ArgumentOutOfRangeException(nameof(selector)),
    };

    private void EnsureLightRenderer(int width, int height)
    {
        if (_lightRenderer is not null && _lightWidth == width && _lightHeight == height)
        {
            return;
        }

        _lightRenderer?.Dispose();
        _lightRenderer = new Ps5NativeLightRenderer(
            _lightProgram,
            width,
            height,
            _bootColorCb);
        _lightWidth = width;
        _lightHeight = height;
    }

    private static bool IsActiveDrawResource(ReadOnlySpan<byte> resource, bool isLarge)
    {
        var countOffset = isLarge ? 0xAC : 0x20;
        return resource.Length >= countOffset + 0x10 &&
            BinaryPrimitives.ReadUInt32LittleEndian(resource[countOffset..]) > 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(resource[(countOffset + 8)..]) > 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(resource[(countOffset + 12)..]) > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _renderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lightRenderer?.Dispose();
            _lightRenderer = null;
            await _largeRenderer.DisposeAsync().ConfigureAwait(false);
            await _smallRenderer.DisposeAsync().ConfigureAwait(false);
            _compute.Dispose();
        }
        finally
        {
            _renderGate.Release();
            _renderGate.Dispose();
        }
    }
}
