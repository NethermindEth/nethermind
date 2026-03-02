// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Threading;

using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

internal sealed class StackPool
{
    // Also have parallel prewarming and Rpc calls
    private const int MaxStacksPooled = MaxCallDepth * 2;

    private readonly ConcurrentQueue<byte[]> _stackPool = new();

    /// <summary>
    /// Returns a data stack to the pool.
    /// </summary>
    public void ReturnStack(byte[] dataStack)
    {
        // Reserve a slot first - O(1) bound without touching ConcurrentQueue.Count.
        if (Interlocked.Increment(ref _poolCount) > MaxStacksPooled)
        {
            // Cap hit - roll back the reservation and drop the item.
            Interlocked.Decrement(ref _poolCount);
            return;
        }

        _stackPool.Enqueue(dataStack);
    }

    // Manual reservation count - upper bound on items actually in the queue.
    private int _poolCount;

    public const int StackLength = (EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32;

    public byte[] RentStack()
    {
        if (Volatile.Read(ref _poolCount) > 0 && _stackPool.TryDequeue(out byte[]? result))
        {
            Interlocked.Decrement(ref _poolCount);
            return result;
        }

        // Count was positive but we lost the race or the enqueuer has not published yet.
        // Include extra Vector256<byte>.Count and pin so we can align to 32 bytes.
        // This ensures the stack is properly aligned for SIMD operations.
        return GC.AllocateUninitializedArray<byte>(StackLength + Vector256<byte>.Count, pinned: true);
    }
}
