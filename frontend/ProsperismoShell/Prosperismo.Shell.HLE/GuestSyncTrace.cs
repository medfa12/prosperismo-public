// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.HLE;

/// <summary>
/// Shared, env-gated runtime tracing for the guest kernel synchronization HLE
/// (semaphores, event flags, POSIX sems, event queues, pthread cond/mutex/rwlock)
/// and the block-in-place park/unpark core. Enabled by <c>PROSPERISMO_LOG_SYNC=1</c>.
///
/// Every trace site MUST guard on <see cref="Enabled"/> before building its
/// interpolated line, so that with the flag off there is no string allocation,
/// no environment read, and byte-identical default behavior. Lines are written
/// to stderr as <c>[LOADER][SYNC] &lt;op&gt; thread='&lt;name&gt;'/id=&lt;id&gt;
/// prim=&lt;handle-or-addr&gt;('&lt;name&gt;') &lt;detail&gt; -&gt; &lt;result&gt;</c> so a
/// never-delivered signal or a lost wakeup shows up as a wait/wait_block line
/// with no matching wake/unpark for the same primitive.
/// </summary>
public static class GuestSyncTrace
{
    /// <summary>
    /// Read once at type-init. Off by default; when off every call site's guard
    /// short-circuits and the trace is allocation-free.
    /// </summary>
    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("PROSPERISMO_LOG_SYNC"), "1", StringComparison.Ordinal);

    /// <summary>
    /// Emit a single sync trace line. Callers pass everything after the tag,
    /// starting with the op token. Guard on <see cref="Enabled"/> at the call
    /// site; this method also re-checks so a stray unguarded call is still free
    /// when tracing is off.
    /// </summary>
    public static void Log(string line)
    {
        if (Enabled)
        {
            Console.Error.WriteLine("[LOADER][SYNC] " + line);
        }
    }
}
