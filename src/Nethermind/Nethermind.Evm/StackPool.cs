// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Word = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.Evm;

internal class StackPool
{
    // Also have parallel prewarming and Rpc calls
    private const int MaxStacksPooled = VirtualMachine.MaxCallDepth * 2;
    private readonly struct StackItem(Word[] dataStack, int[] returnStack)
    {
        public readonly Word[] DataStack = dataStack;
        public readonly int[] ReturnStack = returnStack;
    }

    private readonly ConcurrentQueue<StackItem> _stackPool = new();

    /// <summary>
    /// The word 'return' acts here once as a verb 'to return stack to the pool' and once as a part of the
    /// compound noun 'return stack' which is a stack of subroutine return values.
    /// </summary>
    /// <param name="dataStack"></param>
    /// <param name="returnStack"></param>
    public void ReturnStacks(Word[] dataStack, int[] returnStack)
    {
        if (_stackPool.Count <= MaxStacksPooled)
        {
            _stackPool.Enqueue(new(dataStack, returnStack));
        }
    }

    public (Word[], int[]) RentStacks()
    {
        if (_stackPool.TryDequeue(out StackItem result))
        {
            return (result.DataStack, result.ReturnStack);
        }

        return
        (
            new Word[EvmStack.MaxStackSize + EvmStack.RegisterLength],
            new int[EvmStack.ReturnStackSize]
        );
    }
}

