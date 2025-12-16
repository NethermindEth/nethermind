// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Threading;
using Autofac.Features.ResolveAnything;
using LightningDB;
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

public class LMDBPersistence : IPersistence
{
    public bool SupportConcurrentWrites => false;

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

    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private readonly Histogram.Child _rocksdBPersistenceTimesSlotHit;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotMiss;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotCompareTime;
    private readonly Histogram.Child _rocksdBPersistenceTimesAddressHash;
    private readonly LightningEnvironment _lmdbEnv;

    private static Histogram _rocksdBPersistenceTimes = DevMetric.Factory.CreateHistogram("rocksdb_persistence_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
        Buckets = [1]
    });

    public LMDBPersistence(IColumnsDb<FlatDbColumns> db, LightningEnvironment lmdbEnv)
    {
        _db = db;
        _lmdbEnv = lmdbEnv;

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
        ValueHash256 hashBuffer = ValueKeccak.Zero;
        hashBuffer = addr.ToAccountPath;
        hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        return buffer[..StateKeyPrefixLength];
    }

    internal ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in Address addr, in UInt256 slot)
    {
        ValueHash256 hashBuffer = ValueKeccak.Zero;
        hashBuffer = addr.ToAccountPath; // 75ns on average
        hashBuffer.Bytes[..StorageHashPrefixLength].CopyTo(buffer);

        // around 300ns on average. 30% keccak cache hit rate.
        StorageTree.ComputeKeyWithLookup(slot, buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);

        return buffer[..StorageKeyLength];
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

    public IPersistence.IPersistenceReader CreateReader()
    {
        var tx = _lmdbEnv.BeginTransaction(TransactionBeginFlags.ReadOnly);
        return new PersistenceReader(_db.CreateSnapshot(), this, tx);
    }

    private int _hasWriteBatch = 0;


    private void MarkWriteBatchComplete()
    {
        Interlocked.CompareExchange(ref _hasWriteBatch, 0, 1);
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

        if (Interlocked.CompareExchange(ref _hasWriteBatch, 1, 0) != 0)
        {
            throw new InvalidOperationException("Previous write batch not completed yet");
        }

        var tx = _lmdbEnv.BeginTransaction();
        return new WriteBatch(this, _db.StartWriteBatch(), dbSnap, to, tx);
    }

    private class WriteBatch : IPersistence.IWriteBatch
    {
        private LightningDatabase state;
        private LightningDatabase storage;

        private IWriteOnlyKeyValueStore stateNodes;
        private IWriteOnlyKeyValueStore stateTopNodes;
        private IWriteOnlyKeyValueStore storageNodes;

        private ISortedKeyValueStore storageSnap;
        private ISortedKeyValueStore storageNodesSnap;

        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.None;
        private readonly LMDBPersistence _mainDb;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;
        private readonly LightningTransaction _lmdbTx;

        public WriteBatch(LMDBPersistence mainDb,
            IColumnsWriteBatch<FlatDbColumns> batch,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            StateId to,
            LightningTransaction lmdbTx
            )
        {
            _mainDb = mainDb;
            _batch = batch;
            _dbSnap = dbSnap;
            _to = to;

            _lmdbTx = lmdbTx;

            state = lmdbTx.OpenDatabase(FlatDbColumns.Account.ToString(), new DatabaseConfiguration()
            {
                Flags   = DatabaseOpenFlags.Create
            });
            storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString(), new DatabaseConfiguration()
            {
                Flags   = DatabaseOpenFlags.Create
            });

            stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            stateTopNodes = batch.GetColumnBatch(FlatDbColumns.StateTopNodes);
            storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNodes);

            storageSnap = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage));
            storageNodesSnap = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.StorageNodes));
        }

        public void Dispose()
        {
            SetCurrentState(_batch.GetColumnBatch(FlatDbColumns.Metadata), _to);
            _batch.Dispose();
            _dbSnap.Dispose();

            state.Dispose();
            storage.Dispose();

            _lmdbTx.Commit();
            _lmdbTx.Dispose();
            _mainDb.MarkWriteBatchComplete();
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

            // for storage the prefix might change depending on the encoding
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            _mainDb.EncodeAccountKey(firstKey, addr);
            _mainDb.EncodeAccountKey(lastKey, addr);

            using var storageCursor = _lmdbTx.CreateCursor(storage);
            storageCursor.SetRange(firstKey);

            while (true)
            {
                (MDBResultCode resultCode, MDBValue key, MDBValue value) = storageCursor.GetCurrent();
                if (resultCode != MDBResultCode.Success) break;

                // Out of range
                if (Bytes.BytesComparer.Compare(key.AsSpan(), lastKey) >= 0) break;

                _lmdbTx.Delete(storage, key.AsSpan());

                storageCursor.Next();
            }

            return removedEntry;
        }

        public void RemoveAccount(Address addr)
        {
            _lmdbTx.Delete(state, _mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr));
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _lmdbTx.Put(state, _mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr), stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey =  _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            _lmdbTx.Put(storage, theKey, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            _lmdbTx.Delete(storage, theKey);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            _lmdbTx.Put(storage, _mainDb.EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash.ValueHash256, slotHash.ValueHash256), value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            _lmdbTx.Put(state, addrHash.Bytes[..StateKeyPrefixLength], stream.AsSpan());
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
                storageNodes.PutSpan(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, path), tn.FullRlp.Span, _flags);
            }
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly LightningDatabase _state;
        private readonly LightningDatabase _storage;
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _stateTopNodes;
        private readonly IReadOnlyKeyValueStore _storageNodes;
        private readonly LMDBPersistence _mainDb;
        private readonly LightningTransaction _lmdbTx;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, LMDBPersistence mainDb, LightningTransaction lmdbTx)
        {
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));

            _lmdbTx = lmdbTx;
            _state = lmdbTx.OpenDatabase(FlatDbColumns.Account.ToString());
            _storage = lmdbTx.OpenDatabase(FlatDbColumns.Storage.ToString());

            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _stateTopNodes = _db.GetColumn(FlatDbColumns.StateTopNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNodes);
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
            _lmdbTx.Dispose();
        }

        public bool TryGetAccount(Address address, out Account? acc)
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = _lmdbTx.Get(_state, _mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], address));
            if (resultCode == MDBResultCode.NotFound)
            {
                acc = null;
                return true;
            }
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read account failed with result code {resultCode}");
            ReadOnlySpan<byte> value = valueMdb.AsSpan();
            if (value.IsNullOrEmpty())
            {
                acc = null;
                return true;
            }

            var ctx = new Rlp.ValueDecoderContext(value);
            acc = _mainDb._accountDecoder.Decode(ref ctx);
            return true;
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            ReadOnlySpan<byte> theKey = _mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], address, index);
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = _lmdbTx.Get(_storage, theKey);
            if (resultCode == MDBResultCode.NotFound)
            {
                valueBytes = null;
                return true;
            }
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read slot failed with result code {resultCode}");

            ReadOnlySpan<byte> value = valueMdb.AsSpan();
            if (value.IsNullOrEmpty())
            {
                valueBytes = null;
                return true;
            }

            valueBytes = value.ToArray();
            return true;
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
                return _storageNodes.Get(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, in path));
            }
        }

        public byte[]? GetAccountRaw(Hash256 addrHash)
        {
            return GetAccountRaw(addrHash.ValueHash256);
        }

        private byte[]? GetAccountRaw(in ValueHash256 accountHash)
        {
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = _lmdbTx.Get(_state, accountHash.Bytes[..StateKeyPrefixLength]);
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read account raw failed with result code {resultCode}");
            return valueMdb.CopyToNewArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = _mainDb.EncodeStorageKeyHashed(keySpan, addrHash.ValueHash256, slotHash.ValueHash256);
            (MDBResultCode resultCode, MDBValue key, MDBValue valueMdb) = _lmdbTx.Get(_storage, storageKey);
            if (resultCode != MDBResultCode.Success) throw new Exception($"Read storage raw failed with result code {resultCode}");
            return valueMdb.CopyToNewArray();
        }
    }
}
