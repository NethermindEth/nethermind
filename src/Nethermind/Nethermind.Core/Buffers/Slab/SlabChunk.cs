// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>One native chunk, handing out power-of-two page runs through a binary buddy allocator.</summary>
/// <remarks>
/// All buddy state is managed and indexed by page, so nothing here reads the native memory. A run of
/// order <c>o</c> covers <c>2^o</c> pages starting at a page index that is a multiple of <c>2^o</c>;
/// its buddy is the run at <c>page ^ (1 &lt;&lt; o)</c>, and freeing merges with a free buddy of the
/// same order all the way up. Not thread-safe: the allocator serializes access with its page lock.
/// </remarks>
internal sealed unsafe class SlabChunk
{
    private const int None = -1;

    private byte* _base;
    private readonly int _pageSize;
    private readonly int _maxOrder;
    private readonly byte[] _blockOrder;
    private readonly bool[] _isFree;
    private readonly int[] _nextFree;
    private readonly int[] _previousFree;
    private readonly int[] _freeHead;

    public SlabChunk(int chunkSize, int pageSize)
    {
        int pageCount = chunkSize / pageSize;
        Debug.Assert(BitOperations.IsPow2(pageCount));
        _pageSize = pageSize;
        _maxOrder = BitOperations.Log2((uint)pageCount);
        _blockOrder = new byte[pageCount];
        _isFree = new bool[pageCount];
        _nextFree = new int[pageCount];
        _previousFree = new int[pageCount];
        _freeHead = new int[_maxOrder + 1];
        _freeHead.AsSpan().Fill(None);
        _base = (byte*)NativeMemory.Alloc((nuint)chunkSize);
        // Only after a successful allocation, so a failed one leaves nothing to remove.
        GC.AddMemoryPressure(chunkSize);
        Size = chunkSize;
        Push(0, _maxOrder);
    }

    ~SlabChunk() => Release();

    public int Size { get; }
    public int MaxOrder => _maxOrder;
    public bool IsEmpty => _freeHead[_maxOrder] != None;
    public bool IsReleased => _base is null;

    // Safety: page < pageCount, and the chunk stays mapped until Release.
    public byte* PagePointer(int page) => _base + (long)page * _pageSize;

    /// <summary>Takes a free run of <c>2^order</c> pages, splitting a larger one if needed.</summary>
    /// <returns>The first page of the run, or -1 when no free run is large enough.</returns>
    public int TryAllocatePages(int order)
    {
        int available = order;
        while (available <= _maxOrder && _freeHead[available] == None) available++;
        if (available > _maxOrder) return None;

        int page = _freeHead[available];
        Unlink(page, available);
        while (available > order)
        {
            available--;
            Push(page + (1 << available), available);
        }

        _blockOrder[page] = (byte)order;
        return page;
    }

    /// <summary>Returns a run taken by <see cref="TryAllocatePages"/>, merging it with its free buddies.</summary>
    public void FreePages(int page, int order)
    {
        Debug.Assert(!_isFree[page] && _blockOrder[page] == order, "freeing a run that is not allocated at this order");
        while (order < _maxOrder)
        {
            int buddy = page ^ (1 << order);
            if (!_isFree[buddy] || _blockOrder[buddy] != order) break;
            Unlink(buddy, order);
            page = Math.Min(page, buddy);
            order++;
        }

        Push(page, order);
    }

    /// <summary>Frees the native memory; the chunk must not be used afterwards.</summary>
    public void Release()
    {
        if (_base is null) return;
        NativeMemory.Free(_base);
        GC.RemoveMemoryPressure(Size);
        GC.SuppressFinalize(this);
        _base = null;
    }

    private void Push(int page, int order)
    {
        _blockOrder[page] = (byte)order;
        _isFree[page] = true;
        _previousFree[page] = None;
        _nextFree[page] = _freeHead[order];
        if (_freeHead[order] != None) _previousFree[_freeHead[order]] = page;
        _freeHead[order] = page;
    }

    private void Unlink(int page, int order)
    {
        int next = _nextFree[page];
        int previous = _previousFree[page];
        if (previous == None) _freeHead[order] = next;
        else _nextFree[previous] = next;
        if (next != None) _previousFree[next] = previous;
        _isFree[page] = false;
    }
}
