// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace Prosperismo.HLE;

public readonly record struct GuestThreadStartRequest(
    ulong ThreadHandle,
    ulong EntryPoint,
    ulong Argument,
    ulong AttributeAddress,
    string Name,
    int Priority,
    ulong AffinityMask);

public readonly record struct GuestThreadSnapshot(
    ulong ThreadHandle,
    string Name,
    string State,
    long ImportCount,
    string? LastImportNid,
    ulong LastReturnRip,
    string? BlockReason);

/// <summary>
/// Continuation state for a blocked guest thread, replacing the closure pair a blocking
/// wait used to allocate. TryWake runs under the scheduler's guest-thread gate and
/// returns true when the waiter has a final result and the thread should be re-readied;
/// false leaves it parked. Resume runs later on the woken thread outside that gate, and
/// its return value becomes the guest's RAX for the resumed call.
/// </summary>
public interface IGuestThreadBlockWaiter
{
    int Resume();

    bool TryWake();
}

public interface IGuestThreadScheduler
{
    bool SupportsGuestContextTransfer { get; }

    /// <summary>
    /// True when the host thread running this call has a queued guest exception,
    /// including primary/external executors whose handle
    /// <see cref="GuestThreadExecution"/> never sees.
    /// </summary>
    /// <remarks>
    /// Default false so schedulers that model no external executor need not implement it.
    /// </remarks>
    bool CurrentHostThreadHasPendingGuestException() => false;

    /// <summary>
    /// Runs this thread's queued guest exception handler now, without leaving the
    /// HLE export that is currently on the stack.
    /// </summary>
    /// <remarks>
    /// Delivery normally happens only after an export returns, so a thread parked in a
    /// blocking wait can hold an undeliverable signal indefinitely while whoever raised
    /// it waits for an acknowledgement. FreeBSD runs the handler on the blocked thread
    /// and resumes the wait, and Kyty does the same inside its condition wait - it drops
    /// the cond lock, dispatches, retakes the lock and keeps waiting
    /// (inspiration/KytyPS5/src/kernel/pthread.cpp:3043-3054).
    ///
    /// CALLERS MUST NOT HOLD A LOCK. This runs guest code, which can re-enter the HLE
    /// layer and take the very gate the caller is holding.
    /// </remarks>
    bool TryDispatchPendingGuestExceptionInPlace(CpuContext context) => false;

    /// <summary>
    /// Associates a pthread identity created on the primary guest executor
    /// with its live CPU context. Primary execution does not pass through
    /// TryStartThread, but kernel exception delivery must still be able to
    /// target it.
    /// </summary>
    void RegisterGuestThreadContext(ulong threadHandle, CpuContext context);

    bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error);

    bool TryJoinThread(
        CpuContext callerContext,
        ulong threadHandle,
        out ulong returnValue,
        out string? error);

    void Pump(CpuContext callerContext, string reason);

    int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue);

    /// <summary>
    /// Applies a new guest scheduling priority to a live thread, mapping it
    /// onto the host thread if one is running. Returns false when the thread
    /// handle is unknown.
    /// </summary>
    bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority);

    /// <summary>
    /// Records a new affinity mask for a guest thread and re-applies it to
    /// the host thread where the platform supports it.
    /// </summary>
    bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask);

    IReadOnlyList<GuestThreadSnapshot> SnapshotThreads();

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out string? error);

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong arg2,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out ulong returnValue,
        out string? error);

    bool TryCallGuestContinuation(
        CpuContext callerContext,
        GuestCpuContinuation continuation,
        string reason,
        out string? error);

    /// <summary>
    /// Asynchronously invokes an installed kernel exception handler as the
    /// target guest thread. This is used by IL2CPP's stop-the-world collector:
    /// the handler acknowledges suspension and may remain blocked until the
    /// collecting thread resumes it.
    /// </summary>
    bool TryRaiseGuestException(
        CpuContext callerContext,
        ulong threadHandle,
        ulong handler,
        int exceptionType,
        out string? error);

    /// <summary>
    /// True when a raised exception is queued for <paramref name="threadHandle"/> and has
    /// not been delivered yet.
    /// </summary>
    /// <remarks>
    /// A queued exception is only delivered where the target crosses an HLE boundary, so
    /// any blocking wait an HLE export performs has to consult this and give up when it
    /// answers true. Otherwise the signal cannot reach a thread that is blocked, which is
    /// the one situation the signal exists for.
    /// </remarks>
    bool HasPendingGuestException(ulong threadHandle);
}

