// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Prosperismo.HLE.Diagnostics;

/// <summary>
/// Per-thread entry/exit trace for every HLE export, used to find a call that is
/// entered and never returns.
/// </summary>
/// <remarks>
/// <para>
/// The thread snapshot reports a parked guest thread by mapping its recorded rip to an
/// import stub, but that rip is only updated at import boundaries, so a stale rip is
/// indistinguishable from a genuinely wedged call. This trace settles it: the last
/// record for a thread is an <c>enter</c> with no matching <c>exit</c> exactly when
/// that export never returned.
/// </para>
/// <para>
/// Records go into a fixed in-memory ring and are flushed by a background thread on an
/// interval. The first version appended to a file under a lock on every call, which cost
/// so much that a 170 s boot reached only ~640k calls and never got as far as audio
/// init - it changed the timing enough to hide the very stall it was meant to catch.
/// A ring plus periodic flush keeps the hot path to one array store and an increment.
/// </para>
/// <para>
/// Writes bypass <see cref="Console"/> deliberately: Console.Out/Error are synchronized
/// writers shared with every logging site in the emulator, so if a wedged Console lock
/// is among the candidates, tracing through Console would deadlock the tracer.
/// </para>
/// Enable with <c>PROSPERISMO_LOG_EXPORT_CALLS=1</c>. Destination defaults to
/// <c>export-calls.log</c> (<c>PROSPERISMO_LOG_EXPORT_CALLS_PATH</c>); the ring holds the
/// last <c>PROSPERISMO_LOG_EXPORT_CALLS_RING</c> records (default 65536) and is rewritten
/// every <c>PROSPERISMO_LOG_EXPORT_CALLS_FLUSH_MS</c> milliseconds (default 2000).
/// Restrict volume to one library with <c>PROSPERISMO_LOG_EXPORT_CALLS_LIB</c>.
/// </remarks>
public static class ExportCallTrace
{
    public const string EnableVariable = "PROSPERISMO_LOG_EXPORT_CALLS";
    public const string PathVariable = "PROSPERISMO_LOG_EXPORT_CALLS_PATH";
    public const string LibraryVariable = "PROSPERISMO_LOG_EXPORT_CALLS_LIB";
    public const string RingVariable = "PROSPERISMO_LOG_EXPORT_CALLS_RING";
    public const string FlushVariable = "PROSPERISMO_LOG_EXPORT_CALLS_FLUSH_MS";

    private readonly record struct Record(long Timestamp, int ThreadId, string Library, string Export, string Phase);

    private static readonly bool _enabled = string.Equals(
        Environment.GetEnvironmentVariable(EnableVariable),
        "1",
        StringComparison.Ordinal);

    private static readonly string _path =
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured
            ? configured
            : "export-calls.log";

    private static readonly string? _libraryFilter =
        Environment.GetEnvironmentVariable(LibraryVariable) is { Length: > 0 } library
            ? library
            : null;

    private static readonly int _ringSize = ReadInt(RingVariable, 65536, 1024, 1 << 22);
    private static readonly int _flushMs = ReadInt(FlushVariable, 2000, 100, 60_000);

    private static readonly Record[] _ring = _enabled ? new Record[_ringSize] : Array.Empty<Record>();
    private static long _next = -1;

    /// <summary>True when the trace is on. Check before building any argument string.</summary>
    public static bool Enabled => _enabled;

    static ExportCallTrace()
    {
        if (!_enabled)
        {
            return;
        }

        var flusher = new Thread(FlushLoop)
        {
            IsBackground = true,
            Name = "export-call-trace-flush",
        };
        flusher.Start();
    }

    /// <summary>True when this export should be traced, honouring the library filter.</summary>
    public static bool Tracks(string libraryName) =>
        _enabled &&
        (_libraryFilter is null ||
         libraryName.Contains(_libraryFilter, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Records one event. Hot path: a timestamp, an interlocked increment and one array
    /// store. Deliberately allocation-free and lock-free so it does not reshape the
    /// timing of the run being observed.
    /// </summary>
    public static void Note(string libraryName, string export, string phase)
    {
        if (!_enabled)
        {
            return;
        }

        var slot = Interlocked.Increment(ref _next);
        _ring[(int)((ulong)slot % (ulong)_ringSize)] = new Record(
            System.Diagnostics.Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId,
            libraryName,
            export,
            phase);
    }

    private static void FlushLoop()
    {
        while (true)
        {
            Thread.Sleep(_flushMs);
            try
            {
                Dump();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Rewrites the destination with the ring's contents, oldest first.</summary>
    public static void Dump()
    {
        if (!_enabled)
        {
            return;
        }

        var last = Interlocked.Read(ref _next);
        if (last < 0)
        {
            return;
        }

        var count = (int)Math.Min(last + 1, _ringSize);
        var first = last - count + 1;
        var builder = new StringBuilder(count * 48);
        for (var index = first; index <= last; index++)
        {
            var record = _ring[(int)((ulong)index % (ulong)_ringSize)];
            if (record.Library is null)
            {
                continue;
            }

            builder.Append(record.Timestamp)
                .Append(" tid=")
                .Append(record.ThreadId)
                .Append(' ')
                .Append(record.Phase)
                .Append(' ')
                .Append(record.Library)
                .Append(':')
                .Append(record.Export)
                .Append('\n');
        }

        File.WriteAllText(_path, builder.ToString());
    }

    private static int ReadInt(string variable, int fallback, int min, int max)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable(variable), out var parsed))
        {
            return Math.Clamp(parsed, min, max);
        }

        return fallback;
    }
}
