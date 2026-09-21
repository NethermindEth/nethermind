// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>Per-thread stacks of free regions, one per size class, in front of the shared bins.</summary>
/// <remarks>
/// The owning thread is the only one that rents and returns, but a sweeper may flush an idle cache
/// from another thread, so every operation takes the cache's own lock; uncontended, that costs a
/// fraction of the shared bin lock it saves. A stack refills half its capacity in one bin call and
/// flushes the oldest half when full. <see cref="TrimIfIdle"/> flushes a cache untouched since the
/// previous sweep, so a thread that stopped allocating pins nothing. The finalizer returns whatever
/// is parked when the thread dies, which is safe at any time because bins only touch managed slab state.
/// </remarks>
internal sealed class SlabThreadCache(SizeClassBin[] bins)
{
    private readonly SizeClassBin[] _bins = bins;
    private readonly RegionHandle[]?[] _stacks = new RegionHandle[bins.Length][];
    private readonly int[] _counts = new int[bins.Length];
    private readonly Lock _lock = new();
    private bool _usedSinceSweep = true;

    ~SlabThreadCache() => Flush();

    public RegionHandle Rent(int classIndex)
    {
        lock (_lock)
        {
            _usedSinceSweep = true;
            RegionHandle[] stack = _stacks[classIndex] ??= new RegionHandle[_bins[classIndex].ThreadCacheCapacity];
            int count = _counts[classIndex];
            if (count == 0) count = _bins[classIndex].AllocateBatch(stack.AsSpan(0, stack.Length / 2));
            _counts[classIndex] = count - 1;
            return stack[count - 1];
        }
    }

    public void Return(int classIndex, in RegionHandle handle)
    {
        lock (_lock)
        {
            _usedSinceSweep = true;
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
    }

    /// <summary>Returns every parked region to its bin.</summary>
    public void Flush()
    {
        lock (_lock) FlushCore();
    }

    /// <summary>Flushes the cache unless its thread used it since the previous call; each call arms the next.</summary>
    public void TrimIfIdle()
    {
        lock (_lock)
        {
            if (!_usedSinceSweep) FlushCore();
            _usedSinceSweep = false;
        }
    }

    private void FlushCore()
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
