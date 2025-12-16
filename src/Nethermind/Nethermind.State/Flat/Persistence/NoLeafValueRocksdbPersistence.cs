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
using Org.BouncyCastle.Crmf;
using Org.BouncyCastle.Utilities;
using Prometheus;
using Bytes = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.State.Flat.Persistence;

// this persistence remove the leaf data and rely on the flat db to reconstruct it. It means the trie db is smaller
// and probably faster but adds the latency of the flat during a leaf lookup
public class NoLeafValueRocksdbPersistence : IPersistence
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

    public record Configuration()
    {
    }

    public NoLeafValueRocksdbPersistence(
        IColumnsDb<FlatDbColumns> db,
        Configuration configuration)
    {
        _configuration = configuration;
        _db = db;
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

        return new WriteBatch(this, _db.StartWriteBatch(), dbSnap, to);
    }

    private class WriteBatch : IPersistence.IWriteBatch
    {
        private IWriteOnlyKeyValueStore state;
        private IWriteOnlyKeyValueStore storage;
        private IWriteOnlyKeyValueStore stateNodes;
        private IWriteOnlyKeyValueStore stateTopNodes;
        private IWriteOnlyKeyValueStore storageNodes;

        private ISortedKeyValueStore storageSnap;
        private ISortedKeyValueStore storageNodesSnap;

        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        WriteFlags _flags = WriteFlags.None;
        private readonly NoLeafValueRocksdbPersistence _mainDb;
        private readonly IColumnsWriteBatch<FlatDbColumns> _batch;
        private readonly IColumnDbSnapshot<FlatDbColumns> _dbSnap;
        private readonly StateId _to;

        public WriteBatch(NoLeafValueRocksdbPersistence mainDb,
            IColumnsWriteBatch<FlatDbColumns> batch,
            IColumnDbSnapshot<FlatDbColumns> dbSnap,
            StateId to)
        {
            _mainDb = mainDb;
            _batch = batch;
            _dbSnap = dbSnap;
            _to = to;

            state = batch.GetColumnBatch(FlatDbColumns.Account);
            storage = batch.GetColumnBatch(FlatDbColumns.Storage);

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
            storage.PutSpan(_mainDb.EncodeStorageKeyHashed(stackalloc byte[StorageKeyLength], addrHash.ValueHash256, slotHash.ValueHash256), value, _flags);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            state.PutSpan(addrHash.Bytes[..StateKeyPrefixLength], stream.AsSpan(), _flags);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tn)
        {
            Span<byte> rlpSpan = tn.FullRlp.Span;

            if (tn.IsLeaf)
            {
                Span<byte> truncatedSpan = stackalloc byte[rlpSpan.Length];
                var rlpStream = new Rlp.ValueDecoderContext(rlpSpan);
                // The whole rlp
                rlpStream.ReadSequenceLength();

                int numberOfItems = rlpStream.PeekNumberOfItemsRemaining(null, 3);
                if (numberOfItems != 2) throw new InvalidOperationException($"Rlp of leaf should be exactly sequence of two. But got {numberOfItems}.");

                // Skip the key
                rlpStream.SkipItem();
                int offset;

                if (address is null)
                {
                    // Read the length of the value
                    (int prefixLength, int _) = rlpStream.PeekPrefixAndContentLength();

                    offset = rlpStream.Position + prefixLength;
                }
                else
                {
                    // For storage need to unwrap twice.
                    (int prefixLength, int contentLength) = rlpStream.PeekPrefixAndContentLength();
                    // If prefix length is 0 and content length is 1
                    // We dont want to skip anything.

                    // So the content itself is a bytearray of the actual value.
                    int byteArrayPrefix = 0;
                    if (contentLength > 0)
                    {
                        if (prefixLength > 0)
                        {
                            rlpStream.ReadByte();
                        }
                        if (rlpStream.Length == rlpStream.Position)
                        {
                            Console.Error.WriteLine($"The data is {tn.FullRlp.Span.ToHexString()}, to {prefixLength}, {contentLength}");
                        }
                        int prefix = rlpStream.PeekByte();
                        if (prefix < 128) // If its lower than this, then that is itself the value. So byteArrayPrefix is 0
                        {
                        }
                        else
                        {
                            // Then the value length is this byte - 128.
                            byteArrayPrefix = 1;

                            // Technically, it could also be more, but we dont support it as storage leaf value should not be more than 32
                        }
                    }

                    offset = rlpStream.Position + byteArrayPrefix;
                }

                rlpSpan[..offset].CopyTo(truncatedSpan);
                SetTrieNodesRaw(address, path, truncatedSpan[..offset]);
            }
            else
            {
                SetTrieNodesRaw(address, path, rlpSpan);
            }
        }

        private void SetTrieNodesRaw(Hash256? address, TreePath path, ReadOnlySpan<byte> rlpSpan)
        {
            if (address is null)
            {
                if (path.Length <= StateNodesTopThreshold)
                {
                    stateTopNodes.PutSpan(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopKeyLength], path), rlpSpan, _flags);
                }
                else
                {
                    stateNodes.PutSpan(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], path), rlpSpan, _flags);
                }
            }
            else
            {
                storageNodes.PutSpan(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, path), rlpSpan, _flags);
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
        private readonly NoLeafValueRocksdbPersistence _mainDb;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, NoLeafValueRocksdbPersistence mainDb)
        {
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _state = _db.GetColumn(FlatDbColumns.Account);
            _storage = _db.GetColumn(FlatDbColumns.Storage);
            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _stateTopNodes = _db.GetColumn(FlatDbColumns.StateTopNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNodes);
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
            _db.Dispose();
        }

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
            byte[]? value = DoTryLoadRlp(address, path, hash, flags);
            if (value is null) return null;

            var rlpStream = new Rlp.ValueDecoderContext(value);
            rlpStream.ReadSequenceLength();
            int numberOfItems = rlpStream.PeekNumberOfItemsRemaining(null, 3);
            if (numberOfItems > 2) return value;

            ReadOnlySpan<byte> valueSpan = rlpStream.DecodeByteArraySpan();
            (byte[] key, bool isLeaf) = HexPrefix.FromBytes(valueSpan);
            if (!isLeaf) return value;

            TreePath fullPath = path.Append(key);
            byte[] leafValue;
            if (address is null)
            {
                leafValue = GetAccountRaw(fullPath.Path);
            }
            else
            {
                leafValue = GetStorageRaw(address, fullPath.Path);
            }

            if (leafValue is null) throw new InvalidOperationException("Storage value is null on leaf");
            byte[] resultingValue = Bytes.Concat(value, leafValue);

            /*
            if (Keccak.Compute(resultingValue) != hash)
            {
                byte[]? correctValue = DoTryLoadRlp(address, path.Append([1, 1, 1, 1, 1, 1, 1, 1, 1, 1]), hash, flags);
                Console.Error.WriteLine($"Wrong concatenation {value.ToHexString()} + {leafValue.ToHexString()}.");
                Console.Error.WriteLine($"Correct value is {correctValue.ToHexString()}.");
                Console.Error.WriteLine($"Hash is {hash}");
            }
            */

            return resultingValue;
        }

        public byte[]? DoTryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
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
            return _state.GetSpan(accountHash.Bytes[..StateKeyPrefixLength]).ToArray();
        }

        public byte[]? GetStorageRaw(Hash256? addrHash, Hash256 slotHash)
        {
            return GetStorageRaw(addrHash, slotHash.ValueHash256);
        }

        private byte[]? GetStorageRaw(Hash256? addrHash, ValueHash256 slotHash)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> storageKey = _mainDb.EncodeStorageKeyHashed(keySpan, addrHash.ValueHash256, slotHash);
            return _storage.Get(storageKey);
        }
    }
}
