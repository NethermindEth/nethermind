// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Synchronization.FastSync;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager(
    IWorldState worldState,
    ITrieStore trieStore,
    IDbProvider dbProvider,
    ILogManager logManager,
    IProcessExitSource? processExitSource = null)
    : ReadOnlyWorldStateManager(dbProvider, trieStore.AsReadOnly(), logManager, processExitSource)
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