public readonly record struct GuestImportCallFrame(
    bool IsValid,
    ulong ReturnRip,
    ulong ResumeRsp,
    ulong ReturnSlotAddress);

public readonly record struct GuestCpuContinuation(
    ulong Rip,
    ulong Rsp,
    ulong ReturnSlotAddress,
    ulong Rflags,
    ulong FsBase,
    ulong GsBase,
    ulong Rax,
    ulong Rcx,
    ulong Rdx,
    ulong Rbx,
    ulong Rbp,
    ulong Rsi,
    ulong Rdi,
    ulong R8,
    ulong R9,
    ulong R10,
    ulong R11,
    ulong R12,
    ulong R13,
    ulong R14,
    ulong R15,
    ushort FpuControlWord,
    uint Mxcsr,
    bool RestoreFullFpuState);

public static class GuestThreadExecution
{
    private sealed class DelegateGuestThreadBlockWaiter : IGuestThreadBlockWaiter
    {
        private readonly Func<int> _resume;
        private readonly Func<bool> _tryWake;

        public DelegateGuestThreadBlockWaiter(Func<int> resume, Func<bool> tryWake)
        {
            _resume = resume;
            _tryWake = tryWake;
        }

        public int Resume() => _resume();

        public bool TryWake() => _tryWake();
    }

    [ThreadStatic]
    private static ulong _currentGuestThreadHandle;

    [ThreadStatic]
    private static ulong _currentFiberAddress;

    [ThreadStatic]
    private static string? _pendingBlockReason;

    [ThreadStatic]
    private static bool _pendingBlockContinuationValid;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingBlockContinuation;

    [ThreadStatic]
    private static string? _pendingBlockWakeKey;

    [ThreadStatic]
    private static IGuestThreadBlockWaiter? _pendingBlockWaiter;

    [ThreadStatic]
    private static long _pendingBlockDeadlineTimestamp;

    [ThreadStatic]
    private static bool _pendingEntryExit;

    [ThreadStatic]
    private static ulong _pendingEntryExitValue;

    [ThreadStatic]
    private static string? _pendingEntryExitReason;

    [ThreadStatic]
    private static bool _pendingContextTransfer;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingContextTransferTarget;

    [ThreadStatic]
    private static bool _hasCurrentImportCallFrame;

    [ThreadStatic]
    private static ulong _currentImportReturnRip;

    [ThreadStatic]
    private static ulong _currentImportResumeRsp;

    [ThreadStatic]
    private static ulong _currentImportReturnSlotAddress;

    public static IGuestThreadScheduler? Scheduler { get; set; }

    /// <summary>
    /// Fired when a guest thread is torn down without a clean pthread_exit
    /// (e.g. TBB execute-AV → worker_abort). Libs use this to abandon mutexes.
    /// </summary>
    public static event Func<ulong, string, int>? GuestThreadAbandoned;

