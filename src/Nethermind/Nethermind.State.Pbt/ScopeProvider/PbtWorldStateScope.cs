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
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];
    private readonly BackgroundTask _hintBal;
    private readonly BackgroundTask _nodeGroupPrefetch;

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
        IPbtConfig config,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger<PbtWorldStateScope>();
        _hintBal = new BackgroundTask(_logger, "HintBal prefetch");
        _nodeGroupPrefetch = new BackgroundTask(_logger, "node group prefetch");
        _foldQuota = new ConcurrencyController(config.FoldConcurrency > 0 ? config.FoldConcurrency : Environment.ProcessorCount);
        _foldFanOut = new(config.FoldMinOperationsPerWorker, config.FoldLargeSubtreeBytes, config.FoldLargeSubtreeMinOperationsPerWorker);
        _nodeGroupMemory = nodeGroupMemory;
        _currentStateId = currentStateId;
        _currentHeader = currentHeader;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _childHeaders = childHeaders;
        _treeRoot = bundle.TreeRoot;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, Bundle);
        if (_logger.IsDebug) LogLifecycle("opened");
    }

    private void LogLifecycle(string stage) =>
        _logger.Debug($"PBT scope {_scopeId} {stage}: state={_currentStateId}, pendingMutations={Bundle.PendingMutationCount}, managedBytes={GC.GetTotalMemory(false)}");

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

    /// <summary>
    /// Starts buffering the accounts and slot runs <paramref name="bal"/> writes, so <see cref="ApplyBal"/> finds them in the write buffer,
    /// and starts the node group prefetch for the fold of those writes.
    /// </summary>
    /// <remarks>
    /// The buffered values are the ones visible when read, and a buffered value never replaces a write, so the prefetch
    /// may run alongside writes. Clearing storage could still race the prefetch of a run of the cleared account, so
    /// clearing, like committing and disposing, stops the prefetch first.
    /// With a <paramref name="sink"/>, every account and slot the list names, read-only ones included, is also read and forwarded to it.
    /// The returned task does not cover the node group prefetch, which <see cref="UpdateRootHash"/> stops.
    /// </remarks>
    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        _hintBal.Stop();
        if (bal.AccountChanges.Count == 0) return Task.CompletedTask;
        _nodeGroupPrefetch.Start(cancellation => PbtNodeGroupPrefetch.Prefetch(Bundle, bal, cancellation));
        return _hintBal.Start(cancellation => PrefetchBal(bal, sink, cancellation));
    }

    private void PrefetchBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink, CancellationToken cancellation) =>
        ParallelUnbalancedWork.For(0, bal.AccountChanges.Count, (scope: this, bal, sink, cancellation), static (index, state) =>
        {
            ReadOnlyAccountChanges accountChanges = state.bal.AccountChanges.AsSpan()[index];
            if (state.cancellation.IsCancellationRequested || (!accountChanges.HasStateChanges && state.sink is null)) return state;

            PbtSnapshotBundle bundle = state.scope.Bundle;
            Address address = accountChanges.Address;
            Account? account = accountChanges.HasStateChanges ? bundle.GetAndPromoteAccount(address) : bundle.GetAccount(address);
            if (state.sink?.StillNeeded(address, out _) == true) state.sink.OnAccountRead(address, account);
            // ApplyBal writes no slot of a missing account that the block leaves missing.
            if (account is null && !accountChanges.HasBalanceNonceOrCodeChanges) return state;

            ValueHash256 addressHash = PbtStateKey.AddressKeyHash(address);
            IWorldStateScopeProvider.IAsyncBalReaderSink? slotSink = account is null ? null : state.sink;
            UInt256? bufferedSlot = null;
            foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
            {
                if (slotChanges.Changes.Length == 0 || state.cancellation.IsCancellationRequested) continue;
                if (ReadSlotToSink(bundle, slotSink, address, addressHash, slotChanges.Key))
                {
                    bufferedSlot = slotChanges.Key;
                    continue;
                }
                // The slots are sorted, so the slots of one run are adjacent and the run is buffered once.
                if (bufferedSlot is { } previous && SlotRun.InSameRun(previous, slotChanges.Key)) continue;
                bundle.GetSlot(address, addressHash, slotChanges.Key);
                bufferedSlot = slotChanges.Key;
            }
            foreach (UInt256 slot in accountChanges.StorageReads)
            {
                if (state.cancellation.IsCancellationRequested) break;
                ReadSlotToSink(bundle, slotSink, address, addressHash, slot);
            }
            return state;
        });

    /// <summary>Reads the slot and forwards it to <paramref name="sink"/> when the sink still needs it.</summary>
    /// <returns>Whether the slot was read.</returns>
    private static bool ReadSlotToSink(PbtSnapshotBundle bundle, IWorldStateScopeProvider.IAsyncBalReaderSink? sink, Address address, in ValueHash256 addressHash, in UInt256 slot)
    {
        StorageCell cell = new(address, in slot);
        if (sink?.StillNeeded(in cell) != true) return false;
        EvmWord word = bundle.GetSlot(address, addressHash, slot);
        sink.OnStorageRead(in cell, EvmWordSlot.ToUInt256(in word));
        return true;
    }

    public void ApplyBal(ReadOnlyBlockAccessList bal) => ScopeBalApplier.ApplyConcurrently(this, bal);

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
            _nodeGroupPrefetch.Stop();
        }
    }

    public void Commit(ulong blockNumber)
    {
        if (_logger.IsDebug) LogLifecycle($"commit begin block={blockNumber}");
        long commitStart = Stopwatch.GetTimestamp();
        try
        {
            _hintBal.Stop();
            UpdateRootHash();
            StateId newStateId = new(blockNumber, _rootHash);
            if (newStateId != _currentStateId)
            {
                long addSnapshotStart = Stopwatch.GetTimestamp();
                PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _treeRoot, out PbtTransientResource transientResource);
                _commitTarget.AddSnapshot(snapshot, transientResource);
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
            _hintBal.Stop();
            _nodeGroupPrefetch.Stop();
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
            if (account is null) scope._hintBal.Stop();
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
            scope._hintBal.Stop();
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

    /// <summary>A background task that can be cancelled and waited for from any thread.</summary>
    private sealed class BackgroundTask(ILogger logger, string name)
    {
        private readonly Lock _lock = new();
        private Task? _task;
        private CancellationTokenSource? _cancellation;

        /// <summary>Stops the running task, then runs <paramref name="work"/> in the background.</summary>
        public Task Start(Action<CancellationToken> work)
        {
            Stop();
            lock (_lock)
            {
                CancellationTokenSource cancellation = new();
                _cancellation = cancellation;
                return _task = Task.Run(() => work(cancellation.Token), cancellation.Token);
            }
        }

        /// <summary>Cancels the running task and waits for it to finish.</summary>
        public void Stop()
        {
            if (Volatile.Read(ref _task) is null) return;
            lock (_lock)
            {
                _cancellation?.Cancel();
                try
                {
                    _task?.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (logger.IsError) logger.Error($"PBT {name} faulted", ex);
                }
                _cancellation?.Dispose();
                _cancellation = null;
                Volatile.Write(ref _task, null);
            }
        }
    }
}
