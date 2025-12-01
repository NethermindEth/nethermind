// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Runtime.Intrinsics;
using System.Threading;

using static Nethermind.Evm.EvmState;

namespace Nethermind.Evm;

internal sealed class StackPool
{
    // Also have parallel prewarming and Rpc calls
    private const int MaxStacksPooled = VirtualMachine.MaxCallDepth * 2;
    private readonly struct StackItem(byte[] dataStack, ReturnState[] returnStack)
    {
        public readonly byte[] DataStack = dataStack;
        public readonly ReturnState[] ReturnStack = returnStack;
    }

    private readonly ConcurrentQueue<StackItem> _stackPool = new();

    /// <summary>
    /// The word 'return' acts here once as a verb 'to return stack to the pool' and once as a part of the
    /// compound noun 'return stack' which is a stack of subroutine return values.
    /// </summary>
    /// <param name="dataStack"></param>
    /// <param name="returnStack"></param>
    public void ReturnStacks(byte[] dataStack, ReturnState[] returnStack)
    {
        // Reserve a slot first - O(1) bound without touching ConcurrentQueue.Count.
        if (Interlocked.Increment(ref _poolCount) > MaxStacksPooled)
        {
            // Cap hit - roll back the reservation and drop the item.
            Interlocked.Decrement(ref _poolCount);
            return;
        }

        _stackPool.Enqueue(new StackItem(dataStack, returnStack));
    }

    // Manual reservation count - upper bound on items actually in the queue.
    private int _poolCount;

    public const int StackLength = (EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32;

    public (byte[], ReturnState[]) RentStacks()
    {
        if (Volatile.Read(ref _poolCount) > 0 && _stackPool.TryDequeue(out StackItem result))
        {
            Interlocked.Decrement(ref _poolCount);
            return (result.DataStack, result.ReturnStack);
        }

        // Count was positive but we lost the race or the enqueuer has not published yet.
        // Include extra Vector256<byte>.Count and pin so we can align to 32 bytes.
        // This ensures the stack is properly aligned for SIMD operations.
        return
        (
            GC.AllocateUninitializedArray<byte>(StackLength + Vector256<byte>.Count, pinned: true),
            new ReturnState[EvmStack.ReturnStackSize]
        );
    }
}

