// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
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
    private Entry _entry0;
    private Entry _entry1;
    private Entry _entry2;
    private Entry _entry3;
    private nint _next = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Address GetOrCreate(ReadOnlySpan<byte> addressBytes)
    {
        if (addressBytes.Length != Address.Size)
            throw new ArgumentException("An address must contain 20 bytes.", nameof(addressBytes));

        return GetOrCreate(
            BinaryPrimitives.ReadUInt64BigEndian(addressBytes[12..]),
            BinaryPrimitives.ReadUInt64BigEndian(addressBytes[4..]),
            BinaryPrimitives.ReadUInt32BigEndian(addressBytes));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Address GetOrCreate(in UInt256 word)
        => GetOrCreate(word.u0, word.u1, word.u2 & uint.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Address GetOrCreate(ulong low, ulong middle, ulong high)
    {
        if (_entry0.Matches(low, middle, high)) return _entry0.Address!;
        return GetOrCreateBehindFront(low, middle, high);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Address GetOrCreateBehindFront(ulong low, ulong middle, ulong high)
    {
        if (_entry1.Matches(low, middle, high)) return _entry1.Address!;
        if (_entry2.Matches(low, middle, high)) return _entry2.Address!;
        if (_entry3.Matches(low, middle, high)) return _entry3.Address!;

        Span<byte> bytes = stackalloc byte[Address.Size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)high);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[4..], middle);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[12..], low);
        Address created = new(bytes);
        // Rotate the three older entries without copying every key on a miss.
        if (_next == 1) _entry1 = _entry0;
        else if (_next == 2) _entry2 = _entry0;
        else _entry3 = _entry0;
        _next = _next == 3 ? 1 : _next + 1;
        _entry0 = new Entry(created, low, middle, high);
        return created;
    }

    private readonly struct Entry(Address address, ulong low, ulong middle, ulong high)
    {
        public readonly Address Address = address;
        private readonly ulong _low = low;
        private readonly ulong _middle = middle;
        private readonly ulong _high = high;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(ulong low, ulong middle, ulong high)
            => _low == low && _middle == middle && _high == high && Address is not null;
    }
}
