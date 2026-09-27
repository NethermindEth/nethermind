// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>
/// Cache reusing <see cref="Address"/> instances popped from the EVM stack: the most recent address, then a
/// direct-mapped table, so multicalls and token loops cycling through a few dozen addresses do not allocate.
/// Two hot addresses sharing a slot evict each other. Not thread-safe; owned by a single
/// <see cref="VirtualMachine{TGasPolicy}"/>.
/// </summary>
public sealed class PoppedAddressCache
{
    private const int TableBits = 6;
    private const int TableSize = 1 << TableBits;
    private Entry _front;
    private Table _table;

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
        if (_front.Matches(low, middle, high)) return _front.Address!;
        return GetOrCreateBehindFront(low, middle, high);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private Address GetOrCreateBehindFront(ulong low, ulong middle, ulong high)
    {
        ref Entry slot = ref _table[Slot(low, middle, high)];
        if (!slot.Matches(low, middle, high))
        {
            Span<byte> bytes = stackalloc byte[Address.Size];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)high);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[4..], middle);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[12..], low);
            slot = new Entry(new Address(bytes), low, middle, high);
        }

        _front = slot;
        return slot.Address!;
    }

    /// <summary>The table slot of an address: the top bits of a multiplicative hash, so sequential and precompile addresses spread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Slot(ulong low, ulong middle, ulong high) =>
        (int)(((low ^ (middle * 0xC2B2AE3D27D4EB4FUL) ^ (high << 17)) * 0x9E3779B97F4A7C15UL) >> (64 - TableBits));

    [InlineArray(TableSize)]
    private struct Table
    {
        private Entry _element0;
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
