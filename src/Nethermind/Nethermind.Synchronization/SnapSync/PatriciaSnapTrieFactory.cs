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
        return new PatriciaSnapStateTree(new StateTree(adapter, logManager), adapter, nodeStorage);
    }

    public ISnapTree CreateStorageTree(in ValueHash256 accountPath)
    {
        Hash256 address = accountPath.ToCommitment();
        var storageTrieStore = new RawScopedTrieStore(nodeStorage, address);
        var adapter = new SnapUpperBoundAdapter(storageTrieStore);
        return new PatriciaSnapStorageTree(new StorageTree(adapter, logManager), adapter, nodeStorage, address);
    }

}
