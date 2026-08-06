// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History;

/// <summary>A single storage slot changed within an account entry of a changeset chunk. An empty
/// <see cref="Value"/> is a clear (the slot's value after the block is zero/unset). <see cref="PreValue"/> is the
/// value the slot held immediately before this change - empty means the slot did not exist before (its first-ever
/// change), mirroring <see cref="HistoryStoreV3"/>'s own tombstone convention. It is required so a peer-fed
/// backfill importer building v3 (pre-value) rows from this stream can materialize the oldest touch of a key
/// within an imported range directly - that value is not otherwise derivable from a post-value-only stream.</summary>
public readonly record struct ChangesetSlotEntry(UInt256 Slot, ReadOnlyMemory<byte> Value, ReadOnlyMemory<byte> PreValue);

/// <summary>
/// One address's changes within a changeset chunk, grouped the way a Block Access List groups by address rather
/// than as an independent flat list of account/storage rows. <see cref="AccountValue"/> empty with
/// <see cref="AccountChanged"/> true means the account was deleted. <see cref="AccountPreValue"/> is the account's
/// RLP immediately before this change (empty for a not-previously-existing account), for the same reason
/// <see cref="ChangesetSlotEntry.PreValue"/> exists.
/// </summary>
public readonly record struct ChangesetAccountEntry(
    Address Address,
    bool AccountChanged,
    ReadOnlyMemory<byte> AccountValue,
    ReadOnlyMemory<byte> AccountPreValue,
    IReadOnlyList<ChangesetSlotEntry> StorageChanges);

/// <summary>
/// Placeholder wire encoding for one changeset chunk's payload, grouped by address (BAL-shaped) rather than the
/// flat-key rows the read-path history columns use. Deliberately not tied to any real Block Access List type —
/// this codec is expected to be replaced with one matching the actual BAL encoding (or the chain's own EIP-7928
/// encoding, once adopted) once that shape is settled; only the chunk boundary contract
/// (<see cref="ChangesetSidecarStore"/>'s block-major, 0-based contiguous chunk index, and <see cref="EncodeChunked"/>'s
/// per-chunk independent decodability) is meant to be load-bearing from day one. The wire format itself is not
/// public yet (no external consumers), so its shape changes freely as needs are discovered. Only the post-value
/// projection of this stream (<see cref="ChangesetAccountEntry.AccountValue"/>/<see cref="ChangesetSlotEntry.Value"/>,
/// ignoring the pre-values) is what any changeset hash or BAL-shaped verification is computed over - the pre-values
/// are consumed only by a v3 row-building importer, never by verification.
/// </summary>
public static class ChangesetChunkCodec
{
    private const int UInt256Length = 32;
    private const int EntryCountHeaderLength = sizeof(int);

    public static byte[] Encode(IReadOnlyList<ChangesetAccountEntry> entries)
    {
        int size = EntryCountHeaderLength;
        foreach (ChangesetAccountEntry entry in entries)
        {
            size += EncodedEntrySize(entry);
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
            WriteLengthPrefixed(ref destination, entry.AccountValue.Span);
            WriteLengthPrefixed(ref destination, entry.AccountPreValue.Span);

            BinaryPrimitives.WriteInt32BigEndian(destination, entry.StorageChanges.Count);
            destination = destination[sizeof(int)..];
            foreach (ChangesetSlotEntry slot in entry.StorageChanges)
            {
                slot.Slot.ToBigEndian(destination[..UInt256Length]);
                destination = destination[UInt256Length..];
                WriteLengthPrefixed(ref destination, slot.Value.Span);
                WriteLengthPrefixed(ref destination, slot.PreValue.Span);
            }
        }

        return buffer;
    }

