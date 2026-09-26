// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers.Slab;

namespace Nethermind.Core.Buffers;

/// <summary>
/// An <see cref="IRefCountingMemoryProvider"/> over a <see cref="SlabMemoryAllocator"/>: the memory
/// is native, sized to the allocator's size class, and freed on the last release.
/// </summary>
/// <remarks>Disposing the provider disposes the allocator; memory still leased is freed as it is released.</remarks>
public sealed class SlabRefCountingMemoryProvider(SlabMemoryAllocator allocator) : IRefCountingMemoryProvider, IDisposable
{
    public SlabMemoryAllocator Allocator { get; } = allocator;

    public RefCountingMemory Rent(int length) => RefCountingMemory.OwningNative(Allocator, Allocator.Allocate(length), length);

    public int RoundUpCapacity(int length) => Allocator.RoundUpCapacity(length);

    public void Dispose() => Allocator.Dispose();
}
