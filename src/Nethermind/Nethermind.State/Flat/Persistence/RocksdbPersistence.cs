// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
using ZstdSharp;

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

    // Compress accounts here instead of in rocksdb, allowing disabling rocksdb's compression.
    // Unfortunately, only works well with accounts due to many redundant hash.
    // TODO: Does not help with latency, or anything. Overall use slightly more disk space. Maybe remove.
    private byte[] _zstdDictionary;
    private readonly Configuration _configuration;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotHit;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotMiss;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotCompareTime;
    private readonly Histogram.Child _rocksdBPersistenceTimesAddressHash;

    public record Configuration(
        bool UsePreimage = false
    )
    {
    }

    private static Histogram _rocksdBPersistenceTimes = Prometheus.Metrics.CreateHistogram("rocksdb_persistence_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
    });

    public RocksdbPersistence(IColumnsDb<FlatDbColumns> db, [KeyFilter(DbNames.Preimage)] IDb preimageDb, Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
        _preimageDb = preimageDb;

        LoadZstdDictionary();

        _rocksdBPersistenceTimesAddressHash = _rocksdBPersistenceTimes.WithLabels("address_hash");
        _rocksdBPersistenceTimesSlotHit = _rocksdBPersistenceTimes.WithLabels("slot_hash_hit");
        _rocksdBPersistenceTimesSlotMiss = _rocksdBPersistenceTimes.WithLabels("slot_hash_miss");
        _rocksdBPersistenceTimesSlotCompareTime = _rocksdBPersistenceTimes.WithLabels("slot_hash_compare_time");
        // TrainDictionary();
    }

    /*
    private Decompressor CreateDecompressor()
    {
        Decompressor decomp = new Decompressor();
        decomp.LoadDictionary(_zstdDictionary);
        return decomp;
    }
    */

    private void LoadZstdDictionary()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Nethermind.State.Flat.Persistence.zstddictionary.bin";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _zstdDictionary =  ms.ToArray();
        Console.Error.WriteLine($"Dictionary size is {_zstdDictionary.Length}");
    }

    internal void TrainDictionary()
    {
        using var snapshot = _db.CreateSnapshot();

        List<byte[]> data = new List<byte[]>();

        FlatDbColumns[] columnsToTest =
        [
            FlatDbColumns.State,
            // FlatDbColumns.Storage,
            // FlatDbColumns.StateTopNodes,
            // FlatDbColumns.StateNodes,
            // FlatDbColumns.StorageTopNodes,
            // FlatDbColumns.StorageNodes,
        ];

        Random rand = new Random(0);

        byte[] key = new byte[32];
        byte[] maxKey = new byte[32];
        Keccak.MaxValue.Bytes.CopyTo(maxKey);

        int totalSize = 0;

        foreach (FlatDbColumns column in columnsToTest)
        {
            ISortedKeyValueStore col = snapshot.GetColumn(column) as ISortedKeyValueStore;
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    data.Add(view.CurrentValue.ToArray());
                    totalSize += view.CurrentValue.Length;
                }
            }
        }

        Console.Error.WriteLine($"Training dictionary");
        byte[] dictionary = DictBuilder.TrainFromBuffer(data, 1024 * 2);
        Console.Error.WriteLine($"Trained a dictionary of size {dictionary.Length} from {data.Count} samples of total size {totalSize}");

        File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "flatdictionary.bin"), dictionary);

        using Compressor compressor = new Compressor();
        compressor.LoadDictionary(dictionary);

        foreach (FlatDbColumns column in columnsToTest)
        {
            int compressed = 0;
            int uncompressed = 0;

            ISortedKeyValueStore col = snapshot.GetColumn(column) as ISortedKeyValueStore;
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    data.Add(view.CurrentValue.ToArray());
                    totalSize += view.CurrentValue.Length;
                }
            }
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    uncompressed += view.CurrentValue.Length;
                    compressed += compressor.Wrap(view.CurrentValue).Length;
                }
            }

            Console.Error.WriteLine($"Expected ratio for {column} {(double)compressed / uncompressed}. Comppressed {compressed:N}, Uncompressed {uncompressed:N}");
        }
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

        Compressor compressor = new Compressor();
        compressor.LoadDictionary(_zstdDictionary);

        return new WriteBatch(this, _preimageDb.StartWriteBatch(), _db.StartWriteBatch(), dbSnap, compressor, to);
    }

    private class WriteBatch(
        RocksdbPersistence mainDb,
        IWriteBatch preimageWriteBatch,
        IColumnsWriteBatch<FlatDbColumns> batch,
        IColumnDbSnapshot<FlatDbColumns> dbSnap,
        Compressor compressor,
        StateId to
    ): IPersistence.IWriteBatch
    {
        IWriteOnlyKeyValueStore state = batch.GetColumnBatch(FlatDbColumns.State);
        IWriteOnlyKeyValueStore storage = batch.GetColumnBatch(FlatDbColumns.Storage);
        IWriteOnlyKeyValueStore stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
        IWriteOnlyKeyValueStore stateTopNodes = batch.GetColumnBatch(FlatDbColumns.StateTopNodes);
        IWriteOnlyKeyValueStore storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNodes);
        IWriteOnlyKeyValueStore storageTopNodes = batch.GetColumnBatch(FlatDbColumns.StorageTopNodes);
        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.None;

        public void Dispose()
        {
            SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
            batch.Dispose();
            dbSnap.Dispose();
            preimageWriteBatch.Dispose();
            compressor.Dispose();
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
            using (ISortedView storageNodeReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes)).GetViewBetween(firstKey, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                    removedEntry++;
                }
            }

            using (ISortedView storageNodeReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageTopNodes)).GetViewBetween(firstKey, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                    removedEntry++;
                }
            }

            removedEntry = 0; // Debug
            // for storage the prefix might change depending on the encoding
            firstKey.Fill(0x00);
            lastKey.Fill(0xff);
            mainDb.EncodeAccountKey(firstKey, addr);
            mainDb.EncodeAccountKey(lastKey, addr);
            using (ISortedView storageReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage)).GetViewBetween(firstKey, lastKey))
            {
                var storageWriter = storage;
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
            state.Remove(mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr));
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);
            state.PutSpan(mainDb.EncodeAccountKey(stackalloc byte[StateKeyPrefixLength], addr), stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ValueHash256 hash256 = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, hash256.BytesAsSpan);
            preimageWriteBatch.PutSpan(hash256.Bytes, slot.ToBigEndian());

            ReadOnlySpan<byte> theKey =  mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            storage.PutSpan(theKey, value, _flags);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = mainDb.EncodeStorageKey(stackalloc byte[StorageKeyLength], addr, slot);
            storage.Remove(theKey);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            if (mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");

            storage.PutSpan(mainDb.EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash.ValueHash256, slotHash.ValueHash256), value, _flags);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            if (mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");
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
                if (path.Length <= StorageNodesTopThreshold)
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

        // private Decompressor? _decompressor; // Simple single pool decompressor

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, RocksdbPersistence mainDb)
        {
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _state = _db.GetColumn(FlatDbColumns.State);
            _storage = _db.GetColumn(FlatDbColumns.Storage);
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
                if (path.Length <= StorageNodesTopThreshold)
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
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");
            return _state.GetSpan(accountHash.Bytes[..StateKeyPrefixLength]).ToArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = _mainDb.EncodeStorageKeyHashed(keySpan, addrHash.ValueHash256, slotHash.ValueHash256);
            return _storage.Get(storageKey);
        }
    }
}
