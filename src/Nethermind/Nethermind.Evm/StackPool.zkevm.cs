// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm;

internal sealed partial class StackPool
{
    private readonly ZkEvmQueue<StackItem> _stackPool = new();

    public partial void ReturnStacks(byte[] dataStack)
    {
        // Single-threaded guest: bound directly off the queue's O(1) count, no atomics needed.
        if (_stackPool.Count >= MaxStacksPooled)
            return;

        _stackPool.Enqueue(new(dataStack));
    }

    public partial byte[] RentStacks()
    {
        if (_stackPool.TryDequeue(out StackItem result))
            return result.DataStack;

        // Pool empty - allocate. Over-allocate by one word and pin so VmState can shift
        // the data-stack pointer to a 32-byte (EVM word) boundary.
        return GC.AllocateUninitializedArray<byte>(StackLength + EvmStack.WordSize, pinned: true);
    }
}
