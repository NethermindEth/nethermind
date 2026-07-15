// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// Pure key/commitment derivations and the pre-state reference check for
/// <see href="https://eips.ethereum.org/EIPS/eip-8272">EIP-8272</see> recent roots.
/// </summary>
/// <remarks>
/// The <c>RECENT_ROOT_ADDRESS</c> system contract keeps, for each source, a ring buffer of
/// <see cref="Eip8272Constants.RecentRootLength"/> entries. Entry <c>i = slot mod RECENT_ROOT_LENGTH</c> stores
/// <c>entry_hash = keccak(RECENT_ROOT_ENTRY_DOMAIN ‖ source_id ‖ uint64_be(slot) ‖ root)</c> under
/// <c>storage_key = keccak(RECENT_ROOT_STORAGE_DOMAIN ‖ source_id ‖ uint64_be(i))</c>. Because <c>entry_hash</c>
/// commits to the <em>raw</em> slot (not the ring index), an entry that has been aliased over by a newer slot
/// sharing the same ring index can never satisfy a stale reference.
/// </remarks>
public static class RecentRootStore
{
    private const int HashLength = 32;
    private const int AddressLength = Address.Size;
    private const int SlotLength = sizeof(ulong);

    /// <summary>
    /// Derives <c>source_id = keccak256(pad32(source_address) ‖ salt)</c>.
    /// </summary>
    /// <remarks>
    /// Consensus-critical and spec-ambiguous: EIP-8272 states addresses are 20 bytes, but the only existing
    /// implementation left-pads the address to 32 bytes before hashing. This method follows the 32-byte padding
    /// for cross-client interop; switch to the 20-byte form if the spec is clarified that way.
    /// </remarks>
    public static ValueHash256 SourceId(Address sourceAddress, in ValueHash256 salt)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength];
        // Upper 12 bytes stay zero (pad32); the address occupies the low 20 bytes of the first word.
        sourceAddress.Bytes.CopyTo(input.Slice(HashLength - AddressLength, AddressLength));
        salt.Bytes.CopyTo(input.Slice(HashLength));
        return ValueKeccak.Compute(input);
    }

    /// <summary>
    /// Derives the entry commitment
    /// <c>keccak256(RECENT_ROOT_ENTRY_DOMAIN ‖ source_id ‖ uint64_be(slot) ‖ root)</c> for the given raw slot.
    /// </summary>
    public static ValueHash256 EntryHash(in ValueHash256 sourceId, ulong slot, in ValueHash256 root)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength + HashLength];
        Eip8272Constants.RecentRootEntryDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), slot);
        root.Bytes.CopyTo(input.Slice(HashLength + HashLength + SlotLength));
        return ValueKeccak.Compute(input);
    }

    /// <summary>
    /// Derives the storage key
    /// <c>keccak256(RECENT_ROOT_STORAGE_DOMAIN ‖ source_id ‖ uint64_be(ringIndex))</c> for a ring-buffer slot,
    /// where <c>ringIndex = slot mod RECENT_ROOT_LENGTH</c>.
    /// </summary>
    public static ValueHash256 StorageKey(in ValueHash256 sourceId, ulong ringIndex)
    {
        Span<byte> input = stackalloc byte[HashLength + HashLength + SlotLength];
        Eip8272Constants.RecentRootStorageDomain.Bytes.CopyTo(input);
        sourceId.Bytes.CopyTo(input.Slice(HashLength));
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(HashLength + HashLength, SlotLength), ringIndex);
        return ValueKeccak.Compute(input);
    }

    /// <summary>
    /// Checks whether a recent-root reference is valid against pre-state: it must fall inside the usable window
    /// (<c>1 &lt;= currentSlot - slot &lt;= RECENT_ROOT_USABLE_WINDOW</c>) and the ring-buffer cell for its slot
    /// must hold exactly <see cref="EntryHash"/> for that <c>(sourceId, slot, root)</c>.
    /// </summary>
    public static bool IsReferenceValid(IWorldState state, in ValueHash256 sourceId, ulong slot, in ValueHash256 root, ulong currentSlot)
    {
        // Unsigned subtraction: a future or same slot yields age 0 or an underflowed huge value, both rejected here.
        ulong age = currentSlot - slot;
        if (age is 0 || age > Eip8272Constants.RecentRootUsableWindow)
        {
            return false;
        }

        StorageCell cell = RingBufferCell(sourceId, slot % Eip8272Constants.RecentRootLength);
        ReadOnlySpan<byte> stored = state.Get(cell);

        // Storage values are held as minimal big-endian bytes; pad back to a full word before comparing.
        Span<byte> padded = stackalloc byte[HashLength];
        stored.CopyTo(padded.Slice(HashLength - stored.Length));
        return new ValueHash256(padded) == EntryHash(sourceId, slot, root);
    }

    /// <summary>
    /// Writes the recent-root entry for <c>currentSlot</c> into <c>RECENT_ROOT_ADDRESS</c>, overwriting whatever
    /// currently occupies its ring-buffer cell (last write wins per ring index).
    /// </summary>
    /// <param name="spec">
    /// The active release spec. Reserved for fork gating and a future spec-provided address override, mirroring
    /// the other system-contract stores; the derivation itself does not depend on it.
    /// </param>
    public static void Write(IWorldState state, Address sourceAddress, in ValueHash256 salt, in ValueHash256 root, ulong currentSlot, IReleaseSpec spec)
    {
        ValueHash256 sourceId = SourceId(sourceAddress, salt);
        StorageCell cell = RingBufferCell(sourceId, currentSlot % Eip8272Constants.RecentRootLength);
        ValueHash256 entryHash = EntryHash(sourceId, currentSlot, root);
        state.Set(cell, entryHash.Bytes.WithoutLeadingZeros().ToArray());
    }

    private static StorageCell RingBufferCell(in ValueHash256 sourceId, ulong ringIndex) =>
        new(Eip8272Constants.RecentRootAddress, StorageKey(sourceId, ringIndex).ToUInt256());
}
