// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nethermind.Evm;

/// <summary>
/// Object pool for the EVM call machinery: a per-thread free list in front of a shared queue.
/// </summary>
/// <remarks>
/// Frames rent and return in LIFO order on one thread, so the local tier serves almost every request
/// with no atomics; the shared queue only absorbs deep-frame overflow and cross-thread imbalance.
/// A single <see cref="ConcurrentQueue{T}"/> instead funnelled every thread through one segment head,
/// where the contended CAS dominates - markedly worse on arm64 under LL/SC than on x64 under TSO.
/// <para>
/// The local tier is static per closed generic type, so a second pool over the same
/// <typeparamref name="T"/> would hand out this one's items; the constructor rejects that.
/// </para>
/// </remarks>
internal sealed class EvmObjectPool<T>
{
    private const int DefaultLocalCapacity = 16;

    /// <summary>Items and count in one object, so a pool operation costs one thread-static lookup.</summary>
    /// <remarks>
    /// Two <see cref="ThreadStaticAttribute"/> fields would be a GC and a non-GC thread-static, which
    /// live in separate per-thread blocks: the JIT cannot share a base between them, so each of
    /// <see cref="TryDequeue"/> and <see cref="Enqueue"/> would pay two out-of-line base lookups on the
    /// shared-generic instantiations - four per call frame.
    /// </remarks>
    private sealed class LocalTier
    {
        public T[] Items = null!;
        public int Count;
    }

    [ThreadStatic] private static LocalTier? _local;

    // Never decremented: the pools are singletons built in static field initialisers, so this is a
    // construct-once count, not a live one. A test needing a second pool needs a distinct T.
    private static int _instanceCount;

    private readonly ConcurrentQueue<T> _shared = new();
    private readonly int _localCapacity;
    private readonly int _maxShared;

    /// <summary>
    /// Bounds <see cref="_shared"/> without <see cref="ConcurrentQueue{T}.Count"/>, which walks the
    /// segments. Reserved before an enqueue and released after a dequeue, so it can read high but never
    /// low — zero proves the queue is empty, letting an empty local tier skip it entirely.
    /// </summary>
    private int _sharedCount;

    /// <param name="localCapacity">Items each thread may retain. Overflow goes to the shared queue.</param>
    /// <param name="maxShared">Items the shared queue may retain; further returns are dropped.</param>
    public EvmObjectPool(int localCapacity = DefaultLocalCapacity, int maxShared = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(localCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxShared);
        _localCapacity = localCapacity;
        _maxShared = maxShared;
        // Throws rather than asserts: Debug.Assert is erased from Release and CI runs -c release, so an
        // assertion would guard the type's most dangerous property in neither the node nor the build.
        // Every pool is a static field initialiser, so a violation fails at type init instead of
        // silently handing one thread's items to two renters.
        if (Interlocked.Increment(ref _instanceCount) != 1)
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is pooled by more than one {nameof(EvmObjectPool<T>)}; they would share one " +
                "per-thread free list and hand each other's items out. Hoist the pool to a single instance.");
        }
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item)
    {
        LocalTier? local = _local;
        if (local is not null)
        {
            int count = local.Count - 1;
            if (count >= 0)
            {
                T[] items = local.Items;
                item = items[count];
                // Don't keep the item reachable while it is rented out.
                items[count] = default!;
                local.Count = count;
                return true;
            }
        }

        return TryDequeueShared(out item);
    }

    public void Enqueue(T item)
    {
        LocalTier local = _local ?? CreateLocalTier();
        T[] items = local.Items;
        int count = local.Count;
        if ((uint)count < (uint)items.Length)
        {
            // The array is created in CreateLocalTier as exactly T[] and never escapes this type, so
            // the covariance check a plain store would emit is dead weight: on the three reference-type
            // pools the generic code is shared, the JIT cannot prove the store type-exact, and it
            // lowers to an out-of-line CORINFO_HELP_ARRADDR_ST on the per-frame return path. The
            // (uint) comparison above replaces the bounds check that Unsafe.Add skips.
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(items), (uint)count) = item;
            local.Count = count + 1;
            return;
        }

        EnqueueShared(item);
    }

    // Out of line so the array allocation and its generic-dictionary lookup stay off the per-frame
    // return path, as TryDequeueShared and EnqueueShared already are.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private LocalTier CreateLocalTier() => _local = new LocalTier { Items = new T[_localCapacity] };

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
