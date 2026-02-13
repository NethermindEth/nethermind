// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.SnapSync;

public class PatriciaSnapTrieFactory(INodeStorage nodeStorage, ILogManager logManager) : ISnapTrieFactory
{
    private readonly RawScopedTrieStore _stateTrieStore = new(nodeStorage, null);

    public ISnapTree CreateStateTree()
    {
        var adapter = new SnapUpperBoundAdapter(_stateTrieStore);
        // return new PatriciaSnapStateTree(new StateTree(adapter, logManager), adapter);
        return new PatriciaSnapStateTree(new StateTree(_stateTrieStore, logManager), adapter);
    }

    public ISnapTree CreateStorageTree(in ValueHash256 accountPath)
    {
        var storageTrieStore = new RawScopedTrieStore(nodeStorage, accountPath.ToCommitment());
        var adapter = new SnapUpperBoundAdapter(storageTrieStore);
        // return new PatriciaSnapStorageTree(new StorageTree(adapter, logManager), adapter);
        return new PatriciaSnapStorageTree(new StorageTree(storageTrieStore, logManager), adapter);
    }

}
