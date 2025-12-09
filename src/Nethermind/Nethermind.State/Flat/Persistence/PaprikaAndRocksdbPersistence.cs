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
using Paprika;
using Paprika.Data;
using Prometheus;
using ZstdSharp;
using Account = Nethermind.Core.Account;
using IDb = Nethermind.Db.IDb;

namespace Nethermind.State.Flat.Persistence;

public class PaprikaAndRocksdbPersistence : IPersistence
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

    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private readonly Configuration _configuration;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotHit;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotMiss;
    private readonly Histogram.Child _rocksdBPersistenceTimesSlotCompareTime;
    private readonly Histogram.Child _rocksdBPersistenceTimesAddressHash;
    private readonly Paprika.IDb _paprikaDb;

    public record Configuration(
        bool UsePreimage = false
    ) {
    }

    private static Histogram _rocksdBPersistenceTimes = DevMetric.Factory.CreateHistogram("rocksdb_persistence_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "type" },
        Buckets = [1]
    });

    public PaprikaAndRocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        [KeyFilter(DbNames.Preimage)] IDb preimageDb,
        Paprika.IDb paprikaDb,
        Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
        _paprikaDb = paprikaDb;

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

    private AccountCompressor _accountCompressor = new AccountCompressor();

    public IPersistence.IPersistenceReader CreateReader()
    {
        IReadOnlyBatch paprikaSnapshot = _paprikaDb.BeginReadOnlyBatch();

        return new PersistenceReader(_db.CreateSnapshot(), paprikaSnapshot, this, accountCompressor: _accountCompressor);
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

        var paprikaBatch = _paprikaDb.BeginNextBatch();
        var paprikaReader = _paprikaDb.BeginReadOnlyBatch();
        var compressor = _accountCompressor.CreateCompressor();
        return new WriteBatch(this, paprikaBatch, paprikaReader, _db.StartWriteBatch(), dbSnap, compressor, to);
    }

    private class WriteBatch : IPersistence.IWriteBatch
    {
        private IWriteOnlyKeyValueStore stateNodes;
        private IWriteOnlyKeyValueStore stateTopNodes;
        private IWriteOnlyKeyValueStore storageNodes;

        private ISortedKeyValueStore storageNodesSnap;
        private readonly IBatch _paprikaBatch;

        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.None;
        private readonly PaprikaAndRocksdbPersistence _mainDb;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;
        private readonly Compressor _accountCompressor;
        private readonly IReadOnlyBatch _paprikaReader;

        public bool ConcurrentStorage => true;

        public WriteBatch(
            PaprikaAndRocksdbPersistence mainDb,
            IBatch paprikaBatch,
            IReadOnlyBatch paprikaReader,
            IColumnsWriteBatch<FlatDbColumns> batch,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            Compressor accountCompressor,
            StateId to)
        {
            _mainDb = mainDb;
            _batch = batch;
            _dbSnap = dbSnap;
            _to = to;
            _accountCompressor = accountCompressor;

            stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            stateTopNodes = batch.GetColumnBatch(FlatDbColumns.StateTopNodes);
            storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNodes);

            _paprikaBatch = paprikaBatch;
            _paprikaReader = paprikaReader;
            _paprikaBatch.SetMetadata((uint)to.blockNumber, to.stateRoot.ToCommitment().ToPaprikaKeccak());

            storageNodesSnap = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.StorageNodes));
        }

        public void Dispose()
        {
            SetCurrentState(_batch.GetColumnBatch(FlatDbColumns.Metadata), _to);
            _batch.Dispose();
            _dbSnap.Dispose();
            _paprikaBatch.Commit(CommitOptions.FlushDataAndRoot).AsTask().Wait();
            _paprikaBatch.Dispose();
            _paprikaReader.Dispose();
            _mainDb._paprikaDb.Flush();
            _accountCompressor.Dispose();
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

            Key key = Key.StorageCell(NibblePath.FromKey(addr.ToPaprikaKeccak()), NibblePath.Empty);
            _paprikaBatch.DeleteByPrefix(key);

            return removedEntry;
        }

        public void RemoveAccount(Address addr)
        {
            NibblePath contract = NibblePath.FromKey(addr.ToPaprikaKeccak());
            Key key = Key.Account(contract);

            _paprikaBatch.SetRaw(key, StorageTree.ZeroBytes);
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            Span<byte> accountBuffer = stackalloc byte[256]; // Account is like 90 bytes or something
            int compressedSize = _accountCompressor.Wrap(stream.AsSpan(), accountBuffer);

            NibblePath contract = NibblePath.FromKey(addr.ToPaprikaKeccak());
            Key key = Key.Account(contract);

            _paprikaBatch.SetRaw(key, accountBuffer[..compressedSize]);
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ValueHash256 addrHash = addr.ToAccountPath.ToHash256();
            ValueHash256 slotHash = ValueKeccak.Zero;
            StorageTree.ComputeKeyWithLookup(slot, slotHash.BytesAsSpan);

            SetStorageRaw(in addrHash, in slotHash, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            NibblePath contract = NibblePath.FromKey(addr.ToPaprikaKeccak());
            Key key = Key.StorageCell(contract, slot.SlotToPaprikaKeccak());
            _paprikaBatch.SetRaw(key, StorageTree.ZeroBytes);
        }

        private void SetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ReadOnlySpan<byte> value)
        {
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");

            NibblePath contract = NibblePath.FromKey(addrHash.ToPaprikaKeccak());
            Key key = Key.StorageCell(contract, slotHash.ToPaprikaKeccak());
            _paprikaBatch.SetRaw(key, value);
        }

        public void SetStorageRaw(Hash256 addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            SetStorageRaw(addrHash.ValueHash256, slotHash.ValueHash256, value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            if (_mainDb._configuration.UsePreimage) throw new InvalidOperationException("Cannot set raw when using preimage");
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            Span<byte> accountBuffer = stackalloc byte[256]; // Account is like 90 bytes or something
            int compressedSize = _accountCompressor.Wrap(stream.AsSpan(), accountBuffer);

            NibblePath contract = NibblePath.FromKey(addrHash.ToPaprikaKeccak());
            Key key = Key.Account(contract);

            _paprikaBatch.SetRaw(key, accountBuffer[..compressedSize]);
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
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _stateTopNodes;
        private readonly IReadOnlyKeyValueStore _storageNodes;
        private readonly PaprikaAndRocksdbPersistence _mainDb;
        private readonly IReadOnlyBatch _paprikaSnapshot;
        private readonly AccountCompressor _accountCompressor;
        private readonly bool _usePreimage;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, IReadOnlyBatch paprikaSnapshot, PaprikaAndRocksdbPersistence mainDb, AccountCompressor accountCompressor)
        {
            _usePreimage = mainDb._configuration.UsePreimage;
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _paprikaSnapshot = paprikaSnapshot;
            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _stateTopNodes = _db.GetColumn(FlatDbColumns.StateTopNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNodes);
            _accountCompressor = accountCompressor;
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
            _paprikaSnapshot.Dispose();
        }

        public bool TryGetAccount(Address addr, out Account? acc)
        {
            Key key = Key.Account(addr.ToPaprikaKeccak());
            if (_paprikaSnapshot.TryGet(key, out ReadOnlySpan<byte> accBytes))
            {
                if (accBytes.Length == 0)
                {
                    acc = null;
                    return true;
                }

                if (Bytes.AreEqual(accBytes, StorageTree.ZeroBytes))
                {
                    acc = null;
                    return true;
                }

                Decompressor decompressor = _accountCompressor.RentDecompresor();
                try
                {
                    Span<byte> outBuffer = stackalloc byte[256];
                    int outSize = decompressor.Unwrap(accBytes, outBuffer);

                    var ctx = new Rlp.ValueDecoderContext(outBuffer[..outSize]);
                    acc = _mainDb._accountDecoder.Decode(ref ctx);
                    return true;
                }
                finally
                {
                    _accountCompressor.ReturnDecompressor(decompressor);
                }
            }

            acc = null;
            return true;
        }

        public bool TryGetSlot(Address addr, in UInt256 slot, out byte[] valueBytes)
        {
            NibblePath contract = NibblePath.FromKey(addr.ToPaprikaKeccak());
            Key key = Key.StorageCell(contract, slot.SlotToPaprikaKeccak());

            if (_paprikaSnapshot.TryGet(key, out ReadOnlySpan<byte> span))
            {
                if (span.IsEmpty)
                {
                    valueBytes = null;
                    return true;
                }

                valueBytes = span.ToArray();
                return true;
            }

            valueBytes = null;
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
            if (_usePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");
            Key key = Key.Account(accountHash.ToPaprikaKeccak());
            if (_paprikaSnapshot.TryGet(key, out ReadOnlySpan<byte> accBytes))
            {
                if (accBytes.Length == 0)
                {
                    return null;
                }

                Decompressor decompressor = _accountCompressor.RentDecompresor();
                try
                {
                    Span<byte> outBuffer = stackalloc byte[256];
                    int outSize = decompressor.Unwrap(accBytes, outBuffer);

                    return outBuffer[..outSize].ToArray();
                }
                finally
                {
                    _accountCompressor.ReturnDecompressor(decompressor);
                }
            }

            return null;
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            return GetStorageRaw(addrHash!.ValueHash256, slotHash.ValueHash256);
        }

        public byte[]? GetStorageRaw(ValueHash256 addrHash, ValueHash256 slotHash)
        {
            if (_usePreimage) throw new InvalidOperationException("Raw operation not available in preimage mode");

            NibblePath contract = NibblePath.FromKey(addrHash.ToPaprikaKeccak());
            Key key = Key.StorageCell(contract, slotHash.ToPaprikaKeccak());

            if (_paprikaSnapshot.TryGet(key, out ReadOnlySpan<byte> span))
            {
                return span.ToArray();
            }

            return null;
        }
    }
}
