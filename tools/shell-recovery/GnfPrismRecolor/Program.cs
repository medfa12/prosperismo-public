// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Prosperismo.Libs.Textures;

if (args.Length == 3 && args[0] == "--decode-png")
{
    var decodeSourcePath = Path.GetFullPath(args[1]);
    var decodeOutputPath = Path.GetFullPath(args[2]);
    var image = GnfImage.LoadFile(decodeSourcePath);
    if (!image.IsSupported)
    {
        throw new NotSupportedException("The GNF surface cannot be decoded losslessly to RGBA8.");
    }

    var rgba = image.Decode();
    PngRgbaImage.Write(decodeOutputPath, rgba, image.Width, image.Height);
    var png = File.ReadAllBytes(decodeOutputPath);
    Console.WriteLine($"dimensions={image.Width}x{image.Height}");
    Console.WriteLine($"rgba_sha256={Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant()}");
    Console.WriteLine($"png_sha256={Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()}");
    return 0;
}

if (args.Length == 3 && args[0] == "--decode-light-png")
{
    var decodeSourcePath = Path.GetFullPath(args[1]);
    var decodeOutputPath = Path.GetFullPath(args[2]);
    var sourceBytes = File.ReadAllBytes(decodeSourcePath);
    var rgba = DecodeLightR8(sourceBytes);
    PngRgbaImage.Write(decodeOutputPath, rgba, 128, 128);
    var png = File.ReadAllBytes(decodeOutputPath);
    Console.WriteLine("dimensions=128x128");
    Console.WriteLine($"rgba_sha256={Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant()}");
    Console.WriteLine($"png_sha256={Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()}");
    return 0;
}