    public static int NotifyGuestThreadAbandoned(ulong threadHandle, string reason)
    {
        if (threadHandle == 0 || GuestThreadAbandoned is null)
        {
            return 0;
        }

        try
        {
            return GuestThreadAbandoned.Invoke(threadHandle, reason);
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsGuestThread => _currentGuestThreadHandle != 0;

    public static ulong CurrentGuestThreadHandle => _currentGuestThreadHandle;

    /// <summary>
    /// True when this thread has a raised exception waiting to be delivered.
    /// </summary>
    /// <remarks>
    /// Blocking HLE waits poll this so a signal can interrupt them the way it interrupts
    /// a blocked syscall on FreeBSD. Returning early from a wait is safe for any caller
    /// that follows the POSIX contract, because every such wait may already wake
    /// spuriously and the caller re-checks its predicate.
    /// </remarks>
    public static bool CurrentThreadHasPendingGuestException()
    {
        if (Scheduler is not { } scheduler)
        {
            return false;
        }

        var handle = _currentGuestThreadHandle;
        if (handle != 0 && scheduler.HasPendingGuestException(handle))
        {
            return true;
        }

        // A primary/external executor runs guest code on its own host thread without
        // going through EnterGuestThread, so _currentGuestThreadHandle is zero for it
        // and the check above is blind. Exception delivery already falls back to the
        // backend's external handle; the poll has to use the same fallback or such a
        // thread waits forever holding an undeliverable signal.
        return scheduler.CurrentHostThreadHasPendingGuestException();
    }

    public static ulong CurrentFiberAddress => _currentFiberAddress;

    public static ulong EnterGuestThread(ulong threadHandle)
    {
        var previous = _currentGuestThreadHandle;
        _currentGuestThreadHandle = threadHandle;
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
        return previous;
    }

    public static void RestoreGuestThread(ulong previousThreadHandle)
    {
        _currentGuestThreadHandle = previousThreadHandle;
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
    }

    public static ulong EnterFiber(ulong fiberAddress)
    {
        var previous = _currentFiberAddress;
        _currentFiberAddress = fiberAddress;
        return previous;
    }

    public static void RestoreFiber(ulong previousFiberAddress)
    {
        _currentFiberAddress = previousFiberAddress;
    }

    public static bool RequestCurrentThreadBlock(string reason) => RequestCurrentThreadBlock(null, reason);

    public static bool RequestCurrentThreadBlock(
        CpuContext? context,
        string reason,
        string? wakeKey = null,
        IGuestThreadBlockWaiter? waiter = null,
        long blockDeadlineTimestamp = 0)
    {
        if (!IsGuestThread)
        {
            return false;
        }

        _pendingBlockReason = string.IsNullOrWhiteSpace(reason) ? "guest_thread_blocked" : reason;
        _pendingBlockWakeKey = string.IsNullOrWhiteSpace(wakeKey) ? _pendingBlockReason : wakeKey;
        _pendingBlockWaiter = waiter;
        _pendingBlockDeadlineTimestamp = blockDeadlineTimestamp;
        if (context is not null && TryCaptureCurrentBlockContinuation(context, out var continuation))
        {
            _pendingBlockContinuation = continuation;
            _pendingBlockContinuationValid = true;
        }
        else
        {
            _pendingBlockContinuation = default;
            _pendingBlockContinuationValid = false;
        }

        if (GuestSyncTrace.Enabled)
        {
            GuestSyncTrace.Log(
                $"park thread='?'/id=0x{_currentGuestThreadHandle:X16} prim=wakeKey('{_pendingBlockWakeKey}') " +
                $"reason='{_pendingBlockReason}' continuation={_pendingBlockContinuationValid} -> block_requested");
        }

        return true;
    }

    // Compatibility bridge for exports that still describe blocked work as a
    // resume/wake delegate pair. New hot paths should provide an
    // IGuestThreadBlockWaiter directly to avoid allocating closures.
    public static bool RequestCurrentThreadBlock(
        CpuContext? context,
        string reason,
        string? wakeKey,
        Func<int> resumeHandler,
        Func<bool> wakeHandler,
        long blockDeadlineTimestamp = 0) =>
        RequestCurrentThreadBlock(
            context,
            reason,
            wakeKey,
            new DelegateGuestThreadBlockWaiter(resumeHandler, wakeHandler),
            blockDeadlineTimestamp);

    public static bool TryConsumeCurrentThreadBlock(out string reason)
    {
        return TryConsumeCurrentThreadBlock(out reason, out _, out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out _,
            out _,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out IGuestThreadBlockWaiter? waiter)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out wakeKey,
            out waiter,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out IGuestThreadBlockWaiter? waiter,
        out long blockDeadlineTimestamp)
    {
        reason = _pendingBlockReason ?? string.Empty;
        if (string.IsNullOrEmpty(reason))
        {
            continuation = default;
            hasContinuation = false;
            wakeKey = string.Empty;
            waiter = null;
            blockDeadlineTimestamp = 0;
            return false;
        }

        continuation = _pendingBlockContinuation;
        hasContinuation = _pendingBlockContinuationValid;
        wakeKey = _pendingBlockWakeKey ?? reason;
        waiter = _pendingBlockWaiter;
        blockDeadlineTimestamp = _pendingBlockDeadlineTimestamp;
        _pendingBlockReason = null;
        _pendingBlockContinuation = default;
        _pendingBlockContinuationValid = false;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        return true;
    }

    public static long ComputeDeadlineTimestamp(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return Stopwatch.GetTimestamp();
        }

        var ticks = timeout.TotalSeconds >= long.MaxValue / (double)Stopwatch.Frequency
            ? long.MaxValue
            : (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        if (long.MaxValue - now <= ticks)
        {
            return long.MaxValue;
        }

        return now + Math.Max(1, ticks);
    }

    private static bool TryCaptureCurrentBlockContinuation(CpuContext context, out GuestCpuContinuation continuation)
    {
        if (!TryGetCurrentImportCallFrame(out var frame) ||
            frame.ReturnRip < 65536 ||
            frame.ResumeRsp == 0 ||
            frame.ReturnSlotAddress == 0)
        {
            continuation = default;
            return false;
        }

        continuation = new GuestCpuContinuation(
            frame.ReturnRip,
            frame.ResumeRsp,
            frame.ReturnSlotAddress,
            context.Rflags,
            context.FsBase,
            context.GsBase,
            0,
            context[CpuRegister.Rcx],
            context[CpuRegister.Rdx],
            context[CpuRegister.Rbx],
            context[CpuRegister.Rbp],
            context[CpuRegister.Rsi],
            context[CpuRegister.Rdi],
            context[CpuRegister.R8],
            context[CpuRegister.R9],
            context[CpuRegister.R10],
            context[CpuRegister.R11],
            context[CpuRegister.R12],
            context[CpuRegister.R13],
            context[CpuRegister.R14],
            context[CpuRegister.R15],
            context.FpuControlWord,
            context.Mxcsr,
            RestoreFullFpuState: false);
        return true;
    }

    public static void RequestCurrentEntryExit(string reason, int status)
    {
        RequestCurrentEntryExit(reason, unchecked((ulong)(long)status));
    }

    public static void RequestCurrentEntryExit(string reason, ulong value)
    {
        _pendingEntryExit = true;
        _pendingEntryExitValue = value;
        _pendingEntryExitReason = string.IsNullOrWhiteSpace(reason) ? "guest_entry_exit" : reason;
    }

    public static bool TryConsumeCurrentEntryExit(out ulong value, out string reason)
    {
        value = _pendingEntryExitValue;
        reason = _pendingEntryExitReason ?? string.Empty;
        if (!_pendingEntryExit)
        {
            return false;
        }

        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        return true;
    }

    public static void RequestCurrentContextTransfer(GuestCpuContinuation target)
    {
        _pendingContextTransferTarget = target;
        _pendingContextTransfer = true;
    }

    public static bool TryConsumeCurrentContextTransfer(out GuestCpuContinuation target)
    {
        target = _pendingContextTransferTarget;
        if (!_pendingContextTransfer)
        {
            return false;
        }

        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        return true;
    }

    public static GuestImportCallFrame EnterImportCallFrame(
        ulong returnRip,
        ulong resumeRsp,
        ulong returnSlotAddress)
    {
        var previous = new GuestImportCallFrame(
            _hasCurrentImportCallFrame,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        _hasCurrentImportCallFrame = true;
        _currentImportReturnRip = returnRip;
        _currentImportResumeRsp = resumeRsp;
        _currentImportReturnSlotAddress = returnSlotAddress;
        return previous;
    }

    public static void RestoreImportCallFrame(GuestImportCallFrame previous)
    {
        _hasCurrentImportCallFrame = previous.IsValid;
        _currentImportReturnRip = previous.ReturnRip;
        _currentImportResumeRsp = previous.ResumeRsp;
        _currentImportReturnSlotAddress = previous.ReturnSlotAddress;
    }

    public static bool TryGetCurrentImportCallFrame(out GuestImportCallFrame frame)
    {
        if (!_hasCurrentImportCallFrame)
        {
            frame = default;
            return false;
        }

        frame = new GuestImportCallFrame(
            true,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        return true;
    }
}
