// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Samples the host instruction pointer of a thread that has stopped issuing HLE calls.
/// </summary>
/// <remarks>
/// The periodic guest-thread snapshot reports <c>host_rip</c> only for threads in the
/// backend's guest-thread registry. The entry thread is never registered - it runs
/// through <c>ExecuteEntry</c> on the host thread that called into the backend - so when
/// it stops progressing there is nothing that says where it actually is. Its recorded
/// guest rip is only refreshed at import boundaries, so a thread spinning in guest code
/// that calls no imports is indistinguishable from one wedged inside the last export it
/// happened to call.
/// <para>
/// This closes that gap: every thread that makes an HLE call registers its native thread
/// id, and a background sampler reports the host rip of any thread that has been silent
/// for longer than the threshold. A rip inside translated guest code means a guest-side
/// spin; a rip inside a wait or a managed frame means something else entirely.
/// </para>
/// Enable with <c>PROSPERISMO_SAMPLE_STALLED_THREADS=1</c>; a thread counts as stalled after
/// <c>PROSPERISMO_SAMPLE_STALLED_MS</c> milliseconds of silence (default 20000).
/// </remarks>
public static class StalledThreadSampler
{
    public const string EnableVariable = "PROSPERISMO_SAMPLE_STALLED_THREADS";
    public const string ThresholdVariable = "PROSPERISMO_SAMPLE_STALLED_MS";

    private const uint ThreadGetContext = 0x0008;
    private const uint ThreadSuspendResume = 0x0002;
    private const uint ThreadQueryInformation = 0x0040;
    private const uint ContextControl = 0x00100003; // AMD64 CONTEXT_CONTROL | CONTEXT_INTEGER
    private const nuint ContextSize = 1232;          // sizeof(CONTEXT) on AMD64
    private const int ContextFlagsOffset = 0x030;
    private const int RipOffset = 0x0F8;
    private const int RspOffset = 0x098;
    private const int R9Offset = 0x0C0;
    private const int RcxOffset = 0x080;

    private static readonly bool _enabled = string.Equals(
        Environment.GetEnvironmentVariable(EnableVariable),
        "1",
        StringComparison.Ordinal);

    private static readonly long _thresholdMs =
        long.TryParse(Environment.GetEnvironmentVariable(ThresholdVariable), out var parsed)
            ? Math.Clamp(parsed, 1000, 600_000)
            : 20_000;

    private sealed class ThreadMark
    {
        public uint NativeId;
        public long LastSeenTicks;
        public string LastExport = string.Empty;
        public bool Reported;
    }

    private static readonly ConcurrentDictionary<int, ThreadMark> _threads = new();

    [ThreadStatic]
    private static ThreadMark? _current;

    private static ulong _lastR9;
    private static ulong _lastRsp;
    private static ulong _lastRcx;

    public static bool Enabled => _enabled || GuestWriteWatchpoint.Enabled;

    static StalledThreadSampler()
    {
        if (!_enabled)
        {
            return;
        }

        var sampler = new Thread(SampleLoop)
        {
            IsBackground = true,
            Name = "stalled-thread-sampler",
        };
        sampler.Start();
    }

    /// <summary>Records that this thread is alive and which export it last touched.</summary>
    public static void NoteAlive(string export)
    {
        // Guard on the property, not the field: the thread registry is also what the
        // write watchpoint uses to find threads to arm, so it must fill even when the
        // stall sampler itself is off.
        if (!Enabled)
        {
            return;
        }

        var mark = _current;
        if (mark is null)
        {
            mark = new ThreadMark { NativeId = GetCurrentThreadId() };
            _current = mark;
            _threads[Environment.CurrentManagedThreadId] = mark;
        }

        mark.LastExport = export;
        mark.Reported = false;
        Volatile.Write(ref mark.LastSeenTicks, Environment.TickCount64);
    }


    /// <summary>
    /// The export a native thread last entered, and how long ago it was seen.
    /// </summary>
    /// <remarks>
    /// A blocked thread's host stack names coreclr and KERNELBASE and stops there, because
    /// the managed frames in between live in JIT memory that belongs to no module. This is
    /// the substitute: the last export the thread announced. If it has been silent since,
    /// that export is where it still is.
    /// </remarks>
    public static bool TryDescribeThread(uint nativeThreadId, out string description)
    {
        foreach (var pair in _threads)
        {
            var mark = pair.Value;
            if (mark.NativeId != nativeThreadId)
            {
                continue;
            }

            var last = Volatile.Read(ref mark.LastSeenTicks);
            var silent = last == 0 ? -1 : Environment.TickCount64 - last;
            description = $"last_export={mark.LastExport} silent_ms={silent}";
            return true;
        }

        description = "never made an HLE call";
        return false;
    }

