// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// A 64-bit accumulator whose updates are striped across per-core slots to avoid the cache-line
/// contention of a single shared <see cref="Interlocked"/> target under wide parallelism.
/// </summary>
/// <remarks>
/// Modelled on the LongAdder pattern. <see cref="Add"/> mutates only the slot for the calling core, so
/// concurrent writers running on different cores touch different cache lines and do not ping-pong on a
/// shared line. <see cref="Value"/> sums the slots and is therefore O(slot count) and only eventually
/// consistent with adds that are in flight on other cores — this trades cheap, frequent writes for a
/// more expensive, infrequent read, which suits hot-write/cold-read accounting such as cache memory
/// budgets. Each slot is isolated on its own cache line via <see cref="CacheLinePaddedLong"/>.
/// <para>
/// To keep the contention fix from costing memory where it cannot help, the slot array is only
/// allocated when the machine has more than <see cref="StripeThreshold"/> cores (below that, contention
/// on a single counter is negligible, so a plain <see cref="Interlocked"/> field is used and no array is
/// allocated), and the slot count is capped at <see cref="MaxSlots"/> so very-high-core machines do not
/// allocate an unbounded array. The sum is exact in the absence of concurrent writers.
/// </para>
/// </remarks>
public sealed class StripedLongCounter
{
    /// <summary>Core count at or below which striping is skipped (a single counter is used instead).</summary>
    internal const int StripeThreshold = 8;

    /// <summary>Upper bound on the number of slots, to cap the allocation on very-high-core machines.</summary>
    internal const int MaxSlots = 64;

    private readonly CacheLinePaddedLong[]? _slots;
    private readonly int _mask;
    private long _single;

    public StripedLongCounter() : this(SlotsForProcessorCount(Environment.ProcessorCount)) { }

    internal StripedLongCounter(int slots)
    {
        if (slots <= 1)
        {
            _slots = null;
            _mask = 0;
        }
        else
        {
            _slots = new CacheLinePaddedLong[slots];
            _mask = slots - 1;
        }
    }

    /// <summary>Slot count for a given core count: 1 (unstriped) at or below the threshold, otherwise the
    /// next power of two up to <see cref="MaxSlots"/>.</summary>
    internal static int SlotsForProcessorCount(int processorCount) =>
        processorCount <= StripeThreshold
            ? 1
            : Math.Min((int)BitOperations.RoundUpToPowerOf2((uint)processorCount), MaxSlots);

    /// <summary>Adds <paramref name="delta"/> (which may be negative) to the calling core's slot.</summary>
    public void Add(long delta)
    {
        CacheLinePaddedLong[]? slots = _slots;
        if (slots is null)
        {
            Interlocked.Add(ref _single, delta);
        }
        else
        {
            Interlocked.Add(ref slots[Thread.GetCurrentProcessorId() & _mask].Value, delta);
        }
    }

    /// <summary>
    /// The sum of all slots. O(slot count) and only eventually consistent with concurrent <see cref="Add"/>
    /// calls; exact when no writers are active.
    /// </summary>
    public long Value
    {
        get
        {
            CacheLinePaddedLong[]? slots = _slots;
            if (slots is null)
            {
                return Volatile.Read(ref _single);
            }

            long sum = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                sum += Volatile.Read(ref slots[i].Value);
            }
            return sum;
        }
    }

    /// <summary>
    /// Resets the counter so that <see cref="Value"/> reflects <paramref name="value"/>. Not atomic with
    /// respect to concurrent <see cref="Add"/> calls; intended for the rare recompute path where writers
    /// are quiesced (e.g. under a pruning lock).
    /// </summary>
    public void Reset(long value)
    {
        CacheLinePaddedLong[]? slots = _slots;
        if (slots is null)
        {
            Volatile.Write(ref _single, value);
            return;
        }

        for (int i = 1; i < slots.Length; i++)
        {
            Volatile.Write(ref slots[i].Value, 0);
        }
        Volatile.Write(ref slots[0].Value, value);
    }
}
