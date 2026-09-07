// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Buffers;

namespace Nethermind.State.Pbt.Test;

/// <summary>Hands out pooled memory and keeps a handle on each buffer, for <see cref="CountUnreleased"/>.</summary>
public sealed class TrackingMemoryProvider : IRefCountingMemoryProvider
{
    private readonly List<RefCountingMemory> _rented = [];
    private readonly List<int> _requestedLengths = [];
    private readonly Lock _lock = new();
    private int _rentCount;

    public IReadOnlyList<RefCountingMemory> Rented => _rented;
    public IReadOnlyList<int> RequestedLengths => _requestedLengths;
    public int RentCount => Volatile.Read(ref _rentCount);
    public int? ThrowOnRent { get; set; }
    public byte? FillByte { get; set; }

    /// <remarks>Locked: a parallel fold rents from every one of its worker threads.</remarks>
    public RefCountingMemory Rent(int length)
    {
        int rentNumber = Interlocked.Increment(ref _rentCount);
        if (rentNumber == ThrowOnRent) throw new InvalidOperationException("Configured memory-rent failure.");
        RefCountingMemory memory = PooledRefCountingMemoryProvider.Instance.Rent(length);
        if (FillByte is { } fillByte) memory.GetSpan().Fill(fillByte);
        lock (_lock)
        {
            _rented.Add(memory);
            _requestedLengths.Add(length);
        }
        return memory;
    }

    /// <summary>
    /// How many of <paramref name="memories"/> still hold a lease, including store-owned outputs.
    /// A fully released buffer refuses a fresh lease, an outstanding one takes it.
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
