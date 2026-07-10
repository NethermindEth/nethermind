// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
internal sealed partial class SharedEvmMemory(int reserveBytes = SharedEvmMemory.DefaultReserveBytes)
{
    /// <summary>
    /// Default reserve size, in bytes. Sized to the largest memory a single call frame can reach under
    /// EIP-7825's 2^24 transaction gas cap: solving <c>3w + w^2/512 = 2^24</c> gives ~91,917 words ≈ 2.81 MB,
    /// so 3 MB guarantees no single frame spills. Multi-frame stacks split the same 2^24 budget and spill
    /// only when adversarially deep — correct, just slower. Backed by OS demand-zero pages, so untouched
    /// capacity costs address space, not physical RAM.
    /// </summary>
    internal const int DefaultReserveBytes = 3 * 1024 * 1024;

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
