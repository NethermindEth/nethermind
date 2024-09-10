// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateProvider : ReadOnlyWorldStateProvider
{
    private IWorldState _worldState { get; set; }

    public WorldStateProvider(
        ITrieStore preCachedTrieStore,
        ITrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null) : base(dbProvider, trieStore.AsReadOnly(), logManager)
    {
        _worldState = new WorldState(
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
        return _worldState;
    }

    public override IWorldState GetWorldState()
    {
        return _worldState;
    }
}
