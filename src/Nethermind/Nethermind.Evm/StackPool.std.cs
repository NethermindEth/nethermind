// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Threading;

namespace Nethermind.Evm;

internal sealed partial class StackPool
{
    private readonly ConcurrentQueue<StackItem> _stackPool = new();

    // Stacks are rented and returned on the executing thread in LIFO order, so a small per-thread
    // cache serves nearly every frame with an array that is hot in this core's cache. The shared
    // queue costs two atomics per frame and migrates ~33 KB pinned arrays between cores under
    // concurrent load; it remains as overflow so deep chains keep pooling. [ThreadStatic] is
    // deliberately shared across pool instances: every pool deals in identically-shaped arrays.
    // Retention becomes peak-bounded rather than hard-capped: on top of the queue's
    // MaxStacksPooled, each thread that ever executed EVM code can hold up to
    // MaxStacksCachedPerThread pinned (POH) arrays (~0.5 MB per thread).
    private const int MaxStacksCachedPerThread = 16;
    [ThreadStatic] private static byte[]?[]? _threadStacks;
    [ThreadStatic] private static int _threadStackCount;

    public partial void ReturnStacks(byte[] dataStack)
    {
        int cached = _threadStackCount;
        if (cached < MaxStacksCachedPerThread)
        {
            byte[]?[] threadStacks = _threadStacks ??= new byte[]?[MaxStacksCachedPerThread];
            threadStacks[cached] = dataStack;
            _threadStackCount = cached + 1;
            return;
        }

        // Reserve a slot first - O(1) bound without touching ConcurrentQueue.Count.
        if (Interlocked.Increment(ref _poolCount) > MaxStacksPooled)
        {
            // Cap hit - roll back the reservation and drop the item.
            Interlocked.Decrement(ref _poolCount);
            return;
        }

        _stackPool.Enqueue(new(dataStack));
    }

    // Manual reservation count - upper bound on items actually in the queue.
    private int _poolCount;

    public partial byte[] RentStacks()
    {
        byte[]?[]? threadStacks = _threadStacks;
        int cached = _threadStackCount;
        if (threadStacks is not null && cached > 0)
        {
            cached--;
            byte[] stack = threadStacks[cached]!;
            threadStacks[cached] = null;
            _threadStackCount = cached;
            return stack;
        }

        if (Volatile.Read(ref _poolCount) > 0 && _stackPool.TryDequeue(out StackItem result))
        {
            Interlocked.Decrement(ref _poolCount);
            return result.DataStack;
        }

        // No pooled stack available (empty, or we lost the publish race).
        // Include extra Vector256<byte>.Count and pin so we can align to 32 bytes for SIMD.
        return GC.AllocateUninitializedArray<byte>(StackLength + Vector256<byte>.Count, pinned: true);
    }
}
