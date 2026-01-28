// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// A decorator that records preimages (address/slot hash to original bytes) to a separate database.
/// This is useful for external tooling that needs to look up the original address/slot from a hash.
/// When a preimage database is available, raw operations are translated to non-raw operations.
/// </summary>
public class PreimageRecordingPersistence : IPersistence
{
    private const int PreimageLookupSize = 12;

    private readonly IPersistence _inner;
    private readonly IDb _preimageDb;

    public PreimageRecordingPersistence(IPersistence inner, IDb preimageDb)
    {
        _inner = inner;
        _preimageDb = preimageDb;
    }

    public IPersistence.IPersistenceReader CreateReader() => _inner.CreateReader();

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags)
    {
        IPersistence.IWriteBatch innerBatch = _inner.CreateWriteBatch(from, to, flags);
        IWriteBatch preimageWriteBatch = _preimageDb.StartWriteBatch();

        return new RecordingWriteBatch(innerBatch, preimageWriteBatch, _preimageDb);
    }

    public void Flush() => _inner.Flush();

    private class RecordingWriteBatch(IPersistence.IWriteBatch inner, IWriteBatch preimageWriteBatch, IDb preimageDb) : IPersistence.IWriteBatch
    {
        public void Dispose()
        {
            preimageWriteBatch.Dispose();
            inner.Dispose();
        }

        public void SelfDestruct(Address addr)
        {
            RecordAddressPreimage(addr);
            inner.SelfDestruct(addr);
        }

        public void SetAccount(Address addr, Account? account)
        {
            RecordAddressPreimage(addr);
            inner.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            RecordAddressPreimage(addr);
            RecordSlotPreimage(slot);
            inner.SetStorage(addr, slot, value);
        }

        public void SetStateTrieNode(in TreePath path, TrieNode tnValue) => inner.SetStateTrieNode(path, tnValue);

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue) => inner.SetStorageTrieNode(address, path, tnValue);

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value)
        {
            byte[]? addrPreimage = preimageDb.Get(addrHash.Bytes[..PreimageLookupSize]);
            byte[]? slotPreimage = preimageDb.Get(slotHash.Bytes[..PreimageLookupSize]);
            if (addrPreimage is not null && slotPreimage is not null)
            {
                Address addr = new(addrPreimage);
                UInt256 slot = new(slotPreimage, isBigEndian: true);
                inner.SetStorage(addr, slot, value);
            }
            else
            {
                inner.SetStorageRaw(addrHash, slotHash, value);
            }
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            byte[]? addrPreimage = preimageDb.Get(addrHash.Bytes[..PreimageLookupSize]);
            if (addrPreimage is not null)
            {
                Address addr = new(addrPreimage);
                inner.SetAccount(addr, account);
            }
            else
            {
                inner.SetAccountRaw(addrHash, account);
            }
        }

        private void RecordAddressPreimage(Address addr)
        {
            ValueHash256 addressPath = addr.ToAccountPath;
            preimageWriteBatch.PutSpan(addressPath.BytesAsSpan[..PreimageLookupSize], addr.Bytes);
        }

        private void RecordSlotPreimage(in UInt256 slot)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
            preimageWriteBatch.PutSpan(slotHash.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath) =>
            inner.DeleteAccountRange(fromPath, toPath);

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath) =>
            inner.DeleteStorageRange(addressHash, fromPath, toPath);

        public void DeleteStateTrieNodeRange(in TreePath fromPath, in TreePath toPath) =>
            inner.DeleteStateTrieNodeRange(fromPath, toPath);

        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in TreePath fromPath, in TreePath toPath) =>
            inner.DeleteStorageTrieNodeRange(addressHash, fromPath, toPath);
    }
}