    /// <summary>Native thread ids of every thread that has made an HLE call.</summary>
    public static System.Collections.Generic.IEnumerable<uint> KnownNativeThreadIds()
    {
        foreach (var pair in _threads)
        {
            yield return pair.Value.NativeId;
        }
    }

    private static void SampleLoop()
    {
        var thresholdTicks = _thresholdMs;
        while (true)
        {
            Thread.Sleep(5000);
            var now = Environment.TickCount64;
            foreach (var pair in _threads)
            {
                var mark = pair.Value;
                var last = Volatile.Read(ref mark.LastSeenTicks);
                if (last == 0 || now - last < thresholdTicks || mark.Reported)
                {
                    continue;
                }

                mark.Reported = true;
                var rip = TryReadRip(mark.NativeId);
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Stalled thread: managed={pair.Key} native={mark.NativeId} " +
                    $"silent_ms={now - last} last_export={mark.LastExport} " +
                    $"host_rip={(rip is { } value ? $"0x{value:X16}" : "<unavailable>")} " +
                    $"{(rip is { } addr ? DescribeAddress(addr) : string.Empty)}");
                Console.Error.Flush();
            }
        }
    }


    /// <summary>
    /// Classifies a host address: which native module owns it, or - when it belongs to
    /// no module - the allocation base, type and protection. That distinguishes a rip in
    /// translated guest code (private, executable, no module) from one in the .NET JIT
    /// heap or in a loaded DLL, which the raw address alone cannot.
    /// </summary>

    // Module ranges are resolved once, eagerly, because enumerating modules takes the
    // OS loader lock. Doing that from the sampler while a thread is suspended can
    // deadlock against a thread that holds the loader lock - the diagnostic would then
    // manufacture the very hang it is meant to observe.
    private static (ulong Start, ulong End, string Name)[] _modules = SnapshotModules();

    /// <summary>
    /// Looks a host address up in the module table, refreshing it once on a miss.
    /// The table is snapshotted eagerly so the common case needs no loader access, but
    /// modules load throughout the run, so a miss must be allowed to re-read. This is
    /// only ever called from the reporting path, after the sampled thread has been
    /// resumed - never while a thread is suspended.
    /// </summary>
    private static bool TryResolveModule(ulong address, out string description)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            foreach (var module in _modules)
            {
                if (address >= module.Start && address < module.End)
                {
                    description = $"{module.Name}+0x{address - module.Start:X}";
                    return true;
                }
            }

            if (attempt == 0)
            {
                _modules = SnapshotModules();
            }
        }

        description = string.Empty;
        return false;
    }

    private static (ulong Start, ulong End, string Name)[] SnapshotModules()
    {
        try
        {
            var list = new System.Collections.Generic.List<(ulong, ulong, string)>();
            foreach (System.Diagnostics.ProcessModule module in
                System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                var start = (ulong)module.BaseAddress.ToInt64();
                list.Add((start, start + (ulong)module.ModuleMemorySize, module.ModuleName));
            }

            return list.ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<(ulong, ulong, string)>();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Array.Empty<(ulong, ulong, string)>();
        }
    }

    public static string DescribeAddress(ulong address)
    {
        if (TryResolveModule(address, out var owner))
        {
            return $"region=module:{owner}";
        }

        var info = default(MemoryBasicInformation);
        if (VirtualQuery((IntPtr)address, ref info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
        {
            return "region=<unqueryable>";
        }

        return $"region=private base=0x{info.AllocationBase:X16} size=0x{info.RegionSize:X} " +
            $"state=0x{info.State:X} type=0x{info.Type:X} prot=0x{info.Protect:X} " +
            $"offset=0x{address - info.AllocationBase:X} {DescribeLock()} {DescribeGuardedCall(address)} win[rip-0x40..+0x40]={ReadCode(address)}";
    }



    /// <summary>
    /// Reads the spin-lock's bookkeeping without touching the loader: the owner thread id
    /// at [r9], the recursion count at [r9+8] and the lock word at [rcx].
    /// </summary>
    private static unsafe string DescribeLock()
    {
        try
        {
            var owner = _lastR9 != 0 ? *(ulong*)_lastR9 : 0;
            var depth = _lastR9 != 0 ? *(uint*)(_lastR9 + 8) : 0;
            var word = _lastRcx != 0 ? *(ulong*)_lastRcx : 0;
            return $"r9=0x{_lastR9:X16} owner_tid={owner} recursion={depth} rcx=0x{_lastRcx:X16} lockword=0x{word:X16}";
        }
        catch (AccessViolationException)
        {
            return $"r9=0x{_lastR9:X16} rcx=0x{_lastRcx:X16} <unreadable>";
        }
    }


    /// <summary>
    /// Resolves the target of the stub's guarded call. The stub loads it with
    /// <c>movabs rax, imm64</c> at rip+0x21, so the immediate sits at rip+0x23. Naming the
    /// owning module is what identifies which component emitted the page.
    /// </summary>
    private static unsafe string DescribeGuardedCall(ulong rip)
    {
        try
        {
            if (*(byte*)(rip + 0x21) != 0x48 || *(byte*)(rip + 0x22) != 0xB8)
            {
                return "guarded=<not-a-movabs>";
            }

            var target = *(ulong*)(rip + 0x23);
            return TryResolveModule(target, out var owner)
                ? $"guarded=0x{target:X16} module:{owner}"
                : $"guarded=0x{target:X16} module:<none>";
        }
        catch (AccessViolationException)
        {
            return "guarded=<unreadable>";
        }
    }

    /// <summary>Reads the instruction bytes at a host address, for identifying generated stubs.</summary>
    private static unsafe string ReadCode(ulong address)
    {
        try
        {
            var builder = new System.Text.StringBuilder(400);
            var bytes = (byte*)(address - 0x40);
            for (var index = 0; index < 0x80; index++)
            {
                builder.Append(bytes[index].ToString("X2")).Append(' ');
            }

            return builder.ToString().TrimEnd();
        }
        catch (AccessViolationException)
        {
            return "<unreadable>";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(IntPtr address, ref MemoryBasicInformation buffer, nuint length);

    /// <summary>
    /// A crude return-address trace for the thread <see cref="TryReadRip"/> last sampled.
    /// </summary>
    /// <remarks>
    /// Walking real unwind data would be better, but a blocked thread's stack is mostly
    /// saved return addresses, and resolving each stack word against the module table
    /// picks them out well enough to say <em>which</em> wait a thread is sitting in - the
    /// one thing a bare rip inside ntdll cannot tell you. Sampled after the thread has
    /// resumed, so the words may be stale; that is acceptable for a diagnostic and is far
    /// safer than reading memory while the owner is suspended.
    /// </remarks>
    public static unsafe string DescribeStack(int maxFrames = 8, int wordsToScan = 512)
    {
        var rsp = _lastRsp;
        if (rsp == 0)
        {
            return "<no stack>";
        }

        var frames = new System.Collections.Generic.List<string>();
        var seen = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < wordsToScan && frames.Count < maxFrames; i++)
        {
            ulong word;
            try
            {
                word = *(ulong*)(rsp + (ulong)(i * 8));
            }
            catch
            {
                break;
            }

            if (word < 0x10000 || !TryResolveModule(word, out var description))
            {
                continue;
            }

            if (seen.Add(description))
            {
                frames.Add(description);
            }
        }

        return frames.Count == 0 ? "<no resolvable frames>" : string.Join(" <- ", frames);
    }

    public static unsafe ulong? TryReadRip(uint nativeThreadId)
    {
        var handle = OpenThread(ThreadGetContext | ThreadSuspendResume | ThreadQueryInformation, false, nativeThreadId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // Suspend for the shortest possible window: capture and resume before doing
            // any formatting or I/O, so a thread holding a runtime lock is not held.
            if (SuspendThread(handle) == unchecked((uint)-1))
            {
                return null;
            }

            try
            {
                // The AMD64 CONTEXT record must be 16-byte aligned or GetThreadContext
                // fails outright; a struct on the managed stack carries no such
                // guarantee, which is why the first version reported <unavailable> for
                // every thread.
                var buffer = NativeMemory.AlignedAlloc(ContextSize, 16);
                try
                {
                    NativeMemory.Clear(buffer, ContextSize);
                    *(uint*)((byte*)buffer + ContextFlagsOffset) = ContextControl;
                    if (!GetThreadContext(handle, (IntPtr)buffer))
                    {
                        return null;
                    }

                    _lastR9 = *(ulong*)((byte*)buffer + R9Offset);
                    _lastRcx = *(ulong*)((byte*)buffer + RcxOffset);
                    _lastRsp = *(ulong*)((byte*)buffer + RspOffset);
                    return *(ulong*)((byte*)buffer + RipOffset);
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
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
