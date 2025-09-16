// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Org.BouncyCastle.Pqc.Crypto.Picnic;

namespace Nethermind.State;

public class WorldStateManager : IWorldStateManager
{
    private readonly IWorldStateScopeProvider _worldState;
    private readonly IPruningTrieStore _trieStore;
    private readonly IReadOnlyTrieStore _readOnlyTrieStore;
    private readonly ILogManager _logManager;
    private readonly ReadOnlyDb _readaOnlyCodeCb;
    private readonly IDbProvider _dbProvider;
    private readonly BlockingVerifyTrie? _blockingVerifyTrie;
    private readonly ILastNStateRootTracker _lastNStateRootTracker;

    public WorldStateManager(
        IWorldStateScopeProvider worldState,
        IPruningTrieStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        ILastNStateRootTracker lastNStateRootTracker = null
    )
    {
        _dbProvider = dbProvider;
        _worldState = worldState;
        _trieStore = trieStore;
        _readOnlyTrieStore = trieStore.AsReadOnly();
        _logManager = logManager;

        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        _readaOnlyCodeCb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new StateReader(_readOnlyTrieStore, _readaOnlyCodeCb, _logManager);
        _blockingVerifyTrie = new BlockingVerifyTrie(trieStore, GlobalStateReader, _readaOnlyCodeCb!, logManager);
        _lastNStateRootTracker = lastNStateRootTracker;
    }

    public IWorldStateScopeProvider GlobalWorldState => _worldState;

    public IReadOnlyKeyValueStore? HashServer => _trieStore.Scheme != INodeStorage.KeyScheme.Hash ? null : _trieStore.TrieNodeRlpStore;

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _trieStore.ReorgBoundaryReached += value;
        remove => _trieStore.ReorgBoundaryReached -= value;
    }

    public IStateReader GlobalStateReader { get; }

    public ISnapServer? SnapServer => _trieStore.Scheme == INodeStorage.KeyScheme.Hash ? null : new SnapServer.SnapServer(_readOnlyTrieStore, _readaOnlyCodeCb, GlobalStateReader, _logManager, _lastNStateRootTracker);

    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new TrieStoreScopeProvider(_readOnlyTrieStore, _readaOnlyCodeCb, _logManager);
    }

    public IWorldStateScopeProvider CreateWorldStateForWarmingUp(IWorldStateScopeProvider forWarmup)
    {
        if (forWarmup is IPreBlockCaches preBlockCaches)
        {
            return new PrewarmerScopeProvider(
                new TrieStoreScopeProvider(new PreCachedTrieStore(_readOnlyTrieStore, preBlockCaches.Caches.RlpCache),
                    _readaOnlyCodeCb, _logManager),
                preBlockCaches.Caches,
                populatePreBlockCache: true
            );
        }

        return CreateResettableWorldState();
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return new OverridableWorldStateManager(_dbProvider, _readOnlyTrieStore, _logManager);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        return _blockingVerifyTrie?.VerifyTrie(stateAtBlock, cancellationToken) ?? true;
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        _trieStore.PersistCache(cancellationToken);
    }
}
