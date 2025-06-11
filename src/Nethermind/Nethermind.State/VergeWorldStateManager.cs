// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Healing;
using Nethermind.State.SnapServer;
using Nethermind.State.Transition;
using Nethermind.Trie.Pruning;
using Nethermind.Verkle.Tree.TreeStore;
using Nethermind.Verkle.Tree.VerkleDb;

namespace Nethermind.State;

public class VergeWorldStateManager: ReadOnlyVergeWorldStateManager
{
    private readonly VergeWorldStateProvider _worldState;
    private readonly IVerkleTreeStore _verkleTrieStore;
    private readonly ITrieStore _merkleTrieStore;

    public VergeWorldStateManager(
        VergeWorldStateProvider worldState,
        IVerkleTreeStore verkleTrieStore,
        ITrieStore merkleTrieStore,
        IDbProvider dbProvider,
        ISpecProvider specProvider,
        ILogManager logManager
    ) : base(dbProvider, verkleTrieStore.AsReadOnly(new VerkleMemoryDb()), merkleTrieStore.AsReadOnly(), specProvider, logManager)
    {
        _worldState = worldState;
        _merkleTrieStore = merkleTrieStore;
        _verkleTrieStore = verkleTrieStore;
    }

    public override IWorldState GlobalWorldState => _worldState;

    public override event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add
        {
            _merkleTrieStore.ReorgBoundaryReached += value;
            _verkleTrieStore.ReorgBoundaryReached += value;
        }
        remove
        {
            _merkleTrieStore.ReorgBoundaryReached -= value;
            _verkleTrieStore.ReorgBoundaryReached -= value;
        }
    }
}


public class ReadOnlyVergeWorldStateManager: IWorldStateManager
{
    private IReadOnlyDbProvider _readOnlyDbProvider;

    private IReadOnlyVerkleTreeStore? _readOnlyVerkleTrieStore;
    private readonly IReadOnlyTrieStore? _readOnlyMerkleTrieStore;

    private ILogManager _logManager;
    private readonly IDbProvider _dbProvider;
    private readonly ReadOnlyDb _codeDb;
    private readonly ISpecProvider _specProvider;

    public ReadOnlyVergeWorldStateManager(
        IDbProvider dbProvider,
        IReadOnlyVerkleTreeStore readOnlyVerkleTrieStore,
        IReadOnlyTrieStore readOnlyMerkleTrieStore,
        ISpecProvider specProvider,
        ILogManager logManager
    )
    {
        _specProvider = specProvider;
        _readOnlyMerkleTrieStore = readOnlyMerkleTrieStore;
        _readOnlyVerkleTrieStore = readOnlyVerkleTrieStore;
        _dbProvider = dbProvider;
        _logManager = logManager;

        _readOnlyDbProvider = _dbProvider.AsReadOnly(false);
        _codeDb = _readOnlyDbProvider.GetDb<IDb>(DbNames.Code).AsReadOnly(true);
        GlobalStateReader = new TransitionStateReader(_readOnlyMerkleTrieStore, new VerkleStateTree(_readOnlyVerkleTrieStore, logManager), _codeDb, _logManager);
    }

    public virtual IWorldState GlobalWorldState => throw new InvalidOperationException("global world state not supported");
    public IReadOnlyKeyValueStore? HashServer => null;

    public IStateReader GlobalStateReader { get; }

    public ISnapServer? SnapServer =>  null;

    public IWorldState CreateResettableWorldState()
    {
        return new VergeWorldStateProvider(_readOnlyMerkleTrieStore, _readOnlyVerkleTrieStore, new StateReader(_readOnlyMerkleTrieStore, _codeDb, _logManager), _specProvider, _readOnlyDbProvider, _logManager);
    }

    public IWorldState CreateWorldStateForWarmingUp(IWorldState forWarmup)
    {
        PreBlockCaches? preBlockCaches = (forWarmup as IPreBlockCaches)?.Caches;
        // TODO: fix this as well to actually use the preBlockCache
        return CreateResettableWorldState();
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        throw new NotImplementedException();
    }

    public IWorldState CreateOverlayWorldState(IReadOnlyDbProvider editableDbProvider)
    {
        throw new NotImplementedException();
    }

    public virtual event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached
    {
        add => throw new InvalidOperationException("Unsupported operation");
        remove => throw new InvalidOperationException("Unsupported operation");
    }

    public void InitializeNetwork(IPathRecovery pathRecovery)
    {
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        return true;
    }

}
