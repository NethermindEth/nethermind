// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using Nethermind.Core.Memory;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>Provides the read/write surface for a processing branch backed by one canonical EIP-8297 tree.</summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope
{
    private const int AccountGroupDepth = PbtRocksDbPersistence.AccountTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    private const int StorageGroupDepth = PbtRocksDbPersistence.StemTopDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
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
    private readonly Lock _hintBalLock = new();
    private Task? _hintBalTask;
    private CancellationTokenSource? _hintBalCancellation;
    private Task? _nodeGroupPrefetchTask;
    private CancellationTokenSource? _nodeGroupPrefetchCancellation;

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

    /// <summary>Starts buffering the accounts and slot runs <paramref name="bal"/> writes, so <see cref="ApplyBal"/> finds them in the write buffer.</summary>
    /// <remarks>
    /// The buffered values are the ones visible when read, and a buffered value never replaces a write, so the prefetch
    /// may run alongside writes. Clearing storage could still race the prefetch of a run of the cleared account, so
    /// clearing, like committing and disposing, stops the prefetch first.
    /// </remarks>
    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        StopHintBal();
        if (bal.AccountChanges.Count == 0) return Task.CompletedTask;

        lock (_hintBalLock)
        {
            CancellationTokenSource cancellation = new();
            _hintBalCancellation = cancellation;
            return _hintBalTask = Task.Run(() => PrefetchBal(bal, cancellation.Token), cancellation.Token);
        }
    }

    private void PrefetchBal(ReadOnlyBlockAccessList bal, CancellationToken cancellation) =>
        ParallelUnbalancedWork.For(0, bal.AccountChanges.Count, (scope: this, bal, cancellation), static (index, state) =>
        {
            ReadOnlyAccountChanges accountChanges = state.bal.AccountChanges.AsSpan()[index];
            if (!accountChanges.HasStateChanges || state.cancellation.IsCancellationRequested) return state;

            PbtSnapshotBundle bundle = state.scope.Bundle;
            Address address = accountChanges.Address;
            // ApplyBal writes no slot of a missing account that the block leaves missing.
            bool changesAccount = accountChanges.BalanceChanges.Length > 0 || accountChanges.NonceChanges.Length > 0 || accountChanges.CodeChanges.Length > 0;
            if (bundle.GetAndPromoteAccount(address) is null && !changesAccount) return state;

            ValueHash256 addressHash = PbtStateKey.AddressKeyHash(address);
            UInt256? bufferedSlot = null;
            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (slotChanges.Changes.Length == 0 || state.cancellation.IsCancellationRequested) continue;
                // The slots are sorted, so the slots of one run are adjacent and the run is buffered once.
                if (bufferedSlot is { } previous && SlotRun.InSameRun(previous, slotChanges.Key)) continue;
                bundle.GetSlot(address, addressHash, slotChanges.Key);
                bufferedSlot = slotChanges.Key;
            }
            return state;
        });

    /// <summary>Cancels the running <see cref="HintBal"/> prefetch and waits for it to finish.</summary>
    private void StopHintBal()
    {
        if (Volatile.Read(ref _hintBalTask) is null) return;
        lock (_hintBalLock)
        {
            _hintBalCancellation?.Cancel();
            try
            {
                _hintBalTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (_logger.IsError) _logger.Error("PBT HintBal prefetch faulted", ex);
            }
            _hintBalCancellation?.Dispose();
            _hintBalCancellation = null;
            Volatile.Write(ref _hintBalTask, null);
        }
    }

    public void ApplyBal(ReadOnlyBlockAccessList bal)
    {
        StartNodeGroupPrefetch(bal);
        ScopeBalApplier.ApplyConcurrently(this, bal);
    }

    /// <summary>Starts reading the first groups below the top node groups that the fold of <paramref name="bal"/> walks into.</summary>
    /// <remarks>The reads only warm the store; <see cref="UpdateRootHash"/> stops them before it returns.</remarks>
    private void StartNodeGroupPrefetch(ReadOnlyBlockAccessList bal)
    {
        StopNodeGroupPrefetch();
        if (bal.AccountChanges.Count == 0) return;

        CancellationTokenSource cancellation = new();
        _nodeGroupPrefetchCancellation = cancellation;
        _nodeGroupPrefetchTask = Task.Run(() => PrefetchNodeGroups(bal, cancellation.Token), cancellation.Token);
    }

    private void PrefetchNodeGroups(ReadOnlyBlockAccessList bal, CancellationToken cancellation)
    {
        PbtStorageNodePath[] paths = [.. NodeGroupPrefetchPaths(bal)];
        ParallelUnbalancedWork.For(0, paths.Length, (bundle: Bundle, paths, cancellation), static (index, state) =>
        {
            if (!state.cancellation.IsCancellationRequested) state.bundle.PrefetchNodeGroup(state.paths[index]);
            return state;
        });
    }

    /// <summary>The distinct paths of the first groups below the top node groups that the leaves <paramref name="bal"/> writes lie under.</summary>
    /// <remarks>Code leaves are left out: they are content addressed, so a deployment rarely finds their groups stored.</remarks>
    internal static HashSet<PbtStorageNodePath> NodeGroupPrefetchPaths(ReadOnlyBlockAccessList bal)
    {
        HashSet<PbtStorageNodePath> paths = [];
        Span<byte> pathBytes = stackalloc byte[StorageGroupDepth / 8];
        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            bool writesAccountZone = accountChanges.BalanceChanges.Length > 0 || accountChanges.NonceChanges.Length > 0 || accountChanges.CodeChanges.Length > 0;
            bool writesStorageZone = false;
            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (slotChanges.Changes.Length == 0) continue;
                if (PbtStateKey.IsHeaderSlot(slotChanges.Key)) writesAccountZone = true;
                else writesStorageZone = true;
            }
            if (!writesAccountZone && !writesStorageZone) continue;

            PbtStateKey.AddressKeyHash(accountChanges.Address).Bytes.CopyTo(pathBytes[1..]);
            if (writesAccountZone)
            {
                pathBytes[0] = Eip8297KeyDerivation.AccountZone;
                paths.Add(new PbtStorageNodePath(pathBytes[..(AccountGroupDepth / 8)], AccountGroupDepth));
            }
            if (writesStorageZone)
            {
                pathBytes[0] = Eip8297KeyDerivation.StorageZone;
                paths.Add(new PbtStorageNodePath(pathBytes, StorageGroupDepth));
            }
        }
        return paths;
    }

    /// <summary>Cancels the running node group prefetch and waits for it to finish.</summary>
    private void StopNodeGroupPrefetch()
    {
        if (_nodeGroupPrefetchTask is null) return;
        _nodeGroupPrefetchCancellation!.Cancel();
        try
        {
            _nodeGroupPrefetchTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error("PBT node group prefetch faulted", ex);
        }
        _nodeGroupPrefetchCancellation.Dispose();
        _nodeGroupPrefetchCancellation = null;
        _nodeGroupPrefetchTask = null;
    }

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
        try
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
        finally
        {
            StopNodeGroupPrefetch();
        }
    }

    public void Commit(ulong blockNumber)
    {
        if (_logger.IsDebug) LogLifecycle($"commit begin block={blockNumber}");
        long commitStart = Stopwatch.GetTimestamp();
        try
        {
            StopHintBal();
            UpdateRootHash();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                long addSnapshotStart = Stopwatch.GetTimestamp();
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot, out PbtTransientResource transientResource);
                if (_isReadOnly)
                {
                    snapshot.Dispose();
                    transientResource.ReleaseLease();
                }
                else _commitTarget.AddSnapshot(snapshot, transientResource);
                Metrics.PbtAddSnapshotTime.Observe(Stopwatch.GetTimestamp() - addSnapshotStart);
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
            StopHintBal();
            StopNodeGroupPrefetch();
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
            // Removing an account clears its storage.
            if (account is null) scope.StopHintBal();
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key, estimatedEntries);

        public void Dispose() => Metrics.PbtWriteBatchTime.Observe(Stopwatch.GetTimestamp() - _start);
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address, int estimatedEntries) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        // One batch serves one contract on one thread, so the address hash is derived once for the whole run of slots.
        private readonly ValueHash256 _addressHash = PbtStateKey.AddressKeyHash(address);
        // Applied together, so each slot run the writes touch is rewritten once for all of its adjacent writes.
        private readonly ArrayPoolList<SlotWrite> _writes = new(estimatedEntries);

        public void Set(in UInt256 index, in UInt256 value)
        {
            _writes.Add(new SlotWrite(index, value.ToBigEndianWord()));
            scope._rootDirty = true;
        }

        public void Clear()
        {
            ApplyWrites();
            scope.StopHintBal();
            scope.Bundle.SelfDestruct(_addressHash);
            scope._rootDirty = true;
        }

        public void Dispose()
        {
            ApplyWrites();
            _writes.Dispose();
        }

        private void ApplyWrites()
        {
            scope.Bundle.SetSlots(address, _addressHash, _writes.AsSpan());
            _writes.Clear();
        }
    }
}
