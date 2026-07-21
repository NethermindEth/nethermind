// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;

namespace Nethermind.State.Pbt.Test;

/// <summary>Hands out pooled memory and keeps a handle on each buffer, for <see cref="CountUnreleased"/>.</summary>
public sealed class TrackingMemoryProvider : IRefCountingMemoryProvider
{
    private readonly List<RefCountingMemory> _rented = [];

    public IReadOnlyList<RefCountingMemory> Rented => _rented;

    public RefCountingMemory Rent(int length)
    {
        RefCountingMemory memory = PooledRefCountingMemoryProvider.Instance.Rent(length);
        _rented.Add(memory);
        return memory;
    }

    /// <summary>
    /// How many of <paramref name="memories"/> still hold a lease — none should, once the updater has
    /// returned. A fully released buffer refuses a fresh lease, an outstanding one takes it.
    /// </summary>
    public static int CountUnreleased(IEnumerable<RefCountingMemory> memories)
    {
        int unreleased = 0;
        foreach (RefCountingMemory memory in memories)
        {
            try
            {
                memory.AcquireLease();
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            ((IDisposable)memory).Dispose();
            unreleased++;
        }

        return unreleased;
    }
}
