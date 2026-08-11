// Copyright (C) 2026 Prosperismo Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Prosperismo.NativeBackgroundProducer;

[Flags]
internal enum BackgroundLayerMask : uint
{
    None = 0,
    FirstWaveBase = 1 << 0,
    ParticleOverlay = 1 << 1,
}

internal static class BackgroundPresentationProtocol
{
    internal const string MappingName = "Local\\ProsperismoShellBackgroundControl";
    internal const string ChangedEventName = "Local\\ProsperismoShellBackgroundControlChanged";
    internal const uint Version = 1;
    internal const int HeaderBytes = 64;
    internal const int LayerMaskOffset = 16;
    internal const int SequenceOffset = 24;
    internal const int TimestampOffset = 32;
    internal const BackgroundLayerMask HomeLayers =
        BackgroundLayerMask.FirstWaveBase | BackgroundLayerMask.ParticleOverlay;
    internal const BackgroundLayerMask SettingsLayers = BackgroundLayerMask.FirstWaveBase;
    private static ReadOnlySpan<byte> Magic => "PS5BGCT\0"u8;

    internal static bool TryDecode(
        ReadOnlySpan<byte> header,
        long stableSequence,
        out BackgroundLayerMask layers)
    {
        layers = BackgroundLayerMask.None;
        if (header.Length < HeaderBytes || stableSequence < 0 || (stableSequence & 1) != 0 ||
            !header[..8].SequenceEqual(Magic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != Version ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) != HeaderBytes ||
            BinaryPrimitives.ReadInt64LittleEndian(header[SequenceOffset..]) != stableSequence)
        {
            return false;
        }

        var candidate = (BackgroundLayerMask)BinaryPrimitives.ReadUInt32LittleEndian(
            header[LayerMaskOffset..]);
        if (candidate is not HomeLayers and not SettingsLayers)
        {
            return false;
        }

        layers = candidate;
        return true;
    }

    internal static void EncodeForTest(
        Span<byte> header,
        BackgroundLayerMask layers,
        long sequence,
        ulong timestampQpc = 0)
    {
        if (header.Length < HeaderBytes)
        {
            throw new ArgumentException($"header must be at least {HeaderBytes} bytes", nameof(header));
        }

        header[..HeaderBytes].Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[LayerMaskOffset..], (uint)layers);
        BinaryPrimitives.WriteInt64LittleEndian(header[SequenceOffset..], sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(header[TimestampOffset..], timestampQpc);
    }
}

/// <summary>
/// Reads the shell-owned presentation state. Absence is backwards-compatible
/// Home behavior; once a valid control page is observed, malformed/torn reads
/// retain the last valid state instead of flashing particles on in Settings.
/// </summary>
internal sealed class BackgroundPresentationStateReader : IDisposable
{
    private readonly byte[] _header = new byte[BackgroundPresentationProtocol.HeaderBytes];
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _accessor;
    private EventWaitHandle? _changedEvent;
    private BackgroundLayerMask _layers = BackgroundPresentationProtocol.HomeLayers;
    private long _nextOpenAttemptTick;

    internal bool ParticleOverlayEnabled
    {
        get
        {
            Refresh();
            return (_layers & BackgroundLayerMask.ParticleOverlay) != 0;
        }
    }

    internal void WaitForChangeOrTimeout(CancellationToken cancellationToken, TimeSpan timeout)
    {
        EnsureOpen();
        if (_changedEvent is null)
        {
            cancellationToken.WaitHandle.WaitOne(timeout);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var result = WaitHandle.WaitAny(
            new WaitHandle[] { cancellationToken.WaitHandle, _changedEvent },
            timeout);
        if (result == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void Refresh()
    {
        EnsureOpen();
        if (_accessor is null)
        {
            return;
        }

        try
        {
            var sequenceBefore = _accessor.ReadInt64(BackgroundPresentationProtocol.SequenceOffset);
            if (sequenceBefore < 0 || (sequenceBefore & 1) != 0)
            {
                return;
            }

            _accessor.ReadArray(0, _header, 0, _header.Length);
            Thread.MemoryBarrier();
            var sequenceAfter = _accessor.ReadInt64(BackgroundPresentationProtocol.SequenceOffset);
            if (sequenceBefore == sequenceAfter &&
                BackgroundPresentationProtocol.TryDecode(_header, sequenceBefore, out var layers))
            {
                _layers = layers;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Disconnect();
        }
    }

    private void EnsureOpen()
    {
        if (_accessor is not null || Environment.TickCount64 < _nextOpenAttemptTick)
        {
            return;
        }

        // Named MemoryMappedFile/EventWaitHandle discovery is Windows-only.
        // The renderer helper is presently Windows-hosted, but keep the pure
        // byte-contract self-test runnable on other .NET hosts.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _nextOpenAttemptTick = Environment.TickCount64 + 250;
        try
        {
            _mapping = MemoryMappedFile.OpenExisting(
                BackgroundPresentationProtocol.MappingName,
                MemoryMappedFileRights.Read);
            _accessor = _mapping.CreateViewAccessor(
                0,
                BackgroundPresentationProtocol.HeaderBytes,
                MemoryMappedFileAccess.Read);
            try
            {
                _changedEvent = EventWaitHandle.OpenExisting(
                    BackgroundPresentationProtocol.ChangedEventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _changedEvent = null;
            }
        }
        catch (FileNotFoundException)
        {
            Disconnect();
        }
    }

    private void Disconnect()
    {
        _changedEvent?.Dispose();
        _changedEvent = null;
        _accessor?.Dispose();
        _accessor = null;
        _mapping?.Dispose();
        _mapping = null;
    }

    public void Dispose() => Disconnect();
}
