// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.State.Flat.Persistence;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Changesets;

internal sealed class BulkFillScratchVerifier(IColumnsDb<Columns> db, ulong anchor)
{
    /// <summary>Reconstructs every live storage root and the account root before allowing a scratch replay base.
    /// Every disagreement is a <see cref="ScratchStateUnusableException"/>: the imported rows are wrong and stay wrong.
    /// That includes a live slot with no live account to hold it: the replay reads slots without their account, so a
    /// re-created account would start from it.</summary>
    public void VerifyAnchor(Hash256 expectedRoot, bool rlpWrappedSlots, CancellationToken token)
    {
        foreach (Columns column in new[] { Columns.Accounts, Columns.Storage, Columns.Clears })
        {
            byte[]? checkpoint = db.GetColumnDb(Columns.Metadata)[new byte[] { (byte)column }];
            int keyLength = (column == Columns.Storage ? BaseFlatPersistence.StorageKeyLength : Hash256.Size) + sizeof(ulong);
            if (checkpoint is null || (checkpoint.Length != 1 && checkpoint.Length != 1 + keyLength) || checkpoint[0] != 1)
                throw new InvalidOperationException($"Scratch {column} import has not completed.");
        }

        Span<byte> upper = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
        upper.Fill(0xFF);
        using ISortedView accounts = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Accounts)).GetViewBetween([], upper[..(Hash256.Size + 1)], ReadFlags.HintReadAhead);
        using ISortedView slots = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Storage)).GetViewBetween([], upper, ReadFlags.HintReadAhead);
        using SortedStateRoot state = new();
        using SortedStateRoot storageBuilder = new();
        bool hasSlot = slots.MoveNext();
        ValueHash256 previous = default;
        bool hasPrevious = false;
        while (accounts.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = accounts.CurrentKey;
            if (key.Length != Hash256.Size) throw new ScratchStateUnusableException("Invalid scratch account key length.");
            ValueHash256 path = new(key);
            if (hasPrevious && previous.Bytes[..HistoryKeyLayout.ScopeKeyLength].SequenceEqual(key[..HistoryKeyLayout.ScopeKeyLength]))
                throw new ScratchStateUnusableException("Scratch account paths collide in the storage address prefix.");
            previous = path;
            hasPrevious = true;
            hasSlot = RejectSlotsBefore(slots, hasSlot, path.Bytes[..HistoryKeyLayout.ScopeKeyLength], rlpWrappedSlots, token);
            ReadOnlySpan<byte> row = accounts.CurrentValue;
            if (row.IsEmpty)
            {
                hasSlot = VerifyStorage(path, slots, hasSlot, rlpWrappedSlots, storageBuilder, token, out ValueHash256 leftover);
                if (leftover != Keccak.EmptyTreeHash.ValueHash256)
                    throw new ScratchStateUnusableException($"Scratch holds live storage for the deleted account {path} at {anchor}.");
                continue;
            }
            AccountStruct account;
            // A row that is not RLP at all throws out of the decoder rather than returning false, and scratch data
            // does not heal: left as a decoding failure it reaches the worker's retry and the same bytes are verified
            // again every pass, forever.
            try
            {
                RlpReader decoder = new(row);
                if (!AccountDecoder.Slim.TryDecodeStruct(ref decoder, out account) || decoder.Position != row.Length)
                    throw new ScratchStateUnusableException($"Invalid scratch account value for {path}.");
            }
            catch (RlpException exception)
            {
                throw new ScratchStateUnusableException($"Invalid scratch account value for {path}.", exception);
            }

            hasSlot = VerifyStorage(path, slots, hasSlot, rlpWrappedSlots, storageBuilder, token, out ValueHash256 storage);
            if (storage != account.StorageRoot)
                throw new ScratchStateUnusableException($"Scratch storage root mismatch for {path} at {anchor}: expected {account.StorageRoot}, actual {storage}.");
            state.Add(path, AccountRowRlp.Encode(row));
        }
        Span<byte> end = stackalloc byte[HistoryKeyLayout.ScopeKeyLength + 1];
        end.Fill(0xFF);
        RejectSlotsBefore(slots, hasSlot, end, rlpWrappedSlots, token);
        token.ThrowIfCancellationRequested();
        ValueHash256 actual = state.Finish();
        if (actual != expectedRoot.ValueHash256)
            throw new ScratchStateUnusableException($"Scratch account root mismatch at {anchor}: expected {expectedRoot}, actual {actual}.");
    }

    private bool RejectSlotsBefore(ISortedView slots, bool hasSlot, ReadOnlySpan<byte> prefix, bool rlpWrapped, CancellationToken token)
    {
        const int addressLength = HistoryKeyLayout.ScopeKeyLength;
        while (hasSlot)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = slots.CurrentKey;
            if (key.Length != BaseFlatPersistence.StorageKeyLength) throw new ScratchStateUnusableException("Invalid scratch storage key.");
            if (key[..addressLength].SequenceCompareTo(prefix) >= 0) return true;
            if (!ReadLiveSlot(slots.CurrentValue, 0, rlpWrapped).IsEmpty)
                throw new ScratchStateUnusableException($"Scratch holds a live slot under {Convert.ToHexString(key[..addressLength])} with no account at {anchor}.");
            hasSlot = slots.MoveNext();
        }
        return false;
    }

    private bool VerifyStorage(in ValueHash256 account, ISortedView slots, bool hasSlot, bool rlpWrapped, SortedStateRoot storage, CancellationToken token, out ValueHash256 root)
    {
        const int addressLength = HistoryKeyLayout.ScopeKeyLength;
        byte[]? clear = db.GetColumnDb(Columns.Clears)[account.Bytes];
        if (clear is not null && clear.Length != sizeof(ulong)) throw new ScratchStateUnusableException("Invalid scratch storage clear.");
        ulong clearedAt = clear is null ? 0 : BinaryPrimitives.ReadUInt64BigEndian(clear);
        if (clearedAt > anchor) throw new ScratchStateUnusableException("Scratch storage clear is newer than its anchor.");
        storage.Reset();
        Span<byte> encoded = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        for (; hasSlot; hasSlot = slots.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = slots.CurrentKey;
            if (key.Length != BaseFlatPersistence.StorageKeyLength) throw new ScratchStateUnusableException("Invalid scratch storage key.");
            if (!key[..addressLength].SequenceEqual(account.Bytes[..addressLength])) break;
            ReadOnlySpan<byte> value = ReadLiveSlot(slots.CurrentValue, clearedAt, rlpWrapped);
            if (value.IsEmpty) continue;
            Rlp.Encode(encoded, 0, value);
            storage.Add(new ValueHash256(key[addressLength..]), encoded[..Rlp.LengthOf(value)]);
        }
        root = storage.Finish();
        return hasSlot;
    }

    private ReadOnlySpan<byte> ReadLiveSlot(ReadOnlySpan<byte> record, ulong clearedAt, bool rlpWrapped)
    {
        if (record.Length < sizeof(ulong) || record.Length > sizeof(ulong) + BaseFlatPersistence.RlpSlotValueBufferSize)
            throw new ScratchStateUnusableException("Invalid scratch storage value.");
        ulong writtenAt = BinaryPrimitives.ReadUInt64BigEndian(record);
        if (writtenAt > anchor) throw new ScratchStateUnusableException("Scratch slot is newer than its anchor.");
        if (writtenAt < clearedAt) return [];
        ReadOnlySpan<byte> value = record[sizeof(ulong)..];
        if (value.IsEmpty) return [];
        if (rlpWrapped)
        {
            try
            {
                RlpReader decoder = new(value);
                value = decoder.DecodeByteArraySpan();
                if (decoder.Position != decoder.Length) throw new ScratchStateUnusableException("Trailing bytes in a scratch slot.");
            }
            catch (RlpException exception)
            {
                throw new ScratchStateUnusableException("Invalid scratch slot value.", exception);
            }
        }
        if (value.Length > Hash256.Size) throw new ScratchStateUnusableException("Scratch slot exceeds 256 bits.");
        return value.TrimStart((byte)0);
    }
}
