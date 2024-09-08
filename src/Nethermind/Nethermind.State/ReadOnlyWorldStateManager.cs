// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

/// <summary>
/// Mainly to make it easier for test
/// </summary>
public class ReadOnlyWorldStateManager : IWorldStateManager
{
    private readonly IReadOnlyTrieStore _readOnlyTrieStore;
    private readonly ILogManager _logManager;
    private readonly ReadOnlyDb _codeDb;
    private readonly IDbProvider _dbProvider;

    public virtual IWorldStateProvider WorldStateProvider { get; }
    public PreBlockCaches? Caches { get; }

    public ReadOnlyWorldStateManager(
        ReadOnlyWorldStateProvider worldStateProvider,
        IDbProvider dbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null
    )
    {
        WorldStateProvider = worldStateProvider;
        _readOnlyTrieStore = readOnlyTrieStore;
        _dbProvider = dbProvider;
        _logManager = logManager;
                Caches = preBlockCaches;


        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        _codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
    }

    public IWorldStateProvider CreateResettableWorldStateProvider()
    {
        WorldState? worldState = Caches is not null
            ? new WorldState(
                new PreCachedTrieStore(_readOnlyTrieStore, Caches.RlpCache),
                _codeDb,
                _logManager,
                Caches)
            : new WorldState(
                _readOnlyTrieStore,
                _codeDb,
                _logManager);

        return new WorldStateProvider(worldState, _readOnlyTrieStore, _dbProvider, _logManager);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }

    public bool ClearCache() => Caches?.Clear() == true;
}
