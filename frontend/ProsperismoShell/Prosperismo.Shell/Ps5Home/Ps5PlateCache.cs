// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Media.Imaging;

namespace Prosperismo.GUI.Ps5Home;

/// <summary>
/// A small, bounded, least-recently-used cache of decoded background plates.
///
/// <para><b>Why bounded.</b> A home backdrop is a 3840x2160 BC7 image that
/// decodes to roughly 8 MB on disk and, at the half-size the plate presents,
/// 1920x1080x4 = about 8 MB in memory. Travelling a row of twenty titles with
/// an unbounded cache would pin 160 MB of bitmaps that the user will never look
/// at again; re-decoding on every step instead costs a multi-hundred-millisecond
/// BC7 pass per keypress. The cache exists so scrolling back and forth over a
/// handful of neighbouring tiles is free, and the bound exists so the row's
/// length does not decide the process's memory ceiling.</para>
///
/// <para>Entries are <see cref="Task{TResult}"/> so that two focus moves onto
/// the same title while the first decode is still running share that decode
/// rather than starting a second one.</para>
/// </summary>
public sealed class Ps5PlateCache
{
    /// <summary>
    /// Plates kept alive. Sized for the focused tile plus its neighbours in
    /// either direction, which is the working set of a row being scrolled; at
    /// roughly 8 MB a plate this is a ceiling of about 48 MB.
    /// </summary>
    public const int DefaultCapacity = 6;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index =
        new(StringComparer.OrdinalIgnoreCase);

    // Most recently used at the front.
    private readonly LinkedList<Entry> _order = new();

    /// <summary>Builds a cache holding at most <paramref name="capacity"/> plates.</summary>
    /// <param name="capacity">Maximum retained plates; must be positive.</param>
    public Ps5PlateCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Maximum number of plates retained.</summary>
    public int Capacity { get; }

    /// <summary>Plates currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    /// <summary>
    /// Returns the decode for <paramref name="key"/>, starting one with
    /// <paramref name="factory"/> if it is not already cached, and marks it most
    /// recently used. Evicting past <see cref="Capacity"/> drops the least
    /// recently used entry; its bitmap is left to the garbage collector rather
    /// than disposed, because a plate evicted mid-fade may still be on screen.
    /// </summary>
    /// <param name="key">Absolute path of the plate file.</param>
    /// <param name="factory">Starts a decode when the key is not cached.</param>
    public Task<Bitmap?> GetOrAdd(string key, Func<string, Task<Bitmap?>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Plate;
            }
        }

        // Started outside the lock so a synchronous factory cannot deadlock and
        // a slow one cannot block another title's lookup. A duplicate start is
        // possible under a race; the loser's task is dropped below.
        var started = factory(key);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var raced))
            {
                _order.Remove(raced);
                _order.AddFirst(raced);
                return raced.Value.Plate;
            }

            var node = _order.AddFirst(new Entry(key, started));
            _index[key] = node;

            while (_index.Count > Capacity)
            {
                var evicted = _order.Last;
                if (evicted is null)
                {
                    break;
                }

                _order.RemoveLast();
                _index.Remove(evicted.Value.Key);
            }

            return started;
        }
    }

    /// <summary>Drops every retained plate.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
        }
    }

    /// <summary>Keys currently retained, most recently used first.</summary>
    public IReadOnlyList<string> KeysMostRecentFirst()
    {
        lock (_gate)
        {
            return _order.Select(e => e.Key).ToArray();
        }
    }

    private readonly record struct Entry(string Key, Task<Bitmap?> Plate);
}
