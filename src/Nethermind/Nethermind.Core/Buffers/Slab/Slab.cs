// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>A page run carved into equal regions of one size class, with a managed LIFO free set.</summary>
/// <remarks>Not thread-safe: its bin serializes access under the bin lock.</remarks>
internal sealed unsafe class Slab
{
    private readonly SlabChunk _chunk;
    private readonly byte* _base;
    private readonly int _regionSize;
    private readonly int[] _freeIndices;
    private int _freeCount;

    public Slab(SizeClassBin bin, SlabChunk chunk, int firstPage, int order, int regionSize, int regionCount)
    {
        Bin = bin;
        _chunk = chunk;
        FirstPage = firstPage;
        Order = order;
        _base = chunk.PagePointer(firstPage);
        _regionSize = regionSize;
        RegionCount = regionCount;
        _freeIndices = new int[regionCount];
        // Filled so the first pops hand out region 0, 1, 2, … — ascending addresses for a fresh slab.
        for (int i = 0; i < regionCount; i++) _freeIndices[i] = regionCount - 1 - i;
        _freeCount = regionCount;
    }

    public SizeClassBin Bin { get; }
    public SlabChunk Chunk => _chunk;
    public int FirstPage { get; }
    public int Order { get; }
    public int RegionCount { get; }
    public int AllocatedCount => RegionCount - _freeCount;
    public bool IsFull => _freeCount == 0;
    public bool IsEmpty => _freeCount == RegionCount;
    public Slab? NextInBin { get; set; }
    public Slab? PreviousInBin { get; set; }

    // Safety: index < RegionCount, so the region lies inside this slab's page run of a mapped chunk.
    public byte* RegionPointer(int index) => _base + (long)index * _regionSize;

    public int Pop()
    {
        Debug.Assert(_freeCount > 0);
        return _freeIndices[--_freeCount];
    }

    public void Push(int index)
    {
        Debug.Assert((uint)index < (uint)RegionCount && _freeCount < RegionCount);
        _freeIndices[_freeCount++] = index;
    }
}
