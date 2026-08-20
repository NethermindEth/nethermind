// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>
/// Object pool for the EVM call machinery: a per-thread free list in front of a shared queue.
/// </summary>
/// <remarks>
/// Frames rent and return in LIFO order on one thread, so the local tier serves almost every request
/// with no atomics; the shared queue only absorbs deep-frame overflow and cross-thread imbalance.
/// A single <see cref="ConcurrentQueue{T}"/> instead funnelled every thread through one segment head,
/// which showed up as <see cref="SpinWait"/> in <c>TryDequeue</c> 4.4x worse on arm64 than x64 — a
/// contended CAS costs far more under LL/SC than under x86's TSO.
/// <para>
/// The local tier is static per closed generic type, so a second pool over the same
/// <typeparamref name="T"/> would hand out this one's items; a debug assertion guards that.
/// </para>
/// </remarks>
internal sealed class EvmObjectPool<T>
{
    private const int DefaultLocalCapacity = 16;

    private readonly ConcurrentQueue<T> _shared = new();
    private readonly int _localCapacity;
    private readonly int _maxShared;

    /// <summary>
    /// Bounds <see cref="_shared"/> without <see cref="ConcurrentQueue{T}.Count"/>, which walks the
    /// segments. Reserved before an enqueue and released after a dequeue, so it can read high but never
    /// low — zero proves the queue is empty, letting an empty local tier skip it entirely.
    /// </summary>
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
        ArgumentOutOfRangeException.ThrowIfNegative(localCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxShared);
        _localCapacity = localCapacity;
        _maxShared = maxShared;
#if DEBUG
        int instances = Interlocked.Increment(ref _instanceCount);
        Debug.Assert(instances == 1,
            $"{typeof(T).Name} is pooled by more than one {nameof(EvmObjectPool<T>)}; they would share one " +
            "per-thread free list and hand each other's items out. Hoist the pool to a single instance.");
#endif
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
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
    private bool TryDequeueShared([MaybeNullWhen(false)] out T item)
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
        // Reserve before enqueueing so the bound costs no queue walk.
        if (Interlocked.Increment(ref _sharedCount) > _maxShared)
        {
            Interlocked.Decrement(ref _sharedCount);
            return;
        }

        _shared.Enqueue(item);
    }
}
