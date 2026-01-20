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
using Nethermind.Core.Crypto;
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
    [KeyFilter(DbNames.Code)] IDb codeDb,
    ResourcePool resourcePool,
    ILogManager logManager)
    : IWorldStateManager
{
    private ILogger _logger = logManager.GetClassLogger<FlatWorldStateManager>();
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
        return new FlatOverridableWorldScope(scopeProvider, flatStateReader);
    }

    public bool VerifyTrie(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        using IPersistence.IPersistenceReader reader = flatDbManager.CreateReader();
        using IPersistence.IPersistenceReader reader2 = flatDbManager.CreateReader();

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Task 1: Trie -> Flat verification (existing)
        FlatVerifyTrieVisitor trieVisitor = new FlatVerifyTrieVisitor(codeDb, reader, logManager, cts.Token);

        Task trieToFlatTask = Task.Run(() =>
        {
            flatStateReader.RunTreeVisitor(trieVisitor, stateAtBlock);
        });

        // Task 2: Flat -> Trie verification (new)
        using ReadOnlySnapshotBundle bundle = flatDbManager.GatherReadOnlyReaderAtBaseBlock(new StateId(stateAtBlock));
        ReadOnlyStateTrieStoreAdapter trieStore = new ReadOnlyStateTrieStoreAdapter(bundle);
        FlatToTrieVerifier flatToTrieVerifier = new FlatToTrieVerifier(
            reader2,
            trieStore,
            stateAtBlock.StateRoot!,
            logManager,
            cts.Token);

        Task flatToTrieTask = Task.Run(() =>
        {
            flatToTrieVerifier.Verify();
        });

        Task.WaitAll([trieToFlatTask, flatToTrieTask]);

        // Check both results
        bool trieToFlatOk = trieVisitor.Stats.MismatchedAccount == 0 && trieVisitor.Stats.MismatchedSlot == 0;
        bool flatToTrieOk = flatToTrieVerifier.Stats.MismatchedAccount == 0 && flatToTrieVerifier.Stats.MismatchedSlot == 0;

        if (!trieToFlatOk)
        {
            if (_logger.IsWarn) _logger.Warn($"TrieToFlat: {trieVisitor.Stats.MismatchedAccount} mismatched account and {trieVisitor.Stats.MismatchedSlot} mismatched slot found!");
        }

        if (!flatToTrieOk)
        {
            if (_logger.IsWarn) _logger.Warn($"FlatToTrie: {flatToTrieVerifier.Stats.MismatchedAccount} mismatched account and {flatToTrieVerifier.Stats.MismatchedSlot} mismatched slot found! Missing in trie: {flatToTrieVerifier.Stats.MissingInTrie}");
        }

        if (_logger.IsInfo)
        {
            _logger.Info($"Verification complete. TrieToFlat: {trieVisitor.Stats}, FlatToTrie: {flatToTrieVerifier.Stats}");
        }

        return trieToFlatOk && flatToTrieOk;
    }

    public void FlushCache(CancellationToken cancellationToken) => flatDbManager.FlushCache(cancellationToken);

    private class FlatOverridableWorldScope(FlatScopeProvider worldState, IStateReader stateReader) : IOverridableWorldScope
    {
        public IWorldStateScopeProvider WorldState => worldState;
        public IStateReader GlobalStateReader => stateReader;

        public void ResetOverrides() => worldState.ResetActiveScope();
    }
}
