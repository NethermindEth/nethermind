// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateProvider : ReadOnlyWorldStateProvider
{
    private IWorldState WorldState { get; set; }
    private Hash256 OriginalStateRoot { get; set; }

    public WorldStateProvider(
        ITrieStore preCachedTrieStore,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        WorldState = new WorldState(
            preCachedTrieStore,
            dbProvider.CodeDb,
            logManager,
            preBlockCaches,
            // Main thread should only read from prewarm caches, not spend extra time updating them.
            populatePreBlockCache: false);
    }

    public WorldStateProvider(
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null) : this(
        preBlockCaches is null ? trieStore : new PreCachedTrieStore(trieStore, preBlockCaches.RlpCache),
        trieStore,
        dbProvider,
        logManager,
        preBlockCaches)
    {
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
