// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Prosperismo.HLE;
using Prosperismo.ShaderCompiler;
using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Persistent runtime form of NPXS40087's <c>rect_uv_vv</c> + <c>light_p</c>
/// compositor. Shader code, PNG-transcoded IES textures and ColorCb records
/// retain the recovered ABI; the host translates that ABI to Vulkan.
/// </summary>
public sealed class Ps5NativeLightProgram
{
    private const ulong ProgramAddress = 0x1000_0000;
    private const ulong VolatileCbAddress = 0x0900_0000;
    private const ulong ColorCbAddress = 0x0A00_0000;
    private const ulong RectConstantsAddress = 0x0B00_0000;
    private const ulong TexFloorAddress = 0x1_0000_0000;
    private const ulong TexVolumeAddress = 0x2_0000_0000;
    private const ulong TexParticleAddress = 0x3_0000_0000;

    private const int LightPsOffset = 0x11F9700;
    private const int LightPsLength = 0x818;
    private const int RectVsOffset = 0x11EEE00;
    private const int RectVsLength = 0xCC;
    private const int TexFloorBlob = 0x1006AE0 + 0x4000;
    private const int TexVolumeBlob = 0x10029E0 + 0x4000;
    private const int TextureSize = 128;

    private readonly byte[] _vertexSpirv;
    private readonly byte[] _fragmentSpirv;
    private readonly Gen5GlobalMemoryBinding[] _vertexBindings;
    private readonly Gen5GlobalMemoryBinding[] _fragmentBindings;
    private readonly byte[] _floorRgba;
    private readonly byte[] _volumeRgba;
    private readonly byte[] _imageTags;
    private readonly int[] _imageAliases;
    private readonly Gen5NggPrimitiveConnectivity _nggPrimitiveConnectivity;

    private Ps5NativeLightProgram(
        byte[] vertexSpirv,
        byte[] fragmentSpirv,
        IReadOnlyList<Gen5GlobalMemoryBinding> vertexBindings,
        IReadOnlyList<Gen5GlobalMemoryBinding> fragmentBindings,
        byte[] floorRgba,
        byte[] volumeRgba,
        byte[] imageTags,
        int[] imageAliases,
        Gen5NggPrimitiveConnectivity nggPrimitiveConnectivity)
    {
        _vertexSpirv = vertexSpirv;
        _fragmentSpirv = fragmentSpirv;
        _vertexBindings = vertexBindings.ToArray();
        _fragmentBindings = fragmentBindings.ToArray();
        _floorRgba = floorRgba;
        _volumeRgba = volumeRgba;
        _imageTags = imageTags;
        _imageAliases = imageAliases;
        _nggPrimitiveConnectivity = nggPrimitiveConnectivity;
        ParticleTextureIndex = Array.IndexOf(_imageTags, (byte)3);
        if (ParticleTextureIndex < 0)
        {
            throw new InvalidDataException("light_p does not expose its texP binding");
        }
    }

    public int ParticleTextureIndex { get; }

    /// <summary>The recovered host launch required by <c>rect_uv_vv</c>.</summary>
    public Gen5NggPrimitiveConnectivity NggPrimitiveConnectivity =>
        _nggPrimitiveConnectivity;

    public static Ps5NativeLightProgram Compile(
        ReadOnlySpan<byte> eboot,
        ReadOnlySpan<byte> floorRgba,
        ReadOnlySpan<byte> volumeRgba)
    {
        if (eboot.Length < LightPsOffset + LightPsLength ||
            eboot.Length < RectVsOffset + RectVsLength ||
            eboot.Length < TexFloorBlob + 0x30 ||
            eboot.Length < TexVolumeBlob + 0x30 ||
            floorRgba.Length != TextureSize * TextureSize * 4 ||
            volumeRgba.Length != TextureSize * TextureSize * 4)
        {
            throw new InvalidDataException("NPXS40087 eboot is missing light_p resources");
        }

        var volatileCb = new byte[0x100];
        var colorCb = new byte[0x100];
        var rectConstants = BuildRectConstants();
        var pixelText = eboot.Slice(LightPsOffset, LightPsLength).ToArray();
        var pixelMemory = new LightMemory();
        pixelMemory.AddRegion(ProgramAddress, pixelText);
        pixelMemory.AddRegion(VolatileCbAddress, volatileCb);
        pixelMemory.AddRegion(ColorCbAddress, colorCb);
        var pixelContext = new CpuContext(pixelMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                pixelContext,
                ProgramAddress,
                out var pixelProgram,
                out var error))
        {
            throw new InvalidDataException($"light_p decode failed: {error}");
        }

