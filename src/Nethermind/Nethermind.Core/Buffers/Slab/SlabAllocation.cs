// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers.Slab;

/// <summary>A block handed out by <see cref="SlabMemoryAllocator.Allocate"/>; give it back with <see cref="SlabMemoryAllocator.Free"/>.</summary>
/// <remarks>
/// <see cref="Owner"/> is the slab the block was carved from (null for a dedicated allocation) and
/// <see cref="Index"/> its region there, so freeing needs no address lookup. The block is valid
/// until it is freed; <see cref="Capacity"/> may exceed the requested size.
/// </remarks>
public readonly unsafe struct SlabAllocation
{
    internal SlabAllocation(byte* pointer, int capacity, object? owner, int index)
    {
        Pointer = pointer;
        Capacity = capacity;
        Owner = owner;
        Index = index;
    }

    public byte* Pointer { get; }
    public int Capacity { get; }
    internal object? Owner { get; }
    internal int Index { get; }
}
