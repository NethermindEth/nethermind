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

    public partial void ReturnStacks(byte[] dataStack)
    {
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
