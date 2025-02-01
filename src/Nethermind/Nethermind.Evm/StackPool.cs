// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

using static Nethermind.Evm.EvmState;

namespace Nethermind.Evm;

internal class StackPool
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
        if (_stackPool.Count <= MaxStacksPooled)
        {
            _stackPool.Enqueue(new(dataStack, returnStack));
        }
    }

    public (byte[], ReturnState[]) RentStacks()
    {
        if (_stackPool.TryDequeue(out StackItem result))
        {
            return (result.DataStack, result.ReturnStack);
        }

        return
        (
            new byte[(EvmStack.MaxStackSize + EvmStack.RegisterLength) * 32],
            new ReturnState[EvmStack.ReturnStackSize]
        );
    }
}

