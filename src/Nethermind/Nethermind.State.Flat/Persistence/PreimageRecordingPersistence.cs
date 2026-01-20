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

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags)
    {
        IPersistence.IWriteBatch innerBatch = _inner.CreateWriteBatch(from, to, flags);
        IWriteBatch preimageWriteBatch = _preimageDb.StartWriteBatch();

        return new RecordingWriteBatch(innerBatch, preimageWriteBatch);
    }

    private class RecordingWriteBatch : IPersistence.IWriteBatch
    {
        private readonly IPersistence.IWriteBatch _inner;
        private readonly IWriteBatch _preimageWriteBatch;

        public RecordingWriteBatch(IPersistence.IWriteBatch inner, IWriteBatch preimageWriteBatch)
        {
            _inner = inner;
            _preimageWriteBatch = preimageWriteBatch;
        }

        public void Dispose()
        {
            _preimageWriteBatch.Dispose();
            _inner.Dispose();
        }

        public int SelfDestruct(Address addr)
        {
            RecordAddressPreimage(addr);
            return _inner.SelfDestruct(addr);
        }

        public void SetAccount(Address addr, Account? account)
        {
            RecordAddressPreimage(addr);
            _inner.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in SlotValue? value)
        {
            RecordAddressPreimage(addr);
            RecordSlotPreimage(slot);
            _inner.SetStorage(addr, slot, value);
        }

        public void SetStateTrieNode(in TreePath path, TrieNode tnValue)
        {
            _inner.SetStateTrieNode(path, tnValue);
        }

        public void SetStorageTrieNode(Hash256 address, in TreePath path, TrieNode tnValue)
        {
            _inner.SetStorageTrieNode(address, path, tnValue);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, in SlotValue? value)
        {
            _inner.SetStorageRaw(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            _inner.SetAccountRaw(addrHash, account);
        }

        private void RecordAddressPreimage(Address addr)
        {
            ValueHash256 addressPath = addr.ToAccountPath;
            _preimageWriteBatch.PutSpan(addressPath.BytesAsSpan[..PreimageLookupSize], addr.Bytes);
        }

        private void RecordSlotPreimage(in UInt256 slot)
        {
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
            _preimageWriteBatch.PutSpan(slotHash.BytesAsSpan[..PreimageLookupSize], slot.ToBigEndian());
        }
    }
}
