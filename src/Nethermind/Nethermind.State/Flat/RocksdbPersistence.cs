// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private const int StateNodesKeyLength = 32 + 1;
    private const int StorageNodesKeyLength = 32 + 32 + 1;
    private const int StorageKeyLength = 32 + 32;
    private const int MaxKeyLength = 32 + 32 + 1;
    private AccountDecoder _accountDecoder = AccountDecoder.Instance;

    public StateId CurrentState { get; set; }

    public RocksdbPersistence(IColumnsDb<FlatDbColumns> db)
    {
        _db = db;
        CurrentState = ReadCurrentState(db.GetColumnDb(FlatDbColumns.Metadata));
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

    internal static ReadOnlySpan<byte> EncodeStateNodeKey(Span<byte> buffer, in TreePath path)
    {
        path.Path.Bytes.CopyTo(buffer);
        buffer[32] = (byte)path.Length;
        return buffer[..StateNodesKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageNodeKey(Span<byte> buffer, Hash256 addr, in TreePath path)
    {
        addr.Bytes.CopyTo(buffer);
        path.Path.Bytes.CopyTo(buffer[32..]);
        buffer[32 + 32] = (byte)path.Length;
        return buffer[..StorageNodesKeyLength];
    }

    internal static ReadOnlySpan<byte> EncodeStorageKey(Span<byte> buffer, ValueHash256 addr, UInt256 slot)
    {
        addr.Bytes.CopyTo(buffer);
        slot.ToBigEndian(buffer[32..64]);
        return buffer[..StorageKeyLength];
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        return new PersistenceReader(_db.StartSnapshot(), this);
    }

    public void Add(Snapshot snapshot)
    {
        // TODO: Lock

        using var dbSnap = _db.StartSnapshot();
        var currentState = ReadCurrentState(dbSnap.GetColumn(FlatDbColumns.Metadata));
        if (currentState != snapshot.From)
        {
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {snapshot.From}, Db state: {currentState}");
        }

        Span<byte> keyBuffer = stackalloc byte[MaxKeyLength];

        using (var batch = _db.StartWriteBatch())
        {
            IWriteOnlyKeyValueStore state = batch.GetColumnBatch(FlatDbColumns.State);
            IWriteOnlyKeyValueStore storage = batch.GetColumnBatch(FlatDbColumns.Storage);
            IWriteOnlyKeyValueStore stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            IWriteOnlyKeyValueStore storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNodes);

            foreach (var toSelfDestructStorage in snapshot.SelfDestructedStorages)
            {
                SelfDestruct(toSelfDestructStorage, dbSnap, batch);
            }

            // Selfdestruct
            foreach (var kv in snapshot.Accounts)
            {
                (Address addr, Account? account) = kv;
                if (account is null)
                {
                    state.Remove(addr.ToAccountPath.Bytes);
                }
                else
                {
                    using var stream = _accountDecoder.EncodeToNewNettyStream(account);

                    state.PutSpan(addr.ToAccountPath.Bytes, stream.AsSpan());
                }
            }

            foreach (var kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), byte[] value) = kv;

                ReadOnlySpan<byte> theKey = EncodeStorageKey(keyBuffer, addr.ToAccountPath, slot);
                storage.PutSpan(EncodeStorageKey(keyBuffer, addr.ToAccountPath, slot), value);

            }

            foreach (var tn in snapshot.TrieNodes)
            {
                (Hash256? address, TreePath path) = tn.Key;

                if (tn.Value.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (tn.Value.NodeType == NodeType.Unknown) continue;
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                if (address is null)
                {
                    stateNodes.PutSpan(EncodeStateNodeKey(keyBuffer, path), tn.Value.FullRlp.Span);
                    tn.Value.IsPersisted = true;
                }
                else
                {
                    storageNodes.PutSpan(EncodeStorageNodeKey(keyBuffer, address, path), tn.Value.FullRlp.Span);
                    tn.Value.IsPersisted = true;
                }

                tn.Value.PrunePersistedRecursively(1);
            }

            SetCurrentState(batch.GetColumnBatch(FlatDbColumns.Metadata), snapshot.To);
        }

        CurrentState = snapshot.To;
    }

    private void SelfDestruct(in ValueHash256 addr, IColumnDbSnapshot<FlatDbColumns> dbSnap, IColumnsWriteBatch<FlatDbColumns> writer)
    {
        Span<byte> lastKey = stackalloc byte[StorageNodesKeyLength];
        lastKey.Fill(0xff);
        addr.Bytes.CopyTo(lastKey);

        using ISortedView storageNodeReader = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.StorageNodes))
            .GetViewBetween(addr.Bytes, lastKey);

        var storageNodeWriter = writer.GetColumnBatch(FlatDbColumns.StorageNodes);
        while (storageNodeReader.MoveNext())
        {
            storageNodeWriter.Remove(storageNodeReader.CurrentKey);
        }

        using ISortedView storageReader = ((ISortedKeyValueStore) dbSnap.GetColumn(FlatDbColumns.Storage))
            .GetViewBetween(addr.Bytes, lastKey);

        var storageWriter = writer.GetColumnBatch(FlatDbColumns.Storage);
        while (storageReader.MoveNext())
        {
            storageWriter.Remove(storageReader.CurrentKey);
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly IReadOnlyKeyValueStore _state;
        private readonly IReadOnlyKeyValueStore _storage;
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _storageNodes;
        private readonly RocksdbPersistence _mainDb;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db, RocksdbPersistence mainDb)
        {
            _db = db;
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _state = _db.GetColumn(FlatDbColumns.State);
            _storage = _db.GetColumn(FlatDbColumns.Storage);
            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNodes);
        }

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

        public StateId CurrentState { get; }
        public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
        {
            if (address is null)
            {
                Span<byte> keyBuffer = stackalloc byte[StateNodesKeyLength];
                return _stateNodes.Get(EncodeStateNodeKey(keyBuffer, in path));
            }
            Span<byte> keyBuffer2 = stackalloc byte[StorageNodesKeyLength];
            var rlp = _storageNodes.Get(EncodeStorageNodeKey(keyBuffer2, address, in path));
            return rlp;
        }
    }
}
