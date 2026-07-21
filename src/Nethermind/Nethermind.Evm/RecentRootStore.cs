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

    // Consensus-critical, spec-ambiguous: 32-byte-padded address matches the only existing implementation (spec text says 20 bytes).
    public static ValueHash256 SourceId(Address sourceAddress, in ValueHash256 salt)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength];
        sourceAddress.Bytes.CopyTo(input.Slice(HashLength - AddressLength, AddressLength));
        salt.Bytes.CopyTo(input.Slice(HashLength));
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

    public static bool IsReferenceValid(IWorldState state, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot)
    {
        ulong age = currentSlot - slot; // unsigned: a future or same slot underflows and is rejected below
        if (age is 0 || age > Eip8272Constants.RecentRootUsableWindow)
        {
            return false;
        }

        StorageCell cell = RingBufferCell(sourceId, slot % Eip8272Constants.RecentRootLength);
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

    public static bool AreReferencesValid(IWorldState state, ReadOnlySpan<(ValueHash256 SourceId, ulong Slot, ValueHash256 Root)> references, ulong currentSlot)
    {
        if (references.Length > Eip8272Constants.MaxRecentRootReferences)
        {
            return false;
        }

        foreach ((ValueHash256 sourceId, ulong slot, ValueHash256 root) in references)
        {
            if (!IsReferenceValid(state, sourceId, slot, root, currentSlot))
            {
                return false;
            }
        }

        return true;
    }

    private static StorageCell RingBufferCell(in ValueHash256 sourceId, ulong ringIndex) =>
        new(Eip8272Constants.RecentRootAddress, StorageKey(sourceId, ringIndex).ToUInt256());
}
