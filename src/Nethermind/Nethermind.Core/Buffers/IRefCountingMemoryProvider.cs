// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Allocates the <see cref="RefCountingMemory"/> a producer writes its result into, so the producer
/// need not hardcode a buffer source (pool, arena, …). The returned memory's span is exactly the
/// requested length; the caller releases the backing buffer by disposing it.
/// </summary>
public interface IRefCountingMemoryProvider
{
    RefCountingMemory Rent(int length);

    /// <summary>
    /// The <see cref="RefCountingMemory.Capacity"/> a <see cref="Rent"/> of <paramref name="length"/>
    /// bytes would get, so a producer can tell whether re-renting a shrunk value frees anything.
    /// </summary>
    int RoundUpCapacity(int length) => length == 0 ? 0 : ArrayPoolUtilities.GetPowerOfTwoCapacity(length);
}
