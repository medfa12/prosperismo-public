// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Prosperismo.Libs.Presentation;

namespace Prosperismo.GUI.SystemAssets.Shell;

/// <summary>
/// Shell-facing clock/state adapter for the recovered native particle backend.
/// It converts a global BGLayer state and elapsed time into the native draw
/// buffers consumed by <see cref="IPs5NativeParticleRenderer"/>.
/// </summary>
internal interface IPs5NativeParticleFrameSource : IAsyncDisposable
{
    bool SupportsState(ShellGlobalBackgroundState state) =>
        ShellBackgroundComposition.NativeParticleRouteFor(state).RawState == 3;

    ValueTask<Ps5NativeParticleFrame?> RenderAsync(
        Ps5NativeParticleFrameRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>One frame request from the live shell compositor.</summary>
internal readonly record struct Ps5NativeParticleFrameRequest(
    ShellGlobalBackgroundState State,
    TimeSpan Elapsed,
    int Width,
    int Height,
    float ParticleAlpha = 1.0f);
