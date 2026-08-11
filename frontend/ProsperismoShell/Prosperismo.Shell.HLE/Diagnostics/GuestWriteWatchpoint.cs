// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Arms a hardware write watchpoint on a guest address so the writer names itself.
/// </summary>
/// <remarks>
/// Static analysis cannot find a writer that reaches a field through an inner-struct
/// pointer, because the displacement it uses bears no relation to the field's offset
/// from the object base. Scanning for `[reg + 0x2730]` will never see `[reg + 0x8]`
/// against a pointer to the middle of the object. A debug register has no such blind
/// spot: it fires on the store regardless of how the address was computed.
/// <para>
/// DR0 is armed for an 8-byte write on every thread known to
/// <see cref="StalledThreadSampler"/>. When it trips, the CPU raises a single-step
/// exception and the backend's VEH prints its usual register dump - whose RIP is the
/// instruction that performed the store.
/// </para>
/// Enable with <c>PROSPERISMO_WATCH_WRITE=&lt;hex guest address&gt;</c>, optionally delayed by
/// <c>PROSPERISMO_WATCH_WRITE_DELAY_MS</c> (default 5000) so the arming happens after the
/// guest's threads exist.
/// </remarks>
public static class GuestWriteWatchpoint
{
    public const string AddressVariable = "PROSPERISMO_WATCH_WRITE";
    public const string DelayVariable = "PROSPERISMO_WATCH_WRITE_DELAY_MS";

    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSetContext = 0x0010;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ContextDebugRegisters = 0x00100010;
    private const nuint ContextSize = 1232;
    private const int ContextFlagsOffset = 0x030;
    private const int Dr0Offset = 0x048;
    private const int Dr7Offset = 0x070;

    // DR7: L0 enables DR0; RW0=01 selects "break on write"; LEN0=11 selects 8 bytes.
    private const ulong Dr7EnableWrite8 = (1UL << 0) | (0b01UL << 16) | (0b11UL << 18);

    private static readonly ulong _address = ParseAddress();
    private static readonly int _delayMs =
        int.TryParse(Environment.GetEnvironmentVariable(DelayVariable), out var parsed)
            ? Math.Clamp(parsed, 0, 600_000)
            : 5000;

    public static bool Enabled => _address != 0;

    static GuestWriteWatchpoint()
    {
        if (_address == 0)
        {
            return;
        }

        var arm = new Thread(ArmLoop)
        {
            IsBackground = true,
            Name = "guest-write-watchpoint",
        };
        arm.Start();
    }

    private static ulong ParseAddress()
    {
        var raw = Environment.GetEnvironmentVariable(AddressVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
        }

        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : 0;
    }

    private static void ArmLoop()
    {
        Thread.Sleep(_delayMs);
        var lastArmed = -1;
        while (true)
        {
            var armed = 0;
            foreach (var native in StalledThreadSampler.KnownNativeThreadIds())
            {
                if (Arm(native))
                {
                    armed++;
                }
            }

            if (armed != lastArmed)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Write watchpoint 0x{_address:X16}: armed on {armed} thread(s)");
                Console.Error.Flush();
                lastArmed = armed;
            }

            // Re-arm often: a thread only enters the registry on its first HLE call, so a
            // slow cadence leaves newly created threads unwatched for seconds - long
            // enough to miss the very store being hunted.
            Thread.Sleep(250);
        }
    }

    private static unsafe bool Arm(uint nativeThreadId)
    {
        var handle = OpenThread(ThreadGetContext | ThreadSetContext | ThreadSuspendResume, false, nativeThreadId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (SuspendThread(handle) == unchecked((uint)-1))
            {
                return false;
            }

            try
            {
                var buffer = NativeMemory.AlignedAlloc(ContextSize, 16);
                try
                {
                    NativeMemory.Clear(buffer, ContextSize);
                    *(uint*)((byte*)buffer + ContextFlagsOffset) = ContextDebugRegisters;
                    if (!GetThreadContext(handle, (IntPtr)buffer))
                    {
                        return false;
                    }

                    *(ulong*)((byte*)buffer + Dr0Offset) = _address;
                    *(ulong*)((byte*)buffer + Dr7Offset) = Dr7EnableWrite8;
                    *(uint*)((byte*)buffer + ContextFlagsOffset) = ContextDebugRegisters;
                    return SetThreadContext(handle, (IntPtr)buffer);
                }
                finally
                {
                    NativeMemory.AlignedFree(buffer);
                }
            }
            finally
            {
                ResumeThread(handle);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr handle, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadContext(IntPtr handle, IntPtr context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
