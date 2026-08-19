// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;

internal sealed partial class StackPool
{
    // Stacks are ~32KB and pinned, so a thread keeps only enough for shallow frames; deeper
    // frames overflow to the shared tier, which stays bounded by MaxStacksPooled as before.
    private const int LocalStacksPooled = 8;

    private readonly EvmObjectPool<StackItem> _stackPool = new(LocalStacksPooled, MaxStacksPooled);

    public partial void ReturnStacks(byte[] dataStack) => _stackPool.Enqueue(new(dataStack));

    public partial byte[] RentStacks()
    {
        if (_stackPool.TryDequeue(out StackItem result))
        {
            return result.DataStack;
        }

        // No pooled stack available (empty, or we lost the publish race).
        // Include extra Vector256<byte>.Count and pin so we can align to 32 bytes for SIMD.
        return GC.AllocateUninitializedArray<byte>(StackLength + Vector256<byte>.Count, pinned: true);
    }
}
