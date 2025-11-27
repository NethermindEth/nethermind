// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using Nethermind.Core.Resettables;

namespace Nethermind.Core.Caching;

/// <summary>
/// High performance static pool for reference types that support reset semantics.
/// </summary>
/// <typeparam name="T">
/// The pooled type. Must be a reference type that implements <see cref="IResettable"/> and
/// has a public parameterless constructor.
/// </typeparam>
public static class StaticPool<T> where T : class, IResettable, new()
{
    /// <summary>
    /// Hard cap for the total number of items that can be stored in the shared pool.
    /// Prevents unbounded growth under bursty workloads while still allowing reuse.
    /// </summary>
    private const int MaxPooledCount = 4096;

    /// <summary>
    /// Global pool shared between threads.
    /// </summary>
    private static readonly ConcurrentQueue<T> _pool = [];

    /// <summary>
    /// Manual count of items in the queue.
    /// We maintain this separately because ConcurrentQueue.Count
    /// is an O(n) traversal — it walks the internal segment chain.
    /// Keeping our own count avoids that cost and keeps the hot path O(1).
    /// </summary>
    private static int _poolCount;

    /// <summary>
    /// Rents an instance of <typeparamref name="T"/> from the pool.
    /// </summary>
    /// <remarks>
    /// The method first attempts to dequeue an existing instance from the shared pool.
    /// If the pool is empty, a new instance is created using the parameterless constructor.
    /// </remarks>
    /// <returns>
    /// A reusable instance of <typeparamref name="T"/>. The returned instance is not guaranteed
    /// to be zeroed or reset beyond the guarantees provided by <see cref="IResettable"/> and
    /// the constructor. Callers should treat it as a freshly created instance.
    /// </returns>
    public static T Rent()
    {
        // Try to pop from the global pool — this is only hit when a thread
        // has exhausted its own fast slot or is cross-thread renting.
        if (Volatile.Read(ref _poolCount) > 0 && _pool.TryDequeue(out T? item))
        {
            // We track count manually with Interlocked ops instead of using queue.Count.
            Interlocked.Decrement(ref _poolCount);
            return item;
        }

        // Nothing available, allocate new instance
        return new();
    }

    /// <summary>
    /// Returns an instance of <typeparamref name="T"/> to the pool for reuse.
    /// </summary>
    /// <remarks>
    /// The instance is reset via <see cref="IResettable.Reset"/> before being enqueued.
    /// If adding the instance would exceed <see cref="MaxPooledCount"/>, the instance is
    /// discarded and not pooled.
    /// </remarks>
    /// <param name="item">
    /// The instance to return to the pool. Must not be <see langword="null"/>.
    /// After returning, the caller must not use the instance again.
    /// </param>
    public static void Return(T item)
    {
        // We use Interlocked.Increment to reserve a slot up front.
        // This guarantees a bounded queue length without relying on slow Count().
        if (Interlocked.Increment(ref _poolCount) > MaxPooledCount)
        {
            // Roll back reservation if we'd exceed the cap.
            Interlocked.Decrement(ref _poolCount);
            return;
        }

        item.Reset();
        _pool.Enqueue(item);
    }
}
