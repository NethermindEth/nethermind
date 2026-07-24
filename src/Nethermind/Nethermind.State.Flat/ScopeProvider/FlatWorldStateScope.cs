// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly bool _trieless;

    // Deferred state-root materialization (FlatDb.CommitBatchSize > 1). In batch mode the scope runs every block
    // trie-less during execution and materializes + verifies the trie once per window at its boundary.
    private readonly IFlatDbManager? _batchManager;
    private readonly bool _batchMode;
    // The trusted downloaded state root of the block currently being committed, captured in
    // ShouldComputeStateRoot; used to key interior (unverified) snapshots since their real root is never computed.
    private Hash256? _pendingTrustedStateRoot;
    // Set once the current boundary block's window has been materialized (in UpdateRootHash) so Commit does not
    // redo it; reset per block at the end of Commit.
    private bool _boundaryReady;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? _hintWarmStorages;
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private volatile int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal volatile bool _pausePrewarmer = false;

    private CancellationTokenSource? _hintBalCts;
    private Task? _hintBalTask;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    // A trie-less scope performs flat reads/writes (overlay + columns) but ZERO per-block trie node loads,
    // writes or hashing. True for a history-backed scope and for a deferred-materialization (batch) scope.
    // Read by FlatStorageTree to skip storage-trie bulk writes/root computation.
    internal bool Trieless => _trieless;

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        Lazy<WarmReadPool>? warmReadPool = null,
        bool isReadOnly = false,
        IFlatDbManager? batchManager = null)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = new(
            new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _warmupStateTree = new(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        )
        {
            RootHash = currentStateId.StateRoot.ToCommitment()
        };

        _configuration = configuration;
        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;

        // Deferred state-root materialization only applies to the writable main processing scope; a read-only env
        // (eth_call, block production trial, overridable/simulate) always needs the real root per block.
        _batchManager = batchManager;
        _batchMode = !isReadOnly && batchManager is { CommitBatchSize: > 1 };

        // Trie-less during execution when history-backed OR deferring materialization: post-block state-root
        // recomputation must not traverse the (unmaterialized) state trie per block. Batch scopes instead
        // materialize + verify once per window at its boundary (see MaterializeBoundary).
        _trieless = snapshotBundle.IsHistorical || _batchMode;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        CancelHintBal();
        WaitForOutstandingWarmups();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    private void CancelHintBal()
    {
        _hintBalCts?.Cancel();
        try { _hintBalTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
            if (logger.IsError) logger.Error("HintBal background task faulted during cancel/drain", ex);
        }
        _hintBalCts?.Dispose();
        _hintBalCts = null;
        _hintBalTask = null;
    }

    // Exposed for tests to observe when the wait loop is entered.
    internal Action? OnWaitingForWarmups;

    private void WaitForOutstandingWarmups()
    {
        if (Volatile.Read(ref _outstandingWarmups) == 0) return;

        OnWaitingForWarmups?.Invoke();

        SpinWait spinWait = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (Volatile.Read(ref _outstandingWarmups) != 0)
        {
            if (stopwatch.ElapsedMilliseconds > 1000)
            {
                ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
                if (logger.IsWarn) logger.Warn($"TrieWarmer outstanding jobs ({Volatile.Read(ref _outstandingWarmups)}) did not drain within 1s during scope dispose");
                return;
            }
            spinWait.SpinOnce();
        }
    }

    public Hash256 RootHash => _stateTree.RootHash;

    public void UpdateRootHash()
    {
        if (_batchMode)
        {
            // Interior blocks keep their trusted downloaded root (no per-block trie work). At a window boundary,
            // materialize the window's dirty union onto the last-materialized root and compute the real root so
            // the block processor can verify it against the header.
            if (IsCurrentBlockBoundary()) MaterializeBoundary();
        }
        else if (!_trieless)
        {
            _stateTree.UpdateRootHash();
        }
    }

    /// <inheritdoc/>
    public bool ShouldComputeStateRoot(BlockHeader header)
    {
        if (!_batchMode) return true;

        // Capture the block's trusted downloaded root so the interior snapshot produced by Commit can be keyed
        // by it (an interior block's real root is deliberately never computed). Called once per block before
        // its commit; refreshed for every block in a multi-block branch scope.
        _pendingTrustedStateRoot = header.StateRoot;
        return _batchManager!.IsMaterializationBoundary((ulong)header.Number);
    }

    private bool IsCurrentBlockBoundary() =>
        _batchManager!.IsMaterializationBoundary(_currentStateId.BlockNumber + 1);

    /// <summary>
    /// Materializes the deferred window (last-materialized boundary, current boundary block] into the state trie
    /// and computes its real root. Seeds the state tree at the last-materialized root, rebuilds every dirty
    /// account (and its storage trie) from the CURRENT flat values across the window, and hashes the result.
    /// The block processor then verifies the computed root against the downloaded header at this boundary.
    /// </summary>
    private void MaterializeBoundary()
    {
        // The trie warmer may still be draining hints that touch the bundle; quiesce it before we rebuild trees.
        _pausePrewarmer = true;
        WaitForOutstandingWarmups();

        StateId? lastMaterialized = _batchManager!.GetLastMaterializedStateId();
        Hash256 seedRoot = lastMaterialized is { } lm ? lm.StateRoot.ToCommitment() : Keccak.EmptyTreeHash;
        // Exclusive window start: snapshots ending at or before the last materialized boundary are already folded
        // into the seed root. Genesis (no prior boundary) seeds the empty tree and takes only the current content.
        ulong windowStartBlock = lastMaterialized?.BlockNumber ?? 0;

        // Re-root at the last materialized boundary (its nodes resolve from the snapshot chain / persisted tier),
        // discarding the stale interior seed carried since scope construction.
        _stateTree.RootHash = seedRoot;

        HashSet<AddressAsKey> dirtyAccounts = [];
        Dictionary<AddressAsKey, HashSet<UInt256>> dirtyStorage = [];
        HashSet<AddressAsKey> selfDestructedInWindow = [];
        _snapshotBundle.CollectWindowDirtyUnion(windowStartBlock, dirtyAccounts, dirtyStorage, selfDestructedInWindow);

        // Recompute each dirty account's storage root from current flat slot values, seeded at its pre-window
        // storage root — or the empty root when it self-destructed in-window, so wiped slots do not survive.
        Dictionary<AddressAsKey, Hash256> newStorageRoots = new(dirtyStorage.Count);
        foreach (KeyValuePair<AddressAsKey, HashSet<UInt256>> entry in dirtyStorage)
        {
            Address address = entry.Key.Value;
            if (_snapshotBundle.GetAccount(address) is null) continue; // deleted; storage association is gone

            Hash256 preWindowRoot = selfDestructedInWindow.Contains(entry.Key)
                ? Keccak.EmptyTreeHash
                : _stateTree.Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;

            newStorageRoots[entry.Key] = RecomputeStorageRoot(address, preWindowRoot, entry.Value);
        }

        // Determine each dirty account's authoritative storage root BEFORE opening the bulk setter (trie reads
        // must not interleave with BeginSet). Storage-changed accounts use the recomputed root. For accounts
        // changed only in balance/nonce the flat account's StorageRoot is STALE in trie-less batch mode (it is
        // never updated when storage changes in an interior block, and a later balance-only write propagates the
        // stale value), so it must not be trusted — read the account's storage root from the materialized seed
        // tree instead: storage was unchanged this window, so that root is still current.
        Dictionary<AddressAsKey, Account?> reconciled = new(dirtyAccounts.Count);
        foreach (AddressAsKey addressKey in dirtyAccounts)
        {
            Address address = addressKey.Value;
            Account? account = _snapshotBundle.GetAccount(address);
            if (account is not null)
            {
                Hash256 storageRoot = newStorageRoots.TryGetValue(addressKey, out Hash256? recomputed)
                    ? recomputed
                    : _stateTree.Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
                account = account.WithChangedStorageRoot(storageRoot);
            }
            reconciled[addressKey] = account;
        }

        // A null read means self-destruct or EIP-158 empty, i.e. delete.
        using (StateTree.StateTreeBulkSetter setter = _stateTree.BeginSet(dirtyAccounts.Count))
        {
            foreach (KeyValuePair<AddressAsKey, Account?> kv in reconciled)
                setter.Set(kv.Key.Value, kv.Value);
        }

        _stateTree.UpdateRootHash();
        _boundaryReady = true;
    }

    private Hash256 RecomputeStorageRoot(Address address, Hash256 preWindowRoot, HashSet<UInt256> dirtySlots)
    {
        Hash256 addressHash = address.ToAccountPath.ToHash256();
        StorageTree storageTree = new(
            new StorageTrieStoreAdapter(_snapshotBundle, _concurrencyQuota, addressHash),
            preWindowRoot,
            _logManager);

        int selfDestructIdx = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
        foreach (UInt256 slot in dirtySlots)
        {
            byte[]? value = _snapshotBundle.GetSlot(address, slot, selfDestructIdx);
            storageTree.Set(slot, value is null || value.Length == 0 ? StorageTree.ZeroBytes : value);
        }

        storageTree.Commit();
        return storageTree.RootHash;
    }

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        HintGet(address, account, promote: !isInCurrentSnapshot);

        if (_configuration.VerifyWithTrie)
        {
            Account? accTrie = _stateTree.Get(address);
            if (accTrie != account)
            {
                throw new TrieException($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
            }
        }

        return account;
    }

    public void HintGet(Address address, Account? account) => HintGet(address, account, promote: true);

    private void HintGet(Address address, Account? account, bool promote)
    {
        if (promote) _snapshotBundle.PromoteAccount(address, account);
        if (_snapshotBundle.ShouldQueuePrewarm(address))
        {
            if (_warmer.PushAddressJob(this, address, _hintSequenceId))
                Interlocked.Increment(ref _outstandingWarmups);
        }
    }

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        int accountCount = bal.AccountChanges.Count;
        if (accountCount == 0) return Task.CompletedTask;

        // Copy the span into a pooled array so the Task.Run body can capture it.
        ArrayPoolList<ReadOnlyAccountChanges> accountChanges = new(bal.AccountChanges.AsSpan());

        CancelHintBal();

        _hintBalCts = new CancellationTokenSource();
        CancellationToken token = _hintBalCts.Token;
        int snapshot = _hintSequenceId;

        return _hintBalTask = Task.Run(() =>
        {
            ParallelOptions parallelOptions = new() { CancellationToken = token };

            Account?[]? accounts = sink is null ? null : new Account?[accountCount];
            int[]? selfDestructIdxs = sink is null ? null : new int[accountCount];

            try
            {
                // Phase 1: trie warmup + GetAccount + sink.OnAccountRead. Sink slot reads are
                // deferred to phase 2 so one huge account doesn't bottleneck a single worker.
                Parallel.For(0, accountCount, parallelOptions, (i) =>
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                    ReadOnlyAccountChanges ac = accountChanges[i];
                    Address address = ac.Address;

                    if (_snapshotBundle.ShouldQueuePrewarm(address)
                        && _warmer.PushAddressJob(this, address, snapshot))
                        Interlocked.Increment(ref _outstandingWarmups);

                    ReadOnlySlotChanges[] storageChanges = ac.StorageChanges;
                    int storageChangeCount = storageChanges.Length;

                    Account? account = sink is null && storageChangeCount == 0
                        ? null
                        : _snapshotBundle.GetAccount(address);

                    if (sink is not null && sink.StillNeeded(address, out _))
                        sink.OnAccountRead(address, account);

                    if (account is null) return;
                    Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                    if (storageRoot == Keccak.EmptyTreeHash) return;

                    if (storageChangeCount > 0)
                    {
                        FlatStorageTree storageWarmer = new(
                            this,
                            _warmer,
                            _snapshotBundle,
                            _configuration,
                            _concurrencyQuota,
                            storageRoot,
                            address,
                            _logManager);

                        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                        {
                            UInt256 key = slotChanges.Key;
                            if (_snapshotBundle.ShouldQueuePrewarm(address, key)
                                && _warmer.PushSlotJobMpmc(storageWarmer, key, snapshot))
                                Interlocked.Increment(ref _outstandingWarmups);
                        }
                    }

                    if (accounts is not null)
                    {
                        accounts[i] = account;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }
                });

                if (sink is not null) RunSinkSlotReads(accountChanges, accounts!, selfDestructIdxs!, sink, parallelOptions);
            }
            catch (OperationCanceledException) { }
            finally
            {
                accountChanges.Dispose();
            }
        }, token);
    }

    private void RunSinkSlotReads(
        ArrayPoolList<ReadOnlyAccountChanges> accountChanges,
        Account?[] accounts,
        int[] selfDestructIdxs,
        IWorldStateScopeProvider.IAsyncBalReaderSink sink,
        ParallelOptions parallelOptions)
    {
        // Read-only providers have no pool; sinks are only passed on the writable block-processing path.
        if (_warmReadPool is null) return;

        int totalSlots = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (accounts[i] is null) continue;
            totalSlots += accountChanges[i].StorageChanges.Length
                       + accountChanges[i].StorageReads.Length;
        }

        if (totalSlots == 0) return;

        using ArrayPoolList<(Address Address, int SelfDestructIdx, UInt256 Slot)> jobs = new(totalSlots, totalSlots);
        int idx = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (accounts[i] is null) continue;
            ReadOnlyAccountChanges ac = accountChanges[i];
            Address address = ac.Address;
            int selfDestructIdx = selfDestructIdxs[i];
            foreach (ReadOnlySlotChanges slotChanges in ac.StorageChanges)
                jobs[idx++] = (address, selfDestructIdx, slotChanges.Key);
            foreach (UInt256 readKey in ac.StorageReads)
                jobs[idx++] = (address, selfDestructIdx, readKey);
        }

        // Lazy materialisation: this is the only call site that needs the pool, so chains/forks
        // that never see a BAL never allocate the dedicated reader threads.
        WarmReadPool pool = _warmReadPool.Value;
        int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, idx / 64));

        pool.Run(idx, workers, j =>
        {
            if (_pausePrewarmer) return;
            (Address address, int selfDestructIdx, UInt256 slot) = jobs[j];
            ReadSlotToSink(sink, address, in slot, selfDestructIdx);
        }, parallelOptions.CancellationToken);
    }

    private void ReadSlotToSink(IWorldStateScopeProvider.IAsyncBalReaderSink sink, Address address, in UInt256 slot, int selfDestructIdx)
    {
        StorageCell cell = new(address, in slot);
        if (!sink.StillNeeded(in cell)) return;
        byte[]? raw = _snapshotBundle.GetSlot(address, in slot, selfDestructIdx);
        sink.OnStorageRead(in cell, raw is null || raw.Length == 0 ? StorageTree.ZeroBytes : raw);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;
            if (!_snapshotBundle.TryLeaseReadOnlyBundle()) return false;

            try
            {
                // Note: tree root not changed after writing batch. Also, not cleared. So the result is not correct.
                // this is just for warming up
                _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

                return true;
            }
            finally
            {
                _snapshotBundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => Interlocked.Decrement(ref _outstandingWarmups);

    public void HintWarmAccount(in ValueAddress address)
    {
        if (IsDisposed || _pausePrewarmer) return;
        // The managed Address is materialized only after the dedupe bloom passes, so the
        // allocation happens at most once per account per block.
        if (_snapshotBundle.ShouldQueuePrewarm(address)
            && _warmer.PushAddressJob(this, address.ToAddress(), _hintSequenceId))
            Interlocked.Increment(ref _outstandingWarmups);
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        if (IsDisposed || _pausePrewarmer) return;
        if (!_snapshotBundle.ShouldQueuePrewarm(address, index)) return;

        FlatStorageTree? tree = GetOrCreateHintWarmStorageTree(address.ToAddress());
        if (tree is not null && _warmer.PushSlotJobMpmc(tree, index, _hintSequenceId))
            Interlocked.Increment(ref _outstandingWarmups);
    }

    private FlatStorageTree? GetOrCreateHintWarmStorageTree(Address address) =>
        GetHintWarmStorages().GetOrAdd(address, static (key, scope) =>
        {
            Hash256 storageRoot = scope._snapshotBundle.GetAccount(key.Value)?.StorageRoot ?? Keccak.EmptyTreeHash;
            return storageRoot == Keccak.EmptyTreeHash
                ? null
                : new FlatStorageTree(
                    scope,
                    scope._warmer,
                    scope._snapshotBundle,
                    scope._configuration,
                    scope._concurrencyQuota,
                    storageRoot,
                    key.Value,
                    scope._logManager);
        }, this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?> GetHintWarmStorages()
    {
        ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? storages = Volatile.Read(ref _hintWarmStorages);
        return storages ?? InitializeHintWarmStorages();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?> InitializeHintWarmStorages()
    {
        ConcurrentDictionary<AddressAsKey, FlatStorageTree?> newStorages = new();
        return Interlocked.CompareExchange(ref _hintWarmStorages, newStorages, null) ?? newStorages;
    }

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
    {
        ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists) return storage!;

        Hash256 storageRoot = Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash;
        storage = new FlatStorageTree(
            this,
            _warmer,
            _snapshotBundle,
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        CancelHintBal();
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(ulong blockNumber)
    {
        _pausePrewarmer = true;

        Hash256 committedRoot;
        if (_batchMode)
        {
            if (_batchManager!.IsMaterializationBoundary(blockNumber))
            {
                // Fallback for a path reaching Commit without a prior ComputeStateRoot (e.g. genesis load).
                if (!_boundaryReady) MaterializeBoundary();
                // Persist the materialized window's trie nodes and key by the real, verified root.
                _stateTree.Commit();
                committedRoot = _stateTree.RootHash;
            }
            else
            {
                // Interior block: no trie nodes are produced. Key by the trusted downloaded root so the next
                // block's scope (gathered at that root) chains onto this snapshot. Verification is deferred to
                // the next window boundary.
                committedRoot = _pendingTrustedStateRoot ?? _stateTree.RootHash;
            }
            _boundaryReady = false;
        }
        else
        {
            // Storage tree commits already happened during WriteBatch.Dispose() via
            // StorageTreeBulkWriteBatch(commit: true). Only the state tree needs committing here.
            if (!_trieless) _stateTree.Commit();
            committedRoot = _stateTree.RootHash;
        }

        _storages.Clear();
        _hintWarmStorages?.Clear();

        StateId newStateId = new(blockNumber, committedRoot);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        (Snapshot? newSnapshot, TransientResource? cachedResource) = _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

        if (shouldAddSnapshot)
        {
            if (_currentStateId != newStateId)
            {
                _commitTarget.AddSnapshot(newSnapshot!, cachedResource!);
            }
            else
            {
                newSnapshot?.Dispose();
                cachedResource?.Dispose();
            }
        }

        _currentStateId = newStateId;
        _pausePrewarmer = false;
    }

    // Largely same logic as the the one for TrieStoreScopeProvider, but more confusing when deduplicated.
    // So I just leave it here.
    private class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _dirtyAccounts[key] = account;
            scope._snapshotBundle.SetAccount(key, account);

            if (account is null)
            {
                // This may not get called by the storage write batch as the worldstate does not try to update storage
                // at all if the end account is null. This is not a problem for trie, but is a problem for flat.
                scope.CreateStorageTreeImpl(key).SelfDestruct();
            }
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: (address, newRoot) => MarkDirty(address, newRoot));

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            try
            {
                while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    if (!_dirtyAccounts.TryGetValue(key, out Account? account)) account = scope.Get(key);
                    if (account is null)
                    {
                        if (storageRoot == Keccak.EmptyTreeHash) continue;
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(key.Value, 0);
                        wb.Clear();
                        continue;
                    }
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    scope._snapshotBundle.SetAccount(key, account);

                    Address address = key.Value;
                    OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                    if (logger.IsTrace) Trace(address, storageRoot, account);
                }

                // The per-account flat writes above (scope._snapshotBundle.SetAccount) already carry intra-block state
                // for subsequent txs; normal scopes additionally bulk-apply the dirty accounts into the state trie.
                // Trie-less scopes keep the known root on _stateTree and never touch trie nodes, so they skip this.
                if (!scope._trieless)
                {
                    using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                    foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                    {
                        stateSetter.Set(kv.Key, kv.Value);
                    }
                }
            }
            finally
            {
                _dirtyAccounts.Clear();

                Interlocked.Increment(ref scope._hintSequenceId);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account) =>
                logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
        }
    }
}