    private static void WriteLengthPrefixed(ref Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value.Length);
        destination = destination[sizeof(ushort)..];
        value.CopyTo(destination);
        destination = destination[value.Length..];
    }

    /// <summary>
    /// Splits <paramref name="entries"/> across contiguous chunks, each independently decodable via
    /// <see cref="Decode"/> on its own (each carries its own entry-count header etc.), and each at most
    /// <paramref name="maxChunkBytes"/> once encoded — unlike a raw byte-range slice of one big <see cref="Encode"/>
    /// output, which would start a later chunk mid-record and fail to decode. Concatenating every returned chunk's
    /// decoded entries, in order, reproduces <paramref name="entries"/> exactly; <see cref="ChangesetSidecarStore"/>
    /// writes each yielded chunk under the next sequential chunk index. Splits only between whole entries — a
    /// single entry that alone exceeds <paramref name="maxChunkBytes"/> (an account with an extreme number of
    /// slots) still goes out as its own one-entry chunk rather than being torn apart, since a torn entry could
    /// never decode independently either way. Yields exactly one (possibly empty) chunk for an empty entry list, so
    /// a block with no changeset is still recorded rather than never written at all.
    /// </summary>
    public static IEnumerable<byte[]> EncodeChunked(IReadOnlyList<ChangesetAccountEntry> entries, int maxChunkBytes)
    {
        if (entries.Count == 0)
        {
            yield return Encode(entries);
            yield break;
        }

        List<ChangesetAccountEntry> group = [];
        int groupEncodedSize = EntryCountHeaderLength;
        foreach (ChangesetAccountEntry entry in entries)
        {
            int entrySize = EncodedEntrySize(entry);
            if (group.Count > 0 && groupEncodedSize + entrySize > maxChunkBytes)
            {
                yield return Encode(group);
                group = [];
                groupEncodedSize = EntryCountHeaderLength;
            }

            group.Add(entry);
            groupEncodedSize += entrySize;
        }

        yield return Encode(group);
    }

    private static int EncodedEntrySize(ChangesetAccountEntry entry)
    {
        int size = Address.Size + 1 + sizeof(ushort) + entry.AccountValue.Length + sizeof(ushort) + entry.AccountPreValue.Length + sizeof(int);
        foreach (ChangesetSlotEntry slot in entry.StorageChanges)
        {
            size += UInt256Length + sizeof(ushort) + slot.Value.Length + sizeof(ushort) + slot.PreValue.Length;
        }

        return size;
    }

    // The smallest an encoded entry/slot can legally be (all variable-length fields empty) — a declared count
    // that could not possibly fit in the remaining payload at this minimum is corrupt or hostile, and must be
    // rejected before it drives an allocation, never after (an attacker-controlled length prefix must not be
    // able to request an unbounded List<T> capacity and crash the process with OutOfMemoryException).
    private const int MinEncodedAccountEntrySize = Address.Size + 1 + sizeof(ushort) + sizeof(ushort) + sizeof(int);
    private const int MinEncodedSlotEntrySize = UInt256Length + sizeof(ushort) + sizeof(ushort);

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
            byte[] accountValue = ReadLengthPrefixed(ref payload);
            byte[] accountPreValue = ReadLengthPrefixed(ref payload);

            int storageCount = BinaryPrimitives.ReadInt32BigEndian(payload);
            payload = payload[sizeof(int)..];
            ValidateDeclaredCount(storageCount, payload.Length, MinEncodedSlotEntrySize, "storage slot entry");
            List<ChangesetSlotEntry> storageChanges = new(storageCount);
            for (int j = 0; j < storageCount; j++)
            {
                UInt256 slot = new(payload[..UInt256Length], isBigEndian: true);
                payload = payload[UInt256Length..];
                byte[] value = ReadLengthPrefixed(ref payload);
                byte[] preValue = ReadLengthPrefixed(ref payload);
                storageChanges.Add(new ChangesetSlotEntry(slot, value, preValue));
            }

            entries.Add(new ChangesetAccountEntry(address, accountChanged, accountValue, accountPreValue, storageChanges));
        }

        return entries;
    }

    private static byte[] ReadLengthPrefixed(ref ReadOnlySpan<byte> payload)
    {
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(payload);
        payload = payload[sizeof(ushort)..];
        byte[] value = payload[..length].ToArray();
        payload = payload[length..];
        return value;
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
