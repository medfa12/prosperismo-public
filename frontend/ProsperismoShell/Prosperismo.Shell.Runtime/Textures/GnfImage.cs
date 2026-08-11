// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace Prosperismo.Libs.Textures;

/// <summary>
/// Minimal reader for the PS5 GPU texture container (.gnf) used by the system
/// shell's vsh_asset tree, sufficient for the BGLayer particle sprites.
///
/// The container is a small header followed by the raw surface bytes:
/// <code>
///   +0x00 uint32 magic          'GNF ' (0x20464E47 little-endian)
///   +0x04 uint32 contentsSize   bytes of header content after the magic pair
///   +0x08 uint8  version
///   +0x09 uint8  textureCount
///   +0x0A uint8  alignmentLog2  surface-data alignment (12 => 4 KiB)
///   +0x0B uint8  unused
///   +0x0C uint32 streamSize     total container size
///   +0x10        textureCount x 32-byte image descriptors
/// </code>
/// Each descriptor is a Gen5 (RDNA2) T# image resource, decoded with the same
/// field layout the AGC texture path uses: WIDTH is split across word1[31:30]
/// and word2[13:0], HEIGHT is word2[29:14], the unified format enum is
/// word1[28:20], and word3 carries BASE_LEVEL/LAST_LEVEL, the SW_MODE swizzle
/// index at [24:20] and the resource TYPE at [31:28].
///
/// The surface bytes are GPU-tiled, so <see cref="Decode"/> untiles before the
/// block decode. Only what the shell assets actually use is implemented:
/// single-mip 2D BC7 unorm/sRGB surfaces that are either linear or SW_4KB_S
/// swizzled. Anything else reports <see cref="IsSupported"/> false rather than
/// returning a plausible-looking wrong image.
/// </summary>
public sealed class GnfImage
{
    /// <summary>Container magic, 'GNF ' read little-endian.</summary>
    public const uint Magic = 0x20464E47;

    /// <summary>Gen5 unified format enum for BC7 unorm.</summary>
    public const uint FormatBc7Unorm = 181;

    /// <summary>Gen5 unified format enum for BC7 sRGB.</summary>
    public const uint FormatBc7Srgb = 182;

    /// <summary>SW_MODE 0: untiled, row-major elements.</summary>
    public const uint TileModeLinear = 0;

    /// <summary>SW_MODE 5: SW_4KB_S, the standard 4 KiB swizzle.</summary>
    public const uint TileMode4KbStandard = 5;

    /// <summary>SQ_RSRC_IMG_2D, the only resource type handled here.</summary>
    private const uint ImageType2d = 9;

    private const int HeaderPrefixSize = 8;   // magic + contentsSize
    private const int DescriptorOffset = 16;  // first T# follows the 8-byte contents header
    private const int DescriptorSize = 32;    // 8 dwords per image descriptor
    private const int BlockDim = 4;           // BC block edge in texels
    private const int BlockBytes = 16;        // BC7 block size

    // SW_4KB_S block footprint in 16-byte elements, and the per-bit coordinate
    // masks that build the in-block byte offset:
    //   bit(i) = parity(x & xMask[i]) ^ parity(y & yMask[i])
    // Transcribed from the AddrLib GFX10 swizzle patterns for 128-bit elements;
    // this is the same table the AGC detiler uses for SW_MODE 5.
    private const int Block4KbSizeLog2 = 12;
    private const uint Block4KbElementsX = 16;
    private const uint Block4KbElementsY = 16;

    private static readonly ushort[] Sw4KbStandardXMasks =
        [0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0001, 0x0002, 0x0000, 0x0004, 0x0000, 0x0008];

    private static readonly ushort[] Sw4KbStandardYMasks =
        [0x0000, 0x0000, 0x0000, 0x0000, 0x0001, 0x0002, 0x0000, 0x0000, 0x0004, 0x0000, 0x0008, 0x0000];

    private readonly byte[] _surface;

    private GnfImage(
        int width,
        int height,
        int pitch,
        uint format,
        uint tileMode,
        uint imageType,
        uint lastLevel,
        byte[] surface)
    {
        Width = width;
        Height = height;
        Pitch = pitch;
        Format = format;
        TileMode = tileMode;
        ImageType = imageType;
        LastLevel = lastLevel;
        _surface = surface;
    }

    /// <summary>Width of the top mip in texels.</summary>
    public int Width { get; }

    /// <summary>Height of the top mip in texels.</summary>
    public int Height { get; }

