// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.Trie;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// A world scope whose commits stack in local layers above the main state instead of the global
/// repository, letting override environments (eth_call state overrides) process and reset freely.
/// </summary>
public class PbtOverridableWorldScope : IOverridableWorldScope, IPbtCommitTarget
{
    private readonly ConcurrentDictionary<StateId, PbtSnapshot> _snapshots = new();
    private readonly IReadOnlyDb _codeDbOverlay;
    private readonly IPbtDbManager _manager;
    private readonly IPbtResourcePool _resourcePool;
    private readonly PbtGroupFormat _writeFormat;
    private bool _isDisposed;

    public PbtOverridableWorldScope([KeyFilter(DbNames.Code)] IDb codeDb, IPbtDbManager manager, IPbtResourcePool resourcePool, IPbtConfig config)
    {
        _manager = manager;
        _resourcePool = resourcePool;
        _writeFormat = config.TrieNodeWriteFormat();
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

    /// <summary>Stacks the local override layers reachable from <paramref name="stateId"/> above the main state's shared view.</summary>
    /// <remarks>
    /// The local layers go into the bundle's own chain rather than behind a nested bundle: they are
    /// the same kind of layer as the main state's, so the writable bundle stacks them directly over
    /// the shared read-only view, and one bundle serves the whole override.
    /// </remarks>
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

        // the walk runs head-down; the bundle expects its chain oldest first
        localChain.Reverse();

        PbtReadOnlySnapshotBundle? readOnlyBundle = null;
        try
        {
            readOnlyBundle = _manager.GatherReadOnlyBundle(current);
            // an override scope never commits to the repository, so its layers come from the
            // read-only pool
            return new PbtSnapshotBundle(localChain, readOnlyBundle, _resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv);
        }
        catch
        {
            // the gather throws when the walk truncated onto an override-local state; nothing else
            // releases these
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
            return new PbtWorldStateScope(stateId, outer.GatherBundle(stateId), _codeDb, outer, outer._resourcePool, PbtResourcePool.Usage.ReadOnlyProcessingEnv, isReadOnly: false, outer._writeFormat);
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
