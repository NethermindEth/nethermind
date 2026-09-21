// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Threading;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>One region of a slab: the slab plus the region's index in it.</summary>
internal readonly record struct RegionHandle(Slab Slab, int Index);

/// <summary>The shared slabs of one size class; thread caches move regions in and out in batches under its lock.</summary>
/// <remarks>
/// Non-full slabs are bucketed by their free-region count and allocations always draw from the
/// fullest one, so the emptier slabs get no refills, drain as their regions come back, and are
/// released. One emptied slab is retained out of the buckets so a bin oscillating around a slab
/// boundary does not churn native allocations. A region parked in a thread cache counts as
/// allocated, so an empty slab really has no outstanding region.
/// </remarks>
internal sealed class SizeClassBin
{
    private readonly SlabMemoryAllocator _allocator;
    private readonly Lock _lock = new();
    /// <summary>Doubly linked list heads indexed by free-region count; index 0 is unused (full slabs are tracked by nothing).</summary>
    private readonly Slab?[] _headByFreeCount;
    private readonly ulong[] _occupiedBuckets;
    private Slab? _spareEmpty;
    private long _allocatedRegions;
    private long _slabBytes;

    public SizeClassBin(SlabMemoryAllocator allocator, int classIndex, int classSize, int slabSize, int threadCacheCapacity)
    {
        _allocator = allocator;
        ClassIndex = classIndex;
        ClassSize = classSize;
        SlabSize = slabSize;
        RegionCount = slabSize / classSize;
        ThreadCacheCapacity = threadCacheCapacity;
        _headByFreeCount = new Slab?[RegionCount + 1];
        _occupiedBuckets = new ulong[(RegionCount + 64) / 64];
    }

    public int ClassIndex { get; }
    public int ClassSize { get; }
    public int SlabSize { get; }
    public int RegionCount { get; }
    public int ThreadCacheCapacity { get; }

    /// <summary>Bytes handed out by this bin and not yet given back, regions parked in thread caches included.</summary>
    public long AllocatedBytes => Volatile.Read(ref _allocatedRegions) * ClassSize;

    /// <summary>Native bytes held by this bin's slabs.</summary>
    public long SlabBytes => Volatile.Read(ref _slabBytes);

    public int AllocateBatch(Span<RegionHandle> into)
    {
        lock (_lock)
        {
            int filled = 0;
            while (filled < into.Length)
            {
                Slab? slab = FullestNonFull();
                if (slab is null)
                {
                    slab = _spareEmpty ?? CreateSlab();
                    _spareEmpty = null;
                }
                else
                {
                    Unlink(slab);
                }

                while (filled < into.Length && !slab.IsFull) into[filled++] = new RegionHandle(slab, slab.Pop());
                if (!slab.IsFull) Link(slab);
            }

            _allocatedRegions += filled;
            return filled;
        }
    }

    public void FreeBatch(ReadOnlySpan<RegionHandle> handles)
    {
        lock (_lock)
        {
            foreach (RegionHandle handle in handles)
            {
                Slab slab = handle.Slab;
                // A slab freed by its own finalizer (see Slab.IsReleased) is never in the buckets and must not be handed out again.
                if (!slab.IsFull && !slab.IsReleased) Unlink(slab);
                slab.Push(handle.Index);
                if (slab.IsEmpty) Retire(slab);
                else if (!slab.IsReleased) Link(slab);
            }

            _allocatedRegions -= handles.Length;
        }
    }

    /// <summary>
    /// Frees a slab whose finalizer ran, unless a dead thread cache's flush handed it back to this bin first: a finalizer
    /// runs after the slab was found unreachable, and the cache (finalizable too, in no fixed order) may have resurrected it
    /// meanwhile, in which case it is live again and its finalizer is re-armed.
    /// </summary>
    internal void ReleaseUnreachable(Slab slab)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_spareEmpty, slab) || slab.PreviousInBin is not null || ReferenceEquals(_headByFreeCount[slab.FreeCount], slab))
                GC.ReRegisterForFinalize(slab);
            else
                slab.Release();
        }
    }

    /// <summary>Drops the retained empty slab, once the allocator no longer serves requests.</summary>
    public void ReleaseSpare()
    {
        lock (_lock)
        {
            if (_spareEmpty is null) return;
            ReleaseSlab(_spareEmpty);
            _spareEmpty = null;
        }
    }

    private Slab? FullestNonFull()
    {
        for (int word = 0; word < _occupiedBuckets.Length; word++)
        {
            if (_occupiedBuckets[word] != 0) return _headByFreeCount[word * 64 + BitOperations.TrailingZeroCount(_occupiedBuckets[word])];
        }

        return null;
    }

    private Slab CreateSlab()
    {
        Slab slab = new(this, SlabSize, ClassSize, RegionCount);
        _slabBytes += SlabSize;
        return slab;
    }

    private void ReleaseSlab(Slab slab)
    {
        slab.Release();
        _slabBytes -= SlabSize;
    }

    private void Retire(Slab slab)
    {
        if (_spareEmpty is null && !_allocator.IsDisposed && !slab.IsReleased) _spareEmpty = slab;
        else ReleaseSlab(slab);
    }

    private void Link(Slab slab)
    {
        int bucket = slab.FreeCount;
        Slab? head = _headByFreeCount[bucket];
        slab.PreviousInBin = null;
        slab.NextInBin = head;
        if (head is not null) head.PreviousInBin = slab;
        _headByFreeCount[bucket] = slab;
        _occupiedBuckets[bucket / 64] |= 1UL << (bucket % 64);
    }

    private void Unlink(Slab slab)
    {
        int bucket = slab.FreeCount;
        if (slab.PreviousInBin is null)
        {
            _headByFreeCount[bucket] = slab.NextInBin;
            if (slab.NextInBin is null) _occupiedBuckets[bucket / 64] &= ~(1UL << (bucket % 64));
        }
        else
        {
            slab.PreviousInBin.NextInBin = slab.NextInBin;
        }

        if (slab.NextInBin is not null) slab.NextInBin.PreviousInBin = slab.PreviousInBin;
        slab.NextInBin = null;
        slab.PreviousInBin = null;
    }
}
