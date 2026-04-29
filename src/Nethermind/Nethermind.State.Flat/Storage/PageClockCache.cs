// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Core.Caching;
using Nethermind.Core.Threading;

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// Composite key identifying an OS page within an arena: (<see cref="ArenaId"/>, <see cref="PageIdx"/>).
/// <see cref="PageIdx"/> is <c>offset / Environment.SystemPageSize</c>, where <c>offset</c> is the
/// arena-absolute byte offset of the page's first byte.
/// </summary>
public readonly record struct PageKey(int ArenaId, int PageIdx);

/// <summary>
/// Page-tracking clock cache for arena-backed mmap regions. Stores no payload — only membership +
/// per-slot accessed bits. On <see cref="Touch"/>, marks the slot accessed (fast path) or installs
/// a new slot, evicting the LRU page via the clock algorithm. Eviction invokes a callback whose
/// purpose is to <c>madvise(MADV_DONTNEED)</c> the evicted OS page so the kernel can drop it.
/// </summary>
public sealed class PageClockCache(int maxCapacity, Action<int, int>? onEvict = null)
    : ClockCacheBase<PageKey>(maxCapacity)
{
    private readonly ConcurrentDictionary<PageKey, int> _slotByPage = maxCapacity == 0
        ? new ConcurrentDictionary<PageKey, int>()
        : new ConcurrentDictionary<PageKey, int>(Environment.ProcessorCount, maxCapacity);
    private readonly McsLock _lock = new();
    private readonly Action<int, int>? _onEvict = onEvict;
    private long _touchCount;

    /// <summary>Total number of <see cref="Touch"/> calls observed (including fast-path hits).</summary>
    internal long TouchCount => Volatile.Read(ref _touchCount);

    public void Touch(int arenaId, int pageIdx)
    {
        if (MaxCapacity == 0) return;
        Interlocked.Increment(ref _touchCount);

        PageKey key = new(arenaId, pageIdx);
        if (_slotByPage.TryGetValue(key, out int slot))
        {
            MarkAccessed(slot);
            return;
        }

        InsertSlow(key);
    }

    private void InsertSlow(PageKey key)
    {
        PageKey evicted = default;
        bool didEvict = false;

        using (_lock.Acquire())
        {
            // Re-check under lock — another thread may have inserted concurrently.
            if (_slotByPage.TryGetValue(key, out int existingSlot))
            {
                MarkAccessed(existingSlot);
                return;
            }

            int offset;
            if (FreeOffsets.Count > 0)
            {
                offset = FreeOffsets.Dequeue();
            }
            else if (_count < MaxCapacity)
            {
                offset = _count;
            }
            else
            {
                offset = Replace(out evicted);
                didEvict = true;
                // Replace removed the evicted entry from _slotByPage and decremented _count.
            }

            KeyToOffset[offset] = key;
            _slotByPage[key] = offset;
            _count++;
            // New slot starts with accessed=false — it gets a chance to survive the next clock
            // sweep. Clearing here is defensive in case the bit was left set by a prior evictee.
            ClearAccessed(offset);
        }

        if (didEvict)
            _onEvict?.Invoke(evicted.ArenaId, evicted.PageIdx);
    }

    private int Replace(out PageKey evicted)
    {
        int position = Clock;
        int max = _count;
        Debug.Assert(max > 0);
        while (true)
        {
            if (position >= max) position = 0;

            bool accessed = ClearAccessed(position);
            if (!accessed)
            {
                evicted = KeyToOffset[position];
                if (!_slotByPage.TryRemove(evicted, out _))
                    throw new InvalidOperationException(
                        $"{nameof(PageClockCache)} removing entry {evicted} at slot {position} that doesn't exist");

                _count--;
                Clock = position + 1;
                return position;
            }

            position++;
        }
    }

    internal bool ContainsPage(int arenaId, int pageIdx) =>
        _slotByPage.ContainsKey(new PageKey(arenaId, pageIdx));

    public new void Clear()
    {
        if (MaxCapacity == 0) return;
        using (_lock.Acquire())
        {
            base.Clear();
            _slotByPage.Clear();
        }
    }
}
