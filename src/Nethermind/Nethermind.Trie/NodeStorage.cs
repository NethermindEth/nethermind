// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.Trie;

public class NodeStorage : INodeStorage
{
    private readonly IKeyValueStore _keyValueStore;
    private static byte[] EmptyTreeHashBytes = { 128 };
    public const int StoragePathLength = 44;
    public INodeStorage.KeyScheme Scheme { get; }

    public NodeStorage(IKeyValueStore keyValueStore, INodeStorage.KeyScheme scheme = INodeStorage.KeyScheme.HalfPath)
    {
        _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
        Scheme = scheme;
    }

    public Span<byte> GetExpectedPath(Span<byte> pathSpan, Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        if (Scheme == INodeStorage.KeyScheme.HalfPath)
        {
            return GetHalfPathNodeStoragePathSpan(pathSpan, address, path, keccak);
        }

        return GetHashBasedStoragePath(pathSpan, keccak);
    }

    public static byte[] GetHalfPathNodeStoragePath(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        Span<byte> bytes = new byte[StoragePathLength];
        return GetHalfPathNodeStoragePathSpan(bytes, address, path, keccak).ToArray();
    }

    private static Span<byte> GetHalfPathNodeStoragePathSpan(Span<byte> pathSpan, Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        Debug.Assert(pathSpan.Length == StoragePathLength);

        if (address == null)
        {
            // Separate the top level tree into its own section. This improve cache hit rate by about a few %. The idea
            // being that the top level trie is spread out across the database, leading for a poor cache hit. In practice
            // its not that much, likely because they are also likely to be at the top of LSM tree.
            // The total size of node for path length<=4 should be around 40MB making them very cacheable. In practice
            // whether they get cached or not is seemingly random. This leaves the remaining subtree size of higher depth
            // to be about 44kB meaning they should take at most 2 iops on 16kB block size. Trying to make this tree
            // split at depth 5 does not seems to improve things even with more cache.
            if (path.Length <= 4)
            {
                pathSpan[0] = 0;
            }
            else
            {
                pathSpan[0] = 1;
            }

            // Keep key small
            path.Path.BytesAsSpan[..7].CopyTo(pathSpan[1..]);
            keccak.Bytes.CopyTo(pathSpan[8..]);
            return pathSpan[..40];
        }
        else
        {
            // Technically, you'll need 9 byte for address and 8 byte for storage on mainnet. But we want to keep
            // key small at the same time too. If the key are too small, multiple node will be out of order, which
            // can be slower but as long as they are in the same data block, it should not make a difference.
            // On mainnet, the out of order key is around 0.03% for address and 0.07% for storage.
            pathSpan[0] = 2;
            address.Bytes[..6].CopyTo(pathSpan[1..]);
            path.Path.BytesAsSpan[..5].CopyTo(pathSpan[7..]);

            keccak.Bytes.CopyTo(pathSpan[12..]);
            return pathSpan;
        }

    }

    private static Span<byte> GetHashBasedStoragePath(Span<byte> pathSpan, in ValueHash256 keccak)
    {
        Debug.Assert(pathSpan.Length == StoragePathLength);
        keccak.Bytes.CopyTo(pathSpan);
        return pathSpan[..32];
    }

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (keccak == Keccak.EmptyTreeHash)
        {
            return EmptyTreeHashBytes;
        }

        Span<byte> storagePathSpan = stackalloc byte[StoragePathLength];
        if (Scheme == INodeStorage.KeyScheme.HalfPath)
        {
            return _keyValueStore.Get(GetHalfPathNodeStoragePathSpan(storagePathSpan, address, path, keccak), readFlags)
                   ?? _keyValueStore.Get(GetHashBasedStoragePath(storagePathSpan, keccak), readFlags);
        }
        else
        {
            return _keyValueStore.Get(GetHashBasedStoragePath(storagePathSpan, keccak), readFlags)
                   ?? _keyValueStore.Get(GetHalfPathNodeStoragePathSpan(storagePathSpan, address, path, keccak), readFlags);
        }
    }

    public bool KeyExists(Hash256? address, in TreePath path, in ValueHash256 keccak)
    {
        if (keccak == Keccak.EmptyTreeHash)
        {
            return true;
        }

        Span<byte> storagePathSpan = stackalloc byte[StoragePathLength];
        if (Scheme == INodeStorage.KeyScheme.HalfPath)
        {
            return _keyValueStore.Get(GetHalfPathNodeStoragePathSpan(storagePathSpan, address, path, keccak)) != null
                   || _keyValueStore.Get(GetHashBasedStoragePath(storagePathSpan, keccak)) != null;
        }
        else
        {
            return _keyValueStore.Get(GetHashBasedStoragePath(storagePathSpan, keccak)) != null
                   || _keyValueStore.Get(GetHalfPathNodeStoragePathSpan(storagePathSpan, address, path, keccak)) != null;
        }
    }

    public INodeStorage.WriteBatch StartWriteBatch()
    {
        return new WriteBatch(((IKeyValueStoreWithBatching)_keyValueStore).StartWriteBatch(), this);
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] toArray, WriteFlags writeFlags = WriteFlags.None)
    {
        if (keccak == Keccak.EmptyTreeHash)
        {
            return;
        }

        Span<byte> storagePathSpan = stackalloc byte[StoragePathLength];
        _keyValueStore.Set(GetExpectedPath(storagePathSpan, address, path, keccak), toArray, writeFlags);
    }

    public byte[]? GetByHash(ReadOnlySpan<byte> key, ReadFlags flags)
    {
        return _keyValueStore.Get(key, flags);
    }

    public void Flush()
    {
        if (_keyValueStore is IDb db)
        {
            db.Flush();
        }
    }

    private class WriteBatch : INodeStorage.WriteBatch
    {
        private readonly IWriteBatch _writeBatch;
        private readonly NodeStorage _nodeStorage;

        public WriteBatch(IWriteBatch writeBatch, NodeStorage nodeStorage)
        {
            _writeBatch = writeBatch;
            _nodeStorage = nodeStorage;
        }

        public void Dispose()
        {
            _writeBatch.Dispose();
        }

        public void Set(Hash256? address, in TreePath path, in ValueHash256 keccak, byte[] toArray, WriteFlags writeFlags)
        {
            if (keccak == Keccak.EmptyTreeHash)
            {
                return;
            }

            Span<byte> storagePathSpan = stackalloc byte[StoragePathLength];
            _writeBatch.Set(_nodeStorage.GetExpectedPath(storagePathSpan, address, path, keccak), toArray, writeFlags);
        }
    }
}
