// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides local state layers for resettable override environments, such as <c>eth_call</c> state overrides.</summary>
/// <remarks>
/// Override scopes process synthetic blocks, so they report their folded EIP-8297 root rather than a
/// canonical block header's root.
/// </remarks>
public class PbtOverridableWorldScope : IOverridableWorldScope, IPbtCommitTarget
{
    private readonly ConcurrentDictionary<StateId, PbtSnapshot> _snapshots = new();
    private readonly IReadOnlyDb _codeDbOverlay;
    private readonly ILogManager _logManager;
    private readonly IPbtDbManager _manager;
    private readonly IRefCountingMemoryProvider _nodeGroupMemory;
    private readonly IPbtConfig _config;
    private readonly KnownHeadersScopeProvider _worldState;
    private bool _isDisposed;

    public PbtOverridableWorldScope(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IPbtDbManager manager,
        IRefCountingMemoryProvider nodeGroupMemory,
        IPbtConfig config,
        IStateHeaderProvider stateHeaderProvider,
        ILogManager logManager)
    {
        _logManager = logManager;
        _config = config;
        _manager = manager;
        _nodeGroupMemory = nodeGroupMemory;
        _codeDbOverlay = new ReadOnlyDb(codeDb, createInMemWriteStore: true);
        GlobalStateReader = new OverridableStateReader(this);
        _worldState = new KnownHeadersScopeProvider(stateHeaderProvider, headerProvider => new OverridableScopeProvider(this, headerProvider));
    }

    public IWorldStateScopeProvider WorldState => _worldState;
    public IStateReader GlobalStateReader { get; }

    public void AddSnapshot(PbtSnapshot snapshot, PbtTransientResource transientResource)
    {
        transientResource.ReleaseLease();
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

    private PbtSnapshotBundle? TryGatherBundle(in StateId stateId)
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
        return _manager.TryGatherBundle(current, localChain, PbtResourcePool.Usage.ReadOnlyProcessingEnv);
    }

    private class OverridableScopeProvider(PbtOverridableWorldScope outer, IStateHeaderProvider stateHeaderProvider) : IWorldStateScopeProvider
    {
        private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb = new(outer._codeDbOverlay);

        public bool HasRoot(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);

        public bool HasStateForTargetBlock(BlockHeader targetBlock) => this.HasRootForTarget(stateHeaderProvider, targetBlock);

        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
            this.TryBeginScopeAtBase(stateHeaderProvider, targetBlock, metrics, out scope);

        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            StateId stateId = new(baseBlock);
            if (outer.TryGatherBundle(stateId) is not { } bundle)
            {
                scope = null;
                return false;
            }

            scope = new PbtWorldStateScope(
                stateId, baseBlock, bundle, _codeDb, outer, NullPbtChildHeaderSource.Instance,
                outer._nodeGroupMemory, outer._config, outer._logManager);
            return true;
        }
    }

    private class OverridableStateReader(PbtOverridableWorldScope outer) : IStateReader
    {
        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            using PbtSnapshotBundle bundle = GatherForRead(baseBlock);
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
            using PbtSnapshotBundle bundle = GatherForRead(baseBlock);
            EvmWord word = bundle.GetSlot(address, index);
            value = EvmWordSlot.ToUInt256(in word);
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
            throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

        public bool HasStateForBlock(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);

        private PbtSnapshotBundle GatherForRead(BlockHeader? baseBlock) => PbtStateReader.GatherForRead(baseBlock, stateId => outer.TryGatherBundle(stateId));
    }
}
