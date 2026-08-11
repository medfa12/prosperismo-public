// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;

namespace Prosperismo.Libs.Textures;

/// <summary>
/// Dependency-free reader/writer for non-interlaced RGBA8 PNG images. This is
/// the host-portable runtime format used for textures recovered from GNF.
/// </summary>
public static class PngRgbaImage
{
    private static ReadOnlySpan<byte> Signature =>
        [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Load(string path, out int width, out int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Decode(File.ReadAllBytes(path), out width, out height);
    }

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
            return Load(path, out width, out height);
        }
        catch (Exception)
        {
            width = 0;
            height = 0;
            return null;
        }
    }

    public static byte[] Decode(ReadOnlySpan<byte> png, out int width, out int height)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        width = 0;
        height = 0;
        using var compressed = new MemoryStream();
        var offset = Signature.Length;
        var sawHeader = false;
        var sawEnd = false;
        while (offset <= png.Length - 12)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]));
            offset += 4;
            var type = png.Slice(offset, 4);
            offset += 4;
            if (length < 0 || offset > png.Length - length - 4)
            {
                throw new InvalidDataException("PNG chunk exceeds the file.");
            }

            var data = png.Slice(offset, length);
            offset += length;
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            offset += 4;
            var actualCrc = 0xFFFF_FFFFu;
            UpdateCrc(ref actualCrc, type);
            UpdateCrc(ref actualCrc, data);
            if (~actualCrc != expectedCrc)
            {
                throw new InvalidDataException("PNG chunk CRC failed validation.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || data.Length != 13)
                {
                    throw new InvalidDataException("PNG has an invalid IHDR chunk.");
                }

                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                if (width <= 0 || height <= 0 ||
                    data[8] != 8 || data[9] != 6 || data[10] != 0 ||
                    data[11] != 0 || data[12] != 0)
                {
                    throw new NotSupportedException(
                        "Only non-interlaced 8-bit RGBA PNG images are supported.");
                }
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                break;
            }
        }

        if (!sawHeader || !sawEnd || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG is missing required chunks.");
        }

        var stride = checked(width * 4);
        var expected = checked(height * (stride + 1));
        var filtered = new byte[expected];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.ReadExactly(filtered);
            if (zlib.ReadByte() >= 0)
            {
                throw new InvalidDataException("PNG expands beyond its declared dimensions.");
            }
        }

        var rgba = new byte[checked(height * stride)];
        for (var y = 0; y < height; y++)
        {
            var source = filtered.AsSpan(y * (stride + 1) + 1, stride);
            var destination = rgba.AsSpan(y * stride, stride);
            var previous = y == 0 ? ReadOnlySpan<byte>.Empty : rgba.AsSpan((y - 1) * stride, stride);
            Unfilter(filtered[y * (stride + 1)], source, destination, previous);
        }
        return rgba;
    }

    public static void Write(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA byte count does not match the PNG dimensions.");
        }

        using var stream = File.Create(path);
        stream.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(stream, "IHDR"u8, header);

        var stride = checked(width * 4);
        var raw = new byte[checked(height * (stride + 1))];
        for (var y = 0; y < height; y++)
        {
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }
        WriteChunk(stream, "IDAT"u8, compressed.ToArray());
        WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void Unfilter(
        byte filter,
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        ReadOnlySpan<byte> previous)
    {
        const int bytesPerPixel = 4;
        for (var index = 0; index < source.Length; index++)
        {
            var left = index >= bytesPerPixel ? destination[index - bytesPerPixel] : 0;
            var above = previous.IsEmpty ? 0 : previous[index];
            var upperLeft = previous.IsEmpty || index < bytesPerPixel
                ? 0
                : previous[index - bytesPerPixel];
            destination[index] = filter switch
            {
                0 => source[index],
                1 => unchecked((byte)(source[index] + left)),
                2 => unchecked((byte)(source[index] + above)),
                3 => unchecked((byte)(source[index] + ((left + above) / 2))),
                4 => unchecked((byte)(source[index] + Paeth(left, above, upperLeft))),
                _ => throw new InvalidDataException($"Unsupported PNG filter {filter}."),
            };
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var estimate = left + above - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var aboveDistance = Math.Abs(estimate - above);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        stream.Write(word);
        stream.Write(type);
        stream.Write(data);
        var crc = 0xFFFF_FFFFu;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        stream.Write(word);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB8_8320u & (uint)-(int)(crc & 1));
            }
        }
    }
}
