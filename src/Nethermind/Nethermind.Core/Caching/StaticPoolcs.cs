// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.Core.Caching;

public static class StaticPool<T> where T : class, new()
{
    // Hard cap for shared pool growth.
    // Prevents unbounded accumulation under bursty workloads while still allowing per-thread caching.
    private const int MaxPooledCount = 4096;

    // Global fallback pool shared between threads.
    // ConcurrentQueue is lock-free but not free of contention.
    // It's used only when the thread-local fast path misses.
    private static readonly ConcurrentQueue<T> _pool = [];

    // Manual count of items in the queue.
    // We maintain this separately because ConcurrentQueue.Count
    // is an O(n) traversal — it walks the internal segment chain.
    // Keeping our own count avoids that cost and keeps the hot path O(1).
    private static int _poolCount;

    public static T Rent()
    {
        // Try to pop from the global pool — this is only hit when a thread
        // has exhausted its own fast slot or is cross-thread renting.
        if (_pool.TryDequeue(out T? item))
        {
            // We track count manually with Interlocked ops instead of using queue.Count.
            Interlocked.Decrement(ref _poolCount);
            return item;
        }

        // Nothing available, allocate new instance
        return new();
    }

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

        _pool.Enqueue(item);
    }
}
