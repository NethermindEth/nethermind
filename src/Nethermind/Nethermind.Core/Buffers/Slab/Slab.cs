// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Buffers.Slab;

/// <summary>One native block carved into equal regions of one size class, with a managed LIFO free set.</summary>
/// <remarks>
/// The free set never touches the native memory, so a stale span written after its release can only
/// corrupt user data, and <see cref="Release"/> is safe at any time. Not thread-safe: its bin
/// serializes access under the bin lock.
/// </remarks>
internal sealed unsafe class Slab
{
    private byte* _base;
    private readonly int _regionSize;
    private readonly int[] _freeIndices;
    private int _freeCount;

    public Slab(SizeClassBin bin, int size, int regionSize, int regionCount)
    {
        Bin = bin;
        Size = size;
        _regionSize = regionSize;
        RegionCount = regionCount;
        _freeIndices = new int[regionCount];
        // Filled so the first pops hand out region 0, 1, 2, … — ascending addresses for a fresh slab.
        for (int i = 0; i < regionCount; i++) _freeIndices[i] = regionCount - 1 - i;
        _freeCount = regionCount;
        _base = (byte*)NativeMemory.Alloc((nuint)size);
        // Only after a successful allocation, so a failed one leaves nothing to remove.
        GC.AddMemoryPressure(size);
    }

    ~Slab() => Bin.ReleaseUnreachable(this);

    public SizeClassBin Bin { get; }
    public int Size { get; }
    public int RegionCount { get; }
    public int FreeCount => _freeCount;
    public bool IsFull => _freeCount == 0;
    public bool IsEmpty => _freeCount == RegionCount;
    /// <summary>Whether the native block is already freed, which its finalizer may do before a dead thread's cache hands the last regions back.</summary>
    public bool IsReleased => _base is null;
    public Slab? NextInBin { get; set; }
    public Slab? PreviousInBin { get; set; }

    // Safety: index < RegionCount, so the region lies inside this slab's block, which stays mapped until Release.
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

    /// <summary>Frees the native block; the slab must not be used afterwards.</summary>
    public void Release()
    {
        if (_base is null) return;
        NativeMemory.Free(_base);
        GC.RemoveMemoryPressure(Size);
        GC.SuppressFinalize(this);
        _base = null;
    }
}