    /// <summary>Row pitch in texels; equals <see cref="Width"/> when the descriptor leaves it implicit.</summary>
    public int Pitch { get; }

    /// <summary>Gen5 unified format enum from the descriptor (181/182 are BC7).</summary>
    public uint Format { get; }

    /// <summary>SQ_IMG_RSRC.SW_MODE swizzle index.</summary>
    public uint TileMode { get; }

    /// <summary>SQ_IMG_RSRC.TYPE; 9 is a plain 2D image.</summary>
    public uint ImageType { get; }

    /// <summary>Highest mip level present; 0 means the surface carries only the top mip.</summary>
    public uint LastLevel { get; }

    /// <summary>Bytes of surface data carried after the container header.</summary>
    public int SurfaceByteCount => _surface.Length;

    /// <summary>True when the format is one of the BC7 variants.</summary>
    public bool IsBc7 => Format is FormatBc7Unorm or FormatBc7Srgb;

    /// <summary>
    /// True when <see cref="Decode"/> can produce correct RGBA8: a single-mip 2D
    /// BC7 surface in a swizzle this reader untiles, with enough bytes present.
    /// </summary>
    public bool IsSupported =>
        IsBc7 &&
        ImageType == ImageType2d &&
        LastLevel == 0 &&
        TileMode is TileModeLinear or TileMode4KbStandard &&
        _surface.Length >= RequiredSurfaceBytes();

    /// <summary>
    /// Parses a GNF container from memory. Only the header and the first image
    /// descriptor are interpreted; the surface bytes are kept for a later
    /// <see cref="Decode"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The buffer is not a usable GNF container.</exception>
    public static GnfImage Load(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DescriptorOffset + DescriptorSize)
        {
            throw new ArgumentException("Buffer is smaller than a GNF header.", nameof(bytes));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            throw new ArgumentException("Not a GNF file (bad magic).", nameof(bytes));
        }

        var contentsSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        var textureCount = bytes[9];
        var alignmentLog2 = bytes[10];

        if (textureCount == 0)
        {
            throw new ArgumentException("GNF container declares no textures.", nameof(bytes));
        }

        // Surface data starts after the header contents, rounded up to the
        // declared alignment (dumps use 12, i.e. 4 KiB).
        var dataOffset = (long)HeaderPrefixSize + contentsSize;
        if (alignmentLog2 is > 0 and < 24)
        {
            var alignment = 1L << alignmentLog2;
            dataOffset = (dataOffset + alignment - 1) & ~(alignment - 1);
        }

        if (dataOffset <= 0 || dataOffset > bytes.Length)
        {
            throw new ArgumentException(
                $"GNF surface data starts at {dataOffset}, past the end of a {bytes.Length}-byte buffer.",
                nameof(bytes));
        }

