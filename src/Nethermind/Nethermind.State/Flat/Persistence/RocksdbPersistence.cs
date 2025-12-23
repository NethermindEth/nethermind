// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private readonly Configuration _configuration;
    private readonly SegmentedBloom _bloomFilter;

    public record Configuration(bool FlatInTrie = false)
    {
    }

    public RocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Flat)] SegmentedBloom bloomFilter,
        Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
        _bloomFilter = bloomFilter;
    }

    internal static StateId ReadCurrentState(IReadOnlyKeyValueStore kv)
    {
        byte[] bytes = kv.Get(CurrentStateKey);
        if (bytes is null || bytes.Length == 0)
        {
            return new StateId(-1, Keccak.EmptyTreeHash);
        }

        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
        Hash256 stateHash = new Hash256(bytes[8..]);
        return new StateId(blockNumber, stateHash);
    }

    internal static void SetCurrentState(IWriteOnlyKeyValueStore kv, StateId stateId)
    {
        Span<byte> bytes = stackalloc byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(bytes[..8], stateId.blockNumber);
        stateId.stateRoot.BytesAsSpan.CopyTo(bytes[8..]);

        kv.PutSpan(CurrentStateKey, bytes);
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        return new PersistenceReader(_db.CreateSnapshot(), _bloomFilter, this);
    }

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to, WriteFlags flags)
    {
        var dbSnap = _db.CreateSnapshot();
        var currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        return new WriteBatch(this, _db.StartWriteBatch(), _bloomFilter, dbSnap, to, flags);
    }

    private class WriteBatch : IPersistence.IWriteBatch
    {
        private readonly SegmentedBloom _bloomFilter;

        WriteFlags _flags = WriteFlags.DisableWAL;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;

        private readonly TriePersistence.WriteBatch _trieWriteBatch;
        private readonly HashedFlatPersistence.WriteBatch _flatWriter;

        public WriteBatch(
            RocksdbPersistence mainDb,
            IColumnsWriteBatch<FlatDbColumns> batch,
            SegmentedBloom bloomFilter,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            StateId to,
            WriteFlags flags)
        {
            _flags = flags;
            _batch = batch;
            _bloomFilter = bloomFilter;
            _dbSnap = dbSnap;
            _to = to;

            IWriteOnlyKeyValueStore state;
            IWriteOnlyKeyValueStore storage;
            if (mainDb._configuration.FlatInTrie)
            {
                state = batch.GetColumnBatch(FlatDbColumns.StateNodes);
                storage = batch.GetColumnBatch(FlatDbColumns.StorageNodes);
            }
            else
            {
                state = batch.GetColumnBatch(FlatDbColumns.Account);
                storage = batch.GetColumnBatch(FlatDbColumns.Storage);
            }

            _flatWriter = new HashedFlatPersistence.WriteBatch(
                ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage)),
                state,
                storage,
                flags,
                bloomFilter
            );

            _trieWriteBatch = new TriePersistence.WriteBatch(
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
                batch.GetColumnBatch(FlatDbColumns.StateNodes),
                batch.GetColumnBatch(FlatDbColumns.StorageNodes),
                flags);
        }

        public void Dispose()
        {
            SetCurrentState(_batch.GetColumnBatch(FlatDbColumns.Metadata), _to);
            _batch.Dispose();
            _dbSnap.Dispose();
            if (!_flags.HasFlag(WriteFlags.DisableWAL))
            {
                _bloomFilter.Flush();
            }
        }

        public int SelfDestruct(Address addr)
        {
            int removed = _flatWriter.SelfDestruct(addr);
            _trieWriteBatch.SelfDestruct(addr.ToAccountPath);
            return removed;
        }

        public void RemoveAccount(Address addr)
        {
            _flatWriter.RemoveAccount(addr);
        }

        public void SetAccount(Address addr, Account account)
        {
            _flatWriter.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            _flatWriter.SetStorage(addr, slot, value);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue)
        {
            _trieWriteBatch.SetTrieNodes(address, path, tnValue);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            _flatWriter.RemoveStorage(addr, slot);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            _flatWriter.SetStorageRaw(addrHash, slotHash, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            _flatWriter.SetAccountRaw(addrHash, account);
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly TriePersistence.Reader _trieReader;
        private readonly HashedFlatPersistence.Reader _flatReader;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, SegmentedBloom bloomFilter, RocksdbPersistence mainDb)
        {
            _trieReader = new TriePersistence.Reader(
                _db.GetColumn(FlatDbColumns.StateTopNodes),
                _db.GetColumn(FlatDbColumns.StateNodes),
                _db.GetColumn(FlatDbColumns.StorageNodes)
            );

            _db = db;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));

            IReadOnlyKeyValueStore state;
            IReadOnlyKeyValueStore storage;
            if (mainDb._configuration.FlatInTrie)
            {
                state = _db.GetColumn(FlatDbColumns.StateNodes);
                storage = _db.GetColumn(FlatDbColumns.StorageNodes);
            }
            else
            {
                state = _db.GetColumn(FlatDbColumns.Account);
                storage = _db.GetColumn(FlatDbColumns.Storage);
            }

            _flatReader = new HashedFlatPersistence.Reader(
                state,
                storage,
                bloomFilter
            );
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
        }

        public bool TryGetAccount(Address address, out Account? acc)
        {
            return _flatReader.TryGetAccount(address, out acc);
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            return _flatReader.TryGetSlot(address, in index, out valueBytes);
        }

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            return _trieReader.TryLoadRlp(address, path, hash, flags);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return _flatReader.GetAccountRaw(addrHash);
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            return _flatReader.GetStorageRaw(addrHash, slotHash);
        }
    }
}
