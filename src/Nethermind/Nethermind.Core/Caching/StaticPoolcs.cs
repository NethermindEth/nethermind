// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace Nethermind.Core.Caching;

public static class StaticPool<T> where T : class, new()
{
    // Hard cap for shared pool growth.
    // Prevents unbounded accumulation under bursty workloads while still allowing per-thread caching.
    private const int MaxPooledCount = 4096;

    // Each thread owns a single reusable slot.
    // This avoids any atomic or locking cost on the hot path.
    // Thread-local access is handled via TLS (thread-local storage) and is extremely fast
    // compared to interlocked operations or global queue coordination.
    [ThreadStatic]
    private static T? _localFast;

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
        // Local ref avoids repeated TLS lookups.
        // Accessing a [ThreadStatic] field emits a TLS indirection in IL;
        // caching it in a ref variable avoids repeating that per instruction.
        ref T? local = ref _localFast;

        // 1. Thread-local fast path
        T? item = local;
        if (item is not null)
        {
            // Clear slot and return cached instance
            local = null;
            return item;
        }

        // 2. Shared queue fallback
        // Try to pop from the global pool — this is only hit when a thread
        // has exhausted its own fast slot or is cross-thread renting.
        if (_pool.TryDequeue(out item))
        {
            // We track count manually with Interlocked ops instead of using queue.Count.
            Interlocked.Decrement(ref _poolCount);
            return item;
        }

        // 3. Nothing available, allocate new instance via cached delegate.
        return Create();
    }

    public static void Return(T item)
    {
        // 1. Per-thread fast slot reuse
        // No locking or fencing needed — each thread sees its own copy of _localFast.
        ref T? local = ref _localFast;
        if (local is null)
        {
            local = item;
            return;
        }

        // 2. Shared pool fallback
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

    // Cached compiled constructor delegate.
    // Compiled once at type-load time; eliminates reflection cost on Rent().
    private static Func<T> Create { get; } = BuildCreator();

    private static Func<T> BuildCreator()
    {
        // Use expression tree rather than Activator.CreateInstance which new T() calls.
        // Compiling a NewExpression gives a direct ctor call.
        ConstructorInfo? ci = typeof(T).GetConstructor(BindingFlags.Public | BindingFlags.Instance, []) ??
            throw new InvalidOperationException($"{typeof(T).Name} has no parameterless ctor.");

        NewExpression newExpr = Expression.New(ci);
        Expression<Func<T>> lambda = Expression.Lambda<Func<T>>(
            newExpr,
            name: $"Create{typeof(T).Name}",
            tailCall: false,
            parameters: Array.Empty<ParameterExpression>()
        );
        return lambda.Compile();
    }
}
