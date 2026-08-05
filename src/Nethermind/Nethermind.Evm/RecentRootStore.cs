// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

/// <summary>Key/commitment derivations and the pre-state reference check for <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> recent roots.</summary>
public static class RecentRootStore
{
    private const int HashLength = 32;
    private const int AddressLength = Address.Size;
    private const int SlotLength = sizeof(ulong);

    public static ValueHash256 SourceId(Address sourceAddress, in ValueHash256 salt)
    {
        Span<byte> input = stackalloc byte[AddressLength + HashLength];
        sourceAddress.Bytes.CopyTo(input);
        salt.Bytes.CopyTo(input.Slice(AddressLength));
        return ValueKeccak.Compute(input);
    }

    public static ValueHash256 EntryHash(in ValueHash256 sourceId, ulong slot, in ValueHash256 root)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength + HashLength];
        Eip8272Constants.RecentRootEntryDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), slot);
        root.Bytes.CopyTo(input.Slice(HashLength + HashLength + SlotLength));
        return ValueKeccak.Compute(input);
    }

    public static ValueHash256 StorageKey(in ValueHash256 sourceId, ulong ringIndex)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength];
        Eip8272Constants.RecentRootStorageDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), ringIndex);
        return ValueKeccak.Compute(input);
    }

    public static bool IsReferenceValid(IWorldState state, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot) =>
        IsReferenceValid(state, ReferenceCell(sourceId, slot), sourceId, slot, root, currentSlot);

    /// <summary>
    /// Checks a reference against the commitment held in <paramref name="cell"/>, which the caller has
    /// already derived — the ring-buffer key costs a Keccak the gas schedule pays for once per reference.
    /// </summary>
    public static bool IsReferenceValid(IWorldState state, in StorageCell cell, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot)
    {
        ulong age = currentSlot - slot; // unsigned: a future or same slot underflows and is rejected below
        if (age is 0 || age > Eip8272Constants.RecentRootUsableWindow)
        {
            return false;
        }

        ReadOnlySpan<byte> stored = state.Get(cell);
        if (stored.Length > HashLength)
        {
            return false;
        }

        // Storage values are minimal big-endian; pad to a full word before comparing.
        Span<byte> padded = stackalloc byte[HashLength];
        stored.CopyTo(padded.Slice(HashLength - stored.Length));
        return new ValueHash256(padded) == EntryHash(sourceId, slot, root);
    }

    public static void Write(IWorldState state, Address sourceAddress, in ValueHash256 salt, in ValueHash256 root, ulong currentSlot, IReleaseSpec spec)
    {
        ValueHash256 sourceId = SourceId(sourceAddress, salt);
        StorageCell cell = RingBufferCell(sourceId, currentSlot % Eip8272Constants.RecentRootLength);
        ValueHash256 entryHash = EntryHash(sourceId, currentSlot, root);
        state.Set(cell, entryHash.Bytes.WithoutLeadingZeros().ToArray());
    }

    /// <summary>The predeploy storage cell a reference to <paramref name="slot"/> reads.</summary>
    public static StorageCell ReferenceCell(in ValueHash256 sourceId, ulong slot) =>
        RingBufferCell(sourceId, slot % Eip8272Constants.RecentRootLength);

    private static StorageCell RingBufferCell(in ValueHash256 sourceId, ulong ringIndex) =>
        new(Eip8272Constants.RecentRootAddress, StorageKey(sourceId, ringIndex).ToUInt256());
}