        var pixelUserData = new uint[36];
        WriteImageDescriptor(pixelUserData.AsSpan(0, 8), eboot, TexFloorBlob, TexFloorAddress);
        WriteImageDescriptor(pixelUserData.AsSpan(8, 8), eboot, TexVolumeBlob, TexVolumeAddress);
        WriteImageDescriptor(pixelUserData.AsSpan(16, 8), eboot, TexVolumeBlob, TexParticleAddress);
        pixelUserData[17] = (pixelUserData[17] & 0xFFFF_FF00u) | 3u;
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(pixelUserData.AsSpan(24, 4)),
            VolatileCbAddress,
            0,
            volatileCb.Length);
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(pixelUserData.AsSpan(28, 4)),
            ColorCbAddress,
            0,
            colorCb.Length);
        var pixelState = new Gen5ShaderState(
            pixelProgram,
            pixelUserData,
            Metadata: null,
            UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                pixelContext,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            throw new InvalidDataException($"light_p resource evaluation failed: {error}");
        }

        var vertexText = eboot.Slice(RectVsOffset, RectVsLength).ToArray();
        var vertexMemory = new LightMemory();
        vertexMemory.AddRegion(ProgramAddress, vertexText);
        vertexMemory.AddRegion(RectConstantsAddress, rectConstants);
        var vertexContext = new CpuContext(vertexMemory, Generation.Gen5);
        if (!Gen5ShaderTranslator.TryDecodeProgram(
                vertexContext,
                ProgramAddress,
                out var vertexProgram,
                out error))
        {
            throw new InvalidDataException($"rect_uv_vv decode failed: {error}");
        }

        var vertexUserData = new uint[12];
        vertexUserData[3] = 0x0000_4040;
        Ps5NativeParticleProgramCompiler.WriteBufferDescriptor(
            MemoryMarshal.AsBytes(vertexUserData.AsSpan(8, 4)),
            RectConstantsAddress,
            0,
            rectConstants.Length);
        var vertexState = new Gen5ShaderState(
            vertexProgram,
            vertexUserData,
            Metadata: null,
            UserDataScalarRegisterBase: 0);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                vertexContext,
                vertexState,
                out var vertexEvaluation,
                out error))
        {
            throw new InvalidDataException($"rect_uv_vv resource evaluation failed: {error}");
        }

        var vertexBufferCount = vertexEvaluation.GlobalMemoryBindings.Count;
        var totalBufferCount = vertexBufferCount + pixelEvaluation.GlobalMemoryBindings.Count;
        if (!Gen5SpirvTranslator.TryCompileVertexShader(
                vertexState,
                vertexEvaluation,
                out var vertexShader,
                out error,
                globalBufferBase: 0,
                totalGlobalBufferCount: totalBufferCount,
                requiredVertexOutputCount: 1,
                nggPrimitiveConnectivity: Npxs40087GpuContract.CreateConnectivity(
                    Npxs40087ShellContract.LightRectangle)))
        {
            throw new InvalidDataException($"rect_uv_vv SPIR-V translation failed: {error}");
        }
        if (!Gen5SpirvTranslator.TryCompilePixelShader(
                pixelState,
                pixelEvaluation,
                Gen5PixelOutputKind.Float,
                out var pixelShader,
                out error,
                globalBufferBase: vertexBufferCount,
                totalGlobalBufferCount: totalBufferCount,
                pixelInputEnable: 0x2,
                pixelInputAddress: 0x2))
        {
            throw new InvalidDataException($"light_p SPIR-V translation failed: {error}");
        }

        var imageTags = pixelShader.ImageBindings
            .Select(static binding => (byte)(binding.ResourceDescriptor[1] & 0xFF))
            .ToArray();
        if (imageTags.Any(static tag => tag is < 1 or > 3))
        {
            throw new InvalidDataException("light_p exposed an unknown sampled-image binding");
        }
        var imageAliases = BuildAliases(imageTags);
        var nggPrimitiveConnectivity = vertexShader.NggPrimitiveConnectivity
            ?? throw new InvalidDataException(
                "rect_uv_vv emitted no host-verified NGG primitive connectivity");
        return new Ps5NativeLightProgram(
            vertexShader.Spirv,
            pixelShader.Spirv,
            vertexShader.GlobalMemoryBindings,
            pixelShader.GlobalMemoryBindings,
            floorRgba.ToArray(),
            volumeRgba.ToArray(),
            imageTags,
            imageAliases,
            nggPrimitiveConnectivity);
    }

    public Ps5NativeParticleResources CreateResources(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var floor = new Ps5NativeParticleTexture(TextureSize, TextureSize, _floorRgba);
        var volume = new Ps5NativeParticleTexture(TextureSize, TextureSize, _volumeRgba);
        var particles = new Ps5NativeParticleTexture(
            width,
            height,
            new byte[checked(width * height * 4)]);
        var textures = _imageTags.Select(tag => tag switch
        {
            1 => floor,
            2 => volume,
            3 => particles,
            _ => throw new InvalidDataException("unknown light_p image tag"),
        }).ToArray();
        return new Ps5NativeParticleResources(
            _vertexSpirv,
            _fragmentSpirv,
            textures[0],
            textures[Math.Min(1, textures.Length - 1)],
            Textures: textures,
            TextureAliases: _imageAliases,
            NggPrimitiveConnectivity: _nggPrimitiveConnectivity);
    }

    public Ps5NativeParticleDraw BuildDraw(
        int width,
        int height,
        float time,
        ReadOnlySpan<byte> colorCb,
        float opacity = 1.0f,
        float intensity = 1.0f,
        float particleAlpha = 1.0f)
    {
        if (width <= 0 || height <= 0 || !float.IsFinite(time) ||
            colorCb.Length != Ps5NativeWaveColourPresetMaterializer.ColorCbByteCount ||
            !float.IsFinite(opacity) || !float.IsFinite(intensity) ||
            !float.IsFinite(particleAlpha))
        {
            throw new ArgumentException("invalid light_p draw inputs");
        }

        var volatileCb = new byte[0x100];
        BitConverter.TryWriteBytes(volatileCb.AsSpan(0x00), time);
        BitConverter.TryWriteBytes(volatileCb.AsSpan(0x04), opacity);
        BitConverter.TryWriteBytes(volatileCb.AsSpan(0x08), intensity);
        BitConverter.TryWriteBytes(volatileCb.AsSpan(0x0C), particleAlpha);
        var colorBuffer = new byte[0x100];
        colorCb.CopyTo(colorBuffer);
        var rectConstants = BuildRectConstants();
        var buffers = new byte[_vertexBindings.Length + _fragmentBindings.Length][];
        var aliases = new int[buffers.Length];
        Array.Fill(aliases, -1);
        var byAddress = new Dictionary<ulong, int>();
        for (var index = 0; index < buffers.Length; index++)
        {
            var binding = index < _vertexBindings.Length
                ? _vertexBindings[index]
                : _fragmentBindings[index - _vertexBindings.Length];
            var source = binding.BaseAddress switch
            {
                RectConstantsAddress => rectConstants,
                VolatileCbAddress => volatileCb,
                ColorCbAddress => colorBuffer,
                _ => throw new InvalidDataException(
                    $"unmapped light_p buffer base 0x{binding.BaseAddress:X}"),
            };
            var data = new byte[binding.DataLength];
            source.AsSpan(0, Math.Min(source.Length, data.Length)).CopyTo(data);
            buffers[index] = data;
            if (byAddress.TryGetValue(binding.BaseAddress, out var first) &&
                buffers[first].Length == data.Length)
            {
                aliases[index] = first;
            }
            else
            {
                byAddress[binding.BaseAddress] = index;
            }
        }

        return new Ps5NativeParticleDraw(
            width,
            height,
            // One guest DrawIndexAuto(3) becomes one explicit host rectangle.
            // Pipeline creation checks this target-20 export against the host
            // four-selector triangle-strip launch.
            ParticleCount: 1,
            buffers.Select(static buffer => (ReadOnlyMemory<byte>)buffer).ToArray(),
            BufferAliases: aliases);
    }

    private static int[] BuildAliases(ReadOnlySpan<byte> tags)
    {
        var aliases = new int[tags.Length];
        Array.Fill(aliases, -1);
        var firstByTag = new Dictionary<byte, int>();
        for (var index = 0; index < tags.Length; index++)
        {
            if (firstByTag.TryGetValue(tags[index], out var first))
            {
                aliases[index] = first;
            }
            else
            {
                firstByTag[tags[index]] = index;
            }
        }
        return aliases;
    }

    private static byte[] BuildRectConstants()
    {
        var constants = new byte[0x30];
        var values = new[] { -1f, -1f, 1f, 1f, 0f, 0f, 1f, 1f };
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(constants.AsSpan(0x10));
        return constants;
    }

    private static void WriteImageDescriptor(
        Span<uint> destination,
        ReadOnlySpan<byte> image,
        int blob,
        ulong address)
    {
        for (var index = 0; index < 8; index++)
        {
            destination[index] = BitConverter.ToUInt32(image.Slice(blob + 0x10 + (index * 4), 4));
        }
        destination[0] = (uint)address;
        destination[1] = (destination[1] & 0xFFFF_FF00u) | (uint)(address >> 32);
    }

    private sealed class LightMemory : ICpuMemory
    {
        private readonly List<(ulong Address, byte[] Data)> _regions = [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress >= address &&
                    virtualAddress + (ulong)destination.Length <= address + (ulong)data.Length)
                {
                    data.AsSpan((int)(virtualAddress - address), destination.Length)
                        .CopyTo(destination);
                    return true;
                }
            }
            return false;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            foreach (var (address, data) in _regions)
            {
                if (virtualAddress >= address &&
                    virtualAddress + (ulong)source.Length <= address + (ulong)data.Length)
                {
                    source.CopyTo(data.AsSpan((int)(virtualAddress - address), source.Length));
                    return true;
                }
            }
            return false;
        }

        public void AddRegion(ulong address, byte[] data) => _regions.Add((address, data));
    }
}

