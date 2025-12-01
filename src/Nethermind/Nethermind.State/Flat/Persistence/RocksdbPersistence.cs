// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();

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
    private const int StorageNodesTopThreshold = 4;
    private const int StorageNodesTopPathLength = 2;
    private const int StorageNodesTopKeyLength = StorageHashPrefixLength + StorageNodesTopPathLength + PathLengthLength;


    internal AccountDecoder _accountDecoder = AccountDecoder.Instance;

    public RocksdbPersistence(IColumnsDb<FlatDbColumns> db)
    {
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

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in ValueHash256 addr, in UInt256 slot)
    {
        ValueHash256 hash256 = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, hash256.BytesAsSpan);
        return EncodeStorageKey(buffer, addr, hash256);
    }

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, in ValueHash256 addr, ValueHash256 slot)
    {
        addr.Bytes[..StorageHashPrefixLength].CopyTo(buffer);
        slot.Bytes.CopyTo(buffer[StorageHashPrefixLength..(StorageHashPrefixLength + StorageSlotKeySize)]);
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

        return new WriteBatch(_db.StartWriteBatch(), dbSnap, to);
    }

    private class WriteBatch(
        IColumnsWriteBatch<FlatDbColumns> batch,
        IColumnDbSnapshot<FlatDbColumns> dbSnap,
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

        public void Dispose()
        {
            SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), to);
            batch.Dispose();
            dbSnap.Dispose();
        }

        public void SelfDestruct(in ValueHash256 addr)
        {
            Span<byte> lastKey = stackalloc byte[StorageNodesKeyLength];
            lastKey.Fill(0xff);
            addr.Bytes[..StorageHashPrefixLength].CopyTo(lastKey);

            using (ISortedView storageNodeReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageNodes)).GetViewBetween(addr.Bytes, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                }
            }

            using (ISortedView storageNodeReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.StorageTopNodes)).GetViewBetween(addr.Bytes, lastKey))
            {
                var storageNodeWriter = storageNodes;
                while (storageNodeReader.MoveNext())
                {
                    storageNodeWriter.Remove(storageNodeReader.CurrentKey);
                }
            }

            using (ISortedView storageReader = ((ISortedKeyValueStore)dbSnap.GetColumn(FlatDbColumns.Storage)).GetViewBetween(addr.Bytes, lastKey))
            {
                var storageWriter = storage;
                while (storageReader.MoveNext())
                {
                    storageWriter.Remove(storageReader.CurrentKey);
                }
            }
        }

        public void RemoveAccount(Address addr)
        {
            state.Remove(addr.ToAccountPath.Bytes);
        }

        public void SetAccount(Address addr, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            state.PutSpan(addr.ToAccountPath.Bytes, stream.AsSpan());
        }

        public void SetStorage(Address addr, UInt256 slot, ReadOnlySpan<byte> value)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr.ToAccountPath, slot);
            storage.PutSpan(theKey, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr.ToAccountPath, slot);
            storage.Remove(theKey);
        }

        public void SetStorageRaw(Hash256? addrHash, Hash256 slotHash, ReadOnlySpan<byte> value)
        {
            storage.PutSpan(EncodeStorageKey(stackalloc byte[StorageKeyLength], addrHash, slotHash), value);
        }

        public void SetAccountRaw(Hash256 addrHash, Account account)
        {
            using var stream = _accountDecoder.EncodeToNewNettyStream(account);

            state.PutSpan(addrHash.Bytes, stream.AsSpan());
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tn)
        {
            if (address is null)
            {
                if (path.Length <= StateNodesTopThreshold)
                {
                    stateTopNodes.PutSpan(EncodeStateTopNodeKey(stackalloc byte[StateNodesTopKeyLength], path), tn.FullRlp.Span);
                }
                else
                {
                    stateNodes.PutSpan(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], path), tn.FullRlp.Span);
                }
            }
            else
            {
                if (path.Length <= StorageNodesTopThreshold)
                {
                    storageNodes.PutSpan(EncodeStorageNodeTopKey(stackalloc byte[StorageNodesKeyLength], address, path), tn.FullRlp.Span);
                }
                else
                {
                    storageTopNodes.PutSpan(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, path), tn.FullRlp.Span);
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

        public bool TryGetAccount(Address address, out Account? acc)
        {
            Span<byte> value = _state.GetSpan(address.ToAccountPath.Bytes);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    acc = null;
                    return false;
                }

                var ctx = new Rlp.ValueDecoderContext(value);
                acc = _mainDb._accountDecoder.Decode(ref ctx);
                return true;
            }
            catch (RlpException)
            {
                Console.Error.WriteLine($"The value is {address}, {value.ToHexString()}");
                throw;
            }
            finally
            {
                _state.DangerousReleaseMemory(value);
            }
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] valueBytes)
        {
            Span<byte> keySpan = stackalloc byte[StorageKeyLength];
            ReadOnlySpan<byte> theKey = EncodeStorageKey(keySpan, address.ToAccountPath, index);
            Span<byte> value = _storage.GetSpan(theKey);
            try
            {
                if (value.IsNullOrEmpty())
                {
                    valueBytes = null;
                    return false;
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
                Span<byte> keyBuffer = stackalloc byte[StateNodesKeyLength];

                if (path.Length <= StateNodesTopThreshold)
                {
                    return _stateTopNodes.Get(EncodeStateNodeKey(keyBuffer, in path));
                }
                else
                {
                    return _stateNodes.Get(EncodeStateNodeKey(keyBuffer, in path));
                }
            }
            else
            {
                Span<byte> keyBuffer2 = stackalloc byte[StorageNodesKeyLength];
                if (path.Length <= StorageNodesTopThreshold)
                {
                    var rlp = _storageTopNodes.Get(EncodeStorageNodeTopKey(keyBuffer2, address, in path));
                    return rlp;
                }
                else
                {
                    var rlp = _storageNodes.Get(EncodeStorageNodeKey(keyBuffer2, address, in path));
                    return rlp;
                }
            }
        }
    }
}
