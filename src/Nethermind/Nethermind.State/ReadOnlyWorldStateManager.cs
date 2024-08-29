// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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

    public PreBlockCaches? Caches { get; }

    public ReadOnlyWorldStateManager(IDbProvider dbProvider,
        IReadOnlyTrieStore readOnlyTrieStore,
        ILogManager logManager,
        PreBlockCaches? preBlockCaches = null)
    {
        _readOnlyTrieStore = readOnlyTrieStore;
        _logManager = logManager;

        _dbProvider = dbProvider.AsReadOnly(false);
        _codeDb = _dbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new StateReader(_readOnlyTrieStore, _codeDb, _logManager);
        Caches = preBlockCaches;
    }

    public virtual IWorldState GlobalWorldState =>
        throw new InvalidOperationException("global world state not supported");

    public IStateReader GlobalStateReader { get; }

    public IReadOnlyTrieStore TrieStore => _readOnlyTrieStore;

    public IScopedWorldStateManager CreateResettableWorldStateManager()
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

        return new ScopedReadOnlyWorldStateManager(worldState, _dbProvider, _readOnlyTrieStore, _logManager, Caches);
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }

    public virtual IWorldState GetGlobalWorldState(BlockHeader blockHeader) => throw new InvalidOperationException("global world state not supported");
    public bool ClearCache() => Caches?.Clear() == true;
    public bool HasStateRoot(Hash256 root) => GlobalStateReader.HasStateForRoot(root);
}
