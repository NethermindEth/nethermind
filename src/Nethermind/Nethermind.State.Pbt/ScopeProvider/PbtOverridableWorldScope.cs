// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides local state layers for resettable override environments, such as <c>eth_call</c> state overrides.</summary>
/// <remarks>
/// Override scopes process synthetic blocks, so they report their folded EIP-8297 root rather than a
/// canonical block header's root.
/// </remarks>
public class PbtOverridableWorldScope : IOverridableWorldScope, IPbtCommitTarget
{
    private static readonly ITrieWarmer _noopTrieWarmer = new NoopTrieWarmer();

    private readonly ConcurrentDictionary<StateId, PbtSnapshot> _snapshots = new();
    private readonly IReadOnlyDb _codeDbOverlay;
    private readonly IPbtDbManager _manager;
    private readonly IPbtResourcePool _resourcePool;
    private readonly PbtStoreCache _storeCache;
    private readonly PbtTrieLayout _writeLayout;
    private readonly int _rootFoldConcurrency;
    private readonly bool _recordDetailedMetrics;
    private bool _isDisposed;

    public PbtOverridableWorldScope(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IPbtDbManager manager,
        IPbtResourcePool resourcePool,
        PbtStoreCache storeCache,
        IPbtConfig config,
        IMetricsConfig metricsConfig)
    {
        _manager = manager;
        _resourcePool = resourcePool;
        _storeCache = storeCache;
        _writeLayout = config.TrieNodeLayout;
        _rootFoldConcurrency = config.RootFoldConcurrency;
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
        _codeDbOverlay = new ReadOnlyDb(codeDb, createInMemWriteStore: true);
        GlobalStateReader = new OverridableStateReader(this);
        WorldState = new OverridableScopeProvider(this);
    }

    public IWorldStateScopeProvider WorldState { get; }
    public IStateReader GlobalStateReader { get; }

    public void AddSnapshot(PbtSnapshot snapshot)
    {
        if (!_snapshots.TryAdd(snapshot.To, snapshot)) snapshot.Dispose();
    }

    public void ResetOverrides()
    {
        _codeDbOverlay.ClearTempChanges();
        ClearSnapshots();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        ClearSnapshots();
    }

    private void ClearSnapshots()
    {
        foreach ((_, PbtSnapshot snapshot) in _snapshots)
        {
            snapshot.Dispose();
        }

        _snapshots.Clear();
    }

    private bool HasStateForBlock(BlockHeader? baseBlock)
    {
        StateId stateId = new(baseBlock);
        return _snapshots.ContainsKey(stateId) || _manager.HasStateForBlock(stateId);
    }

    private PbtSnapshotBundle GatherBundle(in StateId stateId)
    {
        PbtSnapshotPooledList localChain = new(1);
        StateId current = stateId;
        while (_snapshots.TryGetValue(current, out PbtSnapshot? snapshot) && snapshot.TryLease())
        {
            localChain.Add(snapshot);
            if (snapshot.From == current) break;
            current = snapshot.From;
        }

        localChain.Reverse();

        PbtReadOnlySnapshotBundle? readOnlyBundle = null;
        try
        {
            readOnlyBundle = _manager.GatherReadOnlyBundle(current);
            return new PbtSnapshotBundle(localChain, readOnlyBundle, _resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        }
        catch
        {
            readOnlyBundle?.Dispose();
            localChain.Dispose();
            throw;
        }
    }

    private class OverridableScopeProvider(PbtOverridableWorldScope outer) : IWorldStateScopeProvider
    {
        private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(outer._codeDbOverlay);

        public bool HasRoot(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
        {
            StateId stateId = new(baseBlock);
            return new PbtWorldStateScope(
                stateId, baseBlock, outer.GatherBundle(stateId), _codeDb, outer, NullPbtChildHeaderSource.Instance,
                outer._resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: false, outer._writeLayout,
                outer._rootFoldConcurrency, _noopTrieWarmer);
        }
    }

    private class OverridableStateReader(PbtOverridableWorldScope outer) : IStateReader
    {
        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            using PbtSnapshotBundle bundle = outer.GatherBundle(new StateId(baseBlock));
            if (bundle.GetAccount(address) is { } accountClass)
            {
                account = accountClass.ToStruct();
                return true;
            }

            account = default;
            return false;
        }

        public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
        {
            using PbtSnapshotBundle bundle = outer.GatherBundle(new StateId(baseBlock));
            EvmWord value = bundle.GetSlot(address, index);
            return EvmWordSlot.IsZero(value) ? [] : EvmWordSlot.ToStrippedBytes(value);
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
            throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

        public bool HasStateForBlock(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);
    }
}
