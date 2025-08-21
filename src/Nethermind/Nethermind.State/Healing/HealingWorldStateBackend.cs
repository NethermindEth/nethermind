// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingWorldStateBackend(ITrieStore trieStore, INodeStorage nodeStorage, ILogManager logManager) : TrieStoreBackend(trieStore, logManager)
{
    private IPathRecovery? _recovery;
    private readonly ILogManager? _logManager = logManager;
    private readonly ITrieStore _trieStore = trieStore;

    public void InitializeNetwork(IPathRecovery recovery)
    {
        (_backingStateTree as HealingStateTree)!.InitializeNetwork(recovery);
        _recovery = recovery;
    }

    protected override StateTree CreateStateTree()
    {
        return new HealingStateTree(_trieStore, nodeStorage, _logManager);
    }

    protected override StorageTree CreateStorageTree(Address address)
    {
        Hash256 storageRoot = _backingStateTree.Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        return new HealingStorageTree(_trieStore.GetTrieStore(address), nodeStorage, storageRoot, _logManager, address, _backingStateTree.RootHash, _recovery);
    }
}