if (args.Length == 3 && args[0] == "--descriptor")
{
    var sourceBytes = File.ReadAllBytes(Path.GetFullPath(args[1]));
    if (sourceBytes.Length < 0x30)
    {
        throw new InvalidDataException("GNF is missing its 32-byte image descriptor.");
    }
    File.WriteAllBytes(Path.GetFullPath(args[2]), sourceBytes.AsSpan(0x10, 32).ToArray());
    return 0;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: GnfPrismRecolor <source.gnf> <output.gnf>");
    Console.Error.WriteLine("       GnfPrismRecolor --decode-png <source.gnf> <output.png>");
    Console.Error.WriteLine("       GnfPrismRecolor --decode-light-png <source.gnf> <output.png>");
    Console.Error.WriteLine("       GnfPrismRecolor --descriptor <source.gnf> <output.bin>");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var source = File.ReadAllBytes(sourcePath);
var transformed = GnfPrismRecolor.Recolour(source);
File.WriteAllBytes(outputPath, transformed.Bytes);
Console.WriteLine($"source_sha256={Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant()}");
Console.WriteLine($"sha256={Convert.ToHexString(SHA256.HashData(transformed.Bytes)).ToLowerInvariant()}");
Console.WriteLine($"header_preserved={transformed.HeaderPreserved}");
Console.WriteLine($"mean_abs_error={transformed.MeanAbsoluteError:F2}");
Console.WriteLine($"max_abs_error={transformed.MaxAbsoluteError}");
return 0;

static byte[] DecodeLightR8(ReadOnlySpan<byte> image)
{
    const int textureSize = 128;
    const int payloadOffset = 0x100;
    const int payloadLength = 0x4000;
    if (image.Length != payloadOffset + payloadLength)
    {
        throw new InvalidDataException("Expected a 128x128 SW_4KB_S R8 GNF texture.");
    }

    var payload = image.Slice(payloadOffset, payloadLength);
    var rgba = new byte[textureSize * textureSize * 4];
    const int blockWidth = 64;
    const int blockHeight = 64;
    const int blockBytes = 4096;
    var blocksPerRow = textureSize / blockWidth;
    for (var y = 0; y < textureSize; y++)
    {
        for (var x = 0; x < textureSize; x++)
        {
            var localX = x & (blockWidth - 1);
            var localY = y & (blockHeight - 1);
            var tiledOffset = 0;
            tiledOffset ^= (localY << 4) & 0x1F0;
            tiledOffset ^= (localY << 5) & 0x400;
            tiledOffset ^= localX & 0x00F;
            tiledOffset ^= (localX << 5) & 0x200;
            tiledOffset ^= (localX << 6) & 0x800;
            var blockIndex = ((y / blockHeight) * blocksPerRow) + (x / blockWidth);
            var value = payload[(blockIndex * blockBytes) + tiledOffset];
            var destination = ((y * textureSize) + x) * 4;
            rgba[destination] = value;
            rgba[destination + 3] = byte.MaxValue;
        }
    }
    return rgba;
}

internal static class GnfPrismRecolor
{
    private const int BlockDim = 4;
    private const int BlockBytes = 16;
    private const int TileBytesLog2 = 12;
    private const uint TileElementsX = 16;
    private const uint TileElementsY = 16;

    private static readonly ushort[] XMasks =
        [0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0001, 0x0002, 0x0000, 0x0004, 0x0000, 0x0008];
    private static readonly ushort[] YMasks =
        [0x0000, 0x0000, 0x0000, 0x0000, 0x0001, 0x0002, 0x0000, 0x0000, 0x0004, 0x0000, 0x0008, 0x0000];
    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    internal static RecolourResult Recolour(byte[] source)
    {
        var image = GnfImage.Load(source);
        if (!image.IsSupported || image.TileMode != GnfImage.TileMode4KbStandard)
        {
            throw new NotSupportedException("Expected a single-mip, 4 KiB tiled BC7 GNF particle texture.");
        }

        var rgba = image.Decode();
        var prism = ApplyPrismPalette(rgba, image.Width, image.Height);
        var output = source.ToArray();
        var dataOffset = SurfaceDataOffset(source);
        var blocksX = (image.Width + BlockDim - 1) / BlockDim;
        var blocksY = (image.Height + BlockDim - 1) / BlockDim;
        var pitchBlocks = (image.Pitch + BlockDim - 1) / BlockDim;
        var tilesPerRow = (uint)((pitchBlocks + TileElementsX - 1) / TileElementsX);
        Span<byte> blockPixels = stackalloc byte[BlockDim * BlockDim * 4];
        Span<byte> block = stackalloc byte[BlockBytes];

        for (var blockY = 0; blockY < blocksY; blockY++)
        {
            var rowOffset = Parities((uint)blockY, YMasks);
            var tileRowBase = ((uint)blockY / TileElementsY) * tilesPerRow;
            for (var blockX = 0; blockX < blocksX; blockX++)
            {
                LoadBlock(prism, image.Width, image.Height, blockX, blockY, blockPixels);
                EncodeMode6(blockPixels, block);
                var tiled = (long)((tileRowBase + ((uint)blockX / TileElementsX)) << TileBytesLog2) |
                            (Parities((uint)blockX, XMasks) ^ rowOffset);
                if (dataOffset + tiled + BlockBytes > output.Length)
                {
                    throw new InvalidDataException("Tiled BC7 block exceeds the GNF surface.");
                }

                block.CopyTo(output.AsSpan(dataOffset + (int)tiled, BlockBytes));
            }
        }

        var roundTrip = GnfImage.Load(output).Decode();
        long absoluteTotal = 0;
        var maximum = 0;
        for (var index = 0; index < prism.Length; index++)
        {
            var difference = Math.Abs(prism[index] - roundTrip[index]);
            absoluteTotal += difference;
            maximum = Math.Max(maximum, difference);
        }

        return new RecolourResult(
            output,
            source.AsSpan(0, dataOffset).SequenceEqual(output.AsSpan(0, dataOffset)),
            (double)absoluteTotal / prism.Length,
            maximum);
    }

    private static byte[] ApplyPrismPalette(byte[] rgba, int width, int height)
    {
        var output = rgba.ToArray();
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            var alpha = output[offset + 3];
            if (alpha == 0)
            {
                continue;
            }

            var luminance = (0.2126 * output[offset]) +
                            (0.7152 * output[offset + 1]) +
                            (0.0722 * output[offset + 2]);
            var (red, green, blue) = PrismRamp(luminance / 255.0);
            var intensity = Math.Clamp(luminance / 255.0, 0.0, 1.0);
            var white = SmoothStep(0.62, 1.0, intensity) * 0.72;
            output[offset] = ToByte(((red * (1.0 - white)) + white) * Math.Max(intensity, 0.18));
            output[offset + 1] = ToByte(((green * (1.0 - white)) + white) * Math.Max(intensity, 0.18));
            output[offset + 2] = ToByte(((blue * (1.0 - white)) + white) * Math.Max(intensity, 0.18));
        }

        return output;
    }

    private static (double R, double G, double B) PrismRamp(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        var low = (R: 0.00, G: 0.729, B: 1.00);
        var middle = (R: 0.435, G: 0.486, B: 1.00);
        var high = (R: 1.00, G: 0.176, B: 0.608);
        return value <= 0.5
            ? Lerp(low, middle, value * 2.0)
            : Lerp(middle, high, (value - 0.5) * 2.0);
    }

    private static (double R, double G, double B) Lerp(
        (double R, double G, double B) left,
        (double R, double G, double B) right,
        double amount) =>
        (left.R + ((right.R - left.R) * amount),
         left.G + ((right.G - left.G) * amount),
         left.B + ((right.B - left.B) * amount));

    private static void LoadBlock(
        byte[] rgba,
        int width,
        int height,
        int blockX,
        int blockY,
        Span<byte> output)
    {
        output.Clear();
        for (var y = 0; y < BlockDim; y++)
        {
            for (var x = 0; x < BlockDim; x++)
            {
                var sourceX = (blockX * BlockDim) + x;
                var sourceY = (blockY * BlockDim) + y;
                if (sourceX >= width || sourceY >= height)
                {
                    continue;
                }

                var sourceOffset = ((sourceY * width) + sourceX) * 4;
                var targetOffset = ((y * BlockDim) + x) * 4;
                rgba.AsSpan(sourceOffset, 4).CopyTo(output.Slice(targetOffset, 4));
            }
        }
    }

    // Mode 6 is one subset with 7-bit RGBA endpoints, per-endpoint p-bits and
    // sixteen colour/alpha indices. It is intentionally simple but valid BC7;
    // diffuse particle sprites tolerate its single-axis fit while preserving
    // the GNF descriptor and tiled footprint exactly.
    private static void EncodeMode6(ReadOnlySpan<byte> rgba, Span<byte> output)
    {
        var minimumPixel = 0;
        var maximumPixel = 0;
        var minimumProjection = double.PositiveInfinity;
        var maximumProjection = double.NegativeInfinity;
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var offset = pixel * 4;
            var projection = (0.2126 * rgba[offset]) +
                             (0.7152 * rgba[offset + 1]) +
                             (0.0722 * rgba[offset + 2]) +
                             (0.35 * rgba[offset + 3]);
            if (projection < minimumProjection)
            {
                minimumProjection = projection;
                minimumPixel = pixel;
            }
            if (projection > maximumProjection)
            {
                maximumProjection = projection;
                maximumPixel = pixel;
            }
        }

        var low = QuantizeEndpoint(rgba.Slice(minimumPixel * 4, 4));
        var high = QuantizeEndpoint(rgba.Slice(maximumPixel * 4, 4));
        Span<int> indices = stackalloc int[16];
        AssignIndices(rgba, low, high, indices);
        if (indices[0] > 7)
        {
            (low, high) = (high, low);
            AssignIndices(rgba, low, high, indices);
        }

        output.Clear();
        var writer = new BitWriter(output);
        writer.Write(1 << 6, 7); // BC7 mode 6: 0000001, least-significant bit first.
        writer.Write(low.R7, 7);
        writer.Write(high.R7, 7);
        writer.Write(low.G7, 7);
        writer.Write(high.G7, 7);
        writer.Write(low.B7, 7);
        writer.Write(high.B7, 7);
        writer.Write(low.A7, 7);
        writer.Write(high.A7, 7);
        writer.Write(low.PBit, 1);
        writer.Write(high.PBit, 1);
        writer.Write(indices[0], 3); // mode-6 subset anchor omits its high bit.
        for (var pixel = 1; pixel < 16; pixel++)
        {
            writer.Write(indices[pixel], 4);
        }
    }

    private static Endpoint QuantizeEndpoint(ReadOnlySpan<byte> rgba)
    {
        var bestError = int.MaxValue;
        var best = default(Endpoint);
        for (var pbit = 0; pbit <= 1; pbit++)
        {
            var r7 = Quantize(rgba[0], pbit);
            var g7 = Quantize(rgba[1], pbit);
            var b7 = Quantize(rgba[2], pbit);
            var a7 = Quantize(rgba[3], pbit);
            var candidate = new Endpoint(r7, g7, b7, a7, pbit);
            var error = Square(candidate.R - rgba[0]) + Square(candidate.G - rgba[1]) +
                        Square(candidate.B - rgba[2]) + Square(candidate.A - rgba[3]);
            if (error < bestError)
            {
                bestError = error;
                best = candidate;
            }
        }

        return best;
    }

    private static int Quantize(byte value, int pbit) => Math.Clamp((int)Math.Round((value - pbit) / 2.0), 0, 127);

    private static void AssignIndices(ReadOnlySpan<byte> rgba, Endpoint low, Endpoint high, Span<int> output)
    {
        for (var pixel = 0; pixel < 16; pixel++)
        {
            var offset = pixel * 4;
            var bestIndex = 0;
            var bestError = long.MaxValue;
            for (var index = 0; index < Weights4.Length; index++)
            {
                var weight = Weights4[index];
                var error = Square(Interpolate(low.R, high.R, weight) - rgba[offset]) +
                            Square(Interpolate(low.G, high.G, weight) - rgba[offset + 1]) +
                            Square(Interpolate(low.B, high.B, weight) - rgba[offset + 2]) +
                            Square(Interpolate(low.A, high.A, weight) - rgba[offset + 3]);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = index;
                }
            }
            output[pixel] = bestIndex;
        }
    }

    private static int Interpolate(int low, int high, int weight) =>
        ((low * (64 - weight)) + (high * weight) + 32) >> 6;

    private static int Square(int value) => value * value;

    private static int SurfaceDataOffset(ReadOnlySpan<byte> source)
    {
        var contentsSize = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4));
        var alignmentLog2 = source[10];
        var offset = (long)8 + contentsSize;
        var alignment = 1L << alignmentLog2;
        offset = (offset + alignment - 1) & ~(alignment - 1);
        return checked((int)offset);
    }

    private static uint Parities(uint coordinate, ushort[] masks)
    {
        var value = 0u;
        for (var bit = 0; bit < masks.Length; bit++)
        {
            value |= (uint)(BitOperations.PopCount(coordinate & masks[bit]) & 1) << bit;
        }
        return value;
    }

    private static double SmoothStep(double low, double high, double value)
    {
        var t = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
        return t * t * (3.0 - (2.0 * t));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0.0, 255.0);

    private readonly record struct Endpoint(int R7, int G7, int B7, int A7, int PBit)
    {
        internal int R => (R7 << 1) | PBit;
        internal int G => (G7 << 1) | PBit;
        internal int B => (B7 << 1) | PBit;
        internal int A => (A7 << 1) | PBit;
    }

    internal readonly record struct RecolourResult(byte[] Bytes, bool HeaderPreserved, double MeanAbsoluteError, int MaxAbsoluteError);

    private ref struct BitWriter
    {
        private Span<byte> _data;
        private int _bit;

        internal BitWriter(Span<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        internal void Write(int value, int count)
        {
            for (var bit = 0; bit < count; bit++, _bit++)
            {
                _data[_bit >> 3] |= (byte)(((value >> bit) & 1) << (_bit & 7));
            }
        }
    }
}
