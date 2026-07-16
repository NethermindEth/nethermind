// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

/// <summary>
/// Allocates the <see cref="RefCountingMemory"/> a producer writes its result into, so the producer
/// need not hardcode a buffer source (pool, arena, …). The returned memory's span is exactly the
/// requested length; the caller releases the backing buffer by disposing it.
/// </summary>
public interface IRefCountingMemoryProvider
{
    /// <summary>Allocates a writable <see cref="RefCountingMemory"/> whose span is <paramref name="length"/> bytes.</summary>
    RefCountingMemory Rent(int length);
}
