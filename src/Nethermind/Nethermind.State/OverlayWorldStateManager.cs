// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class OverlayWorldStateManager : IWorldStateManager
{
    private OverlayTrieStore _overlayTrieStore;
    private ILogManager _logManager;
    private IDbProvider _dbProvider;

    public IWorldStateProvider GlobalWorldStateProvider { get; }
    public PreBlockCaches? Caches { get; }

    public OverlayWorldStateManager(
        IReadOnlyDbProvider dbProvider,
        OverlayTrieStore overlayTrieStore,
        ILogManager logManager,
        PreBlockCaches? caches = null)
    {
        WorldState worldState = new(overlayTrieStore, dbProvider.GetDb<IDb>(DbNames.Code), logManager);
        _overlayTrieStore = overlayTrieStore;
        _dbProvider = dbProvider;
        GlobalWorldStateProvider = new OverlayWorldStateProvider(worldState, dbProvider, overlayTrieStore, logManager);

        _logManager = logManager;
        Caches = caches;
    }

    public IWorldStateProvider CreateResettableWorldStateProvider()
    {
        return new WorldStateProvider(_overlayTrieStore, _dbProvider, _logManager, Caches);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _overlayTrieStore.ReorgBoundaryReached += value;
        remove => _overlayTrieStore.ReorgBoundaryReached -= value;
    }

    public bool ClearCache() => Caches?.Clear() == true;
}
