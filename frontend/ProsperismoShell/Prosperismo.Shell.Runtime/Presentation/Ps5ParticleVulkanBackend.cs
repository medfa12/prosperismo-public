// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

// Off-screen proof for the recovered PS5 BGLayer large-particle draw.
// The shaders, scalar-resource buffers, particle properties, and sprites are
// blend, shader sampler, and PSM UI colour-target format.

using System.Buffers.Binary;
using System.IO.Compression;
using Prosperismo.Libs.Presentation;
using Prosperismo.Libs.Textures;
using Prosperismo.ShaderCompiler.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Prosperismo.Libs.Presentation;

public static class Ps5ParticleDrawProbe
{
    private const uint Width = 1920;
    private const uint Height = 1080;
    private const uint VerticesPerBillboard = 6;

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--ripple")
        {
            return RunRipple(args);
        }

        if (args.Length > 0 && args[0] == "--focus")
        {
            return RunFocus(args);
        }

        if (args.Length > 0 && args[0] == "--sequence")
        {
            return RunSequence(args);
        }

        var probeTimeText = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PROBE_TIME");
        var probeTime = double.TryParse(
            probeTimeText,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedProbeTime)
            ? Math.Max(parsedProbeTime, 0.0)
            : 6.5;

        if (args.Length is < 4 or > 5)
        {
            Console.Error.WriteLine(
                "usage: Prosperismo.Tools.Ps5ParticleDrawProbe " +
                "<particle.vert.spv> <particle.frag.spv> <Particle0.gnf> <Particle1.gnf> [output.png]");
            return 2;
        }

        var vertexPath = Path.GetFullPath(args[0]);
        var fragmentPath = Path.GetFullPath(args[1]);
        var particle0Path = Path.GetFullPath(args[2]);
        var particle1Path = Path.GetFullPath(args[3]);
        var outputPath = Path.GetFullPath(args.Length == 5
            ? args[4]
            : Path.Combine(Path.GetDirectoryName(vertexPath)!, "large-particle-native-t6.png"));

        var vertexCode = File.ReadAllBytes(vertexPath);
        var fragmentCode = File.ReadAllBytes(fragmentPath);
        var geometryPath = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_GEOMETRY_DEBUG");
        var geometryCode = string.IsNullOrWhiteSpace(geometryPath)
            ? null
            : File.ReadAllBytes(Path.GetFullPath(geometryPath));
        var vertexStem = Path.Combine(
            Path.GetDirectoryName(vertexPath)!,
            Path.GetFileNameWithoutExtension(vertexPath).Replace(".vert", string.Empty, StringComparison.Ordinal));
        var guestBufferCount = File.Exists($"{vertexStem}.vert.buffer5.bin") ? 6 : 5;
        var guestData = Enumerable.Range(0, guestBufferCount)
            .Select(index => File.ReadAllBytes($"{vertexStem}.vert.buffer{index}.bin"))
            .ToArray();
        var smallParticleDraw = guestData[1].Length >= 0x140;
        if (!smallParticleDraw)
        {
            // ResourcesLargeParticleVsPs+0x78 is the runtime camera aspect. It
            // is not an authored pattern value, so host setup supplies it.
            BinaryPrimitives.WriteUInt32LittleEndian(
                guestData[1].AsSpan(0x78),
                BitConverter.SingleToUInt32Bits((float)Width / Height));
        }
        var particleCountAtProbeTime = BinaryPrimitives.ReadUInt32LittleEndian(
            guestData[1].AsSpan(smallParticleDraw ? 0x20 : 0xAC));

        var particle0 = GnfImage.TryLoadRgba(particle0Path, out var textureWidth0, out var textureHeight0)
            ?? throw new InvalidDataException($"unsupported GNF: {particle0Path}");
        var particle1 = GnfImage.TryLoadRgba(particle1Path, out var textureWidth1, out var textureHeight1)
            ?? throw new InvalidDataException($"unsupported GNF: {particle1Path}");

        PrintTextureStats("Particle0", particle0, textureWidth0, textureHeight0);
        PrintTextureStats("Particle1", particle1, textureWidth1, textureHeight1);

        if (textureWidth0 != textureWidth1 || textureHeight0 != textureHeight1)
        {
            throw new InvalidDataException("Particle0 and Particle1 dimensions differ");
        }

        ReadOnlyMemory<byte>? geometryMemory = geometryCode is null
            ? (ReadOnlyMemory<byte>?)null
            : new ReadOnlyMemory<byte>(geometryCode);
        var resources = new Ps5NativeParticleResources(
            vertexCode,
            fragmentCode,
            new Ps5NativeParticleTexture(textureWidth0, textureHeight0, particle0),
            new Ps5NativeParticleTexture(textureWidth1, textureHeight1, particle1),
            geometryMemory);
        var draw = new Ps5NativeParticleDraw(
            (int)Width,
            (int)Height,
            particleCountAtProbeTime,
            guestData.Select(static bytes => (ReadOnlyMemory<byte>)bytes).ToArray());

        if (!resources.Particle0.IsValid || !resources.Particle1.IsValid || !draw.IsValid)
        {
            throw new InvalidDataException("native particle resources do not satisfy the recovered draw ABI");
        }

        var baseRgbaPath = Environment.GetEnvironmentVariable("PROSPERISMO_PS5_BASE_RGBA");
        ReadOnlyMemory<byte>? baseRgba = string.IsNullOrWhiteSpace(baseRgbaPath)
            ? (ReadOnlyMemory<byte>?)null
            : File.ReadAllBytes(baseRgbaPath);
        var renderer = new VulkanPs5NativeParticleRenderer();
        renderer.InitializeAsync(resources).GetAwaiter().GetResult();
        var rendered = renderer.RenderAsync(draw).GetAwaiter().GetResult();
        renderer.DisposeAsync().GetAwaiter().GetResult();
        var frame = baseRgba is { } baseFrame
            ? Ps5NativeParticleCompositor.CompositeAdditive(
                new Ps5NativeParticleFrame(rendered.Width, rendered.Height, baseFrame),
                rendered)
            : rendered;
        var rgba = frame.Rgba.ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        PngWriter.Write(outputPath, rgba, frame.Width, frame.Height);
        var rawOutputPath = Path.ChangeExtension(outputPath, ".rgba");
        File.WriteAllBytes(rawOutputPath, rgba);
        var changedPixels = CountChangedPixels(rgba, 1, 1, 9, 255);
        Console.WriteLine($"rendered: {outputPath}");
        Console.WriteLine($"raw RGBA: {rawOutputPath}");
        Console.WriteLine($"non-clear pixels: {changedPixels:N0}/{(long)frame.Width * frame.Height:N0}");
        Console.WriteLine($"native sample: t={probeTime:0.####}, particles={draw.ParticleCount}");
        Console.WriteLine("provenance: original firmware shaders/resources/properties");
        Console.WriteLine("host state: exact ONE/ONE/ADD blend and full-target viewport/scissor");
        Console.WriteLine(
            $"sampler: {string.Join(' ', Ps5NativeParticleRenderState.SamplerDescriptor.ToArray().Select(static word => $"{word:X8}"))} " +
            "(linear min/mag, nearest mip, clamp-to-edge)");
        Console.WriteLine(
            $"target: PSM PixelFormat {Ps5NativeParticleRenderState.UiRenderTargetPixelFormat} " +
            $"({Ps5NativeParticleRenderState.UiRenderTargetHostFormat}); " +
            $"particle intermediate code=0x{Ps5NativeParticleRenderState.ParticleIntermediatePixelFormat:X}");

