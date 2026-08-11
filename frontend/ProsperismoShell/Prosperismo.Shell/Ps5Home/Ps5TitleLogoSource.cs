// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// A locally recoverable title-logo payload. It is intentionally separate from
/// through distinct fields, and a missing one must not suppress the others.
/// </summary>
public sealed class Ps5TitleLogoSource
{
    private readonly Ps5EmbeddedTitleLogoProfile? _embeddedProfile;
    private readonly Ps5PackagedIconWordmarkProfile? _iconProfile;

    internal Ps5TitleLogoSource(string executablePath, Ps5EmbeddedTitleLogoProfile profile)
    {
        ExecutablePath = executablePath;
        _embeddedProfile = profile;
    }

    internal Ps5TitleLogoSource(string executablePath, Ps5PackagedIconWordmarkProfile profile)
    {
        ExecutablePath = executablePath;
        _iconProfile = profile;
    }

    /// <summary>Local package executable that owns the source bytes.</summary>
    public string ExecutablePath { get; }

    /// <summary>Title identity to which this exact recovered profile belongs.</summary>
    public string TitleId => _embeddedProfile?.TitleId ?? _iconProfile!.TitleId;

    /// <summary>Original byte offset in the executable's outer SELF.</summary>
    public long Offset => _embeddedProfile?.Offset ?? 0;

    /// <summary>Exact payload length in bytes.</summary>
    public int Length => _embeddedProfile?.Length ?? _iconProfile!.AssetLength;

    /// <summary>Decoded image width proved by the recovered PNG IHDR.</summary>
    public int PixelWidth => _embeddedProfile?.PixelWidth ?? _iconProfile!.OutputWidth;

    /// <summary>Decoded image height proved by the recovered PNG IHDR.</summary>
    public int PixelHeight => _embeddedProfile?.PixelHeight ?? _iconProfile!.OutputHeight;

