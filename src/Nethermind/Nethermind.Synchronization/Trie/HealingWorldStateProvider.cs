// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingWorldStateProvider : ReadOnlyWorldStateProvider
{
    private HealingWorldState _worldState { get; }

    public HealingWorldStateProvider(
        ITrieStore preCachedTrieStore,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        _worldState = new HealingWorldState(
            preCachedTrieStore,
            dbProvider.CodeDb,
            logManager,
            preBlockCaches,
            // Main thread should only read from prewarm caches, not spend extra time updating them.
            populatePreBlockCache: false);
    }

    public override IWorldState GetGlobalWorldState(BlockHeader header)
    {
        // TODO: return corresponding worldState depending on header
        return _worldState;
    }

    public override IWorldState GetWorldState()
    {
        return _worldState;
    }

    public void InitializeNetwork(ITrieNodeRecovery<GetTrieNodesRequest> recovery)
    {
        _worldState.InitializeNetwork(recovery);
    }
}
