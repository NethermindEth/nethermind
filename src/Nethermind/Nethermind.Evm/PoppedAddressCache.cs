// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Four-entry FIFO cache reusing <see cref="Address"/> instances popped from the EVM stack —
/// repeated and small alternating address working sets dominate real traffic, so reuse removes
/// the per-pop allocation. Not thread-safe; owned by a single <see cref="VirtualMachine{TGasPolicy}"/>.
/// </summary>
public sealed class PoppedAddressCache
{
    private const int CacheSize = 4;

    [InlineArray(CacheSize)]
    private struct AddressEntries
    {
        private Address? _element0;
    }

    private AddressEntries _entries;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address GetOrCreate(ReadOnlySpan<byte> addressBytes)
    {
        Address? front = _entries[0];
        if (front is not null && front.Equals(addressBytes))
        {
            return front;
        }

        return GetOrCreateBehindFront(addressBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Address GetOrCreateBehindFront(ReadOnlySpan<byte> addressBytes)
    {
        // No reordering on hit: promotion would cost field writes and buys nothing at this size.
        for (int i = 1; i < CacheSize; i++)
        {
            Address? entry = _entries[i];
            if (entry is not null && entry.Equals(addressBytes)) return entry;
        }

        Address created = new(addressBytes);
        for (int i = CacheSize - 1; i > 0; i--)
        {
            _entries[i] = _entries[i - 1];
        }

        _entries[0] = created;
        return created;
    }
}
