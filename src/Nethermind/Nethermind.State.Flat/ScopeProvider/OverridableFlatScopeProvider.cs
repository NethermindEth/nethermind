// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using NonBlocking;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Create a scope with additional layers of snapshots independent of the main worldstate. This allow committing
/// worldstate without modifying the global snapshots.
/// </summary>
public class FlatOverridableWorldScope : IOverridableWorldScope, IFlatCommitTarget
{
    private readonly IReadOnlyDb _codeDbOverlay;
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private readonly IResourcePool _resourcePool;
    private readonly IFlatDbManager _flatDbManager;
    private readonly ITrieNodeCache _trieNodeCache;
    private bool _isDisposed = false;

    public FlatOverridableWorldScope(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDbManager flatDbManager,
        IFlatDbConfig configuration,
        ITrieNodeCache trieNodeCache,
        IResourcePool resourcePool,
        ILogManager logManager)
    {
        GlobalStateReader = new OverridableStateReader(this);
        _codeDbOverlay = new ReadOnlyDb(codeDb, true);
        _resourcePool = resourcePool;
        _flatDbManager = flatDbManager;
        _trieNodeCache = trieNodeCache;
        WorldState = new OverridableFlatScopeProvider(
            this,
            configuration,
            new NoopTrieWarmer(),
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(_codeDbOverlay),
            logManager);
    }

    public IWorldStateScopeProvider WorldState { get; }
    public IStateReader GlobalStateReader { get; }

    public void ResetOverrides()
    {
        _codeDbOverlay.ClearTempChanges();
        foreach (Snapshot snapshot in _snapshots.Values)
        {
            snapshot.Dispose();
        }

        _snapshots.Clear();
    }

    private bool HasStateForBlock(BlockHeader? baseBlock)
    {
        StateId stateId = new(baseBlock);
        return _snapshots.ContainsKey(stateId) || _flatDbManager.HasStateForBlock(stateId);
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        if (!_snapshots.TryAdd(snapshot.To, snapshot))
        {
            snapshot.Dispose();
        }

        _resourcePool.ReturnCachedResource(ResourcePool.Usage.ReadOnlyProcessingEnv, transientResource);
    }

    private SnapshotBundle GatherSnapshotBundle(BlockHeader? baseBlock)
    {
        StateId currentState = new(baseBlock);

        SnapshotPooledList snapshots = new(0);
        while (_snapshots.TryGetValue(currentState, out Snapshot? snapshot) && snapshot.TryAcquire())
        {
            snapshots.Add(snapshot);
            if (snapshot.From == currentState) break;
            currentState = snapshot.From;
        }
        snapshots.Reverse();

        ReadOnlySnapshotBundle readOnlySnapshotBundle;
        try
        {
            readOnlySnapshotBundle = _flatDbManager.GatherReadOnlySnapshotBundle(currentState);
        }
        catch (Exception)
        {
            snapshots.Dispose();
            throw;
        }

        return new SnapshotBundle(
            readOnlySnapshotBundle,
            _trieNodeCache,
            _resourcePool,
            ResourcePool.Usage.ReadOnlyProcessingEnv,
            snapshots
        );
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        foreach (Snapshot snapshot in _snapshots.Values)
        {
            snapshot.Dispose();
        }
        _snapshots.Clear();
    }

    private class OverridableFlatScopeProvider(
        FlatOverridableWorldScope flatOverrideScope,
        IFlatDbConfig configuration,
        ITrieWarmer trieWarmer,
        IWorldStateScopeProvider.ICodeDb codeDb,
        ILogManager logManager)
        : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => flatOverrideScope.HasStateForBlock(baseBlock);

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
        {
            StateId currentState = new(baseBlock);
            SnapshotBundle snapshotBundle = flatOverrideScope.GatherSnapshotBundle(baseBlock);

            return new FlatWorldStateScope(
                currentState,
                snapshotBundle,
                codeDb,
                flatOverrideScope,
                configuration,
                trieWarmer,
                logManager);
        }
    }

    private class OverridableStateReader(FlatOverridableWorldScope overridableWorldScope) : IStateReader
    {
        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            using SnapshotBundle snapshotBundle = overridableWorldScope.GatherSnapshotBundle(baseBlock);
            if (snapshotBundle.GetAccount(address) is { } acc)
            {
                account = acc.ToStruct();
                return true;
            }
            account = default;
            return false;
        }

        public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
        {
            using SnapshotBundle snapshotBundle = overridableWorldScope.GatherSnapshotBundle(baseBlock);
            int selfDestructIdx = snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
            return snapshotBundle.GetSlot(address, index, selfDestructIdx) ?? [];
        }

        public byte[]? GetCode(Hash256 codeHash)
            => codeHash == Keccak.OfAnEmptyString ? [] : overridableWorldScope._codeDbOverlay[codeHash.Bytes];

        public byte[]? GetCode(in ValueHash256 codeHash)
            => codeHash == ValueKeccak.OfAnEmptyString ? [] : overridableWorldScope._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
        {
            StateId stateId = new(baseBlock);
            using SnapshotBundle snapshotBundle = overridableWorldScope.GatherSnapshotBundle(baseBlock);

            ConcurrencyController concurrency = new(1);
            StateTrieStoreAdapter trieStoreAdapter = new(snapshotBundle, concurrency);

            PatriciaTree patriciaTree = new(trieStoreAdapter, LimboLogs.Instance);
            patriciaTree.Accept(treeVisitor, stateId.StateRoot.ToCommitment(), visitingOptions);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock) => overridableWorldScope.HasStateForBlock(baseBlock);
    }
}

