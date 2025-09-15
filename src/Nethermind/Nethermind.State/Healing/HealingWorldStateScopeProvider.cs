// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingWorldStateScopeProvider(ITrieStore trieStore, INodeStorage nodeStorage, Lazy<IPathRecovery> recovery, ILogManager logManager) : TrieStoreScopeProvider(trieStore, logManager)
{
    private readonly ILogManager? _logManager = logManager;
    private readonly ITrieStore _trieStore = trieStore;

    protected override StateTree CreateStateTree()
    {
        return new HealingStateTree(_trieStore, nodeStorage, recovery, _logManager);
    }

    protected override StorageTree CreateStorageTree(Address address)
    {
        Hash256 storageRoot = _backingStateTree.Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        return new HealingStorageTree(_trieStore.GetTrieStore(address), nodeStorage, storageRoot, _logManager, address, _backingStateTree.RootHash, recovery);
    }
}
