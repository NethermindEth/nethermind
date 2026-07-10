// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if !ZK_EVM
using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// One growable byte buffer shared by every call frame of a single <see cref="VirtualMachine{TGasPolicy}"/>
/// instance. Each frame occupies a contiguous <c>[base, base + Size)</c> window; frame teardown is free
/// (the window is reused by the next sibling) and zero-initialisation is folded into buffer growth.
/// </summary>
/// <remarks>
/// Not thread-safe by design: each thread has its own VM and a whole transaction runs synchronously on one
/// thread. Fixed capacity means it never reallocates, so zero-copy call-input views into a parent frame's
/// window stay valid for the child's whole execution; frames beyond the reserve spill (see EvmPooledMemory).
/// </remarks>
internal sealed class SharedEvmMemory(int reserveBytes = SharedEvmMemory.DefaultReserveBytes)
{
    /// <summary>
    /// Default reserve size, in bytes. A single frame can't exceed a few MB at realistic block gas limits
    /// (~4-5 MB at 45M gas), so this leaves ample headroom; deeper cumulative stacks spill. Backed by OS
    /// demand-zero pages, so untouched capacity costs address space, not physical RAM.
    /// </summary>
    internal const int DefaultReserveBytes = 8 * 1024 * 1024;

    private readonly int _reserveBytes = reserveBytes;
    private byte[]? _buffer;
    // Highest absolute index any frame has ever grown into; everything at or above it is still zero.
    private int _dirtyHigh;

    internal byte[] Buffer => _buffer ??= new byte[_reserveBytes];

    /// <summary>
    /// Makes the window <c>[baseOffset + oldSize, baseOffset + newSize)</c> read as zero where unwritten,
    /// clearing only the sub-range a previous frame may have dirtied and leaving the pristine tail alone.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Zero(int baseOffset, int oldSize, int newSize)
    {
        int absOldEnd = baseOffset + oldSize;
        int absNewEnd = baseOffset + newSize;
        int clearTo = Math.Min(absNewEnd, _dirtyHigh);
        if (clearTo > absOldEnd)
        {
            Array.Clear(_buffer!, absOldEnd, clearTo - absOldEnd);
        }

        if (absNewEnd > _dirtyHigh)
        {
            _dirtyHigh = absNewEnd;
        }
    }
}
#endif
