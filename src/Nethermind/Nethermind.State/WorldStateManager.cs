// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager(
    IWorldState worldState,
    ITrieStore trieStore,
    IDbProvider dbProvider,
    ILogManager logManager)
    : ReadOnlyWorldStateManager(dbProvider, trieStore.AsReadOnly(), logManager)
{
    public static WorldStateManager CreateForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        ITrieStore trieStore = new TrieStore(dbProvider.StateDb, logManager);
        IWorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider, logManager);
    }

    public override IWorldState GlobalWorldState => worldState;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => trieStore.ReorgBoundaryReached += value;
        remove => trieStore.ReorgBoundaryReached -= value;
    }

    public override void InitializeNetwork(ITrieNodeRecovery<IReadOnlyList<Hash256>> hashRecovery, ITrieNodeRecovery<GetTrieNodesRequest> nodeRecovery)
    {
        if (trieStore is HealingTrieStore healingTrieStore)
        {
            healingTrieStore.InitializeNetwork(hashRecovery);
        }

        if (worldState is HealingWorldState healingWorldState)
        {
            healingWorldState.InitializeNetwork(nodeRecovery);
        }
    }
}
