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
/// budgets. Each slot is isolated on its own cache line via <see cref="CacheLinePaddedLong"/>. When no
/// writers are concurrently active the sum is exact. Slot count is rounded up to a power of two so the
/// core index can be folded with a mask; a core index that exceeds the slot count simply shares a slot,
/// which stays correct because each per-slot add is atomic.
/// </remarks>
public sealed class StripedLongCounter
{
    private readonly CacheLinePaddedLong[] _slots;
    private readonly int _mask;

    public StripedLongCounter()
    {
        int slots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, Environment.ProcessorCount));
        _slots = new CacheLinePaddedLong[slots];
        _mask = slots - 1;
    }

    /// <summary>Adds <paramref name="delta"/> (which may be negative) to the calling core's slot.</summary>
    public void Add(long delta) =>
        Interlocked.Add(ref _slots[Thread.GetCurrentProcessorId() & _mask].Value, delta);

    /// <summary>
    /// The sum of all slots. O(slot count) and only eventually consistent with concurrent <see cref="Add"/>
    /// calls; exact when no writers are active.
    /// </summary>
    public long Value
    {
        get
        {
            long sum = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                sum += Volatile.Read(ref _slots[i].Value);
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
        for (int i = 1; i < _slots.Length; i++)
        {
            Volatile.Write(ref _slots[i].Value, 0);
        }
        Volatile.Write(ref _slots[0].Value, value);
    }
}
