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
    private Address? _entry0;
    private Address? _entry1;
    private Address? _entry2;
    private Address? _entry3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address GetOrCreate(ReadOnlySpan<byte> addressBytes)
    {
        Address? front = _entry0;
        if (front is not null && addressBytes.SequenceEqual(front.Bytes))
        {
            return front;
        }

        return GetOrCreateBehindFront(addressBytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Address GetOrCreateBehindFront(ReadOnlySpan<byte> addressBytes)
    {
        // No reordering on hit: promotion would cost field writes and buys nothing at this size.
        Address? entry = _entry1;
        if (entry is not null && addressBytes.SequenceEqual(entry.Bytes)) return entry;

        entry = _entry2;
        if (entry is not null && addressBytes.SequenceEqual(entry.Bytes)) return entry;

        entry = _entry3;
        if (entry is not null && addressBytes.SequenceEqual(entry.Bytes)) return entry;

        Address created = new(addressBytes);
        _entry3 = _entry2;
        _entry2 = _entry1;
        _entry1 = _entry0;
        _entry0 = created;
        return created;
    }
}
