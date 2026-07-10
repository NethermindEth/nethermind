// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if !ZK_EVM
using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// A single growable byte buffer shared by every call frame of one <see cref="VirtualMachine{TGasPolicy}"/>
/// instance (hence one execution thread). Each frame occupies a contiguous <c>[base, base + Size)</c>
/// window; frame teardown needs no work (the window is simply reused by the next sibling), and the
/// zero-initialisation EVM memory requires is folded into buffer growth instead of a per-frame clear on
/// dispose.
/// </summary>
/// <remarks>
/// Not thread-safe by design: an entire transaction (its full nested call stack) runs synchronously on the
/// owning thread, and each thread has its own <see cref="VirtualMachine{TGasPolicy}"/> (scoped DI
/// registration in <c>BlockProcessingModule</c>). The buffer has a fixed capacity so it never reallocates;
/// that keeps zero-copy call-input views (<c>ExecutionEnvironment.InputData</c>) into a parent frame's
/// window valid for the child's whole execution. Frames that would exceed the reserve spill to a private
/// pooled array (handled in <see cref="EvmPooledMemory"/>).
///
/// <para>Zeroing invariant: unwritten EVM memory must read as zero. <see cref="_dirtyHigh"/> is the
/// highest absolute index any frame has ever grown into on this thread; everything at or above it is still
/// pristine (<c>new byte[]</c> is zero-initialised, backed by OS demand-zero pages), so growth only has to
/// clear the part of the newly exposed window that lies below <see cref="_dirtyHigh"/> — i.e. bytes a
/// previous frame may have written and left behind.</para>
/// </remarks>
internal sealed class SharedEvmMemory
{
    /// <summary>
    /// Fixed reserve size, in bytes. Large enough to hold realistic per-thread call-stack memory; frames
    /// beyond it spill to a private array. Backed by OS demand-zero pages, so untouched capacity costs
    /// address space, not physical RAM.
    /// </summary>
    internal const int ReserveBytes = 16 * 1024 * 1024;

    private byte[]? _buffer;
    private int _dirtyHigh;

    /// <summary>The backing array, allocated (zero-initialised) on first use.</summary>
    internal byte[] Buffer => _buffer ??= new byte[ReserveBytes];

    /// <summary>
    /// Makes the window <c>[baseOffset + oldSize, baseOffset + newSize)</c> read as zero where unwritten,
    /// clearing only the sub-range a previous frame may have dirtied and leaving the pristine tail untouched.
    /// </summary>
    /// <param name="baseOffset">Absolute start of the frame's window in <see cref="Buffer"/>.</param>
    /// <param name="oldSize">Frame-relative extent already ensured-zero on a prior growth.</param>
    /// <param name="newSize">Frame-relative extent to ensure now (word-aligned <c>Size</c>).</param>
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

        // Conservatively treat the whole reached window as possibly-written from now on, so a later
        // sibling reusing this region re-zeroes it. The pristine region above stays free to hand out.
        if (absNewEnd > _dirtyHigh)
        {
            _dirtyHigh = absNewEnd;
        }
    }
}
#endif
