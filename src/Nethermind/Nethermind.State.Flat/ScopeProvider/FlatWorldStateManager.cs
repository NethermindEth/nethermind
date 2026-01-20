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

public class FlatWorldStateManager(
    IFlatDbManager flatDbManager,
    IFlatDbConfig configuration,
    FlatStateReader flatStateReader,
    ITrieWarmer trieWarmer,
    IProcessExitSource exitSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ResourcePool resourcePool,
    ILogManager logManager)
    : IWorldStateManager
{
    private readonly FlatScopeProvider _mainWorldState = new(
        codeDb,
        flatDbManager,
        configuration,
        trieWarmer,
        resourcePool,
        ResourcePool.Usage.MainBlockProcessing,
        logManager);

    public IWorldStateScopeProvider GlobalWorldState => _mainWorldState;
    public IStateReader GlobalStateReader => flatStateReader;
    public ISnapServer? SnapServer => null;
    public IReadOnlyKeyValueStore? HashServer => null;
    public IWorldStateScopeProvider CreateResettableWorldState()
    {
        return new FlatScopeProvider(
            codeDb,
            flatDbManager,
            configuration,
            new NoopTrieWarmer(),
            resourcePool,
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            logManager,
            isReadOnly: true);
    }

    event EventHandler<ReorgBoundaryReached>? IWorldStateManager.ReorgBoundaryReached
    {
        add => flatDbManager.ReorgBoundaryReached += value;
        remove => flatDbManager.ReorgBoundaryReached -= value;
    }

    public IOverridableWorldScope CreateOverridableWorldScope()
    {
        FlatScopeProvider scopeProvider = new FlatScopeProvider(
            codeDb,
            flatDbManager,
            configuration,
            new NoopTrieWarmer(),
            resourcePool,
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            logManager,
            isReadOnly: true);
        return new FakeOverridableWorldScope(scopeProvider, flatStateReader);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        using IPersistence.IPersistenceReader reader = flatDbManager.CreateReader();
        FlatVerifyTrieVisitor trieVisitor = new FlatVerifyTrieVisitor(codeDb, reader, logManager, cancellationToken);

        flatStateReader.RunTreeVisitor(trieVisitor, stateAtBlock, new VisitingOptions()
        {
        });

        if (trieVisitor.Stats.MismatchedAccount > 0 || trieVisitor.Stats.MismatchedSlot > 0)
        {
            exitSource.Exit(10);
        }

        return true;
    }

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    /// <summary>
    /// TODO: Add actual support for ResetOverrides. Meaning, in SnapshotBundle, clear the changed entries. Dont forget
    /// to return to resourccec pool.
    /// </summary>
    /// <param name="worldState"></param>
    /// <param name="stateReader"></param>
    public class FakeOverridableWorldScope(IWorldStateScopeProvider worldState, IStateReader stateReader) : IOverridableWorldScope
    {
        public IWorldStateScopeProvider WorldState => worldState;
        public IStateReader GlobalStateReader => stateReader;
        public void ResetOverrides()
        {
        }
    }
}
