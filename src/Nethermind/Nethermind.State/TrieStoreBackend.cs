// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class TrieStoreBackend : IWorldStateBackend
{
    private readonly ITrieStore _trieStore;
    private readonly ILogManager _logManager;
    protected StateTree _backingStateTree;
    private readonly Dictionary<AddressAsKey, StorageTree> _storages = new();

    public TrieStoreBackend(ITrieStore trieStore, ILogManager logManager)
    {
        _trieStore = trieStore;
        _logManager = logManager;

        _backingStateTree = CreateStateTree();
    }

    protected virtual StateTree CreateStateTree()
    {
        return new StateTree(_trieStore.GetTrieStore(null), _logManager);
    }

    public bool HasRoot(BlockHeader? baseBlock)
    {
        return _trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);
    }

    public IWorldStateBackend.IScope BeginScope(BlockHeader? baseBlock)
    {
        var trieStoreCloser = _trieStore.BeginScope(baseBlock);
        _backingStateTree.RootHash = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;

        return new TrieStoreWorldStateBackendScope(_backingStateTree, this, trieStoreCloser);
    }

    private void Reset()
    {
        _backingStateTree.RootHash = Keccak.EmptyTreeHash;
        _storages.Clear();
    }

    protected virtual StorageTree CreateStorageTree(Address address)
    {
        Hash256 storageRoot = _backingStateTree.Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        return new StorageTree(_trieStore.GetTrieStore(address), storageRoot, _logManager);
    }

    private IWorldStateBackend.IStorageTree CreateAndTrackStorageTree(Address address)
    {
        if (_storages.TryGetValue(address, out var storageTree))
        {
            return storageTree;
        }

        storageTree = CreateStorageTree(address);
        _storages[address] = storageTree;
        return storageTree;
    }

    private void Commit(long blockNumber)
    {
        using var blockCommitter = _trieStore.BeginBlockCommit(blockNumber);

        // Note: These all runs in about 0.4ms. So the little overhead like attempting to sort the tasks
        // may make it worst. Always check on mainnet.
        using ArrayPoolList<Task> commitTask = new ArrayPoolList<Task>(_storages.Count);
        foreach (KeyValuePair<AddressAsKey, StorageTree> storage in _storages)
        {
            if (blockCommitter.TryRequestConcurrencyQuota())
            {
                commitTask.Add(Task.Factory.StartNew((ctx) =>
                {
                    StorageTree st = (StorageTree)ctx;
                    st.Commit();
                    blockCommitter.ReturnConcurrencyQuota();
                }, storage.Value));
            }
            else
            {
                storage.Value.Commit();
            }
        }

        Task.WaitAll(commitTask.AsSpan());
        _backingStateTree.Commit();

        _storages.Clear();
    }

    private class TrieStoreWorldStateBackendScope(StateTree backingStateTree, TrieStoreBackend backend, IDisposable trieStoreCloser) : IWorldStateBackend.IScope
    {
        public void Dispose()
        {
            trieStoreCloser.Dispose();
            backend.Reset();
        }

        public IWorldStateBackend.IStateTree StateTree => backingStateTree;

        public IWorldStateBackend.IStorageTree CreateStorageTree(Address address)
        {
            return backend.CreateAndTrackStorageTree(address);
        }

        public void Commit(long blockNumber)
        {
            backend.Commit(blockNumber);
        }
    }
}
