// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace Prosperismo.GUI.Controls;

/// <summary>The four edges a focus region can be left through.</summary>
public enum ShellFocusDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// One named focus region in a <see cref="ShellFocusGraph"/>.
///
/// This mirrors the home shell's per-region edge contract: each region
/// declares whether focus may leave through a given edge at all
/// (<c>canMove{Left,Right,Up,Down}</c>) and, when it may, the *name* of the
/// region focus lands in (<c>{dir}Candidate</c>). An edge with no candidate,
/// or with its <c>canMove</c> flag left false, is clamped — the shell has no
/// global focus wrap.
///
/// <see cref="LastFocusedItem"/> implements the shell's
/// <c>focusInBehavior: lastFocusedItem</c>: re-entering a region restores the
/// item that was focused when it was last left, not the first one.
/// </summary>
public sealed class ShellFocusRegion
{
    public ShellFocusRegion(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>Region name, e.g. <c>tile-item-focus-layer</c>.</summary>
    public string Name { get; }

    public bool CanMoveLeft { get; init; }

    public bool CanMoveRight { get; init; }

    public bool CanMoveUp { get; init; }

    public bool CanMoveDown { get; init; }

    /// <summary>Region entered when focus leaves through the left edge.</summary>
    public string? LeftCandidate { get; init; }

    /// <summary>Region entered when focus leaves through the right edge.</summary>
    public string? RightCandidate { get; init; }

    /// <summary>Region entered when focus leaves through the top edge.</summary>
    public string? UpCandidate { get; init; }

    /// <summary>Region entered when focus leaves through the bottom edge.</summary>
    public string? DownCandidate { get; init; }

    /// <summary>How many focusable items the region currently holds. A region
    /// with none cannot be entered.</summary>
    public int ItemCount { get; set; }

    /// <summary>The item restored on re-entry (<c>focusInBehavior</c>).</summary>
    public int LastFocusedItem { get; set; }

    internal (bool CanMove, string? Candidate) Edge(ShellFocusDirection direction) => direction switch
    {
        ShellFocusDirection.Left => (CanMoveLeft, LeftCandidate),
        ShellFocusDirection.Right => (CanMoveRight, RightCandidate),
        ShellFocusDirection.Up => (CanMoveUp, UpCandidate),
        _ => (CanMoveDown, DownCandidate),
    };
}

/// <summary>
/// A named-region focus graph with clamp semantics, the model the home shell
/// uses instead of purely geometric spatial navigation. Movement between
/// regions is by explicit named candidate; every other edge is a hard stop.
///
/// The graph is pure state — no Avalonia types — so the navigation contract is
/// testable headless and the host is free to decide what "focusing item N of
/// region R" looks like on screen.
/// </summary>
public sealed class ShellFocusGraph
{
    private readonly Dictionary<string, ShellFocusRegion> _regions = new(StringComparer.Ordinal);

    /// <summary>The region that currently owns focus, or null before the first
    /// <see cref="SetActive"/>.</summary>
    public string? ActiveRegion { get; private set; }

    /// <summary>Registers (or replaces) a region and returns it.</summary>
    public ShellFocusRegion Add(ShellFocusRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        _regions[region.Name] = region;
        ActiveRegion ??= region.Name;
        return region;
    }

    /// <summary>The region called <paramref name="name"/>, or null.</summary>
    public ShellFocusRegion? Find(string? name) =>
        name is not null && _regions.TryGetValue(name, out var region) ? region : null;

    /// <summary>Updates how many items a region holds, clamping its remembered
    /// focus into the new range.</summary>
    public void SetItemCount(string name, int count)
    {
        if (Find(name) is not { } region)
        {
            return;
        }

        region.ItemCount = Math.Max(0, count);
        region.LastFocusedItem = region.ItemCount == 0
            ? 0
            : Math.Clamp(region.LastFocusedItem, 0, region.ItemCount - 1);
    }

    /// <summary>Records the item focused inside a region, for
    /// <c>focusInBehavior: lastFocusedItem</c>.</summary>
    public void Remember(string name, int index)
    {
        if (Find(name) is { } region && index >= 0)
        {
            region.LastFocusedItem = index;
        }
    }

    /// <summary>Makes <paramref name="name"/> the focused region. Returns false
    /// when the region is unknown.</summary>
    public bool SetActive(string name)
    {
        if (Find(name) is null)
        {
            return false;
        }

        ActiveRegion = name;
        return true;
    }

    /// <summary>
    /// Attempts to leave the active region through <paramref name="direction"/>.
    /// Returns false when the edge is clamped, unnamed, or leads to an empty
    /// region — in which case focus does not move at all. On success the active
    /// region is updated and <paramref name="index"/> carries the restored
    /// last-focused item of the region entered.
    /// </summary>
    public bool TryMove(ShellFocusDirection direction, out string region, out int index)
    {
        region = string.Empty;
        index = -1;

        if (Find(ActiveRegion) is not { } current)
        {
            return false;
        }

        var (canMove, candidate) = current.Edge(direction);
        if (!canMove || Find(candidate) is not { } target || target.ItemCount <= 0)
        {
            return false;
        }

        region = target.Name;
        index = Math.Clamp(target.LastFocusedItem, 0, target.ItemCount - 1);
        target.LastFocusedItem = index;
        ActiveRegion = target.Name;
        return true;
    }
}
