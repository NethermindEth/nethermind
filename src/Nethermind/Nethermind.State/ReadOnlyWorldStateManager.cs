// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
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

    public ReadOnlyWorldStateManager(
        IDbProvider dbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        ILogManager logManager
    )
    {
        _dbProvider = dbProvider;
        _readOnlyTrieStore = readOnlyTrieStore;
        _logManager = logManager;

        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        _codeDb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new StateReader(_readOnlyTrieStore, _codeDb, _logManager);
    }

    public virtual IWorldState GlobalWorldState => throw new InvalidOperationException("global world state not supported");

    public IStateReader GlobalStateReader { get; }

    public bool SupportHashLookup => _readOnlyTrieStore.Scheme == INodeStorage.KeyScheme.Hash;

    public IWorldState CreateResettableWorldState(IWorldState? forWarmup = null)
    {
        PreBlockCaches? preBlockCaches = (forWarmup as IPreBlockCaches)?.Caches;
        return preBlockCaches is not null
            ? new WorldState(
                new PreCachedTrieStore(_readOnlyTrieStore, preBlockCaches.RlpCache),
                _codeDb,
                _logManager,
                preBlockCaches)
            : new WorldState(
                _readOnlyTrieStore,
                _codeDb,
                _logManager);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return new OverridableWorldStateManager(_dbProvider, _readOnlyTrieStore, _logManager);
    }

    public IWorldState CreateOverlayWorldState(IKeyValueStoreWithBatching overlayState, IKeyValueStore overlayCode)
    {
        OverlayTrieStore overlayTrieStore = new(overlayState, _readOnlyTrieStore, _logManager);
        return new WorldState(overlayTrieStore, overlayCode, _logManager);
    }
}
