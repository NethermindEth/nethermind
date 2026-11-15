// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Threading;

namespace Nethermind.TxPool;

internal sealed class OperationGate
{
    private const int CacheLineSize = 128;
    private const int SlotStride = CacheLineSize / sizeof(int);
    private static readonly int s_slotMask = (int)BitOperations.RoundUpToPowerOf2((uint)Environment.ProcessorCount) - 1;

    private readonly int[] _slots = new int[(s_slotMask + 2) * SlotStride];
    private CacheLinePaddedLong _closed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnter(out int slot)
    {
        slot = 0;
        if (Volatile.Read(ref _closed.Value) != 0)
        {
            return false;
        }

        int index = ((Thread.GetCurrentProcessorId() & s_slotMask) + 1) * SlotStride;
        Interlocked.Increment(ref _slots[index]);
        if (Volatile.Read(ref _closed.Value) == 0)
        {
            slot = index;
            return true;
        }

        Interlocked.Decrement(ref _slots[index]);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit(int slot) => Interlocked.Decrement(ref _slots[slot]);

    public bool TryClose() => Interlocked.CompareExchange(ref _closed.Value, 1, 0) == 0;

    public bool HasActiveOperations()
    {
        int[] slots = _slots;
        for (int stripe = 0; stripe <= s_slotMask; stripe++)
        {
            if (Volatile.Read(ref slots[(stripe + 1) * SlotStride]) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
