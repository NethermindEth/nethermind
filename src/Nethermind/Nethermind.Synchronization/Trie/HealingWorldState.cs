// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingWorldState : WorldState
{
    public HealingWorldState(ITrieStore? trieStore, [KeyFilter(DbNames.Code)] IKeyValueStore? codeDb, ILogManager? logManager)
        : base(trieStore, codeDb, logManager, new HealingStateTree(trieStore, logManager), new HealingStorageTreeFactory())
    {
    }

    public void InitializeNetwork(ITrieNodeRecovery<GetTrieNodesRequest> recovery)
    {
        StateProviderTree.InitializeNetwork(recovery);
        StorageTreeFactory.InitializeNetwork(recovery);
    }

    private HealingStorageTreeFactory StorageTreeFactory => ((HealingStorageTreeFactory)_persistentStorageProvider._storageTreeFactory);

    private HealingStateTree StateProviderTree => ((HealingStateTree)_stateProvider._tree);
}
