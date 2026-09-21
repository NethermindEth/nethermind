// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>Per-thread stacks of free regions, one per size class, in front of the shared bins.</summary>
/// <remarks>
/// Only its owning thread touches the stacks, so they need no synchronization; every region crossing
/// threads goes through a bin lock. A stack refills half its capacity in one bin call and flushes the
/// oldest half when full. The finalizer returns whatever is parked when the thread dies, which is
/// safe at any time because bins only touch managed slab state.
/// </remarks>
internal sealed class SlabThreadCache(SizeClassBin[] bins)
{
    private readonly SizeClassBin[] _bins = bins;
    private readonly RegionHandle[]?[] _stacks = new RegionHandle[bins.Length][];
    private readonly int[] _counts = new int[bins.Length];

    ~SlabThreadCache() => Flush();

    public RegionHandle Rent(int classIndex)
    {
        RegionHandle[] stack = _stacks[classIndex] ??= new RegionHandle[_bins[classIndex].ThreadCacheCapacity];
        int count = _counts[classIndex];
        if (count == 0) count = _bins[classIndex].AllocateBatch(stack.AsSpan(0, stack.Length / 2));
        _counts[classIndex] = count - 1;
        return stack[count - 1];
    }

    public void Return(int classIndex, in RegionHandle handle)
    {
        RegionHandle[] stack = _stacks[classIndex] ??= new RegionHandle[_bins[classIndex].ThreadCacheCapacity];
        int count = _counts[classIndex];
        if (count == stack.Length)
        {
            int flushed = stack.Length / 2;
            _bins[classIndex].FreeBatch(stack.AsSpan(0, flushed));
            Array.Copy(stack, flushed, stack, 0, count - flushed);
            count -= flushed;
        }

        stack[count] = handle;
        _counts[classIndex] = count + 1;
    }

    /// <summary>Returns every parked region to its bin.</summary>
    public void Flush()
    {
        for (int classIndex = 0; classIndex < _stacks.Length; classIndex++)
        {
            int count = _counts[classIndex];
            if (count == 0) continue;
            _bins[classIndex].FreeBatch(_stacks[classIndex].AsSpan(0, count));
            Array.Clear(_stacks[classIndex]!, 0, count);
            _counts[classIndex] = 0;
        }
    }
}
