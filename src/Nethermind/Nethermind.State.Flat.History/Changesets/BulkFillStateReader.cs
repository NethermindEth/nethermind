// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Columns = Nethermind.State.Flat.History.Changesets.BulkFillScratchState.Columns;

namespace Nethermind.State.Flat.History.Changesets;

internal sealed class BulkFillStateReader(IColumnsDb<Columns> db, StateId state, bool rlpWrappedSlots) : IPersistence.IPersistenceReader
{
    public StateId CurrentState => state;
    public bool IsPreimageMode => false;

    public Account? GetAccount(Address address)
    {
        byte[]? bytes = GetAccountRaw(address.ToAccountPath);
        if (bytes is null || bytes.Length == 0) return null;
        RlpReader reader = new(bytes);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account) || reader.Position != bytes.Length)
            throw new InvalidDataException("Invalid bulk-fill scratch account.");
        return new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment());
    }

    public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
    {
        ValueHash256 slotHash = default;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        return TryGetStorageRaw(address.ToAccountPath, slotHash, ref outValue);
    }

    public byte[]? GetAccountRaw(in ValueHash256 addrHash) => db.GetColumnDb(Columns.Accounts)[addrHash.Bytes];

    public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value)
    {
        Span<byte> key = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        EncodeStorageKey(addrHash, slotHash, key);
        byte[]? record = db.GetColumnDb(Columns.Storage)[key];
        value = UInt256.Zero;
        if (record is null) return false;
        if (record.Length < sizeof(ulong) || record.Length > sizeof(ulong) + BaseFlatPersistence.RlpSlotValueBufferSize)
            throw new InvalidDataException("Invalid bulk-fill scratch slot.");
        ulong writtenAt = BinaryPrimitives.ReadUInt64BigEndian(record);
        if (writtenAt > state.BlockNumber) throw new InvalidDataException("Scratch slot is newer than its checkpoint.");
        byte[]? clear = db.GetColumnDb(Columns.Clears)[addrHash.Bytes];
        if (clear is not null)
        {
            if (clear.Length != sizeof(ulong)) throw new InvalidDataException("Invalid bulk-fill scratch clear.");
            ulong clearedAt = BinaryPrimitives.ReadUInt64BigEndian(clear);
            if (clearedAt > state.BlockNumber) throw new InvalidDataException("Scratch clear is newer than its checkpoint.");
            if (writtenAt < clearedAt) return false;
        }
        ReadOnlySpan<byte> bytes = record.AsSpan(sizeof(ulong));
        if (rlpWrappedSlots && !bytes.IsEmpty)
        {
            RlpReader reader = new(bytes);
            bytes = reader.DecodeByteArraySpan();
            if (reader.Position != reader.Length) throw new InvalidDataException("Trailing bytes in a scratch slot.");
        }
        value = BaseFlatPersistence.DecodeSlotValue(bytes);
        return true;
    }

    internal static void EncodeStorageKey(in ValueHash256 address, in ValueHash256 slot, Span<byte> key)
    {
        address.Bytes[..HistoryKeyLayout.ScopeKeyLength].CopyTo(key);
        slot.Bytes.CopyTo(key[HistoryKeyLayout.ScopeKeyLength..]);
    }

    public void Dispose() { }
    public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => throw new NotSupportedException();
    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => throw new NotSupportedException();
    public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
    public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => throw new NotSupportedException();
}
