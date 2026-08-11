// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.ShaderCompiler.Vulkan;

/// <summary>
/// Host input-assembly topology which realizes an NGG primitive export.
/// SPIR-V vertex stages cannot emit connectivity: it must be preserved by the
/// draw submitted to the host rasterizer.
/// </summary>
public enum Gen5HostPrimitiveTopology
{
    TriangleList,
    TriangleStrip,
}

/// <summary>
/// Explicit bridge from a guest NGG primitive export to the host draw that
/// realizes it. The translator consumes this when it sees EXP target 20 and
/// the presentation backend consumes the same value when it creates the
/// Vulkan input assembly and draw count.
/// </summary>
public readonly record struct Gen5NggPrimitiveConnectivity(
    uint GuestVerticesPerPrimitive,
    uint HostVerticesPerPrimitive,
    Gen5HostPrimitiveTopology HostTopology,
    bool Indexed = false)
{
    /// <summary>True only for a finite, non-indexed host expansion.</summary>
    public bool IsValid =>
        !Indexed &&
        GuestVerticesPerPrimitive > 0 &&
        HostVerticesPerPrimitive >= GuestVerticesPerPrimitive &&
        HostVerticesPerPrimitive >= 3;

    /// <summary>True when the host must add selectors beyond the guest draw.</summary>
    public bool RequiresVertexExpansion =>
        HostVerticesPerPrimitive != GuestVerticesPerPrimitive;
}
