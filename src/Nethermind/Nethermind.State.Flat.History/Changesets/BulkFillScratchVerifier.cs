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
    /// Every disagreement is a <see cref="ScratchStateUnusableException"/>: the imported rows are wrong and stay wrong.</summary>
    public void VerifyAnchor(Hash256 expectedRoot, bool rlpWrappedSlots, CancellationToken token)
    {
        foreach (Columns column in new[] { Columns.Accounts, Columns.Storage, Columns.Clears })
        {
            byte[]? checkpoint = db.GetColumnDb(Columns.Metadata)[new byte[] { (byte)column }];
            int keyLength = (column == Columns.Storage ? BaseFlatPersistence.StorageKeyLength : Hash256.Size) + sizeof(ulong);
            if (checkpoint is null || (checkpoint.Length != 1 && checkpoint.Length != 1 + keyLength) || checkpoint[0] != 1)
                throw new InvalidOperationException($"Scratch {column} import has not completed.");
        }

        Span<byte> upper = stackalloc byte[Hash256.Size + 1];
        upper.Fill(0xFF);
        using ISortedView accounts = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Accounts)).GetViewBetween([], upper, ReadFlags.HintReadAhead);
        using SortedStateRoot state = new();
        using SortedStateRoot storageBuilder = new();
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
            ReadOnlySpan<byte> row = accounts.CurrentValue;
            if (row.IsEmpty) continue;
            RlpReader decoder = new(row);
            if (!AccountDecoder.Slim.TryDecodeStruct(ref decoder, out AccountStruct account) || decoder.Position != row.Length)
                throw new ScratchStateUnusableException("Invalid scratch account value.");
            ValueHash256 storage = VerifyStorage(path, rlpWrappedSlots, storageBuilder, token);
            if (storage != account.StorageRoot)
                throw new ScratchStateUnusableException($"Scratch storage root mismatch for {path} at {anchor}: expected {account.StorageRoot}, actual {storage}.");
            state.Add(path, AccountRowRlp.Encode(row));
        }
        token.ThrowIfCancellationRequested();
        ValueHash256 actual = state.Finish();
        if (actual != expectedRoot.ValueHash256)
            throw new ScratchStateUnusableException($"Scratch account root mismatch at {anchor}: expected {expectedRoot}, actual {actual}.");
    }

    private ValueHash256 VerifyStorage(in ValueHash256 account, bool rlpWrapped, SortedStateRoot storage, CancellationToken token)
    {
        const int addressLength = HistoryKeyLayout.ScopeKeyLength;
        Span<byte> lower = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        lower.Clear();
        account.Bytes[..addressLength].CopyTo(lower);
        Span<byte> upper = stackalloc byte[BaseFlatPersistence.StorageKeyLength + 1];
        upper.Fill(0xFF);
        account.Bytes[..addressLength].CopyTo(upper);
        byte[]? clear = db.GetColumnDb(Columns.Clears)[account.Bytes];
        if (clear is not null && clear.Length != sizeof(ulong)) throw new ScratchStateUnusableException("Invalid scratch storage clear.");
        ulong clearedAt = clear is null ? 0 : BinaryPrimitives.ReadUInt64BigEndian(clear);
        if (clearedAt > anchor) throw new ScratchStateUnusableException("Scratch storage clear is newer than its anchor.");
        using ISortedView slots = ((ISortedKeyValueStore)db.GetColumnDb(Columns.Storage)).GetViewBetween(lower, upper, ReadFlags.HintReadAhead);
        storage.Reset();
        Span<byte> encoded = stackalloc byte[BaseFlatPersistence.RlpSlotValueBufferSize];
        while (slots.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            if (slots.CurrentKey.Length != BaseFlatPersistence.StorageKeyLength || !slots.CurrentKey[..addressLength].SequenceEqual(account.Bytes[..addressLength]))
                throw new ScratchStateUnusableException("Invalid scratch storage key.");
            ReadOnlySpan<byte> record = slots.CurrentValue;
            if (record.Length < sizeof(ulong) || record.Length > sizeof(ulong) + BaseFlatPersistence.RlpSlotValueBufferSize)
                throw new ScratchStateUnusableException("Invalid scratch storage value.");
            ulong writtenAt = BinaryPrimitives.ReadUInt64BigEndian(record);
            if (writtenAt > anchor) throw new ScratchStateUnusableException("Scratch slot is newer than its anchor.");
            if (writtenAt < clearedAt) continue;
            ReadOnlySpan<byte> value = record[sizeof(ulong)..];
            if (value.IsEmpty) continue;
            if (rlpWrapped)
            {
                RlpReader decoder = new(value);
                value = decoder.DecodeByteArraySpan();
                if (decoder.Position != decoder.Length) throw new ScratchStateUnusableException("Trailing bytes in a scratch slot.");
            }
            if (value.Length > Hash256.Size) throw new ScratchStateUnusableException("Scratch slot exceeds 256 bits.");
            value = value.TrimStart((byte)0);
            if (value.IsEmpty) continue;
            Rlp.Encode(encoded, 0, value);
            storage.Add(new ValueHash256(slots.CurrentKey[addressLength..]), encoded[..Rlp.LengthOf(value)]);
        }
        return storage.Finish();
    }
}
