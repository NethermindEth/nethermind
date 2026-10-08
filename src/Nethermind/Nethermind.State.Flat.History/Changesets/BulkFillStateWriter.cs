// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Changesets;

internal sealed class BulkFillStateWriter(IColumnsDb<Columns> db, IColumnsWriteBatch<Columns> batch, ulong block, bool rlpWrappedSlots)
    : IWorldStateScopeProvider.IWorldStateWriteBatch
{
    public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

    /// <summary>The block snapshot carries every account the block touched, reads included, so a null here is a
    /// removal only when the scratch holds the account. A block that read an account which does not exist has
    /// nothing to remove and nothing to clear: written as a removal anyway, a block probing tens of thousands of
    /// empty addresses would hand the cleanup as many clears, each a range scan of its own.</summary>
    public void Set(Address key, Account? account)
    {
        ValueHash256 path = key.ToAccountPath;
        IWriteBatch accounts = batch.GetColumnBatch(Columns.Accounts);
        if (account is null)
        {
            if (db.GetColumnDb(Columns.Accounts)[path.Bytes] is not { Length: > 0 }) return;

            accounts.Remove(path.Bytes);
            new StorageWriter(batch, path, block, rlpWrappedSlots).Clear();
        }
        else accounts.Set(path.Bytes, AccountDecoder.Slim.EncodeAsBytes(account));
    }

    public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
        new StorageWriter(batch, key.ToAccountPath, block, rlpWrappedSlots);

    public void Dispose() { }

    private sealed class StorageWriter(IColumnsWriteBatch<Columns> batch, ValueHash256 address, ulong block, bool rlpWrapped)
        : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, in UInt256 value)
        {
            ValueHash256 slotHash = default;
            StorageTree.ComputeKeyWithLookup(index, ref slotHash);
            Span<byte> key = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
            BulkFillStateReader.EncodeStorageKey(address, slotHash, key);
            IWriteBatch slots = batch.GetColumnBatch(Columns.Storage);
            if (value.IsZero) slots.Remove(key);
            else
            {
                Span<byte> record = stackalloc byte[sizeof(ulong) + BaseFlatPersistence.RlpSlotValueBufferSize];
                BinaryPrimitives.WriteUInt64BigEndian(record, block);
                int length = BaseFlatPersistence.EncodeSlotValue(value, rlpWrapped, record[sizeof(ulong)..]);
                slots.PutSpan(key, record[..(sizeof(ulong) + length)]);
            }
        }

        public void Clear()
        {
            Span<byte> height = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(height, block);
            batch.GetColumnBatch(Columns.Clears).PutSpan(address.Bytes, height);
        }

        public void Dispose() { }
    }
}