        var allowClearFrame = string.Equals(
            Environment.GetEnvironmentVariable("PROSPERISMO_PS5_ALLOW_CLEAR_FRAME"),
            "1",
            StringComparison.Ordinal);
        return changedPixels > 0 || allowClearFrame ? 0 : 1;
    }

    private static int RunRipple(string[] args)
    {
        if (args.Length is not (4 or 5))
        {
            Console.Error.WriteLine(
                "usage: Prosperismo.Tools.Ps5ParticleDrawProbe --ripple " +
                "<ripple.frag.spv> <c0.bin> <c1.bin> [output-directory]");
            return 2;
        }

        const int width = 960;
        const int height = 540;
        var fragmentCode = File.ReadAllBytes(Path.GetFullPath(args[1]));
        var rippleConstants = File.ReadAllBytes(Path.GetFullPath(args[2]));
        var gradationConstants = File.ReadAllBytes(Path.GetFullPath(args[3]));
        if (rippleConstants.Length != 40 || gradationConstants.Length != 160)
        {
            throw new InvalidDataException(
                "ripple c0/c1 buffers must be exactly 40 and 160 bytes");
        }

        var outputDirectory = Path.GetFullPath(args.Length == 5
            ? args[4]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "ripple-proof"));
        Directory.CreateDirectory(outputDirectory);
        var source = CreateRippleProofTexture(width, height, target: false);
        var target = CreateRippleProofTexture(width, height, target: true);
        var resources = new Ps5NativeParticleResources(
            SpirvFixedShaders.CreateFullscreenVertex(1),
            fragmentCode,
            new Ps5NativeParticleTexture(width, height, source),
            new Ps5NativeParticleTexture(width, height, target));
        PngWriter.Write(Path.Combine(outputDirectory, "diagnostic-source.png"), source, width, height);
        PngWriter.Write(Path.Combine(outputDirectory, "diagnostic-target.png"), target, width, height);

        var frames = new (string Name, float Linear)[]
        {
            ("000", 0.00f),
            ("005", 0.05f),
            ("020", 0.20f),
            ("050", 0.50f),
            ("080", 0.80f),
            ("100", 1.00f),
        };
        var draws = new List<Ps5NativeParticleDraw>(frames.Length);
        var easedValues = new float[frames.Length];
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var (_, linear) = frames[frameIndex];
            var c0 = (byte[])rippleConstants.Clone();
            var x = 1.0f - linear;
            var x2 = x * x;
            var x4 = x2 * x2;
            var eased = 1.0f - 0.2f * x2 - 0.65f * x4 * x2 - 0.15f * x4 * x4;
            easedValues[frameIndex] = eased;
            WriteRippleSingle(c0.AsSpan(0x0C), eased);
            WriteRippleSingle(c0.AsSpan(0x10), MathF.Pow(eased, 2.2f));
            draws.Add(new Ps5NativeParticleDraw(
                width,
                height,
                1,
                new ReadOnlyMemory<byte>[]
                {
                    c0,
                    gradationConstants,
                    new byte[4],
                    new byte[4],
                    new byte[4],
                }));
        }

        var rendered = RenderSequence(
            resources,
            draws,
            verticesPerDrawUnit: 3,
            additiveBlend: false,
            separateFrames: true);
        var renderedBytes = rendered.Rgba.ToArray();
        var frameBytes = checked(width * height * 4);
        for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            var (name, linear) = frames[frameIndex];
            var outputPath = Path.Combine(outputDirectory, $"ripple-{name}.png");
            PngWriter.Write(
                outputPath,
                renderedBytes.AsSpan(frameIndex * frameBytes, frameBytes).ToArray(),
                width,
                height);
            Console.WriteLine(
                $"ripple frame: linear={linear:0.00} " +
                $"eased={easedValues[frameIndex]:0.000000} -> {outputPath}");
        }

        Console.WriteLine("provenance: original NPXS40087 ripple_p pixel shader");
        Console.WriteLine("host inputs: diagnostic source/target textures; opaque gradation-disabled path");
        return 0;
    }

    private static int RunFocus(string[] args)
    {
        if (args.Length is not (4 or 5))
        {
            Console.Error.WriteLine(
                "usage: Prosperismo.Tools.Ps5ParticleDrawProbe --focus " +
                "<focus.frag.spv> <c0.bin> <c1.bin> [output.png]");
            return 2;
        }

        const int width = 640;
        const int height = 360;
        var fragmentCode = File.ReadAllBytes(Path.GetFullPath(args[1]));
        var focusConstants = File.ReadAllBytes(Path.GetFullPath(args[2]));
        var displayConstants = File.ReadAllBytes(Path.GetFullPath(args[3]));
        if (focusConstants.Length is not (128 or 160) || displayConstants.Length != 8)
        {
            throw new InvalidDataException(
                "focus c0 must be AreaFocus's 128 or LineFocus's 160 bytes; c1 must be 8 bytes");
        }

        if (focusConstants.All(static value => value == 0) &&
            displayConstants.All(static value => value == 0))
        {
            PopulateFocusProofConstants(focusConstants, displayConstants, width, height);
        }

        // FocusRenderManager colour table; the 64x64 scalar field is a host
        // gradient used only to make every original shader sample observable.
        var colorTable = CreateFocusColorTable();
        var noise = CreateFocusProofNoise();
        var resources = new Ps5NativeParticleResources(
            SpirvFixedShaders.CreateFullscreenVertex(3),
            fragmentCode,
            new Ps5NativeParticleTexture(7, 1, colorTable),
            new Ps5NativeParticleTexture(64, 64, noise));
        var draw = new Ps5NativeParticleDraw(
            width,
            height,
            1,
            new ReadOnlyMemory<byte>[]
            {
                focusConstants,
                displayConstants,
                new byte[4],
                new byte[4],
                new byte[4],
            });
        var frame = RenderSequence(
            resources,
            [draw],
            verticesPerDrawUnit: 3,
            additiveBlend: false);
        var outputPath = Path.GetFullPath(args.Length == 5
            ? args[4]
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(args[1]))!,
                "focus-native-proof.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var rgba = frame.Rgba.ToArray();
        PngWriter.Write(outputPath, rgba, width, height);
        var changedPixels = CountChangedPixels(rgba, 1, 1, 9, 255);
        Console.WriteLine($"focus frame: {outputPath}");
        Console.WriteLine($"non-clear pixels: {changedPixels:N0}/{(long)width * height:N0}");
        Console.WriteLine("provenance: original libScePsm AreaFocus/LineFocus pixel shader");
        Console.WriteLine("host inputs: exact Sony colour table; controlled diagnostic noise/quad");
        return changedPixels > 0 ? 0 : 1;
    }

    private static void PopulateFocusProofConstants(
        byte[] c0,
        byte[] c1,
        int width,
        int height)
    {
        // Shared __GLOBAL_CB__ prefix recovered from reflection metadata.
        for (var offset = 0; offset < 0x20; offset += sizeof(float))
        {
            WriteRippleSingle(c0.AsSpan(offset), 1.0f); // FadeA/FadeB
        }
        WriteRippleSingle(c0.AsSpan(0x20), 0.0f);
        WriteRippleSingle(c0.AsSpan(0x24), 0.0f);
        WriteRippleSingle(c0.AsSpan(0x28), 1.0f);
        WriteRippleSingle(c0.AsSpan(0x2C), 1.0f); // TargetRect
        WriteRippleSingle(c0.AsSpan(0x30), 0.12f); // Radius
        BinaryPrimitives.WriteUInt32LittleEndian(c0.AsSpan(0x34), 0);
        WriteRippleSingle(c0.AsSpan(0x38), 0.12f); // FocusRadius
        WriteRippleSingle(c0.AsSpan(0x3C), (float)width / height);

        if (c0.Length == 128)
        {
            // AreaFocus layout from .metadata member order.
            WriteRippleSingle(c0.AsSpan(0x40), 0.0f); // Angle
            WriteRippleSingle(c0.AsSpan(0x44), 0.0f); // Pressing
            WriteRippleSingle(c0.AsSpan(0x48), 1.0f / Math.Min(width, height));
            WriteRippleSingle(c0.AsSpan(0x4C), 0.0f); // Moving
            WriteRippleSingle(c0.AsSpan(0x50), 5.0f); // NoiseScale
            WriteRippleSingle(c0.AsSpan(0x54), 1.25f); // reciprocal AreaAlphaGamma
            WriteRippleSingle(c0.AsSpan(0x58), 0.0f);
            WriteRippleSingle(c0.AsSpan(0x5C), 0.0f); // NoiseChangeParam
            WriteRippleSingle(c0.AsSpan(0x60), 1.0f); // ShowAlpha
            WriteRippleSingle(c0.AsSpan(0x64), 0.2f);
            WriteRippleSingle(c0.AsSpan(0x68), 0.3f);
            WriteRippleSingle(c0.AsSpan(0x6C), 30.0f);
            WriteRippleSingle(c0.AsSpan(0x70), 80.0f);
            WriteRippleSingle(c0.AsSpan(0x74), 0.25f);
            WriteRippleSingle(c0.AsSpan(0x78), 0.75f); // ShimmerParam
            WriteRippleSingle(c0.AsSpan(0x7C), 0.0f);
        }
        else
        {
            // LineFocus layout from .metadata member order.
            WriteRippleSingle(c0.AsSpan(0x40), 3.0f); // Thickness
            WriteRippleSingle(c0.AsSpan(0x44), 3.0f); // Offset
            WriteRippleSingle(c0.AsSpan(0x48), 1.0f / width);
            WriteRippleSingle(c0.AsSpan(0x4C), 1.0f / height); // Pixel
            WriteRippleSingle(c0.AsSpan(0x50), 5.0f); // NoiseScale
            WriteRippleSingle(c0.AsSpan(0x54), 0.065f); // MinOpacity
            WriteRippleSingle(c0.AsSpan(0x58), 0.0f);
            WriteRippleSingle(c0.AsSpan(0x5C), 0.0f); // NoiseChangeParam
            // Tone-curve vectors are diagnostic monotonic ramps; the original
            // shader consumes them directly and no CPU substitute shades pixels.
            for (var offset = 0x60; offset < 0x90; offset += sizeof(float))
            {
                WriteRippleSingle(c0.AsSpan(offset), (offset - 0x60) / 44.0f);
            }
            WriteRippleSingle(c0.AsSpan(0x90), 0.5f);
            WriteRippleSingle(c0.AsSpan(0x94), 0.5f);
            WriteRippleSingle(c0.AsSpan(0x98), 1.0f); // ShowAlpha
            WriteRippleSingle(c0.AsSpan(0x9C), 3.0f); // StrokePosition
        }

        WriteRippleSingle(c1.AsSpan(0x00), 1.0f); // GlobalAlpha
        WriteRippleSingle(c1.AsSpan(0x04), 1.0f); // GlobalIntensity
    }

    private static byte[] CreateFocusColorTable()
    {
        ReadOnlySpan<byte> rgb =
        [
            204, 255, 255,
            199, 227, 255,
            229, 229, 255,
            187, 196, 237,
            235, 199, 223,
            255, 223, 191,
            255, 204, 204,
        ];
        var rgba = new byte[7 * 4];
        for (var index = 0; index < 7; index++)
        {
            rgba[index * 4] = rgb[index * 3];
            rgba[index * 4 + 1] = rgb[index * 3 + 1];
            rgba[index * 4 + 2] = rgb[index * 3 + 2];
            rgba[index * 4 + 3] = 255;
        }

        return rgba;
    }

    private static byte[] CreateFocusProofNoise()
    {
        var rgba = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var value = (byte)((x * 3 + y * 5) & 0xFF);
                var offset = (y * 64 + x) * 4;
                rgba[offset] = value;
                rgba[offset + 1] = value;
                rgba[offset + 2] = value;
                rgba[offset + 3] = 255;
            }
        }

        return rgba;
    }

    private static byte[] CreateRippleProofTexture(int width, int height, bool target)
    {
        var rgba = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var nx = (float)x / Math.Max(width - 1, 1);
                var ny = (float)y / Math.Max(height - 1, 1);
                var grid = ((x / 80) + (y / 80)) % 2 == 0 ? 24 : 0;
                if (target)
                {
                    rgba[offset + 0] = (byte)Math.Clamp(18 + 95 * nx + grid, 0, 255);
                    rgba[offset + 1] = (byte)Math.Clamp(28 + 125 * (1 - ny), 0, 255);
                    rgba[offset + 2] = (byte)Math.Clamp(88 + 125 * ny, 0, 255);
                }
                else
                {
                    rgba[offset + 0] = (byte)Math.Clamp(5 + 32 * ny, 0, 255);
                    rgba[offset + 1] = (byte)Math.Clamp(20 + 55 * nx + grid, 0, 255);
                    rgba[offset + 2] = (byte)Math.Clamp(70 + 145 * (1 - nx), 0, 255);
                }
                rgba[offset + 3] = 255;
            }
        }
        return rgba;
    }

    private static void WriteRippleSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination,
            unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static int RunSequence(string[] args)
    {
        if (args.Length is not (6 or 7))
        {
            Console.Error.WriteLine(
                "usage: Prosperismo.Tools.Ps5ParticleDrawProbe --sequence " +
                "<particle.vert.spv> <particle.frag.spv> <Particle0.gnf> <Particle1.gnf> " +
                "<draw-root> [output.png]");
            return 2;
        }

        var vertexPath = Path.GetFullPath(args[1]);
        var fragmentPath = Path.GetFullPath(args[2]);
        var particle0Path = Path.GetFullPath(args[3]);
        var particle1Path = Path.GetFullPath(args[4]);
        var drawRoot = Path.GetFullPath(args[5]);
        var outputPath = Path.GetFullPath(args.Length == 7
            ? args[6]
            : Path.Combine(drawRoot, "small-particle-eight-bank.png"));
        var particle0 = GnfImage.TryLoadRgba(
            particle0Path, out var textureWidth0, out var textureHeight0)
            ?? throw new InvalidDataException($"unsupported GNF: {particle0Path}");
        var particle1 = GnfImage.TryLoadRgba(
            particle1Path, out var textureWidth1, out var textureHeight1)
            ?? throw new InvalidDataException($"unsupported GNF: {particle1Path}");
        var resources = new Ps5NativeParticleResources(
            File.ReadAllBytes(vertexPath),
            File.ReadAllBytes(fragmentPath),
            new Ps5NativeParticleTexture(textureWidth0, textureHeight0, particle0),
            new Ps5NativeParticleTexture(textureWidth1, textureHeight1, particle1));
        var bankRoots = Directory.GetDirectories(drawRoot, "bank-*")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (bankRoots.Length != Ps5NativeParticleComputeRequest.SmallParticleBankCount)
        {
            throw new InvalidDataException("draw root must contain eight bank-* snapshots");
        }
        var draws = bankRoots
            .Select(static bankRoot => Directory.GetFiles(bankRoot, "buffer*.bin")
                .Order(StringComparer.Ordinal)
                .Select(static path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path))
                .ToArray())
            .Where(static buffers =>
                buffers.Length > 1 &&
                buffers[1].Length >= 0x140 &&
                BinaryPrimitives.ReadUInt32LittleEndian(buffers[1].Span[0x20..]) > 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(buffers[1].Span[0x28..]) > 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(buffers[1].Span[0x2C..]) > 0)
            .Select(static buffers => new Ps5NativeParticleDraw(
                1920,
                1080,
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    buffers[1].Span[0x20..]),
                buffers))
            .Where(static draw => draw.ParticleCount > 0)
            .ToArray();

        Ps5NativeParticleFrame frame;
        if (Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PERSISTENT_PROBE") == "1")
        {
            var renderer = new VulkanPs5NativeParticleRenderer();
            renderer.InitializeAsync(resources).GetAwaiter().GetResult();
            try
            {
                var repeat = int.TryParse(
                    Environment.GetEnvironmentVariable("PROSPERISMO_PS5_PERSISTENT_PROBE_REPEAT"),
                    out var parsedRepeat)
                    ? Math.Clamp(parsedRepeat, 1, 1000)
                    : 1;
                frame = null!;
                for (var iteration = 0; iteration < repeat; iteration++)
                {
                    frame = renderer.RenderSequenceAsync(draws).GetAwaiter().GetResult();
                }
            }
            finally
            {
                renderer.DisposeAsync().GetAwaiter().GetResult();
            }
        }
        else
        {
            frame = RenderSequence(resources, draws);
        }
        var rgba = frame.Rgba.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        PngWriter.Write(outputPath, rgba, frame.Width, frame.Height);
        File.WriteAllBytes(Path.ChangeExtension(outputPath, ".rgba"), rgba);
        var changedPixels = CountChangedPixels(rgba, 1, 1, 9, 255);
        Console.WriteLine($"rendered eight-bank sequence: {outputPath}");
        Console.WriteLine($"non-clear pixels: {changedPixels:N0}/{(long)frame.Width * frame.Height:N0}");
        return changedPixels > 0 ||
            Environment.GetEnvironmentVariable("PROSPERISMO_PS5_ALLOW_CLEAR_FRAME") == "1" ? 0 : 1;
    }

    internal static Ps5NativeParticleFrame Render(
        Ps5NativeParticleResources resources,
        Ps5NativeParticleDraw draw) => RenderSequence(resources, [draw]);

    internal static Ps5NativeParticleFrame RenderSequence(
        Ps5NativeParticleResources resources,
        IReadOnlyList<Ps5NativeParticleDraw> draws,
        uint verticesPerDrawUnit = VerticesPerBillboard,
        bool additiveBlend = true,
        bool separateFrames = false,
        bool requireFragmentWave32 = false)
    {
        if (!resources.Particle0.IsValid || !resources.Particle1.IsValid ||
            draws.Count == 0 || draws.Any(static draw => !draw.IsValid) ||
            draws.Any(draw => draw.Width != draws[0].Width || draw.Height != draws[0].Height ||
                draw.VertexBuffers.Count != draws[0].VertexBuffers.Count))
        {
            throw new ArgumentException("native particle resources do not satisfy the recovered draw ABI");
        }

        var vertexCode = resources.VertexSpirv.ToArray();
        var fragmentCode = resources.FragmentSpirv.ToArray();
        var geometryCode = resources.GeometrySpirv?.ToArray();
        var guestDataSets = draws
            .Select(static draw => draw.VertexBuffers.Select(static buffer => buffer.ToArray()).ToArray())
            .ToArray();
        var guestData = guestDataSets[0];
        var particle0 = resources.Particle0.Rgba.ToArray();
        var particle1 = resources.Particle1.Rgba.ToArray();
        var textureWidth0 = resources.Particle0.Width;
        var textureHeight0 = resources.Particle0.Height;
        var textureWidth1 = resources.Particle1.Width;
        var textureHeight1 = resources.Particle1.Height;
        var particleCounts = draws.Select(static draw => draw.ParticleCount).ToArray();
        var Width = checked((uint)draws[0].Width);
        var Height = checked((uint)draws[0].Height);

        unsafe
        {
            var vk = Vk.GetApi();
            var appName = (byte*)SilkMarshal.StringToPtr("ProsperismoPs5ParticleDrawProbe");
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApiVersion = Vk.Version13,
            };
            var instanceInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
            };

            // macOS has no native Vulkan driver: it runs on MoltenVK, which
            // presents itself as a *portability* implementation. Without the
            // enumeration extension and its instance flag the loader reports no
            // conformant driver and vkCreateInstance fails with
            // ErrorIncompatibleDriver, even though MoltenVK is installed.
            nint portabilityExtension = 0;
            byte** extensionNames = stackalloc byte*[1];
            if (Ps5VulkanApi.RequiresPortabilityEnumeration(AppContext.BaseDirectory))
            {
                portabilityExtension = SilkMarshal.StringToPtr("VK_KHR_portability_enumeration");
                extensionNames[0] = (byte*)portabilityExtension;
                instanceInfo.EnabledExtensionCount = 1;
                instanceInfo.PpEnabledExtensionNames = extensionNames;
                instanceInfo.Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr;
            }

            var instanceResult = vk.CreateInstance(in instanceInfo, null, out var instance);
            SilkMarshal.Free((nint)appName);
            if (portabilityExtension != 0)
            {
                SilkMarshal.Free(portabilityExtension);
            }

            Check(instanceResult, "vkCreateInstance");

            uint physicalCount = 0;
            Check(vk.EnumeratePhysicalDevices(instance, &physicalCount, null), "vkEnumeratePhysicalDevices(count)");
            if (physicalCount == 0)
            {
                throw new InvalidOperationException("no Vulkan device found");
            }

            var physicals = new PhysicalDevice[physicalCount];
            fixed (PhysicalDevice* pPhysicals = physicals)
            {
                Check(vk.EnumeratePhysicalDevices(instance, &physicalCount, pPhysicals), "vkEnumeratePhysicalDevices");
            }

            var physical = physicals[0];
            foreach (var candidate in physicals)
            {
                vk.GetPhysicalDeviceProperties(candidate, out var candidateProperties);
                if (candidateProperties.DeviceType == PhysicalDeviceType.DiscreteGpu)
                {
                    physical = candidate;
                    break;
                }
            }

            vk.GetPhysicalDeviceProperties(physical, out var physicalProperties);
            Console.WriteLine($"Vulkan device: {SilkMarshal.PtrToString((nint)physicalProperties.DeviceName)}");
            var subgroup = new PhysicalDeviceSubgroupProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
            };
            var subgroupQuery = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &subgroup,
            };
            vk.GetPhysicalDeviceProperties2(physical, &subgroupQuery);
            Console.WriteLine($"Vulkan subgroup size: {subgroup.SubgroupSize}");
            vk.GetPhysicalDeviceFeatures(physical, out var supportedFeatures);
            if (!supportedFeatures.ShaderInt64)
            {
                throw new InvalidOperationException("translated firmware shaders require shaderInt64");
            }

            uint familyCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pFamilies = families)
            {
                vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pFamilies);
            }

            uint? graphicsFamilyFound = null;
            for (uint index = 0; index < familyCount; index++)
            {
                if (families[index].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                {
                    graphicsFamilyFound = index;
                    break;
                }
            }

            var graphicsFamily = graphicsFamilyFound
                ?? throw new InvalidOperationException("device has no graphics queue");
            var priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                ShaderInt64 = true,
                GeometryShader = geometryCode is not null && supportedFeatures.GeometryShader,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
            };
            var subgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
            };
            var subgroupProperties = new PhysicalDeviceSubgroupSizeControlProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
            };
            if (requireFragmentWave32)
            {
                var properties2 = new PhysicalDeviceProperties2
                {
                    SType = StructureType.PhysicalDeviceProperties2,
                    PNext = &subgroupProperties,
                };
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = &subgroupFeatures,
                };
                vk.GetPhysicalDeviceProperties2(physical, &properties2);
                vk.GetPhysicalDeviceFeatures2(physical, &features2);
                if (!subgroupFeatures.SubgroupSizeControl ||
                    subgroupProperties.MinSubgroupSize > 32 ||
                    subgroupProperties.MaxSubgroupSize < 32 ||
                    !subgroupProperties.RequiredSubgroupSizeStages.HasFlag(
                        ShaderStageFlags.FragmentBit))
                {
                    throw new InvalidOperationException(
                        "the original focus shader requires a 32-lane fragment subgroup, " +
                        "but this Vulkan device cannot require wave32");
                }
            }

            var enabledSubgroupFeatures = new PhysicalDeviceSubgroupSizeControlFeatures
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlFeatures,
                SubgroupSizeControl = requireFragmentWave32,
            };
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = requireFragmentWave32 ? &enabledSubgroupFeatures : null,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                PEnabledFeatures = &enabledFeatures,
            };
            Check(vk.CreateDevice(physical, in deviceInfo, null, out var device), "vkCreateDevice");
            vk.GetDeviceQueue(device, graphicsFamily, 0, out var queue);
            vk.GetPhysicalDeviceMemoryProperties(physical, out var memoryProperties);

            var commandPoolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = graphicsFamily,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            };
            Check(vk.CreateCommandPool(device, in commandPoolInfo, null, out var commandPool), "vkCreateCommandPool");
            var commandAllocate = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(vk.AllocateCommandBuffers(device, in commandAllocate, out var commandBuffer), "vkAllocateCommandBuffers");

            var guestBufferSets = new Silk.NET.Vulkan.Buffer[guestDataSets.Length][];
            var guestMemorySets = new DeviceMemory[guestDataSets.Length][];
            for (var drawIndex = 0; drawIndex < guestDataSets.Length; drawIndex++)
            {
                guestBufferSets[drawIndex] = new Silk.NET.Vulkan.Buffer[guestData.Length];
                guestMemorySets[drawIndex] = new DeviceMemory[guestData.Length];
                for (var index = 0; index < guestData.Length; index++)
                {
                    CreateBuffer(
                        vk, device, memoryProperties, (ulong)guestDataSets[drawIndex][index].Length,
                        BufferUsageFlags.StorageBufferBit, true,
                        out guestBufferSets[drawIndex][index], out guestMemorySets[drawIndex][index]);
                    UploadMemory(
                        vk, device, guestMemorySets[drawIndex][index], guestDataSets[drawIndex][index]);
                }
            }

            CreateTexture(
                vk, device, memoryProperties, commandBuffer, queue,
                particle0, (uint)textureWidth0, (uint)textureHeight0,
                out var texture0Image, out var texture0Memory, out var texture0View);
            CreateTexture(
                vk, device, memoryProperties, commandBuffer, queue,
                particle1, (uint)textureWidth1, (uint)textureHeight1,
                out var texture1Image, out var texture1Memory, out var texture1View);

            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                // large_particle_p constructs {0x92, 0, 0x02500000, 0}: linear
                // min/mag, nearest/base-mip selection, and clamp-to-edge on U/V/W.
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = 0,
                MaxLod = 0,
                MaxAnisotropy = 1,
            };
            Check(vk.CreateSampler(device, in samplerInfo, null, out var sampler), "vkCreateSampler");

            CreateImage(
                vk, device, memoryProperties, Width, Height,
                Format.R8G8B8A8Unorm,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                out var targetImage, out var targetMemory);
            var targetView = CreateImageView(vk, device, targetImage, Format.R8G8B8A8Unorm);

            var attachment = new AttachmentDescription
            {
                Format = Format.R8G8B8A8Unorm,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.TransferSrcOptimal,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &attachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(vk.CreateRenderPass(device, in renderPassInfo, null, out var renderPass), "vkCreateRenderPass");
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = &targetView,
                Width = Width,
                Height = Height,
                Layers = 1,
            };
            Check(vk.CreateFramebuffer(device, in framebufferInfo, null, out var framebuffer), "vkCreateFramebuffer");

            var layoutBindings = stackalloc DescriptorSetLayoutBinding[3];
            layoutBindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = (uint)guestData.Length,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            };
            for (uint binding = 1; binding <= 2; binding++)
            {
                layoutBindings[binding] = new DescriptorSetLayoutBinding
                {
                    Binding = binding,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.FragmentBit,
                };
            }
            var setLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 3,
                PBindings = layoutBindings,
            };
            Check(vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out var setLayout), "vkCreateDescriptorSetLayout");
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &setLayout,
            };
            Check(vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out var pipelineLayout), "vkCreatePipelineLayout");

            var vertexModule = CreateShaderModule(vk, device, vertexCode);
            var fragmentModule = CreateShaderModule(vk, device, fragmentCode);
            var geometryModule = geometryCode is null
                ? default
                : CreateShaderModule(vk, device, geometryCode);
            var entryName = (byte*)SilkMarshal.StringToPtr("main");
            var stageCount = geometryCode is null ? 2u : 3u;
            var stages = stackalloc PipelineShaderStageCreateInfo[(int)stageCount];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = entryName,
            };
            var fragmentStageIndex = geometryCode is null ? 1 : 2;
            if (geometryCode is not null)
            {
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.GeometryBit,
                    Module = geometryModule,
                    PName = entryName,
                };
            }
            stages[fragmentStageIndex] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragmentModule,
                PName = entryName,
            };
            var requiredFragmentSubgroupSize = new PipelineShaderStageRequiredSubgroupSizeCreateInfo
            {
                SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                RequiredSubgroupSize = 32,
            };
            if (requireFragmentWave32)
            {
                stages[fragmentStageIndex].PNext = &requiredFragmentSubgroupSize;
            }
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var assembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewport = new Viewport(0, 0, Width, Height, 0, 1);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(Width, Height));
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1,
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = additiveBlend,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.One,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.One,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blendState = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };
            var graphicsInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PColorBlendState = &blendState,
                Layout = pipelineLayout,
                RenderPass = renderPass,
                Subpass = 0,
            };
            Check(vk.CreateGraphicsPipelines(device, default, 1, in graphicsInfo, null, out var pipeline), "vkCreateGraphicsPipelines");
            SilkMarshal.Free((nint)entryName);
            Console.WriteLine(requireFragmentWave32
                ? "driver accepted the recovered Sony pixel pipeline at required wave32"
                : "driver accepted the recovered firmware graphics pipeline");

            var poolSizes = stackalloc DescriptorPoolSize[2];
            poolSizes[0] = new DescriptorPoolSize(
                DescriptorType.StorageBuffer, (uint)(guestData.Length * guestDataSets.Length));
            poolSizes[1] = new DescriptorPoolSize(
                DescriptorType.CombinedImageSampler, (uint)(2 * guestDataSets.Length));
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = (uint)guestDataSets.Length,
                PoolSizeCount = 2,
                PPoolSizes = poolSizes,
            };
            Check(vk.CreateDescriptorPool(device, in poolInfo, null, out var descriptorPool), "vkCreateDescriptorPool");
            var descriptorSets = new DescriptorSet[guestDataSets.Length];
            var setLayouts = Enumerable.Repeat(setLayout, guestDataSets.Length).ToArray();
            fixed (DescriptorSetLayout* pSetLayouts = setLayouts)
            fixed (DescriptorSet* pDescriptorSets = descriptorSets)
            {
                var allocateSetInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptorPool,
                    DescriptorSetCount = (uint)descriptorSets.Length,
                    PSetLayouts = pSetLayouts,
                };
                Check(
                    vk.AllocateDescriptorSets(device, in allocateSetInfo, pDescriptorSets),
                    "vkAllocateDescriptorSets");
            }
            var imageInfos = stackalloc DescriptorImageInfo[2];
            imageInfos[0] = new DescriptorImageInfo(sampler, texture0View, ImageLayout.ShaderReadOnlyOptimal);
            imageInfos[1] = new DescriptorImageInfo(sampler, texture1View, ImageLayout.ShaderReadOnlyOptimal);
            var writes = stackalloc WriteDescriptorSet[3];
            for (var drawIndex = 0; drawIndex < guestDataSets.Length; drawIndex++)
            {
                var bufferInfos = new DescriptorBufferInfo[guestData.Length];
                for (var index = 0; index < guestData.Length; index++)
                {
                    bufferInfos[index] = new DescriptorBufferInfo(
                        guestBufferSets[drawIndex][index],
                        0,
                        (ulong)guestDataSets[drawIndex][index].Length);
                }
                fixed (DescriptorBufferInfo* pBufferInfos = bufferInfos)
                {
                    writes[0] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = descriptorSets[drawIndex],
                        DstBinding = 0,
                        DescriptorCount = (uint)guestData.Length,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = pBufferInfos,
                    };
                    for (uint index = 0; index < 2; index++)
                    {
                        writes[index + 1] = new WriteDescriptorSet
                        {
                            SType = StructureType.WriteDescriptorSet,
                            DstSet = descriptorSets[drawIndex],
                            DstBinding = index + 1,
                            DescriptorCount = 1,
                            DescriptorType = DescriptorType.CombinedImageSampler,
                            PImageInfo = &imageInfos[index],
                        };
                    }
                    vk.UpdateDescriptorSets(device, 3, writes, 0, null);
                }
            }

            var readbackBytes = checked((ulong)Width * Height * 4);
            CreateBuffer(
                vk, device, memoryProperties, readbackBytes,
                BufferUsageFlags.TransferDstBit, true,
                out var readbackBuffer, out var readbackMemory);

            var frameByteCount = checked((int)readbackBytes);
            var rgba = new byte[checked(frameByteCount * (separateFrames ? draws.Count : 1))];

            void RenderAndReadback(int firstDraw, int drawCount, int destinationOffset)
            {
                var drawCommandBuffer = commandBuffer;
                var drawScissor = new Rect2D(
                    new Offset2D(0, 0),
                    new Extent2D(Width, Height));
                Check(vk.ResetCommandBuffer(drawCommandBuffer, 0), "vkResetCommandBuffer(draw)");
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
                Check(vk.BeginCommandBuffer(drawCommandBuffer, in begin), "vkBeginCommandBuffer(draw)");
                var clear = new ClearValue();
                clear.Color = new ClearColorValue(0.002f, 0.004f, 0.035f, 1f);
                var renderBegin = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = renderPass,
                    Framebuffer = framebuffer,
                    RenderArea = drawScissor,
                    ClearValueCount = 1,
                    PClearValues = &clear,
                };
                vk.CmdBeginRenderPass(drawCommandBuffer, in renderBegin, SubpassContents.Inline);
                vk.CmdBindPipeline(drawCommandBuffer, PipelineBindPoint.Graphics, pipeline);
                // invocation id. floor(v5 / 6) selects the particle and v5 % 6
                // indexes its six-entry corner table. An indexed draw replaces v5
                // with a fetched 0..3 corner and collapses every quad onto particle
                // zero, so preserve the native non-indexed triangle-list launch.
                for (var drawIndex = firstDraw; drawIndex < firstDraw + drawCount; drawIndex++)
                {
                    vk.CmdBindDescriptorSets(
                        drawCommandBuffer, PipelineBindPoint.Graphics, pipelineLayout,
                        0, 1, in descriptorSets[drawIndex], 0, null);
                    vk.CmdDraw(
                        drawCommandBuffer,
                        particleCounts[drawIndex] * verticesPerDrawUnit,
                        1,
                        0,
                        0);
                }
                vk.CmdEndRenderPass(drawCommandBuffer);
                var copyRegion = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageOffset = new Offset3D(0, 0, 0),
                    ImageExtent = new Extent3D(Width, Height, 1),
                };
                vk.CmdCopyImageToBuffer(
                    drawCommandBuffer, targetImage, ImageLayout.TransferSrcOptimal,
                    readbackBuffer, 1, in copyRegion);
                var hostBarrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                };
                vk.CmdPipelineBarrier(
                    drawCommandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit,
                    0, 1, in hostBarrier, 0, null, 0, null);
                Check(vk.EndCommandBuffer(drawCommandBuffer), "vkEndCommandBuffer(draw)");
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &drawCommandBuffer,
                };
                Check(vk.QueueSubmit(queue, 1, in submit, default), "vkQueueSubmit(draw)");
                Check(vk.QueueWaitIdle(queue), "vkQueueWaitIdle(draw)");

                void* mapped;
                Check(
                    vk.MapMemory(device, readbackMemory, 0, readbackBytes, 0, &mapped),
                    "vkMapMemory(readback)");
                new ReadOnlySpan<byte>(mapped, frameByteCount).CopyTo(
                    rgba.AsSpan(destinationOffset, frameByteCount));
                vk.UnmapMemory(device, readbackMemory);
            }

            if (separateFrames)
            {
                for (var drawIndex = 0; drawIndex < draws.Count; drawIndex++)
                {
                    RenderAndReadback(drawIndex, 1, drawIndex * frameByteCount);
                }
            }
            else
            {
                RenderAndReadback(0, draws.Count, 0);
            }
            vk.DeviceWaitIdle(device);
            vk.DestroyBuffer(device, readbackBuffer, null);
            vk.FreeMemory(device, readbackMemory, null);
            vk.DestroyDescriptorPool(device, descriptorPool, null);
            vk.DestroyPipeline(device, pipeline, null);
            if (geometryCode is not null)
            {
                vk.DestroyShaderModule(device, geometryModule, null);
            }
            vk.DestroyShaderModule(device, fragmentModule, null);
            vk.DestroyShaderModule(device, vertexModule, null);
            vk.DestroyPipelineLayout(device, pipelineLayout, null);
            vk.DestroyDescriptorSetLayout(device, setLayout, null);
            vk.DestroyFramebuffer(device, framebuffer, null);
            vk.DestroyRenderPass(device, renderPass, null);
            vk.DestroyImageView(device, targetView, null);
            vk.DestroyImage(device, targetImage, null);
            vk.FreeMemory(device, targetMemory, null);
            vk.DestroySampler(device, sampler, null);
            DestroyTexture(vk, device, texture1Image, texture1View, texture1Memory);
            DestroyTexture(vk, device, texture0Image, texture0View, texture0Memory);
            for (var drawIndex = 0; drawIndex < guestBufferSets.Length; drawIndex++)
            {
                for (var index = 0; index < guestBufferSets[drawIndex].Length; index++)
                {
                    vk.DestroyBuffer(device, guestBufferSets[drawIndex][index], null);
                    vk.FreeMemory(device, guestMemorySets[drawIndex][index], null);
                }
            }
            vk.DestroyCommandPool(device, commandPool, null);
            vk.DestroyDevice(device, null);
            vk.DestroyInstance(instance, null);

            return new Ps5NativeParticleFrame(
                (int)Width,
                checked((int)Height * (separateFrames ? draws.Count : 1)),
                rgba);
        }
    }

    static void PrintTextureStats(string name, byte[] rgba, int width, int height)
    {
        var nonzeroRgb = 0;
        var nonzeroAlpha = 0;
        byte maxRgb = 0;
        byte maxAlpha = 0;
        for (var offset = 0; offset + 3 < rgba.Length; offset += 4)
        {
            var rgb = Math.Max(rgba[offset], Math.Max(rgba[offset + 1], rgba[offset + 2]));
            var alpha = rgba[offset + 3];
            if (rgb != 0)
            {
                nonzeroRgb++;
            }
            if (alpha != 0)
            {
                nonzeroAlpha++;
            }
            maxRgb = Math.Max(maxRgb, rgb);
            maxAlpha = Math.Max(maxAlpha, alpha);
        }

        Console.WriteLine(
            $"{name}: {width}x{height} nonzero_rgb={nonzeroRgb} " +
            $"nonzero_alpha={nonzeroAlpha} max_rgb={maxRgb} max_alpha={maxAlpha}");
    }

    internal static unsafe ShaderModule CreateShaderModule(Vk vk, Device device, byte[] code)
    {
        fixed (byte* pCode = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)pCode,
            };
            Check(vk.CreateShaderModule(device, in info, null, out var module), "vkCreateShaderModule");
            return module;
        }
    }

    internal static unsafe void CreateBuffer(
        Vk vk, Device device, PhysicalDeviceMemoryProperties memoryProperties,
        ulong size, BufferUsageFlags usage, bool hostVisible,
        out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(vk.CreateBuffer(device, in info, null, out buffer), "vkCreateBuffer");
        vk.GetBufferMemoryRequirements(device, buffer, out var requirements);
        var flags = hostVisible
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit;
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(memoryProperties, requirements.MemoryTypeBits, flags),
        };
        Check(vk.AllocateMemory(device, in allocation, null, out memory), "vkAllocateMemory(buffer)");
        Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");
    }

    internal static unsafe void UploadMemory(Vk vk, Device device, DeviceMemory memory, byte[] data)
    {
        void* mapped;
        Check(vk.MapMemory(device, memory, 0, (ulong)data.Length, 0, &mapped), "vkMapMemory(upload)");
        data.CopyTo(new Span<byte>(mapped, data.Length));
        vk.UnmapMemory(device, memory);
    }

    internal static unsafe void CreateImage(
        Vk vk, Device device, PhysicalDeviceMemoryProperties memoryProperties,
        uint width, uint height, Format format, ImageUsageFlags usage,
        out Image image, out DeviceMemory memory)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(vk.CreateImage(device, in info, null, out image), "vkCreateImage");
        vk.GetImageMemoryRequirements(device, image, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(
                memoryProperties, requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(vk.AllocateMemory(device, in allocation, null, out memory), "vkAllocateMemory(image)");
        Check(vk.BindImageMemory(device, image, memory, 0), "vkBindImageMemory");
    }

    internal static unsafe ImageView CreateImageView(Vk vk, Device device, Image image, Format format)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        Check(vk.CreateImageView(device, in info, null, out var view), "vkCreateImageView");
        return view;
    }

    internal static unsafe void CreateTexture(
        Vk vk, Device device, PhysicalDeviceMemoryProperties memoryProperties,
        CommandBuffer commandBuffer, Queue queue,
        byte[] rgba, uint width, uint height,
        out Image image, out DeviceMemory memory, out ImageView view)
    {
        CreateBuffer(
            vk, device, memoryProperties, (ulong)rgba.Length,
            BufferUsageFlags.TransferSrcBit, true,
            out var staging, out var stagingMemory);
        UploadMemory(vk, device, stagingMemory, rgba);
        CreateImage(
            vk, device, memoryProperties, width, height, Format.R8G8B8A8Unorm,
            ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            out image, out memory);
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        Check(vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(texture)");
        Check(vk.BeginCommandBuffer(commandBuffer, in begin), "vkBeginCommandBuffer(texture)");
        TransitionImage(
            vk, commandBuffer, image,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, AccessFlags.TransferWriteBit);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(width, height, 1),
        };
        vk.CmdCopyBufferToImage(commandBuffer, staging, image, ImageLayout.TransferDstOptimal, 1, in copy);
        TransitionImage(
            vk, commandBuffer, image,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(texture)");
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        Check(vk.QueueSubmit(queue, 1, in submit, default), "vkQueueSubmit(texture)");
        Check(vk.QueueWaitIdle(queue), "vkQueueWaitIdle(texture)");
        vk.DestroyBuffer(device, staging, null);
        vk.FreeMemory(device, stagingMemory, null);
        view = CreateImageView(vk, device, image, Format.R8G8B8A8Unorm);
    }

    internal static unsafe void TransitionImage(
        Vk vk, CommandBuffer commandBuffer, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags sourceStage, PipelineStageFlags destinationStage,
        AccessFlags sourceAccess, AccessFlags destinationAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
        };
        vk.CmdPipelineBarrier(
            commandBuffer, sourceStage, destinationStage,
            0, 0, null, 0, null, 1, in barrier);
    }

    internal static unsafe void DestroyTexture(
        Vk vk, Device device, Image image, ImageView view, DeviceMemory memory)
    {
        vk.DestroyImageView(device, view, null);
        vk.DestroyImage(device, image, null);
        vk.FreeMemory(device, memory, null);
    }

    internal static uint FindMemoryType(
        PhysicalDeviceMemoryProperties properties,
        uint supportedBits,
        MemoryPropertyFlags required)
    {
        for (var index = 0; index < properties.MemoryTypeCount; index++)
        {
            if ((supportedBits & (1u << index)) != 0 &&
                (properties.MemoryTypes[index].PropertyFlags & required) == required)
            {
                return (uint)index;
            }
        }
        throw new InvalidOperationException($"no Vulkan memory type with {required}");
    }

    static int CountChangedPixels(byte[] rgba, byte r, byte g, byte b, byte a)
    {
        var count = 0;
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            if (rgba[offset] != r || rgba[offset + 1] != g ||
                rgba[offset + 2] != b || rgba[offset + 3] != a)
            {
                count++;
            }
        }
        return count;
    }

    internal static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed: {result}");
        }
    }

    static class PngWriter
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

        public static void Write(string path, byte[] rgba, int width, int height)
        {
            using var stream = File.Create(path);
            stream.Write(Signature);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(stream, "IHDR", header);
            var rowBytes = width * 4;
            var raw = new byte[checked(height * (rowBytes + 1))];
            for (var y = 0; y < height; y++)
            {
                System.Buffer.BlockCopy(rgba, y * rowBytes, raw, y * (rowBytes + 1) + 1, rowBytes);
            }
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            {
                zlib.Write(raw);
            }
            WriteChunk(stream, "IDAT", compressed.ToArray());
            WriteChunk(stream, "IEND", []);
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            stream.Write(length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            var crcInput = new byte[typeBytes.Length + data.Length];
            typeBytes.CopyTo(crcInput, 0);
            data.CopyTo(crcInput, typeBytes.Length);
            Span<byte> crc = stackalloc byte[4];
            WriteBigEndian(crc, 0, Crc32(crcInput));
            stream.Write(crc);
        }

        private static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFF_FFFFu;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ (0xEDB8_8320u & (uint)-(int)(crc & 1));
                }
            }
            return ~crc;
        }

        private static void WriteBigEndian(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(bytes[offset..], value);
    }

}
