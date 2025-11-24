// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

public class RocksdbPersistence : IPersistence
{
    private readonly IColumnsDb<FlatDbColumns> _db;
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private const int StateNodesKeyLength = 32 + 1;
    private const int StorageNodesKeyLength = 32 + 32 + 1;

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

    internal static ReadOnlySpan<byte> EncodeStorageNodeKey(Span<byte> buffer, Hash256 address, in TreePath path)
    {
        address.Bytes.CopyTo(buffer);
        path.Path.Bytes.CopyTo(buffer[32..]);
        buffer[32 + 32] = (byte)path.Length;
        return buffer[..StorageNodesKeyLength];
    }

    public IPersistenceReader CreateReader()
    {
        return new PersistenceReader(_db.StartSnapshot());
    }

    public void Add(Snapshot snapshot)
    {
        // TODO: Lock

        if (CurrentState != snapshot.From)
        {
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {snapshot.From}, Db state: {CurrentState}");
        }

        using (var batch = _db.StartWriteBatch())
        {
            IWriteOnlyKeyValueStore stateNodes = batch.GetColumnBatch(FlatDbColumns.StateNodes);
            IWriteOnlyKeyValueStore storageNodes = batch.GetColumnBatch(FlatDbColumns.StorageNode);

            Span<byte> keyBuffer = stackalloc byte[StorageNodesKeyLength];

            foreach (var tn in snapshot.TrieNodes)
            {
                (Hash256? address, TreePath path) = tn.Key;

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

    private class PersistenceReader : IPersistenceReader
    {
        private readonly IColumnDbSnapshot<FlatDbColumns> _db;
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _storageNodes;

        public PersistenceReader(IColumnDbSnapshot<FlatDbColumns> db)
        {
            _db = db;
            CurrentState = ReadCurrentState(db.GetColumn(FlatDbColumns.Metadata));
            _stateNodes = _db.GetColumn(FlatDbColumns.StateNodes);
            _storageNodes = _db.GetColumn(FlatDbColumns.StorageNode);
        }

        public void Dispose()
        {
            _db.Dispose();
        }

        public bool TryGetAccount(Address address, out Account acc)
        {
            throw new System.NotImplementedException();
        }

        public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
        {
            throw new System.NotImplementedException();
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
            return _storageNodes.Get(EncodeStorageNodeKey(keyBuffer2, hash, in path));
        }
    }
}
