// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by one canonical EIP-8297 tree.</summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope
{
    private static long _nextScopeId;
    private readonly long _scopeId = Interlocked.Increment(ref _nextScopeId);
    private readonly ILogger _logger;
    private readonly ConcurrencyController _foldQuota;
    private readonly FoldFanOut _foldFanOut;
    private readonly IRefCountingMemoryProvider _nodeGroupMemory;
    private readonly IPbtCommitTarget _commitTarget;
    private readonly IPbtChildHeaderSource _childHeaders;
    private readonly bool _isReadOnly;
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private ValueHash256 _treeRoot;
    private Hash256? _authoritativeRoot;
    private BlockHeader? _currentHeader;
    private BlockHeader? _childHeader;
    private bool _rootDirty;
    private bool _isDisposed;

    public PbtWorldStateScope(
        in StateId currentStateId,
        BlockHeader? currentHeader,
        PbtSnapshotBundle bundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IPbtCommitTarget commitTarget,
        IPbtChildHeaderSource childHeaders,
        IRefCountingMemoryProvider nodeGroupMemory,
        bool isReadOnly,
        IPbtConfig config,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<PbtWorldStateScope>();
        _foldQuota = new ConcurrencyController(config.FoldConcurrency > 0 ? config.FoldConcurrency : Environment.ProcessorCount);
        _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);
        _nodeGroupMemory = nodeGroupMemory;
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _isReadOnly = isReadOnly;
        _treeRoot = bundle.TreeRoot;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, Bundle);
        if (_logger.IsDebug) LogLifecycle("opened");
    }

    private void LogLifecycle(string stage) =>
        _logger.Debug($"PBT scope {_scopeId} {stage}: state={_currentStateId}, readOnly={_isReadOnly}, pendingMutations={Bundle.PendingMutationCount}, managedBytes={GC.GetTotalMemory(false)}");

    internal PbtSnapshotBundle Bundle { get; }
    public Hash256 RootHash => _rootHash;

    // PBT has no per-account storage root.
    public bool StorageRootsAreAuthoritative => false;
    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    internal void UseAuthoritativeRoot(Hash256 root)
    {
        _authoritativeRoot = root;
        _rootHash = root;
    }

    public Account? Get(Address address) => Bundle.GetAndPromoteAccount(address);

    public void HintGet(Address address, Account? account) => Bundle.HintAccount(address, account);

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public void ApplyBal(ReadOnlyBlockAccessList bal) => ScopeBalApplier.Apply(this, bal);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        lock (_storages)
        {
            ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists) tree = new PbtStorageTree(this, address);
            return tree!;
        }
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    public void UpdateRootHash()
    {
        if (!_rootDirty) return;
        if (_logger.IsDebug) LogLifecycle("root calculation begin");
        long start = Stopwatch.GetTimestamp();
        PbtPartitionBatches changes = Bundle.PrepareLeafChanges();
        // Counting walks every shard of every partition, and the fold below drains them, so read it only when logged.
        int mutationCount = _logger.IsDebug ? Bundle.PendingMutationCount : 0;
        try
        {
            Metrics.PbtPrepareLeafChangesTime.Observe(Stopwatch.GetTimestamp() - start);
            long updaterStart = Stopwatch.GetTimestamp();
            using (PbtSnapshotStore store = new(Bundle))
                _treeRoot = TrieUpdater.UpdateRoot(store, _treeRoot, changes, _foldQuota, _foldFanOut, Metrics.PbtPartitionFoldTime, memoryProvider: _nodeGroupMemory);
            Metrics.PbtTrieUpdaterTime.Observe(Stopwatch.GetTimestamp() - updaterStart);
            Bundle.CompleteLeafChanges();
        }
        finally
        {
            changes.Dispose();
        }
        Metrics.PbtRootHashTime.Observe(Stopwatch.GetTimestamp() - start);
        _childHeader ??= _currentHeader is null ? null : _childHeaders.TryFindChild(_currentHeader);
        _rootHash = _authoritativeRoot ?? _childHeader?.StateRoot ?? _treeRoot.ToHash256();
        _rootDirty = false;
        if (_logger.IsDebug) LogLifecycle($"root calculated treeRoot={_treeRoot}, mutations={mutationCount}, elapsed={Stopwatch.GetElapsedTime(start)}");
    }

    public void Commit(ulong blockNumber)
    {
        if (_logger.IsDebug) LogLifecycle($"commit begin block={blockNumber}");
        long commitStart = Stopwatch.GetTimestamp();
        try
        {
            UpdateRootHash();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                long publishStart = Stopwatch.GetTimestamp();
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot, out PbtTransientResource transientResource);
                if (_isReadOnly)
                {
                    snapshot.Dispose();
                    transientResource.ReleaseLease();
                }
                else _commitTarget.AddSnapshot(snapshot, transientResource);
                Metrics.PbtPublishSnapshotTime.Observe(Stopwatch.GetTimestamp() - publishStart);
                _currentStateId = newStateId;
            }
            _currentHeader = _childHeader;
            _childHeader = null;
            lock (_storages) _storages.Clear();
            if (_logger.IsDebug) LogLifecycle($"commit completed block={blockNumber}");
        }
        finally
        {
            Metrics.PbtCommitTime.Observe(Stopwatch.GetTimestamp() - commitStart);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_logger.IsDebug) LogLifecycle("close begin");
        try
        {
            Bundle.Dispose();
        }
        finally
        {
            if (_logger.IsDebug) _logger.Debug($"PBT scope {_scopeId} closed: state={_currentStateId}, managedBytes={GC.GetTotalMemory(false)}");
        }
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated { add { } remove { } }

        public void Set(Address key, Account? account)
        {
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key);

        public void Dispose() => Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // One batch serves one contract on one thread, so the address hash is derived once for the whole run of slots.
        private readonly ValueHash256 _addressHash = PbtStateKey.AddressKeyHash(address);

        public void Set(in UInt256 index, in UInt256 value)
        {
            scope.Bundle.SetSlot(address, _addressHash, index, EvmWordSlot.FromUInt256(in value));
            scope._rootDirty = true;
        }

        public void Clear()
        {
            scope.Bundle.SelfDestruct(_addressHash);
            scope._rootDirty = true;
        }

        public void Dispose() { }
    }
}