        var descriptor = bytes.Slice(DescriptorOffset, DescriptorSize);
        Span<uint> words = stackalloc uint[8];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(i * 4, 4));
        }

        var width = (int)((((words[1] >> 30) & 0x3u) | ((words[2] & 0x3FFFu) << 2)) + 1);
        var height = (int)(((words[2] >> 14) & 0xFFFFu) + 1);
        var format = (words[1] >> 20) & 0x1FFu;
        var lastLevel = (words[3] >> 16) & 0xFu;
        var tileMode = (words[3] >> 20) & 0x1Fu;
        var imageType = (words[3] >> 28) & 0xFu;

        // word4[13:0] is (pitch - 1) only for the 256-bit descriptor form; a
        // zeroed upper half means the pitch is implicitly the width.
        var pitch = words[4] != 0 ? (int)((words[4] & 0x3FFFu) + 1) : width;
        if (pitch < width)
        {
            pitch = width;
        }

        return new GnfImage(
            width,
            height,
            pitch,
            format,
            tileMode,
            imageType,
            lastLevel,
            bytes[(int)dataOffset..].ToArray());
    }

    /// <summary>Reads and parses a GNF file from disk.</summary>
    public static GnfImage LoadFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        return Load(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Loads a GNF sprite and returns row-major RGBA8, or null when the file is
    /// missing, is not a GNF, or carries a surface this reader cannot decode
    /// correctly. Never throws: callers fall back to a procedural sprite.
    /// </summary>
    public static byte[]? TryLoadRgba(string? path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var image = LoadFile(path);
            if (!image.IsSupported)
            {
                return null;
            }

            var rgba = image.Decode();
            width = image.Width;
            height = image.Height;
            return rgba;
        }
        catch (Exception)
        {
            // Truncated, hostile or simply unexpected files degrade to null.
            return null;
        }
    }

    /// <summary>
    /// Untiles the surface and decodes the top mip to row-major RGBA8
    /// (4 bytes/pixel, straight alpha).
    /// </summary>
    /// <exception cref="NotSupportedException">The surface is not a decodable BC7 image.</exception>
    public byte[] Decode()
    {
        if (!IsSupported)
        {
            throw new NotSupportedException(
                $"GNF surface {Width}x{Height} format {Format} tileMode {TileMode} type {ImageType} " +
                $"lastLevel {LastLevel} is not decodable; only single-mip 2D BC7 " +
                $"({FormatBc7Unorm}/{FormatBc7Srgb}) in tile mode {TileModeLinear} or {TileMode4KbStandard} is.");
        }

        var blocksX = (Width + BlockDim - 1) / BlockDim;
        var blocksY = (Height + BlockDim - 1) / BlockDim;
        var pitchBlocks = (Pitch + BlockDim - 1) / BlockDim;

        var blocks = TileMode == TileModeLinear
            ? PackLinearBlocks(_surface, blocksX, blocksY, pitchBlocks)
            : Detile4KbStandard(_surface, blocksX, blocksY, pitchBlocks);

        return Bc7Decoder.DecodeImage(blocks, Width, Height);
    }

    // Tiled footprint of the surface: the block grid padded out to whole
    // swizzle blocks, which is what the container actually stores.
    private long RequiredSurfaceBytes()
    {
        if (Width <= 0 || Height <= 0)
        {
            return long.MaxValue;
        }

        var blocksY = (long)((Height + BlockDim - 1) / BlockDim);
        var pitchBlocks = (long)((Pitch + BlockDim - 1) / BlockDim);
        if (TileMode == TileModeLinear)
        {
            return pitchBlocks * blocksY * BlockBytes;
        }

        var blocksPerRow = (pitchBlocks + Block4KbElementsX - 1) / Block4KbElementsX;
        var blockRows = (blocksY + Block4KbElementsY - 1) / Block4KbElementsY;
        return blocksPerRow * blockRows << Block4KbSizeLog2;
    }

    // Row-major blocks with a wider pitch: copy the used prefix of every row.
    private static byte[] PackLinearBlocks(byte[] surface, int blocksX, int blocksY, int pitchBlocks)
    {
        var packed = new byte[(long)blocksX * blocksY * BlockBytes];
        var rowBytes = blocksX * BlockBytes;
        var sourceStride = pitchBlocks * BlockBytes;
        for (var y = 0; y < blocksY; y++)
        {
            var source = (long)y * sourceStride;
            if (source + rowBytes > surface.Length)
            {
                break;
            }

            Array.Copy(surface, source, packed, (long)y * rowBytes, rowBytes);
        }

        return packed;
    }

    // SW_4KB_S: 4 KiB blocks of 16x16 elements laid out row-major over the
    // padded pitch; inside a block the byte offset is an XOR of coordinate-bit
    // parities. Elements past the end of the surface are left transparent.
    private static byte[] Detile4KbStandard(byte[] surface, int blocksX, int blocksY, int pitchBlocks)
    {
        var packed = new byte[(long)blocksX * blocksY * BlockBytes];
        var tilesPerRow = (uint)((pitchBlocks + Block4KbElementsX - 1) / Block4KbElementsX);

        var columnOffsets = new uint[blocksX];
        for (var x = 0; x < blocksX; x++)
        {
            columnOffsets[x] = Parities((uint)x, Sw4KbStandardXMasks);
        }

        for (var y = 0; y < blocksY; y++)
        {
            var rowOffset = Parities((uint)y, Sw4KbStandardYMasks);
            var tileRowBase = (ulong)((uint)y / Block4KbElementsY) * tilesPerRow;
            var destination = (long)y * blocksX * BlockBytes;
            for (var x = 0; x < blocksX; x++, destination += BlockBytes)
            {
                var tiled = (long)(
                    ((tileRowBase + ((uint)x / Block4KbElementsX)) << Block4KbSizeLog2) |
                    (columnOffsets[x] ^ rowOffset));
                if (tiled >= 0 && tiled + BlockBytes <= surface.Length)
                {
                    Array.Copy(surface, tiled, packed, destination, BlockBytes);
                }
            }
        }

        return packed;
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
}
