// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>
/// Object pool for the EVM call machinery: a per-thread free list in front of a shared queue.
/// Exposes the same TryDequeue / Enqueue shape as the <see cref="ConcurrentQueue{T}"/> pools it
/// replaces, and as <c>ZkEvmQueue</c> does for the guest, so call sites are unchanged.
/// </summary>
/// <remarks>
/// Call frames rent and return their state in LIFO order on a single thread, but a plain
/// <see cref="ConcurrentQueue{T}"/> funnels every rent and return of every thread through one segment
/// head. Under RPC concurrency that contention showed up as <see cref="SpinWait"/> inside
/// <c>TryDequeue</c>, 4.4x worse on arm64 than x64 — a contended CAS costs far more under LL/SC and a
/// weak memory model than under x86's TSO. A short per-thread free list serves almost every request
/// with no atomics at all; the shared queue only absorbs overflow from deep frames and any imbalance
/// between renting and returning threads.
/// <para>
/// The per-thread free list is static, so it is shared by every instance of the same closed generic
/// type. That suits the EVM's singleton pools; two pools over the same <typeparamref name="T"/> would
/// let items migrate between them, so a debug assertion guards against it.
/// </para>
/// </remarks>
/// <typeparam name="T">Pooled item type. One pool per type — see the remarks.</typeparam>
internal sealed class EvmObjectPool<T>
{
    /// <summary>Per-thread free list length used by the frame pools, whose items are small.</summary>
    private const int DefaultLocalCapacity = 16;

    private readonly ConcurrentQueue<T> _shared = new();
    private readonly int _localCapacity;
    private readonly int _maxShared;

    /// <summary>
    /// Upper bound on the items in <see cref="_shared"/>. Reserved before enqueueing, so the bound
    /// never needs <see cref="ConcurrentQueue{T}.Count"/>, which walks the segments.
    /// </summary>
    /// <remarks>
    /// Only ever incremented before an enqueue and decremented after a successful dequeue, so it can
    /// read high but never low: zero therefore proves the queue is empty, letting a warm thread skip
    /// the shared queue entirely.
    /// </remarks>
    private int _sharedCount;

    [ThreadStatic] private static T[]? _local;
    [ThreadStatic] private static int _localCount;

#if DEBUG
    private static int _instanceCount;
#endif

    /// <param name="localCapacity">Items each thread may retain. Overflow goes to the shared queue.</param>
    /// <param name="maxShared">Items the shared queue may retain; further returns are dropped.</param>
    public EvmObjectPool(int localCapacity = DefaultLocalCapacity, int maxShared = int.MaxValue)
    {
        _localCapacity = localCapacity;
        _maxShared = maxShared;
#if DEBUG
        Debug.Assert(Interlocked.Increment(ref _instanceCount) == 1,
            $"{typeof(T).Name} is pooled by more than one {nameof(EvmObjectPool<T>)}; they would share one " +
            "per-thread free list and hand each other's items out. Hoist the pool to a single instance.");
#endif
    }

    /// <summary>Takes a pooled item, preferring the calling thread's free list.</summary>
    /// <returns><see langword="true"/> if an item was available; otherwise <see langword="false"/>.</returns>
    public bool TryDequeue(out T item)
    {
        int count = _localCount - 1;
        if (count >= 0)
        {
            T[] local = _local!;
            item = local[count];
            // Don't keep the item reachable while it is rented out.
            local[count] = default!;
            _localCount = count;
            return true;
        }

        return TryDequeueShared(out item);
    }

    /// <summary>Returns an item to the pool, preferring the calling thread's free list.</summary>
    public void Enqueue(T item)
    {
        T[] local = _local ??= new T[_localCapacity];
        int count = _localCount;
        if (count < local.Length)
        {
            local[count] = item;
            _localCount = count + 1;
            return;
        }

        EnqueueShared(item);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryDequeueShared(out T item)
    {
        if (Volatile.Read(ref _sharedCount) > 0 && _shared.TryDequeue(out item))
        {
            Interlocked.Decrement(ref _sharedCount);
            return true;
        }

        item = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnqueueShared(T item)
    {
        // Reserve a slot first - an O(1) bound without touching ConcurrentQueue.Count.
        if (Interlocked.Increment(ref _sharedCount) > _maxShared)
        {
            // Cap hit - roll back the reservation and drop the item.
            Interlocked.Decrement(ref _sharedCount);
            return;
        }

        _shared.Enqueue(item);
    }
}
