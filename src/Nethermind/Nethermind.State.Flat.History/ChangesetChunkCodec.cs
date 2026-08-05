// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History;

/// <summary>A single storage slot changed within an account entry of a changeset chunk. An empty
/// <see cref="Value"/> is a clear (the slot's value after the block is zero/unset).</summary>
public readonly record struct ChangesetSlotEntry(UInt256 Slot, ReadOnlyMemory<byte> Value);

/// <summary>
/// One address's changes within a changeset chunk, grouped the way a Block Access List groups by address rather
/// than as an independent flat list of account/storage rows. <see cref="AccountValue"/> empty with
/// <see cref="AccountChanged"/> true means the account was deleted.
/// </summary>
public readonly record struct ChangesetAccountEntry(
    Address Address,
    bool AccountChanged,
    ReadOnlyMemory<byte> AccountValue,
    IReadOnlyList<ChangesetSlotEntry> StorageChanges);

/// <summary>
/// Placeholder wire encoding for one changeset chunk's payload, grouped by address (BAL-shaped) rather than the
/// flat-key rows the read-path history columns use. Deliberately not tied to any real Block Access List type —
/// 39-2 is expected to replace this codec with one matching the actual BAL encoding (or the chain's own EIP-7928
/// encoding, once adopted) once that shape is settled; only the chunk boundary contract
/// (<see cref="ChangesetSidecarStore"/>'s block-major, 0-based contiguous chunk index) is meant to be load-bearing
/// from day one.
/// </summary>
public static class ChangesetChunkCodec
{
    private const int UInt256Length = 32;

    public static byte[] Encode(IReadOnlyList<ChangesetAccountEntry> entries)
    {
        int size = sizeof(int);
        foreach (ChangesetAccountEntry entry in entries)
        {
            size += Address.Size + 1 + sizeof(ushort) + entry.AccountValue.Length + sizeof(int);
            foreach (ChangesetSlotEntry slot in entry.StorageChanges)
            {
                size += UInt256Length + sizeof(ushort) + slot.Value.Length;
            }
        }

        byte[] buffer = new byte[size];
        Span<byte> destination = buffer;
        BinaryPrimitives.WriteInt32BigEndian(destination, entries.Count);
        destination = destination[sizeof(int)..];

        foreach (ChangesetAccountEntry entry in entries)
        {
            entry.Address.Bytes.CopyTo(destination);
            destination = destination[Address.Size..];
            destination[0] = entry.AccountChanged ? (byte)1 : (byte)0;
            destination = destination[1..];
            BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)entry.AccountValue.Length);
            destination = destination[sizeof(ushort)..];
            entry.AccountValue.Span.CopyTo(destination);
            destination = destination[entry.AccountValue.Length..];

            BinaryPrimitives.WriteInt32BigEndian(destination, entry.StorageChanges.Count);
            destination = destination[sizeof(int)..];
            foreach (ChangesetSlotEntry slot in entry.StorageChanges)
            {
                slot.Slot.ToBigEndian(destination[..UInt256Length]);
                destination = destination[UInt256Length..];
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)slot.Value.Length);
                destination = destination[sizeof(ushort)..];
                slot.Value.Span.CopyTo(destination);
                destination = destination[slot.Value.Length..];
            }
        }

        return buffer;
    }

    // The smallest an encoded entry/slot can legally be (all variable-length fields empty) — a declared count
    // that could not possibly fit in the remaining payload at this minimum is corrupt or hostile, and must be
    // rejected before it drives an allocation, never after (an attacker-controlled length prefix must not be
    // able to request an unbounded List<T> capacity and crash the process with OutOfMemoryException).
    private const int MinEncodedAccountEntrySize = Address.Size + 1 + sizeof(ushort) + sizeof(int);
    private const int MinEncodedSlotEntrySize = UInt256Length + sizeof(ushort);

    public static List<ChangesetAccountEntry> Decode(ReadOnlySpan<byte> payload)
    {
        int entryCount = BinaryPrimitives.ReadInt32BigEndian(payload);
        payload = payload[sizeof(int)..];
        ValidateDeclaredCount(entryCount, payload.Length, MinEncodedAccountEntrySize, "account entry");

        List<ChangesetAccountEntry> entries = new(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            Address address = new(payload[..Address.Size]);
            payload = payload[Address.Size..];
            bool accountChanged = payload[0] == 1;
            payload = payload[1..];
            ushort accountValueLength = BinaryPrimitives.ReadUInt16BigEndian(payload);
            payload = payload[sizeof(ushort)..];
            byte[] accountValue = payload[..accountValueLength].ToArray();
            payload = payload[accountValueLength..];

            int storageCount = BinaryPrimitives.ReadInt32BigEndian(payload);
            payload = payload[sizeof(int)..];
            ValidateDeclaredCount(storageCount, payload.Length, MinEncodedSlotEntrySize, "storage slot entry");
            List<ChangesetSlotEntry> storageChanges = new(storageCount);
            for (int j = 0; j < storageCount; j++)
            {
                UInt256 slot = new(payload[..UInt256Length], isBigEndian: true);
                payload = payload[UInt256Length..];
                ushort valueLength = BinaryPrimitives.ReadUInt16BigEndian(payload);
                payload = payload[sizeof(ushort)..];
                byte[] value = payload[..valueLength].ToArray();
                payload = payload[valueLength..];
                storageChanges.Add(new ChangesetSlotEntry(slot, value));
            }

            entries.Add(new ChangesetAccountEntry(address, accountChanged, accountValue, storageChanges));
        }

        return entries;
    }

    private static void ValidateDeclaredCount(int declaredCount, int remainingPayloadLength, int minEntrySize, string entryKind)
    {
        if (declaredCount < 0 || declaredCount > remainingPayloadLength / minEntrySize)
        {
            throw new InvalidOperationException(
                $"Changeset chunk payload declares {declaredCount} {entryKind} entries, which cannot fit in the {remainingPayloadLength} remaining bytes — the payload is corrupt or hostile.");
        }
    }
}
