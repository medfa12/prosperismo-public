// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Counts, per HLE export, how often a call told the guest "done" while leaving
/// nothing behind that the guest could observe.
///
/// <para>A stub that returns success and does nothing is the most expensive kind
/// of wrong: the guest proceeds on a false premise, no error is raised anywhere,
/// and the failure surfaces thousands of calls later as a black screen with
/// nothing to grep for. Static analysis has repeatedly failed to find these —
/// "does this method mutate anything" is undecidable in practice and the
/// heuristics misclassify hundreds of exports. So this measures instead: during a
/// real boot, every export call is observed, and the ones that returned success
/// while changing nothing observable are counted and ranked.</para>
///
/// <para>A call is <c>INERT</c> when all three hold:</para>
/// <list type="number">
///   <item>the value the guest saw was not an error (non-negative), and</item>
///   <item>zero bytes of guest memory were written on this thread during it, and</item>
///   <item>no CPU context state was written other than RAX.</item>
/// </list>
///
/// no-ops exist (see <see cref="HleVerifiedNoOps"/>, which moves them into their
/// own section of the report), and some honest exports have only host-side
/// effects — an allocator that reserves a range, a call that queues work on
/// another thread — which are invisible from here by construction. The ranking
/// exists to answer one question: which lie is being told most often, so a human
/// can go look at that one first.</para>
///
/// <para>Guest writes are observed through <see cref="GuestWriteWatch.Check"/>,
/// which the memory implementations call after every successful managed write or
/// copy. Writes that do not pass through it are invisible to the census and can
/// make an effectful export look inert:</para>
/// <list type="bullet">
///   <item>stores executed by translated guest code (an export that runs a guest
///   callback writes through the CPU backend, not through <c>ICpuMemory</c>);</item>
///   <item>the handful of HLE sites that store straight into identity-mapped
///   guest pages — <c>KernelMemoryCompatExports</c> (the <c>new Span&lt;byte&gt;((void*)address, …)</c>
///   and <c>Buffer.MemoryCopy</c> paths), <c>KernelPthreadState</c>,
///   <c>KernelRuntimeCompatExports</c>, <c>GuestTlsTemplate</c>;</item>
///   <item>writes an export causes on another thread (GPU, audio, worker), since
///   the byte counter is per-thread so one thread's traffic cannot mask another's
///   call.</item>
/// </list>
///
/// <para>Configuration — every part is off unless asked for:</para>
/// <list type="bullet">
///   <item><c>PROSPERISMO_HLE_EFFECT_CENSUS=1</c> — enable. When unset, exports keep
///   their original delegate and nothing here is installed at all.</item>
///   <item><c>PROSPERISMO_HLE_EFFECT_CENSUS_REPORT=&lt;path&gt;</c> — write the report
///   here instead of stderr.</item>
///   <item><c>PROSPERISMO_HLE_EFFECT_CENSUS_INTERVAL=&lt;seconds&gt;</c> — also report
///   periodically, so a boot that is killed still leaves one.</item>
///   <item><c>PROSPERISMO_HLE_EFFECT_CENSUS_TRIGGER=&lt;path&gt;</c> — report whenever
///   that file appears, then delete it. A boot can be asked for a census on
///   demand with <c>touch</c>.</item>
///   <item><c>PROSPERISMO_HLE_EFFECT_CENSUS_TOP=&lt;n&gt;</c> — how many ranked rows to
///   print (default 40, <c>0</c> for all).</item>
///   no-ops to move out of the ranking; see <see cref="HleVerifiedNoOps"/>.</item>
/// </list>
/// </summary>
public static class HleEffectCensus
{
    private const string EnableVariable = "PROSPERISMO_HLE_EFFECT_CENSUS";
    private const string ReportPathVariable = "PROSPERISMO_HLE_EFFECT_CENSUS_REPORT";
    private const string IntervalVariable = "PROSPERISMO_HLE_EFFECT_CENSUS_INTERVAL";
    private const string TriggerVariable = "PROSPERISMO_HLE_EFFECT_CENSUS_TRIGGER";
    private const string LimitVariable = "PROSPERISMO_HLE_EFFECT_CENSUS_TOP";

    private const int DefaultReportRows = 40;

    private static readonly Lock Gate = new();

    private static readonly ConcurrentDictionary<string, HleExportCensusEntry> Entries =
        new(StringComparer.Ordinal);

    // Per-thread so that an export cannot be credited with — or acquitted by —
    // writes another thread happened to make while it ran.
    [ThreadStatic]
    private static long _threadGuestBytesWritten;

    private static bool _loaded;

