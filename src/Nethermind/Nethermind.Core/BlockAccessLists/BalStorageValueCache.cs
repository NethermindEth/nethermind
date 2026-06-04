// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Prefetch destination indexed directly by the dense global read ordinal from
/// <see cref="BalStorageReadPlan"/> - O(1) store/lookup with no hashing and no eviction, sized
/// once to the block's exact declared-read count. Used when a block's read set is too large for
/// the fixed-capacity associative cache (which would conflict-evict).
/// </summary>
/// <remarks>
/// Each ordinal has exactly one prefetch writer, so publication uses a per-slot release/acquire on
/// a state byte rather than a CAS: <see cref="Set"/> writes the value then releases the
/// <c>Ready</c> flag; <see cref="TryGet"/> acquires the flag before reading the value. A loaded
/// zero/absent slot is <c>Ready</c> with a zero/empty value - distinct from <c>Empty</c>
/// ("not loaded yet"), which is what lets a reader tell "known zero" from "must read myself".
/// Backing arrays are pooled and released on <see cref="Dispose"/>.
/// </remarks>
public sealed class BalStorageValueCache : IDisposable
{
    private const byte Empty = 0;
    private const byte Ready = 1;

    private byte[]?[] _values;
    private byte[] _state;
    private readonly int _count;

    /// <summary>Number of ordinal slots (the block's total declared reads).</summary>
    public int Count => _count;

    public BalStorageValueCache(int count)
    {
        _count = count;
        _values = count == 0 ? [] : ArrayPool<byte[]?>.Shared.Rent(count);
        _state = count == 0 ? [] : ArrayPool<byte>.Shared.Rent(count);
        // Rented arrays carry the previous tenant's contents; state must start Empty.
        _state.AsSpan(0, count).Clear();
    }

    /// <summary>
    /// Publishes the resolved value for <paramref name="ordinal"/>. A <c>null</c>/empty value
    /// records a known-zero slot (still <c>Ready</c>). Called once per ordinal by its prefetch writer.
    /// </summary>
    public void Set(int ordinal, byte[]? value)
    {
        _values[ordinal] = value;
        // Release: the value write above is visible to any reader that observes Ready.
        Volatile.Write(ref _state[ordinal], Ready);
    }

    /// <summary>
    /// Returns the published value for <paramref name="ordinal"/> if it has been loaded.
    /// <paramref name="value"/> is <c>null</c>/empty for a known-zero slot.
    /// </summary>
    /// <returns><c>true</c> if loaded (zero or non-zero); <c>false</c> if not yet loaded.</returns>
    public bool TryGet(int ordinal, out byte[]? value)
    {
        // Acquire: pairs with the release in Set so the value read below is current.
        if (Volatile.Read(ref _state[ordinal]) == Ready)
        {
            value = _values[ordinal];
            return true;
        }

        value = null;
        return false;
    }

    public void Dispose()
    {
        byte[]?[] values = _values;
        byte[] state = _state;
        _values = [];
        _state = [];

        if (values.Length > 0)
        {
            // Drop held value references before returning the array to the pool.
            values.AsSpan(0, _count).Clear();
            ArrayPool<byte[]?>.Shared.Return(values);
        }
        if (state.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(state);
        }
    }
}
