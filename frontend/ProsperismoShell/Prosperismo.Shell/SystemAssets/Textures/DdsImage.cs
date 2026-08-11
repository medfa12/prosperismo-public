// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.IO;
using Prosperismo.Libs.Textures;

namespace Prosperismo.GUI.SystemAssets.Textures;

/// <summary>
/// Minimal reader for DirectDraw Surface (.dds) files that carry a DX10
/// extended header, sufficient for the BC7 hub backgrounds shipped in the PS5
/// system shell. It parses the 128-byte base header plus the 20-byte DX10
/// extension and, for the BC7 formats, decodes the top mip to RGBA8 via
/// <see cref="Bc7Decoder"/>. Unsupported formats raise
/// <see cref="NotSupportedException"/> rather than returning garbage.
/// </summary>
public sealed class DdsImage
{
    /// <summary>DXGI_FORMAT_BC7_UNORM.</summary>
    public const int DxgiFormatBc7Unorm = 98;

    /// <summary>DXGI_FORMAT_BC7_UNORM_SRGB.</summary>
    public const int DxgiFormatBc7UnormSrgb = 99;

    private const uint DdsMagic = 0x20534444;  // "DDS " little-endian.
    private const uint Dx10FourCc = 0x30315844; // "DX10" little-endian.
    private const int BaseHeaderSize = 128;
    private const int Dx10HeaderSize = 20;

    private readonly byte[] _pixelData;

    private DdsImage(int width, int height, int dxgiFormat, byte[] pixelData)
    {
        Width = width;
        Height = height;
        DxgiFormat = dxgiFormat;
        _pixelData = pixelData;
    }

    /// <summary>Image width in pixels (top mip).</summary>
    public int Width { get; }

    /// <summary>Image height in pixels (top mip).</summary>
    public int Height { get; }

    /// <summary>The DXGI format enum from the DX10 header extension.</summary>
    public int DxgiFormat { get; }

    /// <summary>True when <see cref="Decode"/> can produce RGBA8 for this file.</summary>
    public bool IsSupported => DxgiFormat is DxgiFormatBc7Unorm or DxgiFormatBc7UnormSrgb;

    /// <summary>
    /// Parses a DDS image from an in-memory buffer. Only the header is read
    /// here; the pixel data is retained for a later <see cref="Decode"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The buffer is not a DX10 DDS file.</exception>
    public static DdsImage Load(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < BaseHeaderSize)
        {
            throw new ArgumentException("Buffer is smaller than a DDS header.", nameof(bytes));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != DdsMagic)
        {
            throw new ArgumentException("Not a DDS file (bad magic).", nameof(bytes));
        }

        // Base DDS_HEADER: height at offset 12, width at offset 16, and the
        // pixel-format fourCC at offset 84.
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(12, 4));
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(16, 4));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(84, 4));

        if (fourCc != Dx10FourCc)
        {
            throw new NotSupportedException(
                "Only DX10-extended DDS files are supported; this file lacks a 'DX10' fourCC.");
        }

        if (bytes.Length < BaseHeaderSize + Dx10HeaderSize)
        {
            throw new ArgumentException("Buffer is truncated before the DX10 header.", nameof(bytes));
        }

        // DDS_HEADER_DXT10 begins at offset 128; its first dword is the DXGI
        // format enum. Pixel data follows at offset 148.
        int dxgiFormat = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(BaseHeaderSize, 4));

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"Invalid DDS dimensions {width}x{height}.", nameof(bytes));
        }

        int dataOffset = BaseHeaderSize + Dx10HeaderSize;
        var pixelData = bytes.Slice(dataOffset).ToArray();
        return new DdsImage(width, height, dxgiFormat, pixelData);
    }

    /// <summary>Reads and parses a DDS file from disk.</summary>
    public static DdsImage LoadFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        return Load(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Decodes the top mip to row-major RGBA8 (4 bytes/pixel). Supports the two
    /// BC7 DXGI formats; SRGB is decoded as raw unorm bytes (no gamma applied),
    /// which is what the shell UI expects when it uploads the data to an SRGB
    /// surface.
    /// </summary>
    /// <exception cref="NotSupportedException">The format is not a BC7 unorm variant.</exception>
    public byte[] Decode()
    {
        if (!IsSupported)
        {
            throw new NotSupportedException(
                $"DXGI format {DxgiFormat} is not supported; only BC7_UNORM ({DxgiFormatBc7Unorm}) and BC7_UNORM_SRGB ({DxgiFormatBc7UnormSrgb}) decode.");
        }

        return Bc7Decoder.DecodeImage(_pixelData, Width, Height);
    }
}
