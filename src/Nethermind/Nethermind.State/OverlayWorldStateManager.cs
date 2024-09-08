// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateManager : IWorldStateManager
{
    private OverlayTrieStore _overlayTrieStore;
    private ILogManager? _logManager;
    private readonly IDb _codeDb;
    private IDbProvider _dbProvider;

    public IWorldStateProvider WorldStateProvider { get; }
    public PreBlockCaches? Caches { get; }

    public OverlayWorldStateManager(
        IReadOnlyDbProvider dbProvider,
        OverlayTrieStore overlayTrieStore,
        ILogManager? logManager,
        PreBlockCaches? caches = null)
    {
        WorldState worldState = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        _overlayTrieStore = overlayTrieStore;
        _dbProvider = dbProvider;
        WorldStateProvider = new OverlayWorldStateProvider(worldState, dbProvider, overlayTrieStore, logManager);
        _codeDb = dbProvider.GetDb<IDb>(DbNames.Code);

        _logManager = logManager;
        Caches = caches;
    }

    public IWorldStateProvider CreateResettableWorldStateProvider()
    {
        WorldState? worldState = Caches is not null
            ? new WorldState(
                new PreCachedTrieStore(_overlayTrieStore, Caches.RlpCache),
                _codeDb,
                _logManager,
                Caches)
            : new WorldState(
                _overlayTrieStore,
                _codeDb,
                _logManager);

        return new WorldStateProvider(worldState, _overlayTrieStore, _dbProvider, _logManager);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _overlayTrieStore.ReorgBoundaryReached += value;
        remove => _overlayTrieStore.ReorgBoundaryReached -= value;
    }

    public bool ClearCache() => Caches?.Clear() == true;
}
