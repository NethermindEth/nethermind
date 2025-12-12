// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Prometheus;

namespace Nethermind.State.Flat.Persistence;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

    private const int StateKeyPrefixLength = 20;

    private const int StorageHashPrefixLength = 20; // Store prefix of the 32 byte of the storage. Reduces index size.
    private const int StorageSlotKeySize = 32;
    private const int StorageKeyLength = StorageHashPrefixLength + StorageSlotKeySize;
    private const int FullPathLength = 32;
    private const int PathLengthLength = 1;

    private const int StateNodesKeyLength = FullPathLength + PathLengthLength;
    private const int StateNodesTopThreshold = 5;
    private const int StateNodesTopPathLength = 3;
    private const int StateNodesTopKeyLength = StateNodesTopPathLength + PathLengthLength;

    private const int StorageNodesKeyLength = StorageHashPrefixLength + FullPathLength + PathLengthLength;
    private const int StorageNodesTopThreshold = 3;
    private const int StorageNodesTopPathLength = 2;
    private const int StorageNodesTopKeyLength = StorageHashPrefixLength + StorageNodesTopPathLength + PathLengthLength;

    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;
    private readonly IKeyValueStoreWithBatching _preimageDb;

    private readonly Configuration _configuration;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotHit;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotMiss;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotCompareTime;
    private readonly Histogram.Child _rocksdBPersistenceTimesAddressHash;

    public record Configuration(
        bool UsePreimage = false,
        bool FlatInTrie = false,
        bool SeparateStorageTop = false
    )
    {
    }

    private static Histogram _rocksdBPersistenceTimes = DevMetric.Factory.CreateHistogram("rocksdb_persistence_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
        Buckets = [1]
    });

    public RocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Preimage)] IDb preimageDb,
        Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
        _preimageDb = preimageDb;

        _rocksdBPersistenceTimesAddressHash = _rocksdBPersistenceTimes.WithLabels("address_hash");
        _rocksdBPersistenceTimesSlotHit = _rocksdBPersistenceTimes.WithLabels("slot_hash_hit");
        _rocksdBPersistenceTimesSlotMiss = _rocksdBPersistenceTimes.WithLabels("slot_hash_miss");
        _rocksdBPersistenceTimesSlotCompareTime = _rocksdBPersistenceTimes.WithLabels("slot_hash_compare_time");
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

    private ReadOnlySpan<byte> EncodeAccountKey(Span<byte> buffer, in Address addr)
    {
        if (_configuration.UsePreimage)
        {
            addr.Bytes.CopyTo(buffer);
            return buffer[..StateKeyPrefixLength];
        }
        else
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            hashBuffer = addr.ToAccountPath;
            hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
            return buffer[..StateKeyPrefixLength];
        }
    }

    internal ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in Address addr, in UInt256 slot)
    {
        if (_configuration.UsePreimage)
        {
            addr.Bytes.CopyTo(buffer);
            slot.ToBigEndian(buffer[StorageHashPrefixLength..]);
            return buffer[..StorageKeyLength];
        }
        else
        {
            ValueHash256 hashBuffer = ValueKeccak.Zero;
            hashBuffer = addr.ToAccountPath; // 75ns on average
            hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);

            // around 300ns on average. 30% keccak cache hit rate.
            StorageTree.ComputeKeyWithLookup(slot, buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);

            return buffer[..StorageKeyLength];
        }
    }

    internal ReadOnlySpan<byte> EncodeStorageKeyHashed(Span<byte> buffer, in ValueHash256 addrHash, in ValueHash256 slotHash)
    {
        addrHash.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slotHash.Bytes.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);
        return buffer[..StorageKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStateNodeKey(Span<byte> buffer, in TreePath path)
    {
        path.Path.Bytes.CopyTo(buffer);
        buffer[FullPathLength] = (byte)path.Length;
        return buffer[..StateNodesKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStateTopNodeKey(Span<byte> buffer, in TreePath path)
    {
        path.Path.Bytes[0..StateNodesTopPathLength].CopyTo(buffer);
        buffer[StateNodesTopPathLength] = (byte)path.Length;
        return buffer[..StateNodesTopKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageNodeKey(Span<byte> buffer, Hash256 addr, in TreePath path)
    {
        addr.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        path.Path.Bytes.CopyTo(buffer[StorageHashPrefixLength..]);
        buffer[StorageHashPrefixLength + FullPathLength] = (byte)path.Length;
        return buffer[..StorageNodesKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageNodeTopKey(Span<byte> buffer, Hash256 addr, in TreePath path)
    {
        addr.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        path.Path.Bytes[..StorageNodesTopPathLength].CopyTo(buffer[StorageHashPrefixLength..]);
        buffer[StorageHashPrefixLength + StorageNodesTopPathLength] = (byte)path.Length;
        return buffer[..StorageNodesTopKeyLength];
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        return new PersistenceReader(_db.CreateSnapshot(), this);
    }

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to)
    {
        var dbSnap = _db.CreateSnapshot();
        var currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != from)
        {
            dbSnap.Dispose();
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        return new WriteBatch(this, _preimageDb.StartWriteBatch(), _db.StartWriteBatch(), dbSnap, to);
    }

    private class WriteBatch : IPersistence.IWriteBatch
    {
        private IWriteOnlyKeyValueStore state;
        private IWriteOnlyKeyValueStore storage;
        private IWriteOnlyKeyValueStore stateNodes;
        private IWriteOnlyKeyValueStore stateTopNodes;
        private IWriteOnlyKeyValueStore storageNodes;
        private IWriteOnlyKeyValueStore storageTopNodes;

        private ISortedKeyValueStore storageSnap;
        private ISortedKeyValueStore storageNodesSnap;
        private ISortedKeyValueStore storageTopNodesSnap;

        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.None;
        private readonly RocksdbPersistence _mainDb;
        private readonly IWriteBatch _preimageWriteBatch;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;
        private readonly bool _flatInTrie;
        private readonly bool _separateStorageTop;

        public WriteBatch(RocksdbPersistence mainDb,
            IWriteBatch preimageWriteBatch,
            IColumnsWriteBatch<FlatDbColumns> batch,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            StateId to)
        {
            _mainDb = mainDb;
            _preimageWriteBatch = preimageWriteBatch;
            _batch = batch;
            _dbSnap = dbSnap;
            _to = to;

            _flatInTrie = mainDb._configuration.FlatInTrie;
            _separateStorageTop = mainDb._configuration.SeparateStorageTop;
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

            stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            stateTopNodes = batch.GetColumnBatch(FlatDbColumns.StateTopNodes);
            storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNodes);
            storageTopNodes = batch.GetColumnBatch(FlatDbColumns.StorageTopNodes);

            storageSnap = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage));
            storageNodesSnap = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.StorageNodes));
            storageTopNodesSnap = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageTopNodes));
        }

        public void Dispose()
        {
            SetCurrentState(_batch.GetColumnBatch(FlatDbColumns.Metadata), _to);
            _batch.Dispose();
            _dbSnap.Dispose();
            _preimageWriteBatch.Dispose();
        }

        public int SelfDestruct(Address addr)
        {
            ValueHash256 accountPath = addr.ToAccountPath;
            Span<byte> firstKey = stackalloc byte[StorageHashPrefixLength]; // Because slot 0 is a thing, its just the address prefix.
            Span<byte> lastKey = stackalloc byte[StorageNodesKeyLength];
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(firstKey);
            accountPath.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            int removedEntry = 0;
            using (ISortedView storageNodeReader = storageNodesSnap.GetViewBetween(firstKey, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                    removedEntry++;
                }
            }

            if (_separateStorageTop)
            {
                using (ISortedView storageNodeReader = storageTopNodesSnap.GetViewBetween(firstKey, lastKey))
                {
                    var storageNodeWriter = storageNodes;
                    while (storageNodeReader.MoveNext())
                    {
                        storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                        removedEntry++;
                    }
                }
            }

            if (!_flatInTrie)
            {
                removedEntry = 0; // Debug
                // for storage the prefix might change depending on the encoding
                firstKey.Fill(0x00);
                lastKey.Fill(0xff);
                _mainDb.EncodeAccountKey(firstKey, addr);
                _mainDb.EncodeAccountKey(lastKey, addr);
                using (ISortedView storageReader = storageSnap.GetViewBetween(firstKey, lastKey))
                {
                    IWriteOnlyKeyValueStore? storageWriter = storage;
                    while (storageReader.MoveNext())
                    {
                        storageWriter.Remove(storageReader.CurrentKey);
                        removedEntry++;
                    }
                }
            }

            return removedEntry;
        }

        public void RemoveAccount(Address addr)
        {
            state.Remove(_mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr));
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            state.PutSpan(_mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr), stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ValueHash256 hash256 = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, hash256.BytesAsSpan);
            _preimageWriteBatch.PutSpan(hash256.Bytes, slot.ToBigEndian());

            ReadOnlySpan<byte> theKey =  _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            storage.PutSpan(theKey, value, _flags);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            storage.Remove(theKey);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");

            storage.PutSpan(_mainDb.EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash.ValueHash256, slotHash.ValueHash256), value, _flags);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            state.PutSpan(addrHash.Bytes[..StateKeyPrefixLength], stream.AsSpan(), _flags);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tn)
        {
            if (address is null)
            {
                if (path.Length <= StateNodesTopThreshold)
                {
                    stateTopNodes.PutSpan(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopKeyLength], path), tn.FullRlp.Span, _flags);
                }
                else
                {
                    stateNodes.PutSpan(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], path), tn.FullRlp.Span, _flags);
                }
            }
            else
            {
                if (_separateStorageTop && path.Length <= StorageNodesTopThreshold)
                {
                    storageTopNodes.PutSpan(EncodeStorageNodeTopKey(stackalloc byte[StorageNodesTopKeyLength], address, path), tn.FullRlp.Span, _flags);
                }
                else
                {
                    storageNodes.PutSpan(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, path), tn.FullRlp.Span, _flags);
                }
            }
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly IReadOnlyKeyValueStore _state;
        private readonly IReadOnlyKeyValueStore _storage;
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _stateTopNodes;
        private readonly IReadOnlyKeyValueStore _storageNodes;
        private readonly IReadOnlyKeyValueStore _storageTopNodes;
        private readonly RocksdbPersistence _mainDb;
        private readonly bool _usePreimage;
        private readonly bool _flatInTrie;
        private readonly bool _separateStorageTop;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, RocksdbPersistence mainDb)
        {
            _usePreimage = mainDb._configuration.UsePreimage;
            _flatInTrie = mainDb._configuration.FlatInTrie;
            _separateStorageTop = mainDb._configuration.SeparateStorageTop;
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            if (_flatInTrie)
            {
                _state = _db.GetColumn(FlatDbColumns.StateNodes);
                _storage = _db.GetColumn(FlatDbColumns.StorageNodes);
            }
            else
            {
                _state = _db.GetColumn(FlatDbColumns.Account);
                _storage = _db.GetColumn(FlatDbColumns.Storage);
            }
            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _stateTopNodes = _db.GetColumn(FlatDbColumns.StateTopNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNodes);
            _storageTopNodes = _db.GetColumn(FlatDbColumns.StorageTopNodes);
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
        }

        /*
        private Decompressor RentDecompressor()
        {
            Decompressor? decompressor = _decompressor;
            if (decompressor is null) return _mainDb.CreateDecompressor();
            if (Interlocked.CompareExchange(ref _decompressor, null, decompressor) == decompressor) return decompressor;
            return _mainDb.CreateDecompressor();
        }

        private void ReturnDecompressor(Decompressor decompressor)
        {
            if (Interlocked.CompareExchange(ref _decompressor, decompressor, null) == null)
            {
                return;
            }
            decompressor.Dispose();
        }
        */

        public bool TryGetAccount(Address address, out Account? acc)
        {
            Span<byte> value = _state.GetSpan(_mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address));
            try
            {
                if (address == FlatWorldStateScope.DebugAddress)
                {
                    Console.Error.WriteLine($"Get {address}, got {value.ToHexString()}");
                }
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

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], address, index);
            Span<byte> value = _storage.GetSpan(theKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    valueBytes = null;
                    return true;
                }

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
            if (address is null)
            {
                if (path.Length <= StateNodesTopThreshold)
                {
                    return _stateTopNodes.Get(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopKeyLength], in path));
                }
                else
                {
                    return _stateNodes.Get(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], in path));
                }
            }
            else
            {
                if (_separateStorageTop && path.Length <= StorageNodesTopThreshold)
                {
                    return _storageTopNodes.Get(EncodeStorageNodeTopKey(stackalloc byte[StorageNodesTopKeyLength], address, in path));
                }
                else
                {
                    return _storageNodes.Get(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, in path));
                }
            }
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return GetAccountRaw(addrHash.ValueHash256);
        }

        private byte[]? GetAccountRaw(in ValueHash256 accountHash)
        {
            if (_usePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");
            return _state.GetSpan(accountHash.Bytes[..StateKeyPrefixLength]).ToArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            if (_usePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = _mainDb.EncodeStorageKeyHashed(keySpan, addrHash.ValueHash256, slotHash.ValueHash256);
            return _storage.Get(storageKey);
        }
    }
}
