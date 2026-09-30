// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>Key/commitment derivations and the pre-state reference check for <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> recent roots.</summary>
/// <remarks>Recent-root storage is written by the <c>RECENT_ROOT_ADDRESS</c> predeploy bytecode during ordinary execution, not by the client, so this type only derives keys and validates references against already-written state.</remarks>
public static class RecentRootStore
{
    private const int HashLength = 32;
    private const int AddressLength = Address.Size;
    private const int SlotLength = sizeof(ulong);

    /// <summary>The <c>source_id</c> keying a root source's ring buffer: <c>keccak256(source_address || salt)</c>.</summary>
    /// <remarks>EIP-8272 hashes the address unpadded (20 bytes); a left-padded preimage would fork from the predeploy.</remarks>
    [SkipLocalsInit]
    public static ValueHash256 SourceId(Address sourceAddress, in ValueHash256 salt)
    {
        Span<byte> input = stackalloc byte[AddressLength + HashLength];
        sourceAddress.Bytes.CopyTo(input);
        salt.Bytes.CopyTo(input.Slice(AddressLength));
        return ValueKeccak.Compute(input);
    }

    [SkipLocalsInit]
    public static ValueHash256 EntryHash(in ValueHash256 sourceId, ulong slot, in ValueHash256 root)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength + HashLength];
        Eip8272Constants.RecentRootEntryDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), slot);
        root.Bytes.CopyTo(input.Slice(HashLength + HashLength + SlotLength));
        return ValueKeccak.Compute(input);
    }

    [SkipLocalsInit]
    public static ValueHash256 StorageKey(in ValueHash256 sourceId, ulong ringIndex)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength];
        Eip8272Constants.RecentRootStorageDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), ringIndex);
        return ValueKeccak.Compute(input);
    }

    public static bool IsReferenceValid(IReadOnlyStateProvider state, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot) =>
        IsReferenceValid(state, ReferenceCell(sourceId, slot), sourceId, slot, root, currentSlot);

    /// <summary>Checks a reference against the commitment in <paramref name="cell"/>, which the caller has already derived.</summary>
    public static bool IsReferenceValid(IReadOnlyStateProvider state, in StorageCell cell, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot)
    {
        ulong age = currentSlot - slot; // unsigned: a future or same slot underflows and is rejected below
        if (age is 0 || age > Eip8272Constants.RecentRootUsableWindow)
        {
            return false;
        }

        state.Get(cell, out UInt256 stored);
        return stored.ToValueHash() == EntryHash(sourceId, slot, root);
    }

    /// <summary>True if every 72-byte <c>(source_id, slot, root)</c> tuple of <paramref name="tuples"/> is valid at <paramref name="currentSlot"/>.</summary>
    public static bool AreReferencesValid(IReadOnlyStateProvider state, ReadOnlySpan<byte> tuples, ulong currentSlot)
    {
        for (int offset = 0; offset < tuples.Length; offset += Eip8272Constants.RecentRootTupleLength)
        {
            (ValueHash256 sourceId, ulong slot, ValueHash256 root) = ReadTuple(tuples.Slice(offset));
            if (!IsReferenceValid(state, sourceId, slot, root, currentSlot)) return false;
        }

        return true;
    }

    /// <summary>True if a tuple of <paramref name="tuples"/> names a slot aged out of the ring buffer at <paramref name="currentSlot"/>,
    /// <c>current_slot - slot &gt;= RECENT_ROOT_LENGTH</c>, which no later slot can make valid again.</summary>
    /// <remarks>Treats aging out as final, which holds while <paramref name="currentSlot"/> only grows. A reorg onto a
    /// sibling head at a lower slot can bring a tuple back inside the window; the pool accepts that rare loss.</remarks>
    public static bool HasAgedOutReference(ReadOnlySpan<byte> tuples, ulong currentSlot)
    {
        for (int offset = 0; offset < tuples.Length; offset += Eip8272Constants.RecentRootTupleLength)
        {
            ulong slot = ReadTuple(tuples.Slice(offset)).Slot;
            if (slot < currentSlot && currentSlot - slot >= Eip8272Constants.RecentRootLength) return true;
        }

        return false;
    }

    /// <summary>Reads the <c>source_id(32) || slot(8, big-endian) || root(32)</c> tuple at the start of <paramref name="tuple"/>.</summary>
    public static (ValueHash256 SourceId, ulong Slot, ValueHash256 Root) ReadTuple(ReadOnlySpan<byte> tuple) =>
        (new ValueHash256(tuple.Slice(0, HashLength)),
         BinaryPrimitives.ReadUInt64BigEndian(tuple.Slice(HashLength, SlotLength)),
         new ValueHash256(tuple.Slice(HashLength + SlotLength, HashLength)));

    /// <summary>The predeploy storage cell a reference to <paramref name="slot"/> reads.</summary>
    public static StorageCell ReferenceCell(in ValueHash256 sourceId, ulong slot) =>
        RingBufferCell(sourceId, slot % Eip8272Constants.RecentRootLength);

    private static StorageCell RingBufferCell(in ValueHash256 sourceId, ulong ringIndex) =>
        new(Eip8272Constants.RecentRootAddress, StorageKey(sourceId, ringIndex).ToUInt256());
}