    /// <summary>
    /// Reads the payload only when both the outer SELF and the sliced PNG match
    /// their recorded SHA-256 values. This never persists or packages game
    /// bytes; it reads the user's supplied installable package at runtime.
    /// </summary>
    public byte[]? TryRead()
    {
        if (_embeddedProfile is null)
        {
            return null;
        }

        try
        {
            using var executable = new FileStream(
                ExecutablePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return _embeddedProfile.TryRead(executable, out var payload) ? payload : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes either the hash-pinned PNG inside a SELF or the exact wordmark
    /// regions from a hash-pinned package icon. Neither route writes source
    /// artwork outside the user's package.
    /// </summary>
    public Bitmap? TryLoadBitmap(int pngDecodeWidth = 960)
    {
        if (_embeddedProfile is not null)
        {
            var payload = TryRead();
            if (payload is null)
            {
                return null;
            }

            using var stream = new MemoryStream(payload, writable: false);
            return Bitmap.DecodeToWidth(stream, pngDecodeWidth);
        }

        if (_iconProfile is null ||
            !_iconProfile.TryResolve(ExecutablePath, out var assetPath))
        {
            return null;
        }

        using var source = new Bitmap(assetPath);
        if (source.PixelSize.Width != _iconProfile.AssetWidth ||
            source.PixelSize.Height != _iconProfile.AssetHeight)
        {
            return null;
        }

        using var sourcePixels = new WriteableBitmap(
            source.PixelSize,
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        using (var target = sourcePixels.Lock())
        {
            source.CopyPixels(target, AlphaFormat.Unpremul);
        }

        var result = new WriteableBitmap(
            new PixelSize(_iconProfile.OutputWidth, _iconProfile.OutputHeight),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        using var sourceBuffer = sourcePixels.Lock();
        using var resultBuffer = result.Lock();
        var sourceRow = new byte[source.PixelSize.Width * 4];
        var destinationRow = new byte[_iconProfile.OutputWidth * 4];
        foreach (var region in _iconProfile.Regions)
        {
            for (var y = 0; y < region.Height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(sourceBuffer.Address, (region.SourceY + y) * sourceBuffer.RowBytes),
                    sourceRow,
                    0,
                    sourceRow.Length);
                Array.Clear(destinationRow);
                for (var x = 0; x < region.Width; x++)
                {
                    var sourceOffset = (region.SourceX + x) * 4;
                    var destinationOffset = (region.DestinationX + x) * 4;

                    // The recovered mark is white over the icon's dark-blue
                    // field. Red is therefore the clean coverage channel; use
                    // it to restore straight alpha and remove the blue field.
                    var coverage = Math.Clamp((sourceRow[sourceOffset] - 8) * 255 / 247, 0, 255);
                    destinationRow[destinationOffset] = 255;
                    destinationRow[destinationOffset + 1] = 255;
                    destinationRow[destinationOffset + 2] = 255;
                    destinationRow[destinationOffset + 3] = (byte)coverage;
                }

                Marshal.Copy(
                    destinationRow,
                    0,
                    IntPtr.Add(resultBuffer.Address, (region.DestinationY + y) * resultBuffer.RowBytes),
                    destinationRow.Length);
            }
        }

        return result;
    }
}

/// <summary>
/// Version-pinned package icon containing an exact title wordmark. The selected
/// regions exclude character/key art; blue-field removal restores the mark's
/// </summary>
internal sealed record Ps5PackagedIconWordmarkProfile(
    string TitleId,
    long ExecutableLength,
    string ExecutableSha256,
    string RelativeAssetPath,
    int AssetLength,
    string AssetSha256,
    int AssetWidth,
    int AssetHeight,
    int OutputWidth,
    int OutputHeight,
    IReadOnlyList<Ps5TitleLogoRegion> Regions)
{
    internal bool TryResolve(string executablePath, out string assetPath)
    {
        assetPath = string.Empty;
        try
        {
            using (var executable = File.OpenRead(executablePath))
            {
                if (executable.Length != ExecutableLength ||
                    !string.Equals(
                        Convert.ToHexString(SHA256.HashData(executable)),
                        ExecutableSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var directory = Path.GetDirectoryName(executablePath);
            if (directory is null)
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(directory, RelativeAssetPath));
            using var asset = File.OpenRead(candidate);
            if (asset.Length != AssetLength ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(asset)),
                    AssetSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            assetPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record Ps5TitleLogoRegion(
    int SourceX,
    int SourceY,
    int Width,
    int Height,
    int DestinationX,
    int DestinationY);

/// <summary>
/// Hash-pinned embedded-logo profile. The profile is deliberately internal:
/// every new title/version needs its own inspection and provenance entry rather
/// than a speculative container scan at runtime.
/// </summary>
internal sealed record Ps5EmbeddedTitleLogoProfile(
    string TitleId,
    long ExecutableLength,
    string ExecutableSha256,
    long Offset,
    int Length,
    string PayloadSha256,
    int PixelWidth,
    int PixelHeight)
{
    internal bool TryRead(Stream executable, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (!executable.CanRead || !executable.CanSeek || executable.Length != ExecutableLength ||
            Offset < 0 || Length <= 0 || Offset > executable.Length - Length)
        {
            return false;
        }

        executable.Position = 0;
        if (!HashMatches(executable, ExecutableSha256))
        {
            return false;
        }

        executable.Position = Offset;
        payload = new byte[Length];
        int read = 0;
        while (read < payload.Length)
        {
            int count = executable.Read(payload, read, payload.Length - read);
            if (count == 0)
            {
                payload = Array.Empty<byte>();
                return false;
            }

            read += count;
        }

        if (!HashMatches(payload, PayloadSha256) || !HasExpectedPngHeader(payload, PixelWidth, PixelHeight))
        {
            payload = Array.Empty<byte>();
            return false;
        }

        return true;
    }

    private static bool HashMatches(Stream stream, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HashMatches(ReadOnlySpan<byte> bytes, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

    private static bool HasExpectedPngHeader(ReadOnlySpan<byte> payload, int width, int height)
    {
        return payload.Length >= 24 &&
            payload[0] == 0x89 && payload[1] == 0x50 &&
            payload[2] == 0x4E && payload[3] == 0x47 &&
            payload[4] == 0x0D && payload[5] == 0x0A &&
            payload[6] == 0x1A && payload[7] == 0x0A &&
            payload[8] == 0x00 && payload[9] == 0x00 &&
            payload[10] == 0x00 && payload[11] == 0x0D &&
            payload[12..16].SequenceEqual("IHDR"u8) &&
            ((payload[16] << 24) | (payload[17] << 16) | (payload[18] << 8) | payload[19]) == width &&
            ((payload[20] << 24) | (payload[21] << 16) | (payload[22] << 8) | payload[23]) == height;
    }
}
