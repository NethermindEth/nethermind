// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Composite key identifying an OS page within an arena: (<see cref="ArenaId"/>, <see cref="PageIdx"/>).
/// <see cref="PageIdx"/> is <c>offset / Environment.SystemPageSize</c>, where <c>offset</c> is the
/// arena-absolute byte offset of the page's first byte.
/// </summary>
public readonly record struct PageKey(int ArenaId, int PageIdx);

/// <summary>
/// Receives eviction notifications from <see cref="PageSlotCache"/>. Implementations typically
/// issue <c>madvise(MADV_DONTNEED)</c> on the evicted page so the kernel can drop it.
/// </summary>
public interface IPageEvictionHandler
{
    void OnPageEvicted(int arenaId, int pageIdx);
}

/// <summary>
/// Direct-mapped page-tracking cache for arena-backed mmap regions. Two parallel arrays of equal
/// size — one slot of <see cref="PageKey"/>, one <see cref="Lock"/> — sized to the next power of
/// two of the requested capacity. <see cref="Touch"/> hashes the key to a slot, locks it, and
/// either no-ops on hit or replaces the occupant, invoking the eviction handler so the caller can
/// <c>madvise(MADV_DONTNEED)</c> the displaced page. There is no LRU or clock arm: collision is
/// the eviction policy.
/// </summary>
public sealed class PageSlotCache
{
    private static readonly PageKey EmptySlot = new(-1, -1);

    private readonly PageKey[] _slots;
    private readonly Lock[] _locks;
    private readonly int _mask;
    private readonly IPageEvictionHandler _evictionHandler;
    private long _touchCount;

    public int MaxCapacity => _slots.Length;

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                lock (_locks[i])
                    if (_slots[i] != EmptySlot) count++;
            }
            return count;
        }
    }

    /// <summary>Total number of <see cref="Touch"/> calls observed (including no-op hits).</summary>
    internal long TouchCount => Volatile.Read(ref _touchCount);

    public PageSlotCache(int maxCapacity, IPageEvictionHandler evictionHandler)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCapacity);
        ArgumentNullException.ThrowIfNull(evictionHandler);
        _evictionHandler = evictionHandler;

        if (maxCapacity == 0)
        {
            _slots = [];
            _locks = [];
            _mask = 0;
            return;
        }

        int size = (int)BitOperations.RoundUpToPowerOf2((uint)maxCapacity);
        _slots = new PageKey[size];
        _locks = new Lock[size];
        Array.Fill(_slots, EmptySlot);
        for (int i = 0; i < size; i++) _locks[i] = new Lock();
        _mask = size - 1;
    }

    public void Touch(int arenaId, int pageIdx)
    {
        if (_slots.Length == 0) return;
        Interlocked.Increment(ref _touchCount);

        PageKey key = new(arenaId, pageIdx);
        int idx = (int)((uint)key.GetHashCode() & (uint)_mask);

        PageKey evicted;
        lock (_locks[idx])
        {
            PageKey existing = _slots[idx];
            if (existing == key) return;
            _slots[idx] = key;
            if (existing == EmptySlot) return;
            evicted = existing;
        }

        _evictionHandler.OnPageEvicted(evicted.ArenaId, evicted.PageIdx);
    }

    internal bool ContainsPage(int arenaId, int pageIdx)
    {
        if (_slots.Length == 0) return false;
        PageKey key = new(arenaId, pageIdx);
        int idx = (int)((uint)key.GetHashCode() & (uint)_mask);
        lock (_locks[idx])
            return _slots[idx] == key;
    }

    public void Clear()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            lock (_locks[i]) _slots[i] = EmptySlot;
        }
    }
}
