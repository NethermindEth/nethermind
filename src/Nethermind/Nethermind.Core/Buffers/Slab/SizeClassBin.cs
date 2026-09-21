// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>One region of a slab: the slab plus the region's index in it.</summary>
internal readonly record struct RegionHandle(Slab Slab, int Index);

/// <summary>The shared slabs of one size class; thread caches move regions in and out in batches under its lock.</summary>
/// <remarks>
/// Slabs with a free region sit in a doubly linked list; one emptied slab is retained out of the
/// list so a bin oscillating around a slab boundary does not churn pages, and further emptied slabs
/// return their pages. A region parked in a thread cache counts as allocated, so an empty slab really
/// has no outstanding region. Lock order is bin then page: the allocator's page lock is taken inside.
/// </remarks>
internal sealed class SizeClassBin(SlabMemoryAllocator allocator, int classIndex, int classSize, int slabOrder, int regionCount, int threadCacheCapacity)
{
    private readonly Lock _lock = new();
    private Slab? _nonFullHead;
    private Slab? _spareEmpty;
    private long _allocatedRegions;

    public int ClassIndex { get; } = classIndex;
    public int ClassSize { get; } = classSize;
    public int SlabOrder { get; } = slabOrder;
    public int RegionCount { get; } = regionCount;
    public int ThreadCacheCapacity { get; } = threadCacheCapacity;

    /// <summary>Bytes handed out by this bin and not yet given back, regions parked in thread caches included.</summary>
    public long AllocatedBytes => Volatile.Read(ref _allocatedRegions) * ClassSize;

    public int AllocateBatch(Span<RegionHandle> into)
    {
        lock (_lock)
        {
            int filled = 0;
            while (filled < into.Length)
            {
                Slab? slab = _nonFullHead;
                if (slab is null)
                {
                    slab = _spareEmpty ?? allocator.CreateSlab(this);
                    _spareEmpty = null;
                    Link(slab);
                }

                while (filled < into.Length && !slab.IsFull) into[filled++] = new RegionHandle(slab, slab.Pop());
                if (slab.IsFull) Unlink(slab);
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
                bool wasFull = slab.IsFull;
                slab.Push(handle.Index);
                if (wasFull) Link(slab);
                if (slab.IsEmpty) Retire(slab);
            }

            _allocatedRegions -= handles.Length;
        }
    }

    /// <summary>Drops the retained empty slab, once the allocator no longer serves requests.</summary>
    public void ReleaseSpare()
    {
        lock (_lock)
        {
            if (_spareEmpty is null) return;
            allocator.ReleaseSlab(_spareEmpty);
            _spareEmpty = null;
        }
    }

    private void Retire(Slab slab)
    {
        Unlink(slab);
        if (_spareEmpty is null && !allocator.IsDisposed) _spareEmpty = slab;
        else allocator.ReleaseSlab(slab);
    }

    private void Link(Slab slab)
    {
        slab.PreviousInBin = null;
        slab.NextInBin = _nonFullHead;
        if (_nonFullHead is not null) _nonFullHead.PreviousInBin = slab;
        _nonFullHead = slab;
    }

    private void Unlink(Slab slab)
    {
        if (slab.PreviousInBin is null) _nonFullHead = slab.NextInBin;
        else slab.PreviousInBin.NextInBin = slab.NextInBin;
        if (slab.NextInBin is not null) slab.NextInBin.PreviousInBin = slab.PreviousInBin;
        slab.NextInBin = null;
        slab.PreviousInBin = null;
    }
}
