// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public class WorldStateManager : IWorldStateManager
{
    private readonly IWorldState _worldState;
    private readonly IPruningTrieStore _trieStore;
    private readonly IReadOnlyTrieStore _readOnlyTrieStore;
    private readonly ILogManager _logManager;
    private readonly ReadOnlyDb _readaOnlyCodeCb;
    private readonly IDbProvider _dbProvider;
    private readonly BlockingVerifyTrie? _blockingVerifyTrie;
    private readonly ILastNStateRootTracker _lastNStateRootTracker;

    public WorldStateManager(
        IWorldState worldState,
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

    public IWorldState GlobalWorldState => _worldState;

    public IReadOnlyKeyValueStore? HashServer => _trieStore.Scheme != INodeStorage.KeyScheme.Hash ? null : _trieStore.TrieNodeRlpStore;

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _trieStore.ReorgBoundaryReached += value;
        remove => _trieStore.ReorgBoundaryReached -= value;
    }

    public void InitializeNetwork(IPathRecovery pathRecovery)
    {
        if (_worldState is HealingWorldState healingWorldState)
        {
            healingWorldState.InitializeNetwork(pathRecovery);
        }
    }

    public IStateReader GlobalStateReader { get; }

    public ISnapServer? SnapServer => _trieStore.Scheme == INodeStorage.KeyScheme.Hash ? null : new SnapServer.SnapServer(_readOnlyTrieStore, _readaOnlyCodeCb, GlobalStateReader, _logManager, _lastNStateRootTracker);

    public IWorldState CreateResettableWorldState()
    {
        return new WorldState(
            _readOnlyTrieStore,
            _readaOnlyCodeCb,
            _logManager);
    }

    public IWorldState CreateWorldStateForWarmingUp(IWorldState forWarmup)
    {
        PreBlockCaches? preBlockCaches = (forWarmup as IPreBlockCaches)?.Caches;
        return preBlockCaches is not null
            ? new WorldState(
                new PreCachedTrieStore(_readOnlyTrieStore, preBlockCaches.RlpCache),
                _readaOnlyCodeCb,
                _logManager,
                preBlockCaches)
            : CreateResettableWorldState();
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return new OverridableWorldStateManager(_dbProvider, _readOnlyTrieStore, _logManager);
    }

    public IWorldState CreateOverlayWorldState(IKeyValueStoreWithBatching overlayState, IKeyValueStoreWithBatching overlayCode)
    {
        OverlayTrieStore overlayTrieStore = new(overlayState, _readOnlyTrieStore);
        return new WorldState(overlayTrieStore, overlayCode, _logManager);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        return _blockingVerifyTrie?.VerifyTrie(stateAtBlock, cancellationToken) ?? true;
    }
}
