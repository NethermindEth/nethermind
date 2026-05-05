// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
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
    {
        Hash256 address = accountPath.ToCommitment();
        RawScopedTrieStore storageTrieStore = new(nodeStorage, address);
        SnapUpperBoundAdapter adapter = new(storageTrieStore);
        return new PatriciaSnapStorageTree(new StorageTree(adapter, logManager), adapter, nodeStorage, address);
    }

}