public sealed class Ps5NativeLightRenderer : IDisposable
{
    private readonly Ps5NativeLightProgram _program;
    private readonly Ps5ParticleVulkanSession _session;
    private readonly int _width;
    private readonly int _height;
    private bool _disposed;

    public Ps5NativeLightRenderer(
        Ps5NativeLightProgram program,
        int width,
        int height,
        ReadOnlySpan<byte> initialColorCb)
    {
        _program = program;
        _width = width;
        _height = height;
        var resources = program.CreateResources(width, height);
        var exemplar = program.BuildDraw(width, height, 0.0f, initialColorCb);
        _session = new Ps5ParticleVulkanSession(
            resources,
            exemplar,
            drawCapacity: 1,
            verticesPerDrawUnit: _program.NggPrimitiveConnectivity.HostVerticesPerPrimitive,
            additiveBlend: false,
            clearColor: (0.0f, 0.0f, 0.0f, 1.0f),
            topology: PrimitiveTopology.TriangleStrip);
    }

    public Ps5NativeParticleFrame Render(
        Ps5NativeParticleFrame particles,
        float time,
        ReadOnlySpan<byte> colorCb,
        float opacity = 1.0f,
        float intensity = 1.0f,
        float particleAlpha = 1.0f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!particles.IsValid || particles.Width != _width || particles.Height != _height)
        {
            throw new ArgumentException("light_p particle target extent mismatch", nameof(particles));
        }
        _session.UpdateTexture(
            _program.ParticleTextureIndex,
            new Ps5NativeParticleTexture(_width, _height, particles.Rgba));
        return _session.Render([
            _program.BuildDraw(
                _width,
                _height,
                time,
                colorCb,
                opacity,
                intensity,
                particleAlpha),
        ]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.Dispose();
    }
}
