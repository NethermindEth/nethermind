// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.VerkleDb;
using System.Threading;
using Nethermind.State.Healing;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;


namespace Nethermind.State;

public class VerkleWorldStateManager: IWorldStateManager
{
    private readonly IWorldState _worldState;
    private readonly IVerkleTreeStore _trieStore;
    private readonly IReadOnlyVerkleTreeStore _readOnlyTrieStore;
    private readonly ILogManager _logManager;
    private readonly ReadOnlyDb _readaOnlyCodeCb;
    private readonly IDbProvider _dbProvider;
    private readonly BlockingVerifyTrie? _blockingVerifyTrie;

    // can be used to verkle sync
    private readonly ILastNStateRootTracker _lastNStateRootTracker;


    public VerkleWorldStateManager(
        IWorldState worldState,
        IVerkleTreeStore trieStore,
        IDbProvider dbProvider,
        ILogManager logManager,
        ILastNStateRootTracker? lastNStateRootTracker = null
    )
    {
        _dbProvider = dbProvider;
        _trieStore = trieStore;
        _readOnlyTrieStore = trieStore.AsReadOnly(new VerkleMemoryDb());
        _worldState = worldState;
        _logManager = logManager;

        IReadOnlyDbProvider readOnlyDbProvider = dbProvider.AsReadOnly(false);
        _readaOnlyCodeCb = readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new VerkleStateReader(_readOnlyTrieStore, _readaOnlyCodeCb, _logManager);
        _blockingVerifyTrie = new BlockingVerifyTrie(null, GlobalStateReader, _readaOnlyCodeCb!, logManager);
        _lastNStateRootTracker = lastNStateRootTracker;

    }

    public static WorldStateManager CreateForTest(IDbProvider dbProvider, ILogManager logManager)
    {
        ITrieStore trieStore = new TrieStore(dbProvider.StateDb, logManager);
        IWorldState worldState = new WorldState(trieStore, dbProvider.CodeDb, logManager);

        return new WorldStateManager(worldState, trieStore, dbProvider, logManager);
    }

    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldState GlobalWorldState => _worldState;

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => _trieStore.ReorgBoundaryReached += value;
        remove => _trieStore.ReorgBoundaryReached -= value;
    }

    public void InitializeNetwork(IPathRecovery pathRecovery)
    {
    }

    public IStateReader GlobalStateReader { get; }

    public ISnapServer? SnapServer =>  null;

    public IWorldState CreateResettableWorldState()
    {
        return new VerkleWorldState(
            _readOnlyTrieStore,
            _readaOnlyCodeCb,
            _logManager);
    }

    public IWorldState CreateWorldStateForWarmingUp(IWorldState forWarmup)
    {
        PreBlockCaches? preBlockCaches = (forWarmup as IPreBlockCaches)?.Caches;
        // TODO: fix this as well to actually use the preBlockCache
        return CreateResettableWorldState();
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        return new OverridableWorldStateManager(_dbProvider, _readOnlyTrieStore, _logManager);
    }

    public IWorldState CreateOverlayWorldState(IReadOnlyDbProvider editableDbProvider)
    {
        OverlayVerkleTreeStore overlayTrieStore = new(editableDbProvider, _readOnlyTrieStore, _logManager);
        return new VerkleWorldState(overlayTrieStore, editableDbProvider.CodeDb, _logManager);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        return _blockingVerifyTrie?.VerifyTrie(stateAtBlock, cancellationToken) ?? true;
    }
}
