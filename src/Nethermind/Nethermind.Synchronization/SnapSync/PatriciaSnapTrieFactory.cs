// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

public class PatriciaSnapTrieFactory : ISnapTrieFactory
{
    private readonly INodeStorage _nodeStorage;
    private readonly ILogManager _logManager;
    private readonly int _disableWalBatchSize;
    private readonly RawScopedTrieStore _stateTrieStore;

    public PatriciaSnapTrieFactory(INodeStorage nodeStorage, ILogManager logManager, int disableWalBatchSize = 0)
    {
        _nodeStorage = nodeStorage;
        _logManager = logManager;
        _disableWalBatchSize = TrieWriteBatchSettings.GetDisableWalBatchSize(disableWalBatchSize);
        _stateTrieStore = new RawScopedTrieStore(nodeStorage, null, disableWalBatchSize: _disableWalBatchSize);
    }

    public ISnapTree<PathWithAccount> CreateStateTree()
    {
        SnapUpperBoundAdapter adapter = new(_stateTrieStore);
        return new PatriciaSnapStateTree(new StateTree(adapter, _logManager), adapter, _nodeStorage);
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

        RawScopedTrieStore storageTrieStore = new(_nodeStorage, address, sharedWriteBatch, _disableWalBatchSize);
        SnapUpperBoundAdapter adapter = new(storageTrieStore);
        return new PatriciaSnapStorageTree(new StorageTree(adapter, _logManager), adapter, _nodeStorage, address);
    }

    public ISnapStorageBatch StartStorageBatch() =>
        new PatriciaSnapStorageBatch(_nodeStorage, _disableWalBatchSize);

    private sealed class PatriciaSnapStorageBatch(INodeStorage nodeStorage, int disableWalBatchSize) : IParallelSnapStorageBatch, INodeStorage.IWriteBatch
    {
        private readonly object _lock = new();
        private readonly List<NodeWrite> _writes = [];
        private bool _disposed;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
        {
            byte[] dataCopy = data.ToArray();
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _writes.Add(new NodeWrite(address, path, currentNodeKeccak, dataCopy, writeFlags));
            }
        }

        public void Commit()
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _disposed = true;
            }

            for (int writeIndex = 0; writeIndex < _writes.Count;)
            {
                int endIndex = Math.Min(writeIndex + disableWalBatchSize, _writes.Count);
                using INodeStorage.IWriteBatch writeBatch = nodeStorage.StartWriteBatch();
                for (; writeIndex < endIndex; writeIndex++)
                {
                    NodeWrite write = _writes[writeIndex];
                    writeBatch.Set(write.Address, write.Path, write.Hash, write.Data, write.WriteFlags);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
            }
        }

        private readonly record struct NodeWrite(Hash256? Address, TreePath Path, ValueHash256 Hash, byte[] Data, WriteFlags WriteFlags);
    }
}
