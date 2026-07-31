// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// A counter incremented without any atomic operation, for counters updated once per unit of work by
/// many threads at once.
/// </summary>
/// <remarks>
/// Each thread owns its own slot, so an increment is a plain add to a line no other core touches. That
/// matters more than avoiding contention: measured on mainnet payloads, most of the cost of the trie's
/// per-node counters was the atomic instruction itself rather than cache-line transfer, so spreading the
/// atomic over more lines recovered almost nothing while removing it recovered ~0.23 ms per block.
/// <para>
/// Totals stay exact. Slots are registered on first use and never removed, so a thread ending does not
/// lose its counts; the registry is bounded by the number of threads that ever touch the counter.
/// <see cref="Sum"/> reads each slot with <see cref="Volatile"/>, so a concurrent scrape can miss an
/// increment that has not yet been published, exactly as it could with an interlocked counter.
/// </para>
/// </remarks>
public sealed class PerThreadCounter
{
    private readonly ConcurrentBag<CacheLinePaddedLongRef> _slots = [];
    private readonly ThreadLocal<CacheLinePaddedLongRef> _slot;

    public PerThreadCounter() => _slot = new ThreadLocal<CacheLinePaddedLongRef>(CreateSlot);

    private CacheLinePaddedLongRef CreateSlot()
    {
        CacheLinePaddedLongRef slot = new();
        _slots.Add(slot);
        return slot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment() => _slot.Value!.Count++;

    public long Sum()
    {
        long total = 0;
        foreach (CacheLinePaddedLongRef slot in _slots)
        {
            total += Volatile.Read(ref slot.Count);
        }

        return total;
    }

    /// <summary>
    /// One counter per object so that slots owned by different threads never share a cache line.
    /// </summary>
    private sealed class CacheLinePaddedLongRef
    {
        private CacheLinePaddedLong _padded;

        public ref long Count => ref _padded.Value;
    }
}
