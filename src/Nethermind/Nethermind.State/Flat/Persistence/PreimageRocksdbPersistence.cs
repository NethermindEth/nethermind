// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.Trie;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat.Persistence;

public class PreimageRocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private const int StateKeyPrefixLength = 20;

    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;

    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private readonly SegmentedBloom _bloomFilter;

    public PreimageRocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Flat)] SegmentedBloom bloomFilter)
    {
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

    private ReadOnlySpan<byte> EncodeAccountKey(Span<byte> buffer, in Address addr, out ulong h1)
    {
        addr.Bytes.CopyTo(buffer);
        h1 = BinaryPrimitives.ReadUInt64LittleEndian(addr.Bytes);
        return (buffer[..StateKeyPrefixLength]);
    }

    private ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in Address addr, in UInt256 slot, out ulong h1)
    {
        addr.Bytes.CopyTo(buffer);
        slot.ToBigEndian(buffer[StorageHashPrefixLength..]);
        h1 = Mix(BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadUInt64LittleEndian(buffer[StorageHashPrefixLength..]));
        return buffer[..StorageKeyLength];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong Mix(ulong a, ulong b)
    {
        return (a ^ RotateLeft(b, 23)) * 0x9E3779B97F4A7C15UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static ulong RotateLeft(ulong x, int k)
        => (x << k) | (x >> (64 - k));

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
        private IWriteOnlyKeyValueStore state;
        private IWriteOnlyKeyValueStore storage;

        private ISortedKeyValueStore storageSnap;
        private readonly SegmentedBloom _bloomFilter;

        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.DisableWAL;
        private readonly PreimageRocksdbPersistence _mainDb;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;

        private readonly TriePersistence.WriteBatch _trieWriteBatch;

        public WriteBatch(
            PreimageRocksdbPersistence mainDb,
            IColumnsWriteBatch<FlatDbColumns> batch,
            SegmentedBloom bloomFilter,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            StateId to,
            WriteFlags flags)
        {
            _flags = flags;
            _mainDb = mainDb;
            _batch = batch;
            _bloomFilter = bloomFilter;
            _dbSnap = dbSnap;
            _to = to;

            state = batch.GetColumnBatch(FlatDbColumns.Account);
            storage = batch.GetColumnBatch(FlatDbColumns.Storage);


            _trieWriteBatch = new TriePersistence.WriteBatch(
                (ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage),
                batch.GetColumnBatch(FlatDbColumns.StateTopNodes),
                batch.GetColumnBatch(FlatDbColumns.StateNodes),
                batch.GetColumnBatch(FlatDbColumns.StorageNodes),
                flags);

            storageSnap = (ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage);
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
            _trieWriteBatch.SelfDestruct(addr.ToAccountPath);

            ValueHash256 accountPath = addr.ToAccountPath;
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
            // for storage the prefix might change depending on the encoding
            _mainDb.EncodeAccountKey(firstKey, addr, out _);
            _mainDb.EncodeAccountKey(lastKey, addr, out _);
            using (ISortedView storageReader = storageSnap.GetViewBetween(firstKey, lastKey))
            {
                IWriteOnlyKeyValueStore? storageWriter = storage;
                while (storageReader.MoveNext())
                {
                    storageWriter.Remove(storageReader.CurrentKey);
                    removedEntry++;
                }
            }

            return removedEntry;
        }

        public void RemoveAccount(Address addr)
        {
            state.Remove(_mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr, out _));
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            ReadOnlySpan<byte> key = _mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr, out var bloomHash);

            if (account != null)
            {
                _bloomFilter.Add(bloomHash);
            }

            state.PutSpan(key, stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey =  _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot, out ulong bloomHash);

            _bloomFilter.Add(bloomHash);
            storage.PutSpan(theKey, value, _flags);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tnValue)
        {
            _trieWriteBatch.SetTrieNodes(address, path, tnValue);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot, out _);
            storage.Remove(theKey);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            throw new InvalidOperationException("Cannot set raw when using preimage");
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            throw new InvalidOperationException("Cannot set raw when using preimage");
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly IReadOnlyKeyValueStore _state;
        private readonly IReadOnlyKeyValueStore _storage;
        private readonly PreimageRocksdbPersistence _mainDb;
        private readonly SegmentedBloom _bloomFilter;
        private readonly TriePersistence.Reader _trieReader;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, SegmentedBloom bloomFilter, PreimageRocksdbPersistence mainDb)
        {
            _trieReader = new TriePersistence.Reader(
                _db.GetColumn(FlatDbColumns.StateTopNodes),
                _db.GetColumn(FlatDbColumns.StateNodes),
                _db.GetColumn(FlatDbColumns.StorageNodes)
            );
            _bloomFilter = bloomFilter;
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _state = _db.GetColumn(FlatDbColumns.Account);
            _storage = _db.GetColumn(FlatDbColumns.Storage);
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
        }

        public bool TryGetAccount(Address address, out Account? acc)
        {
            var key = _mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address, out ulong bloomHash);
            if (!_bloomFilter.MightContain(bloomHash))
            {
                acc = null;
                return true;
            }

            Span<byte> value = _state.GetSpan(key);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    acc = null;
                    return true;
                }

                var ctx = new Rlp.ValueDecoderContext(value);
                acc = _mainDb._accountDecoder.Decode(ref ctx);
                return true;
            }
            finally
            {
                _state.DangerousReleaseMemory(value);
            }
        }

        private static Counter _slotBloomHit = Metrics.CreateCounter("rocksdb_slot_bloom", "slot_blom", "hitmiss");
        private static Counter.Child _slotBloomHitHit = _slotBloomHit.WithLabels("true_positive");
        private static Counter.Child _slotBloomHitMiss = _slotBloomHit.WithLabels("false_positive");


        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], address, index, out ulong h1);
            if (!_bloomFilter.MightContain(h1))
            {
                valueBytes = null;
                return true;
            }

            Span<byte> value = _storage.GetSpan(theKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    _slotBloomHitMiss.Inc();
                    valueBytes = null;
                    return true;
                }

                _slotBloomHitHit.Inc();
                valueBytes = value.ToArray();
                return true;
            }
            finally
            {
                _storage.DangerousReleaseMemory(value);
            }
        }

        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            return _trieReader.TryLoadRlp(address, path, hash, flags);
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return GetAccountRaw(addrHash.ValueHash256);
        }

        private byte[]? GetAccountRaw(in ValueHash256 accountHash)
        {
            throw new InvalidOperationException("Raw operation not available in preimage mode");
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            throw new InvalidOperationException("Raw operation not available in preimage mode");
        }
    }
}
