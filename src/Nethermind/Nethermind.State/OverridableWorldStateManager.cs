// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverridableWorldStateManager : IOverridableWorldScope
{
    private readonly StateReader _reader;
    private readonly IReadOnlyDbProvider _dbProvider;

    public OverridableWorldStateManager(IDbProvider dbProvider, IReadOnlyTrieStore trieStore, IBlocksConfig blocksConfig, ILogManager? logManager)
    {
        IReadOnlyDbProvider readOnlyDbProvider = new ReadOnlyDbProvider(dbProvider, true);
        ITrieStore overlayTrieStore = new OverlayTrieStore(readOnlyDbProvider.StateDb, trieStore);

        PreBlockCaches? preBlockCaches = null;
        if (blocksConfig.PreWarmStateOnBlockProcessing)
        {
            preBlockCaches = new PreBlockCaches();
            overlayTrieStore = new PreCachedTrieStore(overlayTrieStore, preBlockCaches.RlpCache);
        }

        _dbProvider = readOnlyDbProvider;
        _reader = new(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager);
        WorldState = new WorldState(overlayTrieStore, readOnlyDbProvider.CodeDb, logManager, preBlockCaches, populatePreBlockCache: false);
    }

    public IWorldState WorldState { get; }
    public IStateReader GlobalStateReader => _reader;
    public void ResetOverrides()
    {
        _dbProvider.ClearTempChanges();
    }
}
