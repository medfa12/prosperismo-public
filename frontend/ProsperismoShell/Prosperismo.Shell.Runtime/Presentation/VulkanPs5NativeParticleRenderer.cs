// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Libs.Presentation;

/// <summary>
/// Interface adapter around the byte-exact Vulkan renderer. The Vulkan device,
/// remain alive between frames; only the guest buffers are uploaded again.
/// </summary>
public sealed class VulkanPs5NativeParticleRenderer : IPs5NativeParticleRenderer
{
    private readonly object _gate = new();
    private Ps5NativeParticleResources? _resources;
    private Ps5ParticleVulkanSession? _session;

    public ValueTask InitializeAsync(
        Ps5NativeParticleResources resources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!resources.HasValidTextures ||
            resources.VertexSpirv.IsEmpty || resources.FragmentSpirv.IsEmpty)
        {
            throw new ArgumentException("invalid native particle resources", nameof(resources));
        }

        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _resources = resources;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<Ps5NativeParticleFrame> RenderAsync(
        Ps5NativeParticleDraw draw,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RenderSequenceAsync([draw], cancellationToken);
    }

    public ValueTask<Ps5NativeParticleFrame> RenderSequenceAsync(
        IReadOnlyList<Ps5NativeParticleDraw> draws,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var resources = _resources ??
                throw new InvalidOperationException("renderer has not been initialized");
            if (draws.Count == 0)
            {
                throw new ArgumentException("native particle draw sequence is empty", nameof(draws));
            }

            if (_session is null || !_session.Supports(draws))
            {
                _session?.Dispose();
                _session = new Ps5ParticleVulkanSession(
                    resources,
                    draws[0],
                    Math.Max(
                        Ps5NativeParticleComputeRequest.SmallParticleBankCount,
                        draws.Count));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_session.Render(draws));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _resources = null;
        }
        return ValueTask.CompletedTask;
    }
}
