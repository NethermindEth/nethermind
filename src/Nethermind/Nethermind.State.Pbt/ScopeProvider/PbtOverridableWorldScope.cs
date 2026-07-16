// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
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
    private bool _isDisposed;

    public PbtOverridableWorldScope([KeyFilter(DbNames.Code)] IDb codeDb, IPbtDbManager manager)
    {
        _manager = manager;
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

    /// <summary>Stacks the local layers reachable from <paramref name="stateId"/> above a bundle gathered from the main state.</summary>
    private PbtSnapshotBundle GatherBundle(in StateId stateId, bool isReadOnly)
    {
        List<PbtSnapshot> localChain = [];
        StateId current = stateId;
        while (_snapshots.TryGetValue(current, out PbtSnapshot? snapshot) && snapshot.TryLease())
        {
            localChain.Add(snapshot);
            if (snapshot.From == current) break;
            current = snapshot.From;
        }

        try
        {
            return new PbtSnapshotBundle(localChain, new BundleBackedReader(_manager.GatherBundle(current, isReadOnly: true), current), isReadOnly);
        }
        catch
        {
            foreach (PbtSnapshot snapshot in localChain)
            {
                snapshot.Dispose();
            }

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
            return new PbtWorldStateScope(stateId, outer.GatherBundle(stateId, isReadOnly: false), _codeDb, outer, isReadOnly: false);
        }
    }

    private class OverridableStateReader(PbtOverridableWorldScope outer) : IStateReader
    {
        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            using PbtSnapshotBundle bundle = outer.GatherBundle(new StateId(baseBlock), isReadOnly: true);
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
            using PbtSnapshotBundle bundle = outer.GatherBundle(new StateId(baseBlock), isReadOnly: true);
            EvmWord value = bundle.GetSlot(address, index);
            return EvmWordSlot.IsZero(value) ? [] : EvmWordSlot.ToStrippedBytes(value);
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == ValueKeccak.OfAnEmptyString ? [] : outer._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null) where TCtx : struct, INodeContext<TCtx> =>
            throw new NotSupportedException("Trie visiting is not supported by the pbt state backend");

        public bool HasStateForBlock(BlockHeader? baseBlock) => outer.HasStateForBlock(baseBlock);
    }

    private sealed class BundleBackedReader(PbtSnapshotBundle inner, StateId currentState) : IPbtPersistence.IReader
    {
        public StateId CurrentState => currentState;

        public Account? GetAccount(Address address) => inner.GetAccount(address);

        public EvmWord GetSlot(Address address, in UInt256 slot) => inner.GetSlot(address, slot);

        public RefCountingMemory? GetLeafBlob(in Stem stem) => inner.GetLeafBlob(stem);

        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => inner.GetTrieNode(key);

        public void Dispose() => inner.Dispose();
    }
}
