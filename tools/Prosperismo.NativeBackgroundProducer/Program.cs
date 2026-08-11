// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpEmu.Libs.Presentation;
using SharpEmu.Libs.Textures;

namespace Prosperismo.NativeBackgroundProducer;

internal static class Program
{
    private const string MappingName = "Local\\ProsperismoShellBackground";
    private const string FrameEventName = "Local\\ProsperismoShellBackgroundFrame";
    private const string ConsumedEventName = "Local\\ProsperismoShellBackgroundConsumed";
    private const uint ProtocolVersion = 1;
    private const uint FormatBgra8Premultiplied = 1;
    private const uint LayerParticleOverlay = 2;
    private const int HeaderBytes = 64;
    private const int ActiveSlotOffset = 32;
    private const int LayerKindOffset = 36;
    private const int SequenceOffset = 40;
    private const int TimestampOffset = 48;
    private static ReadOnlySpan<byte> Magic => "PS5BGRA\0"u8;

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            RunSelfTest();
            Console.WriteLine("native background producer self-test passed");
            return 0;
        }

        if (args.Length == 2 && args[0] == "--validate-firstwave")
        {
            var program = FirstWaveFirmwareProgram.Load(args[1]);
            Console.WriteLine("validated NPXS40087 12.40 FirstWave program: {0}", program.EbootPath);
            foreach (var stage in program.Stages)
            {
                Console.WriteLine(
                    "  {0,-18} type={1} header=0x{2:X} code=0x{3:X}+0x{4:X} sha256={5}",
                    stage.Name,
                    stage.ShaderType,
                    stage.HeaderFileOffset,
                    stage.CodeFileOffset,
                    stage.CodeBytes,
                    stage.Sha256);
            }
            return 0;
        }

        if (args.Length == 2 && args[0] == "--compile-firstwave-post")
        {
            var firmware = FirstWaveFirmwareProgram.Load(args[1]);
            var programs = FirstWaveFirmwarePostCompiler.Compile(firmware);
            Console.WriteLine("translated {0} original FirstWave pixel programs", programs.Count);
            foreach (var program in programs)
            {
                Console.WriteLine("  {0,-18} SPIR-V bytes=0x{1:X}", program.Name, program.Spirv.Length);
            }
            return 0;
        }

        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            Produce(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static unsafe void Produce(Options options, CancellationToken cancellationToken)
    {
        var cache = DrawCache.Load(options.CacheRoot);
        var resources = LoadResources(options.CacheRoot, options.FirmwareRoot);
        var renderer = new VulkanPs5NativeParticleRenderer();
        renderer.InitializeAsync(resources, cancellationToken).GetAwaiter().GetResult();

        var stride = checked(options.Width * 4);
        var slotBytes = checked(stride * options.Height);
        var mappingBytes = checked(HeaderBytes + (slotBytes * 2L));
        using var mapping = MemoryMappedFile.CreateNew(
            MappingName,
            mappingBytes,
            MemoryMappedFileAccess.ReadWrite);
        using var accessor = mapping.CreateViewAccessor(0, mappingBytes, MemoryMappedFileAccess.ReadWrite);
        using var frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, FrameEventName);
        using var consumedEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ConsumedEventName);
        using var presentationState = new BackgroundPresentationStateReader();

        try
        {
            byte* mapped = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref mapped);
            mapped += checked((nint)accessor.PointerOffset);
            try
            {
                InitializeHeader(mapped, options.Width, options.Height, stride, slotBytes);
                var indices = PingPong(cache.FrameRoots.Count).GetEnumerator();
                var period = TimeSpan.FromSeconds(1.0 / cache.FramesPerSecond);
                var clock = Stopwatch.StartNew();
                var deadline = TimeSpan.Zero;
                var published = 0;

                Console.WriteLine(
                    "publishing {0} particle-overlay frames at {1:0.###} fps through {2}",
                    cache.FrameRoots.Count,
                    cache.FramesPerSecond,
                    MappingName);

                while (!cancellationToken.IsCancellationRequested &&
                       (options.FrameLimit is null || published < options.FrameLimit))
                {
                    if (!presentationState.ParticleOverlayEnabled)
                    {
                        // Settings keeps the FirstWave base alive but selects
                        // NoParticle. Do not advance the recovered particle
                        // clock or spend GPU work while that overlay is gated.
                        presentationState.WaitForChangeOrTimeout(
                            cancellationToken,
                            TimeSpan.FromMilliseconds(250));
                        deadline = clock.Elapsed;
                        continue;
                    }

                    indices.MoveNext();
                    var draws = LoadDraws(
                        cache.FrameRoots[indices.Current],
                        options.Width,
                        options.Height);
                    var frame = renderer.RenderSequenceAsync(draws, cancellationToken)
                        .GetAwaiter().GetResult();
                    PublishParticleOverlay(mapped, frame, slotBytes, frameEvent);
                    published++;

                    deadline += period;
                    var remaining = deadline - clock.Elapsed;
                    if (remaining > TimeSpan.Zero && cancellationToken.WaitHandle.WaitOne(remaining))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (remaining < -period)
                    {
                        // Drop schedule debt rather than emitting a burst of stale frames.
                        deadline = clock.Elapsed;
                    }
                }

                Console.WriteLine("published {0} particle-overlay frame(s)", published);
            }
            finally
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        finally
        {
            renderer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static Ps5NativeParticleResources LoadResources(string cacheRoot, string firmwareRoot)
    {
        var vertexPath = Path.Combine(cacheRoot, "particle.vert.spv");
        var fragmentPath = Path.Combine(cacheRoot, "particle.frag.spv");
        var assetRoot = Path.Combine(firmwareRoot, "filesystems", "system_ex", "vsh_asset");
        var particle0Path = Path.Combine(assetRoot, "Sce.Vsh.ShellUI.BGLayer.Particle0.gnf");
        var particle1Path = Path.Combine(assetRoot, "Sce.Vsh.ShellUI.BGLayer.Particle1.gnf");
        var particle0 = GnfImage.TryLoadRgba(particle0Path, out var width0, out var height0);
        var particle1 = GnfImage.TryLoadRgba(particle1Path, out var width1, out var height1);
        if (!File.Exists(vertexPath) || !File.Exists(fragmentPath) ||
            particle0 is null || particle1 is null || width0 != width1 || height0 != height1)
        {
            throw new InvalidDataException("cache shaders or user-local BGLayer particle textures are unavailable");
        }

        return new Ps5NativeParticleResources(
            File.ReadAllBytes(vertexPath),
            File.ReadAllBytes(fragmentPath),
            new Ps5NativeParticleTexture(width0, height0, particle0),
            new Ps5NativeParticleTexture(width1, height1, particle1));
    }

    private static IReadOnlyList<Ps5NativeParticleDraw> LoadDraws(
        string frameRoot,
        int width,
        int height)
    {
        var properties = File.ReadAllBytes(Path.Combine(frameRoot, "properties.bin"));
        return Directory.GetDirectories(frameRoot, "bank-*")
            .Order(StringComparer.Ordinal)
            .Select(bankRoot => Enumerable.Range(0, 6)
                .Select(index => (ReadOnlyMemory<byte>)(index == 2
                    ? properties
                    : File.ReadAllBytes(Path.Combine(bankRoot, $"buffer{index}.bin"))))
                .ToArray())
            .Where(static buffers => IsActiveDrawResource(buffers[1].Span))
            .Select(buffers => new Ps5NativeParticleDraw(
                width,
                height,
                BinaryPrimitives.ReadUInt32LittleEndian(buffers[1].Span[0x20..]),
                buffers))
            .Where(static draw => draw.ParticleCount > 0)
            .ToArray();
    }

    private static bool IsActiveDrawResource(ReadOnlySpan<byte> resource) =>
        resource.Length >= 0x140 &&
        BinaryPrimitives.ReadUInt32LittleEndian(resource[0x20..]) > 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(resource[0x28..]) > 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(resource[0x2C..]) > 0;

    private static unsafe void InitializeHeader(
        byte* mapped,
        int width,
        int height,
        int stride,
        int slotBytes)
    {
        new Span<byte>(mapped, HeaderBytes).Clear();
        Magic.CopyTo(new Span<byte>(mapped, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 8, 4), ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 12, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 16, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 20, 4), checked((uint)stride));
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 24, 4), FormatBgra8Premultiplied);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + 28, 4), checked((uint)slotBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(mapped + LayerKindOffset, 4), LayerParticleOverlay);
    }

    private static unsafe void PublishParticleOverlay(
        byte* mapped,
        Ps5NativeParticleFrame frame,
        int slotBytes,
        EventWaitHandle frameEvent)
    {
        if (!frame.IsValid || frame.Rgba.Length != slotBytes)
        {
            throw new InvalidDataException("renderer returned a frame that does not match the shared surface");
        }

        ref var activeSlot = ref Unsafe.AsRef<int>(mapped + ActiveSlotOffset);
        var inactiveSlot = Volatile.Read(ref activeSlot) == 0 ? 1 : 0;
        var target = new Span<byte>(mapped + HeaderBytes + (inactiveSlot * slotBytes), slotBytes);
        ConvertRgbaFramebufferToAdditiveBgra(frame.Rgba.Span, target);
        BinaryPrimitives.WriteUInt64LittleEndian(
            new Span<byte>(mapped + TimestampOffset, 8),
            checked((ulong)Stopwatch.GetTimestamp()));
        Interlocked.Exchange(ref activeSlot, inactiveSlot);
        ref var sequence = ref Unsafe.AsRef<long>(mapped + SequenceOffset);
        Interlocked.Increment(ref sequence);
        frameEvent.Set();
    }

    // The native particle pass clears to RGBA (1,1,9,255) before ONE/ONE/ADD.
    // Publishing that opaque clear would wrongly replace the persistent dark
    // folded-room and warm/gold-ray base. Remove the clear and carry the
    // additive delta in a zero-alpha premultiplied BGRA surface. The RNW
    // consumer owns additive composition and disables this layer in Settings.
    internal static void ConvertRgbaFramebufferToAdditiveBgra(
        ReadOnlySpan<byte> rgba,
        Span<byte> bgra)
    {
        if (rgba.Length != bgra.Length || rgba.Length % 4 != 0)
        {
            throw new ArgumentException("RGBA and BGRA spans must have equal whole-pixel lengths");
        }

        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            bgra[offset] = (byte)Math.Max(0, rgba[offset + 2] - 9);
            bgra[offset + 1] = (byte)Math.Max(0, rgba[offset + 1] - 1);
            bgra[offset + 2] = (byte)Math.Max(0, rgba[offset] - 1);
            bgra[offset + 3] = 0;
        }
    }

    internal static IEnumerable<int> PingPong(int frameCount)
    {
        if (frameCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        while (true)
        {
            for (var index = 0; index < frameCount; index++)
            {
                yield return index;
            }
            for (var index = frameCount - 2; index > 0; index--)
            {
                yield return index;
            }
        }
    }

    private static void RunSelfTest()
    {
        FirstWaveFirmwareProgram.ValidateContractTable();

        var converted = new byte[8];
        ConvertRgbaFramebufferToAdditiveBgra(
            new byte[] { 1, 1, 9, 255, 11, 21, 39, 255 },
            converted);
        if (!converted.SequenceEqual(new byte[] { 0, 0, 0, 0, 30, 20, 10, 0 }))
        {
            throw new InvalidOperationException("particle clear subtraction or BGRA swizzle is wrong");
        }

        var indices = PingPong(4).Take(10).ToArray();
        if (!indices.SequenceEqual(new[] { 0, 1, 2, 3, 2, 1, 0, 1, 2, 3 }))
        {
            throw new InvalidOperationException("ping-pong sequence is wrong");
        }

        var control = new byte[BackgroundPresentationProtocol.HeaderBytes];
        BackgroundPresentationProtocol.EncodeForTest(
            control,
            BackgroundPresentationProtocol.HomeLayers,
            sequence: 4,
            timestampQpc: 123);
        if (!BackgroundPresentationProtocol.TryDecode(
                control,
                stableSequence: 4,
                out var homeLayers) ||
            homeLayers != BackgroundPresentationProtocol.HomeLayers)
        {
            throw new InvalidOperationException("Home layer-mask protocol is wrong");
        }

        BackgroundPresentationProtocol.EncodeForTest(
            control,
            BackgroundPresentationProtocol.SettingsLayers,
            sequence: 6);
        if (!BackgroundPresentationProtocol.TryDecode(
                control,
                stableSequence: 6,
                out var settingsLayers) ||
            settingsLayers != BackgroundPresentationProtocol.SettingsLayers)
        {
            throw new InvalidOperationException("Settings layer-mask protocol is wrong");
        }

        // A state without the persistent base, an odd in-progress sequence,
        // and a stale snapshot must all be rejected.
        BackgroundPresentationProtocol.EncodeForTest(
            control,
            BackgroundLayerMask.ParticleOverlay,
            sequence: 8);
        if (BackgroundPresentationProtocol.TryDecode(control, 8, out _))
        {
            throw new InvalidOperationException("control accepted particles without FirstWave base");
        }
        BackgroundPresentationProtocol.EncodeForTest(
            control,
            BackgroundPresentationProtocol.HomeLayers,
            sequence: 9);
        if (BackgroundPresentationProtocol.TryDecode(control, 9, out _) ||
            BackgroundPresentationProtocol.TryDecode(control, 10, out _))
        {
            throw new InvalidOperationException("control accepted a torn or stale sequence");
        }
    }

    private sealed record DrawCache(IReadOnlyList<string> FrameRoots, double FramesPerSecond)
    {
        public static DrawCache Load(string root)
        {
            root = Path.GetFullPath(root);
            var manifestBytes = File.ReadAllBytes(Path.Combine(root, "sequence.json"));
            var bomBytes = manifestBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var manifestJson = manifestBytes.AsMemory(bomBytes);
            using var document = JsonDocument.Parse(manifestJson);
            var manifest = document.RootElement;
            var framesPerSecond = manifest.GetProperty("framesPerSecond").GetDouble();
            var expectedCount = manifest.GetProperty("frameCount").GetInt32();
            var frames = Directory.GetDirectories(root, "frame-*")
                .Order(StringComparer.Ordinal)
                .Where(IsCompleteFrame)
                .ToArray();
            if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0 ||
                frames.Count() < 2 || frames.Length != expectedCount)
            {
                throw new InvalidDataException("draw-cache manifest and complete frame directories disagree");
            }
            return new DrawCache(frames, framesPerSecond);
        }

        private static bool IsCompleteFrame(string frameRoot)
        {
            var banks = Directory.GetDirectories(frameRoot, "bank-*");
            return File.Exists(Path.Combine(frameRoot, "properties.bin")) &&
                banks.Length == Ps5NativeParticleComputeRequest.SmallParticleBankCount &&
                banks.All(bank => new[] { 0, 1, 3, 4, 5 }
                    .All(index => File.Exists(Path.Combine(bank, $"buffer{index}.bin"))));
        }
    }

    private sealed record Options(
        string CacheRoot,
        string FirmwareRoot,
        int Width,
        int Height,
        int? FrameLimit)
    {
        public const string Usage =
            "usage: Prosperismo.NativeBackgroundProducer --cache-root <draw-cache> " +
            "--firmware-root <PS5 dump> [--width 1920] [--height 1080] [--frame-limit N]\n" +
            "       Prosperismo.NativeBackgroundProducer --validate-firstwave <NPXS40087 eboot.bin>\n" +
            "       Prosperismo.NativeBackgroundProducer --compile-firstwave-post <NPXS40087 eboot.bin>\n" +
            "       Prosperismo.NativeBackgroundProducer --self-test";

        public static Options? Parse(string[] args)
        {
            string? cacheRoot = null;
            string? firmwareRoot = null;
            var width = 1920;
            var height = 1080;
            int? frameLimit = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--cache-root" when index + 1 < args.Length:
                        cacheRoot = args[++index];
                        break;
                    case "--firmware-root" when index + 1 < args.Length:
                        firmwareRoot = args[++index];
                        break;
                    case "--width" when index + 1 < args.Length:
                        width = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--height" when index + 1 < args.Length:
                        height = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--frame-limit" when index + 1 < args.Length:
                        frameLimit = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        return null;
                }
            }

            return string.IsNullOrWhiteSpace(cacheRoot) || string.IsNullOrWhiteSpace(firmwareRoot) ||
                width <= 0 || height <= 0 || width > 8192 || height > 8192 || frameLimit <= 0
                ? null
                : new Options(
                    Path.GetFullPath(cacheRoot),
                    Path.GetFullPath(firmwareRoot),
                    width,
                    height,
                    frameLimit);
        }
    }
}
