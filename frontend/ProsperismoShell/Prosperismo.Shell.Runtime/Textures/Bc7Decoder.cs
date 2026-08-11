// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace Prosperismo.Libs.Textures;

/// <summary>
/// Dependency-free CPU decoder for the BC7 (BPTC unorm) block-compressed
/// texture format, following the Khronos/Direct3D BPTC specification. BC7
/// stores 4x4 texel blocks in 16 bytes and selects one of eight encoding
/// "modes" per block; all eight are implemented here, including the two- and
/// three-subset partitionings, per-endpoint and shared P-bits, component
/// rotation and the dual index sets of modes 4 and 5.
/// </summary>
/// <remarks>
/// Output is straight (non-premultiplied) RGBA8 in memory order R,G,B,A. The
/// decoder is written for correctness rather than speed: the shell backgrounds
/// it targets are decoded once and cached.
/// </remarks>
public static class Bc7Decoder
{
    private const int BlockBytes = 16;
    private const int BlockDim = 4;

    /// <summary>
    /// Decodes a BC7 (DXGI_FORMAT_BC7_UNORM) image into row-major RGBA8. The
    /// texel data is a tight grid of 16-byte blocks laid out left-to-right then
    /// top-to-bottom, ceil(width/4) by ceil(height/4) blocks. Dimensions that
    /// are not multiples of four are handled by decoding full 4x4 blocks and
    /// clipping to the requested extent.
    /// </summary>
    /// <param name="bc7Blocks">The compressed block stream.</param>
    /// <param name="width">Image width in pixels; must be positive.</param>
    /// <param name="height">Image height in pixels; must be positive.</param>
    /// <returns>width*height*4 bytes of RGBA8, row-major.</returns>
    public static byte[] DecodeImage(ReadOnlySpan<byte> bc7Blocks, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height), "Image dimensions must be positive.");
        }

        int blocksX = (width + BlockDim - 1) / BlockDim;
        int blocksY = (height + BlockDim - 1) / BlockDim;
        long required = (long)blocksX * blocksY * BlockBytes;
        if (bc7Blocks.Length < required)
        {
            throw new ArgumentException(
                $"BC7 data is too small: expected at least {required} bytes for {width}x{height}, got {bc7Blocks.Length}.",
                nameof(bc7Blocks));
        }

        var rgba = new byte[(long)width * height * 4];
        Span<byte> blockPixels = stackalloc byte[BlockDim * BlockDim * 4];

        int blockIndex = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                var block = bc7Blocks.Slice(blockIndex * BlockBytes, BlockBytes);
                DecodeBlock(block, blockPixels);
                blockIndex++;

                int baseX = bx * BlockDim;
                int baseY = by * BlockDim;
                for (int py = 0; py < BlockDim; py++)
                {
                    int y = baseY + py;
                    if (y >= height)
                    {
                        break;
                    }

                    for (int px = 0; px < BlockDim; px++)
                    {
                        int x = baseX + px;
                        if (x >= width)
                        {
                            break;
                        }

                        int src = (py * BlockDim + px) * 4;
                        long dst = ((long)y * width + x) * 4;
                        rgba[dst] = blockPixels[src];
                        rgba[dst + 1] = blockPixels[src + 1];
                        rgba[dst + 2] = blockPixels[src + 2];
                        rgba[dst + 3] = blockPixels[src + 3];
                    }
                }
            }
        }

        return rgba;
    }

    /// <summary>
    /// Decodes a single 16-byte BC7 block into a 4x4 patch of RGBA8 (64 bytes,
    /// row-major within the block). Exposed for unit tests that build blocks by
    /// hand.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> rgbaOut)
    {
        if (block.Length < BlockBytes)
        {
            throw new ArgumentException("A BC7 block is 16 bytes.", nameof(block));
        }

        if (rgbaOut.Length < BlockDim * BlockDim * 4)
        {
            throw new ArgumentException("Output must hold a 4x4 RGBA8 patch (64 bytes).", nameof(rgbaOut));
        }

        var reader = new BitReader(block);

        // The mode is the count of leading zero bits before the first set bit
        // of the block (bit 0 is the least-significant bit of byte 0).
        int mode = 0;
        while (mode < 8 && reader.Read(1) == 0)
        {
            mode++;
        }

        if (mode >= 8)
        {
            // Reserved / illegal encoding: the spec leaves this undefined; emit
            // opaque black rather than reading past the mode bits.
            for (int i = 0; i < BlockDim * BlockDim; i++)
            {
                rgbaOut[i * 4] = 0;
                rgbaOut[i * 4 + 1] = 0;
                rgbaOut[i * 4 + 2] = 0;
                rgbaOut[i * 4 + 3] = 255;
            }

            return;
        }

        ref readonly ModeInfo info = ref Modes[mode];
        int endpoints = info.Subsets * 2;

        int partition = info.PartitionBits > 0 ? reader.Read(info.PartitionBits) : 0;
        int rotation = info.RotationBits > 0 ? reader.Read(info.RotationBits) : 0;
        int indexMode = info.IndexSelectionBits > 0 ? reader.Read(info.IndexSelectionBits) : 0;

        // Endpoint components are stored component-major: every endpoint's red,
        // then every endpoint's green, then blue, then (if present) alpha.
        Span<int> r = stackalloc int[6];
        Span<int> g = stackalloc int[6];
        Span<int> b = stackalloc int[6];
        Span<int> a = stackalloc int[6];

        for (int e = 0; e < endpoints; e++)
        {
            r[e] = reader.Read(info.ColorBits);
        }

        for (int e = 0; e < endpoints; e++)
        {
            g[e] = reader.Read(info.ColorBits);
        }

        for (int e = 0; e < endpoints; e++)
        {
            b[e] = reader.Read(info.ColorBits);
        }

        if (info.AlphaBits > 0)
        {
            for (int e = 0; e < endpoints; e++)
            {
                a[e] = reader.Read(info.AlphaBits);
            }
        }

        // P-bits become the new least-significant bit of each endpoint
        // component, raising its precision by one.
        Span<int> pbits = stackalloc int[6];
        int colorPrecision = info.ColorBits;
        int alphaPrecision = info.AlphaBits;
        if (info.PerEndpointPBit)
        {
            for (int e = 0; e < endpoints; e++)
            {
                pbits[e] = reader.Read(1);
            }

            colorPrecision++;
            alphaPrecision = info.AlphaBits > 0 ? info.AlphaBits + 1 : 0;
        }
        else if (info.SharedPBit)
        {
            Span<int> shared = stackalloc int[3];
            for (int s = 0; s < info.Subsets; s++)
            {
                shared[s] = reader.Read(1);
            }

            for (int e = 0; e < endpoints; e++)
            {
                pbits[e] = shared[e / 2];
            }

            colorPrecision++;
            alphaPrecision = info.AlphaBits > 0 ? info.AlphaBits + 1 : 0;
        }

        bool hasPBit = info.PerEndpointPBit || info.SharedPBit;
        for (int e = 0; e < endpoints; e++)
        {
            r[e] = Finalize(r[e], pbits[e], colorPrecision, hasPBit);
            g[e] = Finalize(g[e], pbits[e], colorPrecision, hasPBit);
            b[e] = Finalize(b[e], pbits[e], colorPrecision, hasPBit);
            a[e] = info.AlphaBits > 0
                ? Finalize(a[e], pbits[e], alphaPrecision, hasPBit)
                : 255;
        }

        // Anchor texel of each subset carries one fewer index bit (its high bit
        // is implicitly zero), which keeps the block bit-exact.
        Span<int> anchors = stackalloc int[3];
        anchors[0] = 0;
        if (info.Subsets == 2)
        {
            anchors[1] = Anchor2[partition];
        }
        else if (info.Subsets == 3)
        {
            anchors[1] = Anchor3A[partition];
            anchors[2] = Anchor3B[partition];
        }

        // Primary index set.
        Span<int> primary = stackalloc int[16];
        for (int p = 0; p < 16; p++)
        {
            int subset = SubsetOf(info.Subsets, partition, p);
            int bits = info.IndexBits;
            if (p == anchors[subset])
            {
                bits--;
            }

            primary[p] = reader.Read(bits);
        }

        // Secondary index set (modes 4 and 5). Its only subset is subset 0, so
        // texel 0 is the anchor.
        Span<int> secondary = stackalloc int[16];
        if (info.IndexBits2 > 0)
        {
            for (int p = 0; p < 16; p++)
            {
                int bits = info.IndexBits2;
                if (p == 0)
                {
                    bits--;
                }

                secondary[p] = reader.Read(bits);
            }
        }

        // Choose which index set drives colour and which drives alpha. Without a
        // second set both share the primary indices.
        bool swap = indexMode == 1;
        Span<int> colorIndices = info.IndexBits2 > 0 ? (swap ? secondary : primary) : primary;
        Span<int> alphaIndices = info.IndexBits2 > 0 ? (swap ? primary : secondary) : primary;
        int colorIndexBits = info.IndexBits2 > 0 ? (swap ? info.IndexBits2 : info.IndexBits) : info.IndexBits;
        int alphaIndexBits = info.IndexBits2 > 0 ? (swap ? info.IndexBits : info.IndexBits2) : info.IndexBits;

        for (int p = 0; p < 16; p++)
        {
            int subset = SubsetOf(info.Subsets, partition, p);
            int e0 = subset * 2;
            int e1 = e0 + 1;

            int cw = Weight(colorIndexBits, colorIndices[p]);
            int aw = Weight(alphaIndexBits, alphaIndices[p]);

            byte rr = (byte)Interpolate(r[e0], r[e1], cw);
            byte gg = (byte)Interpolate(g[e0], g[e1], cw);
            byte bb = (byte)Interpolate(b[e0], b[e1], cw);
            byte aa = info.AlphaBits > 0 ? (byte)Interpolate(a[e0], a[e1], aw) : (byte)255;

            // Rotation swaps the alpha channel with one colour channel so the
            // encoder can spend the higher-precision index set on whichever
            // channel needs it.
            switch (rotation)
            {
                case 1:
                    (aa, rr) = (rr, aa);
                    break;
                case 2:
                    (aa, gg) = (gg, aa);
                    break;
                case 3:
                    (aa, bb) = (bb, aa);
                    break;
            }

            int outIndex = p * 4;
            rgbaOut[outIndex] = rr;
            rgbaOut[outIndex + 1] = gg;
            rgbaOut[outIndex + 2] = bb;
            rgbaOut[outIndex + 3] = aa;
        }
    }

    private static int SubsetOf(int subsets, int partition, int pixel)
    {
        return subsets switch
        {
            1 => 0,
            2 => Partition2[partition * 16 + pixel],
            _ => Partition3[partition * 16 + pixel],
        };
    }

    // Appends the P-bit (if any) as the low bit, then replicates the value up to
    // eight bits so 0 maps to 0 and the maximum maps to 255.
    private static int Finalize(int value, int pbit, int precision, bool hasPBit)
    {
        if (hasPBit)
        {
            value = (value << 1) | pbit;
        }

        if (precision >= 8)
        {
            return value & 0xFF;
        }

        return (value << (8 - precision)) | (value >> (2 * precision - 8));
    }

    private static int Interpolate(int e0, int e1, int weight)
    {
        return (e0 * (64 - weight) + e1 * weight + 32) >> 6;
    }

    private static int Weight(int indexBits, int index)
    {
        return indexBits switch
        {
            2 => Weights2[index],
            3 => Weights3[index],
            _ => Weights4[index],
        };
    }

    private readonly struct ModeInfo
    {
        public ModeInfo(
            int subsets,
            int partitionBits,
            int rotationBits,
            int indexSelectionBits,
            int colorBits,
            int alphaBits,
            bool perEndpointPBit,
            bool sharedPBit,
            int indexBits,
            int indexBits2)
        {
            Subsets = subsets;
            PartitionBits = partitionBits;
            RotationBits = rotationBits;
            IndexSelectionBits = indexSelectionBits;
            ColorBits = colorBits;
            AlphaBits = alphaBits;
            PerEndpointPBit = perEndpointPBit;
            SharedPBit = sharedPBit;
            IndexBits = indexBits;
            IndexBits2 = indexBits2;
        }

        public int Subsets { get; }
        public int PartitionBits { get; }
        public int RotationBits { get; }
        public int IndexSelectionBits { get; }
        public int ColorBits { get; }
        public int AlphaBits { get; }
        public bool PerEndpointPBit { get; }
        public bool SharedPBit { get; }
        public int IndexBits { get; }
        public int IndexBits2 { get; }
    }

    // Per-mode field widths from the BPTC specification.
    private static readonly ModeInfo[] Modes =
    {
        //          NS PB RB ISB CB AB  EPB    SPB    IB IB2
        new ModeInfo(3, 4, 0, 0,  4, 0,  true,  false, 3, 0), // 0
        new ModeInfo(2, 6, 0, 0,  6, 0,  false, true,  3, 0), // 1
        new ModeInfo(3, 6, 0, 0,  5, 0,  false, false, 2, 0), // 2
        new ModeInfo(2, 6, 0, 0,  7, 0,  true,  false, 2, 0), // 3
        new ModeInfo(1, 0, 2, 1,  5, 6,  false, false, 2, 3), // 4
        new ModeInfo(1, 0, 2, 0,  7, 8,  false, false, 2, 2), // 5
        new ModeInfo(1, 0, 0, 0,  7, 7,  true,  false, 4, 0), // 6
        new ModeInfo(2, 6, 0, 0,  5, 5,  true,  false, 2, 0), // 7
    };

    private static readonly int[] Weights2 = { 0, 21, 43, 64 };
    private static readonly int[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
    private static readonly int[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

    // Two-subset partition assignments: 64 partitions x 16 texels.
    private static readonly byte[] Partition2 =
    {
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        0,0,0,1,0,0,0,1,0,0,0,1,0,0,0,1,
        0,1,1,1,0,1,1,1,0,1,1,1,0,1,1,1,
        0,0,0,1,0,0,1,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,1,0,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,1,0,1,1,1,1,1,1,1,
        0,0,0,1,0,0,1,1,0,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,1,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,1,0,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,1,0,1,1,1,
        0,0,0,1,0,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,1,1,1,1,1,1,1,1,
        0,0,0,0,1,1,1,1,1,1,1,1,1,1,1,1,
        0,0,0,0,0,0,0,0,0,0,0,0,1,1,1,1,
        0,0,0,0,1,0,0,0,1,1,1,0,1,1,1,1,
        0,1,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,1,0,0,0,1,1,1,0,
        0,1,1,1,0,0,1,1,0,0,0,1,0,0,0,0,
        0,0,1,1,0,0,0,1,0,0,0,0,0,0,0,0,
        0,0,0,0,1,0,0,0,1,1,0,0,1,1,1,0,
        0,0,0,0,0,0,0,0,1,0,0,0,1,1,0,0,
        0,1,1,1,0,0,1,1,0,0,1,1,0,0,0,1,
        0,0,1,1,0,0,0,1,0,0,0,1,0,0,0,0,
        0,0,0,0,1,0,0,0,1,0,0,0,1,1,0,0,
        0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,0,
        0,0,1,1,0,1,1,0,0,1,1,0,1,1,0,0,
        0,0,0,1,0,1,1,1,1,1,1,0,1,0,0,0,
        0,0,0,0,1,1,1,1,1,1,1,1,0,0,0,0,
        0,1,1,1,0,0,0,1,1,0,0,0,1,1,1,0,
        0,0,1,1,1,0,0,1,1,0,0,1,1,1,0,0,
        0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,
        0,0,0,0,1,1,1,1,0,0,0,0,1,1,1,1,
        0,1,0,1,1,0,1,0,0,1,0,1,1,0,1,0,
        0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,
        0,0,1,1,1,1,0,0,0,0,1,1,1,1,0,0,
        0,1,0,1,0,1,0,1,1,0,1,0,1,0,1,0,
        0,1,1,0,1,0,0,1,0,1,1,0,1,0,0,1,
        0,1,0,1,1,0,1,0,1,0,1,0,0,1,0,1,
        0,1,1,1,0,0,1,1,1,1,0,0,1,1,1,0,
        0,0,0,1,0,0,1,1,1,1,0,0,1,0,0,0,
        0,0,1,1,0,0,1,0,0,1,0,0,1,1,0,0,
        0,0,1,1,1,0,1,1,1,1,0,1,1,1,0,0,
        0,1,1,0,1,0,0,1,1,0,0,1,0,1,1,0,
        0,0,1,1,1,1,0,0,1,1,0,0,0,0,1,1,
        0,1,1,0,0,1,1,0,1,0,0,1,1,0,0,1,
        0,0,0,0,0,1,1,0,0,1,1,0,0,0,0,0,
        0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,0,
        0,0,1,0,0,1,1,1,0,0,1,0,0,0,0,0,
        0,0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,
        0,0,0,0,0,1,0,0,1,1,1,0,0,1,0,0,
        0,1,1,0,1,1,0,0,1,0,0,1,0,0,1,1,
        0,0,1,1,0,1,1,0,1,1,0,0,1,0,0,1,
        0,1,1,0,0,0,1,1,1,0,0,1,1,1,0,0,
        0,0,1,1,1,0,0,1,1,1,0,0,0,1,1,0,
        0,1,1,0,1,1,0,0,1,1,0,0,1,0,0,1,
        0,1,1,0,0,0,1,1,0,0,1,1,1,0,0,1,
        0,1,1,1,1,1,1,0,1,0,0,0,0,0,0,1,
        0,0,0,1,1,0,0,0,1,1,1,0,0,1,1,1,
        0,0,0,0,1,1,1,1,0,0,1,1,0,0,1,1,
        0,0,1,1,0,0,1,1,1,1,1,1,0,0,0,0,
        0,0,1,0,0,0,1,0,1,1,1,0,1,1,1,0,
        0,1,0,0,0,1,0,0,0,1,1,1,0,1,1,1,
    };

    // Three-subset partition assignments: 64 partitions x 16 texels.
    private static readonly byte[] Partition3 =
    {
        0,0,1,1,0,0,1,1,0,2,2,1,2,2,2,2,
        0,0,0,1,0,0,1,1,2,2,1,1,2,2,2,1,
        0,0,0,0,2,0,0,1,2,2,1,1,2,2,1,1,
        0,2,2,2,0,0,2,2,0,0,1,1,0,1,1,1,
        0,0,0,0,0,0,0,0,1,1,2,2,1,1,2,2,
        0,0,1,1,0,0,1,1,0,0,2,2,0,0,2,2,
        0,0,2,2,0,0,2,2,1,1,1,1,1,1,1,1,
        0,0,1,1,0,0,1,1,2,2,1,1,2,2,1,1,
        0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,
        0,0,0,0,1,1,1,1,1,1,1,1,2,2,2,2,
        0,0,0,0,1,1,1,1,2,2,2,2,2,2,2,2,
        0,0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,
        0,1,1,2,0,1,1,2,0,1,1,2,0,1,1,2,
        0,1,2,2,0,1,2,2,0,1,2,2,0,1,2,2,
        0,0,1,1,0,1,1,2,1,1,2,2,1,2,2,2,
        0,0,1,1,2,0,0,1,2,2,0,0,2,2,2,0,
        0,0,0,1,0,0,1,1,0,1,1,2,1,1,2,2,
        0,1,1,1,0,0,1,1,2,0,0,1,2,2,0,0,
        0,0,0,0,1,1,2,2,1,1,2,2,1,1,2,2,
        0,0,2,2,0,0,2,2,0,0,2,2,1,1,1,1,
        0,1,1,1,0,1,1,1,0,2,2,2,0,2,2,2,
        0,0,0,1,0,0,0,1,2,2,2,1,2,2,2,1,
        0,0,0,0,0,0,1,1,0,1,2,2,0,1,2,2,
        0,0,0,0,1,1,0,0,2,2,1,0,2,2,1,0,
        0,1,2,2,0,1,2,2,0,0,1,1,0,0,0,0,
        0,0,1,2,0,0,1,2,1,1,2,2,2,2,2,2,
        0,1,1,0,1,2,2,1,1,2,2,1,0,1,1,0,
        0,0,0,0,0,1,1,0,1,2,2,1,1,2,2,1,
        0,0,2,2,1,1,0,2,1,1,0,2,0,0,2,2,
        0,1,1,0,0,1,1,0,2,0,0,2,2,2,2,2,
        0,0,1,1,0,1,2,2,0,1,2,2,0,0,1,1,
        0,0,0,0,2,0,0,0,2,2,1,1,2,2,2,1,
        0,0,0,0,0,0,0,2,1,1,2,2,1,2,2,2,
        0,2,2,2,0,0,2,2,0,0,1,2,0,0,1,1,
        0,0,1,1,0,0,1,2,0,0,2,2,0,2,2,2,
        0,1,2,0,0,1,2,0,0,1,2,0,0,1,2,0,
        0,0,0,0,1,1,1,1,2,2,2,2,0,0,0,0,
        0,1,2,0,1,2,0,1,2,0,1,2,0,1,2,0,
        0,1,2,0,2,0,1,2,1,2,0,1,0,1,2,0,
        0,0,1,1,2,2,0,0,1,1,2,2,0,0,1,1,
        0,0,1,1,1,1,2,2,2,2,0,0,0,0,1,1,
        0,1,0,1,0,1,0,1,2,2,2,2,2,2,2,2,
        0,0,0,0,0,0,0,0,2,1,2,1,2,1,2,1,
        0,0,2,2,1,1,2,2,0,0,2,2,1,1,2,2,
        0,0,2,2,0,0,1,1,0,0,2,2,0,0,1,1,
        0,2,2,0,1,2,2,1,0,2,2,0,1,2,2,1,
        0,1,0,1,2,2,2,2,2,2,2,2,0,1,0,1,
        0,0,0,0,2,1,2,1,2,1,2,1,2,1,2,1,
        0,1,0,1,0,1,0,1,0,1,0,1,2,2,2,2,
        0,2,2,2,0,1,1,1,0,2,2,2,0,1,1,1,
        0,0,0,2,1,1,1,2,0,0,0,2,1,1,1,2,
        0,0,0,0,2,1,1,2,2,1,1,2,2,1,1,2,
        0,2,2,2,0,1,1,1,0,1,1,1,0,2,2,2,
        0,0,0,2,1,1,1,2,1,1,1,2,0,0,0,2,
        0,1,1,0,0,1,1,0,0,1,1,0,2,2,2,2,
        0,0,0,0,0,0,0,0,2,1,1,2,2,1,1,2,
        0,1,1,0,0,1,1,0,2,2,2,2,2,2,2,2,
        0,0,2,2,0,0,1,1,0,0,1,1,0,0,2,2,
        0,0,2,2,1,1,2,2,1,1,2,2,0,0,2,2,
        0,0,0,0,0,0,0,0,0,0,0,0,2,1,1,2,
        0,0,0,2,0,0,0,1,0,0,0,2,0,0,0,1,
        0,2,2,2,1,2,2,2,0,2,2,2,1,2,2,2,
        0,1,0,1,2,2,2,2,2,2,2,2,2,2,2,2,
        0,1,1,1,2,0,1,1,2,2,0,1,2,2,2,0,
    };

    // Second-subset anchor texel for each two-subset partition.
    private static readonly byte[] Anchor2 =
    {
        15,15,15,15,15,15,15,15,
        15,15,15,15,15,15,15,15,
        15, 2, 8, 2, 2, 8, 8,15,
         2, 8, 2, 2, 8, 8, 2, 2,
        15,15, 6, 8, 2, 8,15,15,
         2, 8, 2, 2, 2,15,15, 6,
         6, 2, 6, 8,15,15, 2, 2,
        15,15,15,15,15, 2, 2,15,
    };

    // Second-subset anchor texel for each three-subset partition.
    private static readonly byte[] Anchor3A =
    {
         3, 3,15,15, 8, 3,15,15,
         8, 8, 6, 6, 6, 5, 3, 3,
         3, 3, 8,15, 3, 3, 6,10,
         5, 8, 8, 6, 8, 5,15,15,
         8,15, 3, 5, 6,10, 8,15,
        15, 3,15, 5,15,15,15,15,
         3,15, 5, 5, 5, 8, 5,10,
         5,10, 8,13,15,12, 3, 3,
    };

    // Third-subset anchor texel for each three-subset partition.
    private static readonly byte[] Anchor3B =
    {
        15, 8, 8, 3,15,15, 3, 8,
        15,15,15,15,15,15,15, 8,
        15, 8,15, 3,15, 8,15, 8,
         3,15, 6,10,15,15,10, 8,
        15, 3,15,10,10, 8, 9,10,
         6,15, 8,15, 3, 6, 6, 8,
        15, 3,15,15,15,15,15,15,
        15,15,15,15, 3,15,15, 8,
    };

    /// <summary>
    /// Reads fields from a BC7 block least-significant-bit first, matching the
    /// little-endian bit order the format is stored in.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        public int Read(int count)
        {
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                int position = _bit;
                int value = (_data[position >> 3] >> (position & 7)) & 1;
                result |= value << i;
                _bit++;
            }

            return result;
        }
    }
}
