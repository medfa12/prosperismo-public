// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Prosperismo.Logging;

/// <summary>
/// Dispatches each <see cref="LogEntry"/> to every child sink.
/// Exceptions thrown by a child are swallowed so one failing sink
/// (e.g. a closed file) cannot silence the others.
/// </summary>
public sealed class CompositeLogSink : IProsperismoLogSink, IDisposable
{
    private readonly IProsperismoLogSink[] _sinks;
    private bool _disposed;

    public CompositeLogSink(params IProsperismoLogSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        foreach (var sink in sinks)
        {
            ArgumentNullException.ThrowIfNull(sink, nameof(sinks));
        }

        _sinks = sinks;
    }

    public IReadOnlyList<IProsperismoLogSink> Sinks => _sinks;

    public void Write(in LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(in entry);
            }
            catch
            {
                // A broken sink must not prevent the remaining sinks from logging.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var sink in _sinks)
        {
            if (sink is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
