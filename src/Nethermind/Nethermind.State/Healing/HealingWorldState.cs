// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Healing;

public class HealingWorldState(
    ITrieStore trieStore,
    INodeStorage nodeStorage,
    IKeyValueStoreWithBatching codeDb,
    Lazy<IPathRecovery> pathRecovery,
    Lazy<ICodeRecovery> codeRecovery,
    ILogManager logManager,
    PreBlockCaches? preBlockCaches = null,
    bool populatePreBlockCache = true)
    : WorldState(trieStore, new HealingCodeDb(codeDb, codeRecovery), logManager, new HealingStateTree(trieStore, nodeStorage, pathRecovery, logManager), new HealingStorageTreeFactory(nodeStorage, pathRecovery), preBlockCaches, populatePreBlockCache)
{
}
