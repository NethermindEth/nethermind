// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingWorldStateScopeProvider(
    ITrieStore trieStore,
    IKeyValueStoreWithBatching codeDb,
    INodeStorage nodeStorage,
    Lazy<IPathRecovery> recovery,
    Lazy<ICodeRecovery> codeRecovery,
    IStateHeaderProvider stateHeaderProvider,
    ILogManager logManager)
    : TrieStoreScopeProvider(trieStore, new HealingCodeDb(codeDb, codeRecovery), stateHeaderProvider, logManager, codeDbIsPersistent: true)
{
    private readonly ILogManager _logManager = logManager;
    private readonly ITrieStore _trieStore = trieStore;

    // The healing trees fetch any node missing locally, the root included, so a scope can open on a root the trie store does not hold.
    protected override bool CanBeginScope(BlockHeader? baseBlock) => true;

    protected override StateTree CreateStateTree() => new HealingStateTree(_trieStore, nodeStorage, recovery, _logManager);

    protected override StorageTree CreateStorageTree(Address address, Hash256 storageRoot) => new HealingStorageTree(_trieStore.GetTrieStore(address), nodeStorage, storageRoot, _logManager, address, BackingStateTree.RootHash, recovery);
}
