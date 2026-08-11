// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.ShaderCompiler.Vulkan;

namespace Prosperismo.Libs.Presentation;

/// <summary>Maps recovered NPXS40087 draw evidence to the generic Gen5 GPU contract.</summary>
internal static class Npxs40087GpuContract
{
    internal static Gen5NggPrimitiveConnectivity CreateConnectivity(
        Npxs40087DrawTopologyContract contract) =>
        new(
            (uint)contract.GuestSubmittedVerticesPerElement,
            (uint)contract.HostVerticesPerElement,
            contract.HostTopology switch
            {
                Npxs40087HostTopology.TriangleList => Gen5HostPrimitiveTopology.TriangleList,
                Npxs40087HostTopology.TriangleStrip => Gen5HostPrimitiveTopology.TriangleStrip,
                _ => throw new InvalidDataException("unknown NPXS40087 host topology"),
            },
            contract.Indexed);
}
