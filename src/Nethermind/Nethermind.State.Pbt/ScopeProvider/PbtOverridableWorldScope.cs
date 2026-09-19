// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.Monitoring.Config;
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
    private readonly ILogManager _logManager;
    private readonly IPbtDbManager _manager;
    private readonly IPbtResourcePool _resourcePool;
    private readonly IPbtConfig _config;
    private readonly PbtTrieNodeCache? _trieNodeCache;
    private readonly bool _recordDetailedMetrics;
    private readonly KnownHeadersScopeProvider _worldState;
    private bool _isDisposed;

    public PbtOverridableWorldScope(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IPbtDbManager manager,
        IPbtResourcePool resourcePool,
        IMetricsConfig metricsConfig,
        IPbtConfig config,
        IStateHeaderProvider stateHeaderProvider,
        ILogManager? logManager = null,
        PbtTrieNodeCache? trieNodeCache = null)
    {
        _logManager = logManager ?? NullLogManager.Instance;
        _config = config;
        _manager = manager;
        _resourcePool = resourcePool;
        _trieNodeCache = trieNodeCache;
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
        _codeDbOverlay = new ReadOnlyDb(codeDb, createInMemWriteStore: true);
        GlobalStateReader = new OverridableStateReader(this);
        _worldState = new KnownHeadersScopeProvider(stateHeaderProvider, headerProvider => new OverridableScopeProvider(this, headerProvider));
    }

    public IWorldStateScopeProvider WorldState => _worldState;
    public IStateReader GlobalStateReader { get; }

    public void AddSnapshot(PbtSnapshot snapshot)
    {
        if (!_snapshots.TryAdd(snapshot.To, snapshot)) snapshot.Dispose();
    }

    public void ResetOverrides()
    {
        _codeDbOverlay.ClearTempChanges();
        _worldState.Clear();
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
            return new PbtSnapshotBundle(localChain, readOnlyBundle, _resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, _trieNodeCache);
        }
        catch
        {
            readOnlyBundle?.Dispose();
            localChain.Dispose();
            throw;
        }
    }

    private class OverridableScopeProvider(PbtOverridableWorldScope outer, IStateHeaderProvider stateHeaderProvider) : IWorldStateScopeProvider
    {
        private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(outer._codeDbOverlay);

        public bool HasRoot(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);

        public bool HasStateForTargetBlock(BlockHeader targetBlock) =>
            stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent) && HasRoot(parent);

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            if (stateHeaderProvider.TryGetBaseBlock(targetBlock, out BlockHeader? parent)) return TryBeginScope(parent, metrics, out scope);
            scope = null;
            return false;
        }

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            if (!HasRoot(baseBlock))
            {
                scope = null;
                return false;
            }

            StateId stateId = new(baseBlock);
            scope = new PbtWorldStateScope(
                stateId, baseBlock, outer.GatherBundle(stateId), _codeDb, outer, NullPbtChildHeaderSource.Instance,
                outer._resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: false, _noopTrieWarmer, outer._config, outer._logManager);
            return true;
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

        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value)
        {
            using PbtSnapshotBundle bundle = outer.GatherBundle(new StateId(baseBlock));
            EvmWord word = bundle.GetSlot(address, index);
            value = EvmWordSlot.ToUInt256(in word);
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
            throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

        public bool HasStateForBlock(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);
    }
}
