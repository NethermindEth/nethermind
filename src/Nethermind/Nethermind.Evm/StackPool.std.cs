// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;

internal static partial class StackPool
{
    // Stacks are ~32KB and pinned, and MaxStacksPooled bounds only the shared tier, so the pinned
    // ceiling is MaxStacksPooled + LocalStacksPooled per thread that has run an EVM frame - held until
    // the thread dies, and RegisterRpcModules raises the thread-pool minimum by ProcessorCount.
    // A retiring thread abandons its slots rather than returning them, so thread churn also costs fresh
    // pinned allocations, and a parked slot is unreachable to a busy thread with a dry shared tier. Every
    // abandoned array is the size of its replacement, so both stay a Gen2 and footprint cost.
    private const int LocalStacksPooled = 8;

    private static readonly EvmObjectPool<StackItem> _stackPool = new(LocalStacksPooled, MaxStacksPooled);

    public static partial void ReturnStacks(byte[] dataStack) => _stackPool.Enqueue(new(dataStack));

    public static partial byte[] RentStacks()
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
