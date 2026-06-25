// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapTrieFactory(INodeStorage nodeStorage, ILogManager logManager) : ISnapTrieFactory
{
    private readonly RawScopedTrieStore _stateTrieStore = new(nodeStorage, null);

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        SnapUpperBoundAdapter adapter = new(_stateTrieStore);
        return new PatriciaSnapStateTree(new StateTree(adapter, logManager), adapter, nodeStorage);
    }

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
        => CreateStorageTree(accountPath, storageBatch: null);

    public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath, ISnapStorageBatch? storageBatch)
    {
        Hash256 address = accountPath.ToCommitment();
        INodeStorage.IWriteBatch? sharedWriteBatch = storageBatch switch
        {
            null => null,
            PatriciaSnapStorageBatch batch => batch,
            _ => throw new ArgumentException($"Unsupported storage batch type {storageBatch.GetType().FullName}.", nameof(storageBatch))
        };

        RawScopedTrieStore storageTrieStore = new(nodeStorage, address, sharedWriteBatch);
        SnapUpperBoundAdapter adapter = new(storageTrieStore);
        return new PatriciaSnapStorageTree(new StorageTree(adapter, logManager), adapter, nodeStorage, address);
    }

    public ISnapStorageBatch StartStorageBatch() =>
        new PatriciaSnapStorageBatch(nodeStorage);

    private sealed class PatriciaSnapStorageBatch(INodeStorage nodeStorage) : ISnapStorageBatch, INodeStorage.IWriteBatch
    {
        private readonly List<NodeWrite> _writes = [];
        private bool _disposed;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writes.Add(new NodeWrite(address, path, currentNodeKeccak, data.ToArray(), writeFlags));
        }

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _disposed = true;
            using INodeStorage.IWriteBatch writeBatch = nodeStorage.StartWriteBatch();
            foreach (NodeWrite write in _writes)
            {
                writeBatch.Set(write.Address, write.Path, write.Hash, write.Data, write.WriteFlags);
            }
        }

        public void Dispose() => _disposed = true;

        private readonly record struct NodeWrite(Hash256? Address, TreePath Path, ValueHash256 Hash, byte[] Data, WriteFlags WriteFlags);
    }
}
