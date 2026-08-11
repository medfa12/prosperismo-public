// Copyright (C) 2026 Prosperismo Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Prosperismo.GUI.SystemAssets.Audio;

/// <summary>
/// macOS output path for the shell's UI sound mixer.
///
/// <para>The donor's <see cref="UiSoundPlayer"/> reaches the speakers through
/// Win32 <c>waveOut*</c>, so <c>IsSupported</c> was false on every other host
/// assets decode fine, there was simply nowhere to send them.</para>
///
/// <para>This is the equivalent AudioToolbox AudioQueue path. It consumes the
/// same interleaved 16-bit stereo mix at <see cref="UiSoundPlayer.MixSampleRate"/>,
/// so the mixer, voice limit and VAG decoding are untouched.</para>
/// </summary>
internal static class CoreAudioOutput
{
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    private const int FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagSignedInteger = 0x4;
    private const uint FlagPacked = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public nint AudioData;
        public uint AudioDataByteSize;
        public nint UserData;
        public uint PacketDescriptionCapacity;
        public nint PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    private delegate void OutputCallback(nint userData, nint queue, nint buffer);

    private static int _lastStatus = int.MinValue;

    /// <summary>Last AudioQueue status observed by diagnostics.</summary>
    internal static int LastStatus => Volatile.Read(ref _lastStatus);

    internal static void ResetStatus() => Volatile.Write(ref _lastStatus, int.MinValue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        OutputCallback callback,
        nint userData,
        nint runLoop,
        nint runLoopMode,
        uint flags,
        out nint queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(nint queue, uint byteSize, out nint buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(
        nint queue, nint buffer, uint packetCount, nint packetDescs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(nint queue, nint startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(nint queue, bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(nint queue, bool immediate);

    /// <summary>True when this host can open an AudioQueue.</summary>
    internal static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>
    /// Opens an output queue and pumps it until <paramref name="fill"/> reports
    /// silence for longer than <paramref name="idleMilliseconds"/>, or
    /// <paramref name="shouldStop"/> goes true.
    ///
    /// <paramref name="fill"/> receives a scratch buffer of interleaved 16-bit
    /// stereo frames and returns false when nothing is audible.
    /// </summary>
    internal static void Run(
        int bufferFrames,
        int bufferCount,
        Func<short[], bool> fill,
        Func<bool> shouldStop,
        int idleMilliseconds)
    {
        if (!IsSupported || fill is null || shouldStop is null)
        {
            return;
        }

        var channels = (uint)UiSoundPlayer.MixChannels;
        var bytesPerFrame = channels * sizeof(short);
        var format = new AudioStreamBasicDescription
        {
            SampleRate = UiSoundPlayer.MixSampleRate,
            FormatId = FormatLinearPcm,
            FormatFlags = FlagSignedInteger | FlagPacked,
            BytesPerPacket = bytesPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = bytesPerFrame,
            ChannelsPerFrame = channels,
            BitsPerChannel = 16,
            Reserved = 0,
        };

        var scratch = new short[bufferFrames * UiSoundPlayer.MixChannels];
        var bufferBytes = (uint)(scratch.Length * sizeof(short));
        var idleSince = Environment.TickCount64;
        var queued = new SemaphoreSlim(0, bufferCount);
        var returnedBuffers = new ConcurrentQueue<nint>();

        // Held for the queue's lifetime: the native side keeps the pointer.
        OutputCallback callback = (_, _, buffer) =>
        {
            returnedBuffers.Enqueue(buffer);
            try
            {
                queued.Release();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SemaphoreFullException)
            {
            }
        };
        var handle = GCHandle.Alloc(callback);

        nint queue = nint.Zero;
        try
        {
            var status = AudioQueueNewOutput(ref format, callback, nint.Zero, nint.Zero, nint.Zero, 0,
                out queue);
            Volatile.Write(ref _lastStatus, status);
            if (status != 0 || queue == nint.Zero)
            {
                return;
            }

            var buffers = new nint[bufferCount];
            for (var i = 0; i < bufferCount; i++)
            {
                status = AudioQueueAllocateBuffer(queue, bufferBytes, out buffers[i]);
                Volatile.Write(ref _lastStatus, status);
                if (status != 0)
                {
                    return;
                }

                Submit(queue, buffers[i], scratch, fill, ref idleSince);
            }

            status = AudioQueueStart(queue, nint.Zero);
            Volatile.Write(ref _lastStatus, status);
            if (status != 0)
            {
                return;
            }

            while (!shouldStop())
            {
                if (!queued.Wait(250))
                {
                    continue;
                }

                // The callback identifies the exact buffer that drained. Reusing
                // an arbitrary buffer can enqueue one that is still in flight,
                // which eventually starves or corrupts the macOS output queue.
                var firstReturned = true;
                while (returnedBuffers.TryDequeue(out var returned))
                {
                    // Wait() above consumed the completion token for the first
                    // returned buffer. Consume one more token for every
                    // additional buffer drained in this batch so the semaphore
                    // cannot drift upward when callbacks arrive in a burst.
                    if (!firstReturned)
                    {
                        queued.Wait(0);
                    }

                    firstReturned = false;
                    Submit(queue, returned, scratch, fill, ref idleSince);
                }

                if (Environment.TickCount64 - idleSince > idleMilliseconds)
                {
                    break;
                }
            }

            AudioQueueStop(queue, true);
        }
        catch (DllNotFoundException)
        {
            // No AudioToolbox: stay silent rather than take the shell down.
        }
        catch (EntryPointNotFoundException)
        {
        }
        finally
        {
            if (queue != nint.Zero)
            {
                AudioQueueDispose(queue, true);
            }

            handle.Free();
            queued.Dispose();
        }
    }

    private static void Submit(
        nint queue, nint buffer, short[] scratch, Func<short[], bool> fill, ref long idleSince)
    {
        var audible = fill(scratch);
        if (audible)
        {
            idleSince = Environment.TickCount64;
        }

        var native = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
        Marshal.Copy(scratch, 0, native.AudioData, scratch.Length);
        native.AudioDataByteSize = (uint)(scratch.Length * sizeof(short));
        Marshal.StructureToPtr(native, buffer, false);
        var status = AudioQueueEnqueueBuffer(queue, buffer, 0, nint.Zero);
        Volatile.Write(ref _lastStatus, status);
    }
}
