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


public class FlatOverridableWorldScope : IOverridableWorldScope, IFlatCommitTarget
{
    internal readonly IReadOnlyDb _codeDbOverlay;
    private readonly TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb _codeDb;
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private readonly OverridableFlatScopeProvider _worldState;
    private readonly IStateReader _stateReader;
    private readonly IResourcePool _resourcePool;
    private readonly IFlatDbManager _flatDbManager;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly ResourcePool.Usage _usage = ResourcePool.Usage.ReadOnlyProcessingEnv;

    public FlatOverridableWorldScope(
        [KeyFilter(DbNames.Code)] IDb codeDb,
        IFlatDbManager flatDbManager,
        IFlatDbConfig configuration,
        ITrieNodeCache trieNodeCache,
        IResourcePool resourcePool,
        ILogManager logManager)
    {
        _stateReader = new OverridableStateReader(this);
        _codeDbOverlay = new ReadOnlyDb(codeDb, true);
        _codeDb = new(_codeDbOverlay);
        _resourcePool = resourcePool;
        _flatDbManager = flatDbManager;
        _trieNodeCache = trieNodeCache;
        _worldState = new OverridableFlatScopeProvider(
            this,
            configuration,
            new NoopTrieWarmer(),
            _codeDb,
            logManager);
    }

    public IWorldStateScopeProvider WorldState => _worldState;
    public IStateReader GlobalStateReader => _stateReader;

    public void ResetOverrides()
    {
        _codeDbOverlay.ClearTempChanges();
        foreach (KeyValuePair<StateId, Snapshot> keyValuePair in _snapshots)
        {
            keyValuePair.Value.Dispose();
        }

        _snapshots.Clear();
    }

    private bool HasStateForBlock(BlockHeader? baseBlock)
    {
        StateId stateId = new StateId(baseBlock);
        if (_snapshots.ContainsKey(stateId)) return true;
        return _flatDbManager.HasStateForBlock(stateId);
    }

    public SnapshotBundle GatherSnapshotBundle(BlockHeader? baseBlock)
    {
        StateId currentState = new StateId(baseBlock);

        SnapshotPooledList snapshots = new SnapshotPooledList(0);
        while (_snapshots.TryGetValue(currentState, out Snapshot? snapshot) && snapshot.TryAcquire())
        {
            snapshots.Add(snapshot);
            if (snapshot.From == currentState) break;
            currentState = snapshot.From;
        }

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
            _usage,
            snapshots
        );
    }

    private bool _isDisposed = false;
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        foreach (KeyValuePair<StateId, Snapshot> keyValuePair in _snapshots)
        {
            keyValuePair.Value.Dispose();
        }
        _snapshots.Clear();
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        if (!_snapshots.TryAdd(snapshot.To, snapshot))
        {
            snapshot.Dispose();
        }

        _resourcePool.ReturnCachedResource(ResourcePool.Usage.ReadOnlyProcessingEnv, transientResource);
    }

    /// <summary>
    /// OverridableFlatScopeProvider is more complicated as it allow committing to an internal buffer separate
    /// to the main flat manager.
    /// </summary>
    public class OverridableFlatScopeProvider(
        FlatOverridableWorldScope flatOverrideScope,
        IFlatDbConfig configuration,
        ITrieWarmer trieWarmer,
        IWorldStateScopeProvider.ICodeDb codeDb,
        ILogManager logManager)
        : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock)
        {
            return flatOverrideScope.HasStateForBlock(baseBlock);
        }

        public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
        {
            StateId currentState = new StateId(baseBlock);
            SnapshotBundle snapshotBundle = flatOverrideScope.GatherSnapshotBundle(baseBlock);

            FlatWorldStateScope scope = new FlatWorldStateScope(
                currentState,
                snapshotBundle,
                codeDb,
                flatOverrideScope,
                configuration,
                trieWarmer,
                logManager);

            return scope;
        }
    }

    public class OverridableStateReader(FlatOverridableWorldScope overridableWorldScope) : IStateReader
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
            => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : overridableWorldScope._codeDbOverlay[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
        {
            StateId stateId = new StateId(baseBlock);
            using SnapshotBundle snapshotBundle = overridableWorldScope.GatherSnapshotBundle(baseBlock);

            ConcurrencyController concurrency = new ConcurrencyController(1);
            StateTrieStoreAdapter trieStoreAdapter = new(snapshotBundle, concurrency, isTrieWarmer: false);

            PatriciaTree patriciaTree = new PatriciaTree(trieStoreAdapter, LimboLogs.Instance);
            patriciaTree.Accept(treeVisitor, stateId.StateRoot.ToCommitment(), visitingOptions);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock)
        {
            return overridableWorldScope.HasStateForBlock(baseBlock);
        }
    }
}

