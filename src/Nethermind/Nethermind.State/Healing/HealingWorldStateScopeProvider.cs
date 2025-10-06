// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingWorldStateScopeProvider(ITrieStore trieStore, IKeyValueStoreWithBatching codeDb, INodeStorage nodeStorage, Lazy<IPathRecovery> recovery, IProcessExitSource processExitSource, bool isPrimary, ILogManager logManager) :
    TrieStoreScopeProvider(trieStore, codeDb, processExitSource, isPrimary, logManager)
{
    private readonly ILogManager? _logManager = logManager;
    private readonly ITrieStore _trieStore = trieStore;

    protected override StateTree CreateStateTree()
    {
        return new HealingStateTree(_trieStore, nodeStorage, recovery, _logManager);
    }

    protected override StorageTree CreateStorageTree(Address address, Hash256 storageRoot)
    {
        return new HealingStorageTree(_trieStore.GetTrieStore(address), nodeStorage, storageRoot, _logManager, address, _backingStateTree.RootHash, recovery);
    }
}
