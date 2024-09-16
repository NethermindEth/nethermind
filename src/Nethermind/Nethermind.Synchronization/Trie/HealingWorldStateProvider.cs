// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.Trie.Pruning;

namespace Nethermind.Synchronization.Trie;

public class HealingWorldStateProvider : ReadOnlyWorldStateProvider
{
    private HealingWorldState WorldState { get; }
    private Hash256 OriginalStateRoot { get; set; }

    public HealingWorldStateProvider(
        ITrieStore preCachedTrieStore,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        WorldState = new HealingWorldState(
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
        return WorldState;
    }

    public override IWorldState GetWorldState()
    {
        return WorldState;
    }

    public void InitializeNetwork(ITrieNodeRecovery<GetTrieNodesRequest> recovery)
    {
        WorldState.InitializeNetwork(recovery);
    }

    public override void SetStateRoot(Hash256 stateRoot)
    {
        OriginalStateRoot = WorldState.StateRoot;
        WorldState.StateRoot = stateRoot;
    }

    public override void Reset()
    {
        WorldState.StateRoot = OriginalStateRoot;
        WorldState.Reset();
    }
}
