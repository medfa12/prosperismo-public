// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Keeping the raw words beside their decoded meaning prevents the live
/// renderer and the off-screen conformance probe from drifting apart.
/// </summary>
public static class Ps5NativeParticleRenderState
{
    /// <summary>
    /// The four sampler SGPR words built by <c>large_particle_p</c> before its
    /// two <c>image_sample</c> instructions.
    /// </summary>
    public static ReadOnlySpan<uint> SamplerDescriptor =>
        [0x0000_0092u, 0x0000_0000u, 0x0250_0000u, 0x0000_0000u];

    public const bool MinFilterLinear = true;
    public const bool MagFilterLinear = true;
    public const bool MipFilterNearest = true;
    public const bool ClampUToEdge = true;
    public const bool ClampVToEdge = true;
    public const bool ClampWToEdge = true;
    public const float MinLod = 0.0f;
    public const float MaxLod = 0.0f;

    /// <summary>
    /// The PSM UI renderer creates its colour context with
    /// <c>PixelFormat.Rgba</c>, whose enum value is one. The Vulkan equivalent
    /// used by the native-particle path is R8G8B8A8_UNORM.
    /// </summary>
    public const uint UiRenderTargetPixelFormat = 1u;

    public const string UiRenderTargetHostFormat = "R8G8B8A8_UNORM";

    /// <summary>
    /// XF/PGraphics pixel-format code passed by NPXS40087 <c>E2FE0</c> to
    /// render-target constructor <c>153E70(width, height, 0x11)</c>. The object
    /// is then bound as the particle colour target and its texture view is
    /// handed to <c>light_p</c> through <c>EA650</c>. The exact Vulkan format
    /// represented by the private PGraphics mapping remains intentionally
    /// unnamed until that mapping is decoded.
    /// </summary>
    public const uint ParticleIntermediatePixelFormat = 0x11u;
}
