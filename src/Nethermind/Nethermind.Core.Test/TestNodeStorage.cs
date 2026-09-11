// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Trie;

namespace Nethermind.Core.Test;

/// <summary>
/// Hash-addressed node storage used by trie tests.
/// </summary>
/// <remarks>
/// The production stores may use the trie address and path to organize their keys. Tests only need the
/// content-addressed behavior, so nodes are keyed by their hash in the supplied test database. Access is
/// serialized because several trie committers can write to the same in-memory database concurrently.
/// </remarks>
public sealed class TestNodeStorage(IKeyValueStoreWithBatching keyValueStore) : INodeStorage
{
    private static readonly byte[] EmptyTreeHashBytes = [128];
    private readonly IKeyValueStoreWithBatching _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));
    private readonly object _lock = new();

    public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None)
    {
        if (keccak == Keccak.EmptyTreeHash.ValueHash256)
        {
            return [.. EmptyTreeHashBytes];
        }

        lock (_lock)
        {
            return _keyValueStore.Get(keccak.Bytes, readFlags);
        }
    }

    public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
    {
        if (hash == Keccak.EmptyTreeHash.ValueHash256)
        {
            return;
        }

        lock (_lock)
        {
            if (data.IsNull())
            {
                _keyValueStore.Remove(hash.Bytes);
            }
            else
            {
                _keyValueStore.PutSpan(hash.Bytes, data, writeFlags);
            }
        }
    }

    public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash)
    {
        if (hash == Keccak.EmptyTreeHash.ValueHash256)
        {
            return true;
        }

        lock (_lock)
        {
            return _keyValueStore.KeyExists(hash.Bytes);
        }
    }

    public INodeStorage.IWriteBatch StartWriteBatch()
    {
        lock (_lock)
        {
            return new WriteBatch(_keyValueStore.StartWriteBatch(), _lock);
        }
    }

    public void Flush(bool onlyWal)
    {
        lock (_lock)
        {
            if (_keyValueStore is IDb db)
            {
                db.Flush(onlyWal);
            }
        }
    }

    public void Compact()
    {
        lock (_lock)
        {
            if (_keyValueStore is IDb db)
            {
                db.Compact();
            }
        }
    }

    private sealed class WriteBatch(IWriteBatch writeBatch, object lockObject) : INodeStorage.IWriteBatch
    {
        private int _disposed;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags)
        {
            if (hash == Keccak.EmptyTreeHash.ValueHash256)
            {
                return;
            }

            lock (lockObject)
            {
                writeBatch.PutSpan(hash.Bytes, data, writeFlags);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (lockObject)
            {
                writeBatch.Dispose();
            }
        }
    }
}
