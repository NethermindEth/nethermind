// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// Use to overcome the missing metric for column. Probably should not be used on prod.
/// </summary>
public class RocksdbSeparatePersistence : IPersistence
{
    private static byte[] CurrentStateKey = Keccak.Compute("CurrentState").BytesToArray();
    private const int StateNodesKeyLength = 32 + 1;
    private const int StorageNodesKeyLength = 32 + 32 + 1;
    private const int StorageKeyLength = 32 + 32;
    private AccountDecoder _accountDecoder = AccountDecoder.Instance;

    private IDb _metadataDb;
    private IDb _flatStateDb;
    private IDb _flatStorageDb;
    private IDb _flatStateNodesDb;
    private IDb _flatStateTopNodesDb;
    private IDb _flatStorageNodesDb;

    public RocksdbSeparatePersistence(
        [KeyFilter(DbNames.FlatMetadata)] IDb metadataDb,
        [KeyFilter(DbNames.FlatState)] IDb flatStateDb,
        [KeyFilter(DbNames.FlatStorage)] IDb flatStorageDb,
        [KeyFilter(DbNames.FlatStateNodes)] IDb flatStateNodesDb,
        [KeyFilter(DbNames.FlatStateNodesTop)] IDb flatStateTopNodesDb,
        [KeyFilter(DbNames.FlatStorageNodes)] IDb flatStorageNodesDb
    )
    {
        _metadataDb = metadataDb;
        _flatStateDb = flatStateDb;
        _flatStorageDb = flatStorageDb;
        _flatStateNodesDb = flatStateNodesDb;
        _flatStateTopNodesDb = flatStateTopNodesDb;
        _flatStorageNodesDb = flatStorageNodesDb;
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
        return new PersistenceReader(
            _metadataDb,
            _flatStateDb,
            _flatStorageDb,
            _flatStateNodesDb,
            _flatStateTopNodesDb,
            _flatStorageNodesDb,
            this);
    }

    public IPersistence.IWriteBatch CreateWriteBatch(StateId from, StateId to)
    {
        var currentState = ReadCurrentState(_metadataDb);
        if (currentState != from)
        {
            throw new InvalidOperationException(
                $"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState}");
        }

        return new WriteBatch(
            _flatStorageNodesDb,
            _flatStorageDb,
            _metadataDb.StartWriteBatch(),
            _flatStateDb.StartWriteBatch(),
            _flatStorageDb.StartWriteBatch(),
            _flatStateNodesDb.StartWriteBatch(),
            _flatStateTopNodesDb.StartWriteBatch(),
            _flatStorageNodesDb.StartWriteBatch(),
            to
        );
    }

    private class WriteBatch(
        IDb storageNodesDb,
        IDb storageDb,
        IWriteBatch metadata,
        IWriteBatch state,
        IWriteBatch storage,
        IWriteBatch stateNodes,
        IWriteBatch stateNodesTop,
        IWriteBatch storageNodes,
        StateId to
    ): IPersistence.IWriteBatch
    {
        private AccountDecoder _accountDecoder = AccountDecoder.Instance;

        public void Dispose()
        {
            SetCurrentState(metadata, to);
            metadata.Dispose();
            state.Dispose();
            storage.Dispose();
            stateNodes.Dispose();
            stateNodesTop.Dispose();
            storageNodes.Dispose();
        }

        public void SelfDestruct(in ValueHash256 addr)
        {
            Span<byte> lastKey = stackalloc byte[StorageNodesKeyLength];
            lastKey.Fill(0xff);
            addr.Bytes.CopyTo(lastKey);

            using ISortedView storageNodeReader = ((ISortedKeyValueStore) storageNodesDb)
                .GetViewBetween(addr.Bytes, lastKey);

            var storageNodeWriter = storageNodes;
            while (storageNodeReader.MoveNext())
            {
                storageNodeWriter.Remove(storageNodeReader.CurrentKey);
            }

            using ISortedView storageReader = ((ISortedKeyValueStore) storageDb)
                .GetViewBetween(addr.Bytes, lastKey);

            var storageWriter = storage;
            while (storageReader.MoveNext())
            {
                storageWriter.Remove(storageReader.CurrentKey);
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

        public void SetStorage(Address addr, UInt256 slot, byte[] value)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr.ToAccountPath, slot);
            storage.PutSpan(theKey, value);
        }

        public void RemoveStorage(Address addr, UInt256 slot)
        {
            ReadOnlySpan<byte> theKey = EncodeStorageKey(stackalloc byte[StorageKeyLength], addr.ToAccountPath, slot);
            storage.Remove(theKey);
        }

        public void SetTrieNodes(Hash256? address, TreePath path, TrieNode tn)
        {
            if (address is null)
            {
                if (path.Length <= 5)
                {
                    stateNodesTop.PutSpan(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], path), tn.FullRlp.Span);
                }
                else
                {
                    stateNodes.PutSpan(EncodeStateNodeKey(stackalloc byte[StateNodesKeyLength], path), tn.FullRlp.Span);
                }
            }
            else
            {
                storageNodes.PutSpan(EncodeStorageNodeKey(stackalloc byte[StorageNodesKeyLength], address, path), tn.FullRlp.Span);
            }
        }
    }

    private class PersistenceReader : IPersistence.IPersistenceReader
    {
        private readonly IReadOnlyKeyValueStore _state;
        private readonly IReadOnlyKeyValueStore _storage;
        private readonly IReadOnlyKeyValueStore _stateNodes;
        private readonly IReadOnlyKeyValueStore _stateNodesTop;
        private readonly IReadOnlyKeyValueStore _storageNodes;
        private readonly RocksdbSeparatePersistence _mainDb;

        public PersistenceReader(
            IDb metadataDb,
            IDb flatStateDb,
            IDb flatStorageDb,
            IDb flatStateNodesDb,
            IDb flatStateTopNodesDb,
            IDb flatStorageNodesDb,
            RocksdbSeparatePersistence mainDb)
        {
            _mainDb = mainDb;
            CurrentState = ReadCurrentState(metadataDb);
            _state = flatStateDb;
            _storage = flatStorageDb;
            _stateNodes = flatStateNodesDb;
            _stateNodesTop = flatStateTopNodesDb;
            _storageNodes = flatStorageNodesDb;
        }

        public StateId CurrentState { get; }

        public void Dispose()
        {
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

                if (path.Length <= 5)
                {
                    return _stateNodesTop.Get(EncodeStateNodeKey(keyBuffer, in path));
                }
                else
                {
                    return _stateNodes.Get(EncodeStateNodeKey(keyBuffer, in path));
                }
            }
            Span<byte> keyBuffer2 = stackalloc byte[StorageNodesKeyLength];
            var rlp = _storageNodes.Get(EncodeStorageNodeKey(keyBuffer2, address, in path));
            return rlp;
        }
    }
}