    // Plain, not volatile, and read without the lazy-load check on the paths that
    // run per guest write: an acquire load there measured ~2ns per write against
    // ~0.3ns for a plain one, and the value is published under the lock before any
    // decorated export exists, so nothing can observe it stale in a way that
    // matters.
    private static bool _enabled;
    private static Action<string>? _sink;
    private static Timer? _timer;
    private static bool _reportersInstalled;
    private static long _lastPeriodicReportTicks;

    /// <summary>
    /// True when this boot was asked for a census. Decoration is decided once, at
    /// export-construction time, from this.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            EnsureLoaded();
            return _enabled;
        }
    }

    /// <summary>
    /// Whether guest writes need to be counted. Read once per managed guest write
    /// (through <see cref="GuestWriteWatch.Armed"/>), so it is a bare field read:
    /// the flag is resolved when the first export is decorated, which is long
    /// before any export call can be measured.
    /// </summary>
    public static bool CountsGuestWrites => _enabled;

    /// <summary>
    /// Where the report goes when no report file is configured. Defaults to
    /// stderr; hosts replace it with their log sink.
    /// </summary>
    public static void SetSink(Action<string>? sink)
    {
        lock (Gate)
        {
            _sink = sink;
        }
    }

    /// <summary>
    /// Records that <paramref name="byteCount"/> bytes of guest memory were
    /// written on this thread. Called from the guest write watch, which every
    /// memory implementation notifies after a successful write.
    /// </summary>
    public static void NoteGuestWrite(int byteCount)
    {
        if (!_enabled)
        {
            return;
        }

        _threadGuestBytesWritten += byteCount;
    }

    /// <summary>
    /// Wraps <paramref name="function"/> so its effects are counted, or returns it
    /// unchanged when the census is off. Returning the original delegate is the
    /// point: a boot that did not ask for a census must not pay for one, and this
    /// sits on the hottest path in the emulator.
    /// </summary>
    public static SysAbiFunction Decorate(SysAbiFunction function, string libraryName, string name, string nid)
    {
        ArgumentNullException.ThrowIfNull(function);

        // The call trace is independent of the census: it answers "which export was
        // entered and never returned", which the census cannot, because the census
        // only records calls that completed.
        if (ExportCallTrace.Tracks(libraryName))
        {
            var traced = function;
            var tracedLibrary = libraryName;
            var tracedName = name;
            function = context =>
            {
                ExportCallTrace.Note(tracedLibrary, tracedName, "enter");
                try
                {
                    return traced(context);
                }
                finally
                {
                    ExportCallTrace.Note(tracedLibrary, tracedName, "exit");
                }
            };
        }

        // Liveness is tracked separately from the call trace: a thread that stops
        // issuing calls is exactly the case the ring buffer cannot show, because the
        // busy threads overwrite the window before anyone notices the quiet one.
        if (StalledThreadSampler.Enabled)
        {
            var watched = function;
            var watchedName = name;
            function = context =>
            {
                StalledThreadSampler.NoteAlive(watchedName);
                return watched(context);
            };
        }

        if (!IsEnabled)
        {
            return function;
        }

        var entry = GetOrAddEntry(libraryName, nid, name);

        return context =>
        {
            var bytesBefore = _threadGuestBytesWritten;

            // Bracketing the mask attributes context writes to this call alone;
            // the caller's mask is restored (with ours folded in) below, so a
            // nested export cannot erase what its caller had already written —
            // and WasRaxWritten keeps meaning exactly what it meant before.
            var outerMask = context.ExchangeStateWriteMask(0);
            var completed = false;
            var result = 0;

            try
            {
                result = function(context);
                completed = true;
                return result;
            }
            finally
            {
                var innerMask = context.StateWriteMask;
                context.ExchangeStateWriteMask(outerMask | innerMask);
                entry.Observe(
                    completed,
                    result,
                    innerMask,
                    _threadGuestBytesWritten - bytesBefore,
                    context[CpuRegister.Rax]);
            }
        };
    }

    /// <summary>Every export observed so far, unordered.</summary>
    public static IReadOnlyList<HleExportCensusEntry> Snapshot() => Entries.Values.ToArray();

    /// <summary>
    /// Renders the ranked report. <paramref name="limit"/> caps the ranked rows;
    /// 0 means "every export".
    /// </summary>
    public static string RenderReport(string reason, int limit)
    {
        var all = Entries.Values.Where(entry => entry.Calls > 0).ToList();
        var verified = new List<HleExportCensusEntry>();
        var candidates = new List<HleExportCensusEntry>();

        foreach (var entry in all)
        {
            if (HleVerifiedNoOps.TryGet(entry.Nid, out _))
            {
                verified.Add(entry);
            }
            else
            {
                candidates.Add(entry);
            }
        }

        long totalCalls = 0, totalInert = 0, totalEffectful = 0, totalErrors = 0, totalFaulted = 0;
        foreach (var entry in all)
        {
            totalCalls += entry.Calls;
            totalInert += entry.InertCalls;
            totalEffectful += entry.EffectfulCalls;
            totalErrors += entry.ErrorCalls;
            totalFaulted += entry.FaultedCalls;
        }

        // Most-called CONSTANT-RETURN inert export first: that ordering is the
        // deliverable, and it is what someone staring at a black screen reads first.
        //
        // An export whose observed return VARIES is sorted to the bottom regardless of
        // its inert count. It wrote only RAX, but it wrote a different value each time,
        // so it is delivering a result rather than telling a constant lie — think
        // sceKernelReadTsc or a frequency getter. Ranking those at the top would put two
        // to fix working code, which is the single worst outcome for this tool.
        candidates.Sort(static (left, right) =>
        {
            var byShape = left.ReturnVaried.CompareTo(right.ReturnVaried);
            if (byShape != 0)
            {
                return byShape;
            }

            var byInert = right.InertCalls.CompareTo(left.InertCalls);
            return byInert != 0 ? byInert : right.Calls.CompareTo(left.Calls);
        });

        var builder = new StringBuilder();
        void Line(string text) => builder.Append("[HLE-CENSUS] ").AppendLine(text);

        Line($"reason={reason}  exports={all.Count}  calls={Number(totalCalls)}");
        Line("INERT = the guest was told success AND zero bytes of guest memory were written " +
             "AND no CPU context state but RAX was written.");
        Line("INERT is behaviour, not a verdict: host-side effects (allocation bookkeeping, work " +
             "queued to another thread) are invisible here, and firmware no-ops are listed separately below.");
        Line("Rows with return=var are sorted LAST: they wrote only RAX but a different value each " +
             "time, so they are returning a result, not telling a constant lie. Read the top of the " +
             "list, not the bottom.");
        Line("Blind spots: stores by translated guest code, the identity-mapped-store sites in " +
             "KernelMemoryCompatExports/KernelPthreadState/KernelRuntimeCompatExports/GuestTlsTemplate, " +
             "and writes an export causes on another thread.");
        Line($"totals: inert={Number(totalInert)} ({Percent(totalInert, totalCalls)}) " +
             $"effectful={Number(totalEffectful)} error={Number(totalErrors)} threw={Number(totalFaulted)}");

        foreach (var diagnostic in HleVerifiedNoOps.LoadDiagnostics)
        {
            Line(diagnostic);
        }

        var shown = limit > 0 ? Math.Min(limit, candidates.Count) : candidates.Count;
        Line($"ranked by inert calls ({shown} of {candidates.Count} shown):");
        builder.AppendLine(
            "[HLE-CENSUS]     # |   inert calls |   total calls | inert% | errors |     return | export");

        for (var i = 0; i < shown; i++)
        {
            var entry = candidates[i];
            builder
                .Append("[HLE-CENSUS] ")
                .Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5))
                .Append(" | ")
                .Append(Number(entry.InertCalls).PadLeft(13))
                .Append(" | ")
                .Append(Number(entry.Calls).PadLeft(13))
                .Append(" | ")
                .Append(Percent(entry.InertCalls, entry.Calls).PadLeft(6))
                .Append(" | ")
                .Append(Number(entry.ErrorCalls).PadLeft(6))
                .Append(" | ")
                .Append(entry.DescribeReturn().PadLeft(10))
                .Append(" | ")
                .Append(entry.Name)
                .Append(" (")
                .Append(entry.Nid)
                .Append(") [")
                .Append(entry.LibraryName)
                .AppendLine("]");
        }

        Line($"verified firmware no-ops, excluded from the ranking ({verified.Count} called):");
        foreach (var entry in verified.OrderByDescending(entry => entry.Calls))
        {
            HleVerifiedNoOps.TryGet(entry.Nid, out var noOp);
            builder
                .Append("[HLE-CENSUS]   ")
                .Append(Number(entry.Calls).PadLeft(13))
                .Append(" calls  ")
                .Append(entry.Name)
                .Append(" (")
                .Append(entry.Nid)
                .Append("): ")
                .AppendLine(noOp.Evidence);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders and emits the report. Returns false when the census is off, so a
    /// caller can say "nothing was measured" rather than print an empty table.
    /// </summary>
    public static bool Report(string reason)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var text = RenderReport(reason, ReadLimit());
        var path = Environment.GetEnvironmentVariable(ReportPathVariable);

        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                File.WriteAllText(path, text);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the sink: losing the report entirely because a
                // path was wrong is worse than printing it in the wrong place.
                text = $"[HLE-CENSUS] report file unwritable ({path}): {ex.Message}{Environment.NewLine}{text}";
            }
        }

        var sink = _sink;
        if (sink is not null)
        {
            try
            {
                sink(text);
                return true;
            }
            catch
            {
                // A failing sink must not propagate out of a diagnostic.
            }
        }

        Console.Error.Write(text);
        Console.Error.Flush();
        return true;
    }

    /// <summary>
    /// Turns the census on or off directly, bypassing the environment, and forgets
    /// everything counted so far. For tests and for hosts with their own settings.
    /// </summary>
    public static void SetEnabledForTesting(bool enabled)
    {
        lock (Gate)
        {
            Entries.Clear();
            _enabled = enabled;
            Volatile.Write(ref _loaded, true);
            if (enabled)
            {
                InstallReporters();
            }
        }
    }

    /// <summary>Forgets counts and the resolved configuration. For tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Entries.Clear();
            _enabled = false;
            Volatile.Write(ref _loaded, false);
            _threadGuestBytesWritten = 0;
        }
    }

    private static HleExportCensusEntry GetOrAddEntry(string libraryName, string nid, string name)
    {
        var library = string.IsNullOrWhiteSpace(libraryName) ? "?" : libraryName;
        var key = library + ":" + nid + ":" + name;

        // The entry is resolved once, when the export is constructed, and captured
        // by the returned closure — so a call costs no dictionary lookup.
        return Entries.GetOrAdd(key, _ => new HleExportCensusEntry(library, nid, name));
    }

    private static int ReadLimit()
    {
        var raw = Environment.GetEnvironmentVariable(LimitVariable);
        return !string.IsNullOrWhiteSpace(raw) &&
               int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : DefaultReportRows;
    }

    private static void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded))
        {
            return;
        }

        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            var raw = Environment.GetEnvironmentVariable(EnableVariable);
            var enabled = raw is not null && raw.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

            // Publish the flag before the latch: a reader that sees _loaded set
            // (an acquire read) is then guaranteed to see the flag it implies.
            _enabled = enabled;
            Volatile.Write(ref _loaded, true);

            if (enabled)
            {
                InstallReporters();
            }
        }
    }

    private static void InstallReporters()
    {
        if (_reportersInstalled)
        {
            return;
        }

        _reportersInstalled = true;

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Report("process-exit");

        var interval = ReadInterval();
        var trigger = Environment.GetEnvironmentVariable(TriggerVariable);
        if (interval <= TimeSpan.Zero && string.IsNullOrWhiteSpace(trigger))
        {
            return;
        }

        // A trigger file needs polling; an interval alone can tick at its own rate.
        var period = string.IsNullOrWhiteSpace(trigger)
            ? interval
            : TimeSpan.FromSeconds(1);

        _lastPeriodicReportTicks = Environment.TickCount64;
        _timer = new Timer(_ => OnTick(interval, trigger), null, period, period);
    }

    private static void OnTick(TimeSpan interval, string? trigger)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(trigger) && File.Exists(trigger))
            {
                try
                {
                    File.Delete(trigger);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Leaving the file in place would re-fire every tick, so say so
                    // once and keep going.
                    Console.Error.WriteLine($"[HLE-CENSUS] trigger file undeletable: {ex.Message}");
                }

                Report("trigger");
                _lastPeriodicReportTicks = Environment.TickCount64;
                return;
            }

            if (interval > TimeSpan.Zero &&
                Environment.TickCount64 - _lastPeriodicReportTicks >= (long)interval.TotalMilliseconds)
            {
                _lastPeriodicReportTicks = Environment.TickCount64;
                Report("interval");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HLE-CENSUS] periodic report failed: {ex.Message}");
        }
    }

    private static TimeSpan ReadInterval()
    {
        var raw = Environment.GetEnvironmentVariable(IntervalVariable);
        return !string.IsNullOrWhiteSpace(raw) &&
               double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
               seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(long part, long whole) =>
        whole == 0
            ? "-"
            : (100.0 * part / whole).ToString("F1", CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// The running tally for one export. Counters are bumped from the export call
/// itself, so everything here is sized for that: one entry per export, resolved
/// once at construction, two interlocked increments per call.
/// </summary>
public sealed class HleExportCensusEntry
{
    private long _calls;
    private long _inert;
    private long _effectful;
    private long _errors;
    private long _faulted;
    private long _guestBytesWritten;

    // Return-value shape is a plain (racy) field triple on purpose: it is a hint
    // for a human reading the ranking — "this returned the same constant every
    // one of its 40,000 calls" — and not worth an interlocked op per call.
    private int _returnSeen;
    private int _returnFirst;
    private int _returnVaried;

    internal HleExportCensusEntry(string libraryName, string nid, string name)
    {
        LibraryName = libraryName;
        Nid = nid;
        Name = name;
    }

    public string LibraryName { get; }

    public string Nid { get; }

    public string Name { get; }

    /// <summary>Every observed call, whatever it did.</summary>
    public long Calls => Volatile.Read(ref _calls);

    /// <summary>Calls that returned success and left nothing observable behind.</summary>
    public long InertCalls => Volatile.Read(ref _inert);

    /// <summary>
    /// Whether the value the guest observed ever changed between calls. An export that
    /// writes only RAX but returns a DIFFERENT value each time is delivering a result
    /// (a counter, a frequency, a handle) — it is not a constant-success shell, and
    /// ranking it as one would be a false accusation.
    /// </summary>
    public bool ReturnVaried => Volatile.Read(ref _returnVaried) != 0;

    /// <summary>Calls that returned success and wrote guest memory or context state.</summary>
    public long EffectfulCalls => Volatile.Read(ref _effectful);

    /// <summary>Calls that handed the guest a negative (error) value — honest by construction.</summary>
    public long ErrorCalls => Volatile.Read(ref _errors);

    /// <summary>Calls that threw out of the HLE implementation.</summary>
    public long FaultedCalls => Volatile.Read(ref _faulted);

    /// <summary>Total guest bytes written across all calls to this export.</summary>
    public long GuestBytesWritten => Volatile.Read(ref _guestBytesWritten);

    /// <summary>
    /// The value the guest saw, when it was always the same one; otherwise
    /// <c>var</c>. A constant return with a high inert count is the signature of a
    /// stub.
    /// </summary>
    public string DescribeReturn()
    {
        if (Volatile.Read(ref _returnSeen) == 0)
        {
            return "-";
        }

        if (Volatile.Read(ref _returnVaried) != 0)
        {
            return "var";
        }

        // Error codes are recognisable as 0x8xxxxxxx and unreadable in decimal.
        var value = Volatile.Read(ref _returnFirst);
        return value < 0
            ? "=0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture)
            : "=" + value.ToString(CultureInfo.InvariantCulture);
    }

    internal void Observe(bool completed, int returnValue, ulong stateMask, long guestBytesWritten, ulong rax)
    {
        Interlocked.Increment(ref _calls);

        if (!completed)
        {
            Interlocked.Increment(ref _faulted);
            return;
        }

        if (guestBytesWritten != 0)
        {
            Interlocked.Add(ref _guestBytesWritten, guestBytesWritten);
        }

        // What the guest actually saw: the import dispatcher copies the C# return
        // into RAX only when the export did not write RAX itself, so RAX wins when
        // it was written.
        var observed = (stateMask & CpuContext.RaxWriteBit) != 0
            ? unchecked((int)(uint)rax)
            : returnValue;

        if (Volatile.Read(ref _returnSeen) == 0)
        {
            _returnFirst = observed;
            Volatile.Write(ref _returnSeen, 1);
        }
        else if (Volatile.Read(ref _returnFirst) != observed)
        {
            Volatile.Write(ref _returnVaried, 1);
        }

        // An SCE/POSIX status is a sign-extended 32-bit value. A 64-bit result -- a
        // timestamp from sceKernelReadTsc, a size, a guest pointer -- is not, and
        // truncating it to int made bit 31 decide: a correct rdtsc was filed as 5000
        // errors purely because its high bit happened to be set. Require RAX to
        // actually look like a sign-extended int32 before calling a return an error.
        var raxIsStatusShaped =
            (stateMask & CpuContext.RaxWriteBit) == 0 ||
            unchecked((ulong)(long)(int)rax) == rax;

        if (observed < 0 && raxIsStatusShaped)
        {
            Interlocked.Increment(ref _errors);
            return;
        }

        if (guestBytesWritten == 0 && (stateMask & ~CpuContext.RaxWriteBit) == 0)
        {
            // Wrote nothing but RAX. That is only a LIE if the value never changes --
            // a constant success. An export whose contract IS to return a computed
            // value (a frequency, a counter, a handle) also writes only RAX, and
            // calling it inert would be a false accusation that sends someone to
            // "fix" correct code. The distinction is not knowable per call, so record
            // it here and let the report separate the two using _returnVaried.
            Interlocked.Increment(ref _inert);
            return;
        }

        Interlocked.Increment(ref _effectful);
    }
}
