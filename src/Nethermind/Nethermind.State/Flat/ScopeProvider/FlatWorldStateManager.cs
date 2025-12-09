// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.ScopeProvider;

public class FlatWorldStateManager : IWorldStateManager
{
    private readonly IFlatDiffRepository _flatDiffRepository;
    private readonly FlatStateReader _flatStateReader;
    private readonly IProcessExitSource _exitSource;
    private readonly IDb _codeDb;
    private readonly ILogManager _logManager;
    private readonly FlatDiffRepository.Configuration _configuration;
    private readonly FlatScopeProvider _mainWorldState;
    private readonly ResourcePool _resourcePool;

    public FlatWorldStateManager(
        IFlatDiffRepository flatDiffRepository,
        FlatDiffRepository.Configuration configuration,
        FlatStateReader flatStateReader,
        TrieWarmer trieWarmer,
        IProcessExitSource exitSource,
        [KeyFilter(DbNames.Code)] IDb codeDb,
        ResourcePool resourcePool,
        ILogManager logManager
    )
    {
        _flatDiffRepository = flatDiffRepository;
        _flatStateReader = flatStateReader;
        _codeDb = codeDb;
        _logManager = logManager;
        _configuration = configuration;
        _exitSource = exitSource;
        _resourcePool = resourcePool;
        _mainWorldState = new FlatScopeProvider(
            codeDb,
            flatDiffRepository,
            configuration,
            trieWarmer,
            resourcePool,
            IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing,
            logManager);
    }

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => _flatStateReader;
    public ISnapServer? SnapServer => null;
    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new FlatScopeProvider(
            _codeDb,
            _flatDiffRepository,
            _configuration,
            new NoopTrieWarmer(),
            _resourcePool,
            IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv,
            _logManager,
            isReadOnly: true);
    }

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => _flatDiffRepository.ReorgBoundaryReached += value;
        remove => _flatDiffRepository.ReorgBoundaryReached -= value;
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        var scopeProvider = new FlatScopeProvider(
            _codeDb,
            _flatDiffRepository,
            _configuration,
            new NoopTrieWarmer(),
            _resourcePool,
            IFlatDiffRepository.SnapshotBundleUsage.ReadOnlyProcessingEnv,
            _logManager,
            isReadOnly: true);
        return new FakeOverridableWorldScope(scopeProvider, _flatStateReader);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        using IPersistence.IPersistenceReader reader = _flatDiffRepository.LeaseReader();
        FlatVerifyTrieVisitor trieVisitor = new FlatVerifyTrieVisitor(_codeDb, reader, _logManager, cancellationToken);

        // Just a bit hacky due to mistmatch with best persisted state and actually persisted state.
        StateId? stateId = _flatDiffRepository.FindLatestAvailableState();

        // StateId? stateId = _flatDiffRepository.FindStateIdForStateRoot(stateAtBlock.StateRoot);
        _flatStateReader.RunTreeVisitor(trieVisitor, stateId.Value.stateRoot.ToHash256(), new VisitingOptions()
        {
        });

        if (trieVisitor.Stats.MismatchedAccount > 0 || trieVisitor.Stats.MismatchedSlot > 0)
        {
            _exitSource.Exit(10);
        }

        return true;
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        _flatDiffRepository.FlushCache(cancellationToken);
    }

    public class FakeOverridableWorldScope(IWorldStateScopeProvider worldState, IStateReader stateReader) : IOverridableWorldScope
    {
        public IWorldStateScopeProvider WorldState => worldState;
        public IStateReader GlobalStateReader => stateReader;
        public void ResetOverrides()
        {
        }
    }
}
