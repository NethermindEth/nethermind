// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

public sealed class FlatWorldStateScope :
    IWorldStateScopeProvider.IScope,
    IWorldStateScopeProvider.ICommittedStateRootSink,
    ITrieWarmer.IAddressWarmer
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly FlatSparseTrieCache? _sparseCache;
    // True only in the clean state right after a successful Commit; a started write batch clears
    // it. The scope offers its generation to the retention cache at dispose only while true, so an
    // aborted (mid-batch or failed) block never admits a candidate that does not match a root.
    private bool _lastCommitClean;
    private readonly bool _trieless;

    private readonly ConcurrencyController _concurrencyQuota;
    // Diagnostic Patricia cross-check of the sparse root; non-null only under VerifyWithTrie.
    private readonly StateTree? _stateTree;
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

    private volatile ReadOnlyBlockAccessList? _warmupWriteSet;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    internal FlatSparseTrieSession SparseSession { get; }

    // A history-backed scope is trie-less: flat reads/writes only, no trie node loads, writes or hashing.
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
        bool isReadOnly = false)
        : this(currentStateId, snapshotBundle, codeDb, commitTarget, configuration, trieCacheWarmer, logManager, warmReadPool, isReadOnly, sparseCache: null)
    {
    }

    internal FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        Lazy<WarmReadPool>? warmReadPool,
        bool isReadOnly,
        FlatSparseTrieCache? sparseCache,
        SparseTrieRootWorker? rootWorker = null)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;
        _sparseCache = sparseCache;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        _stateTree = configuration.VerifyWithTrie
            ? new StateTree(new StateTrieStoreAdapter(snapshotBundle, _concurrencyQuota), logManager)
            {
                RootHash = currentStateId.StateRoot.ToCommitment()
            }
            : null;

        RetainedGeneration? checkedOut = sparseCache?.TryCheckout(currentStateId.StateRoot);
        SparseSession = new FlatSparseTrieSession(
            snapshotBundle,
            currentStateId.StateRoot.ToCommitment(),
            logManager.GetClassLogger<FlatSparseTrieSession>(),
            checkedOut,
            retentionEnabled: sparseCache is not null,
            rootWorker);
        _configuration = configuration;
        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
        _trieless = snapshotBundle.IsHistorical;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        PauseAndDrainPrewarmer();
        // Offer the scope's warm generation to the retention cache only after a clean commit, so a
        // block that aborted mid-branch discards its (possibly mid-mutation) tries instead of
        // admitting them. ExtractGeneration transfers ownership; SparseSession.Dispose is then a
        // no-op, otherwise it releases the tries.
        if (_sparseCache is not null && _lastCommitClean)
        {
            _sparseCache.Admit(SparseSession.ExtractGeneration(_currentStateId.StateRoot));
        }

        SparseSession.Dispose();
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

    private bool NeedsStateTrieWarmup(Address address)
    {
        ReadOnlyBlockAccessList? bal = _warmupWriteSet;
        return bal is null || bal.GetAccountChanges(address)?.HasStateChanges == true;
    }

    private void QueueStateTrieWarmup(Address address, int sequenceId)
    {
        if (!NeedsStateTrieWarmup(address)) return;

        // Count the job before it can run: the warmer decrements on completion, and a job that
        // finished before the increment would let the drain loop return with warmups still in flight.
        Interlocked.Increment(ref _outstandingWarmups);
        if (!_warmer.PushAddressJob(this, address, sequenceId))
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
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
                if (logger.IsWarn) logger.Warn($"TrieWarmer outstanding jobs ({Volatile.Read(ref _outstandingWarmups)}) did not drain within 1s");
                stopwatch.Restart();
            }
            spinWait.SpinOnce();
        }
    }

    public Hash256 RootHash => SparseSession.RootHash;

    public void UpdateRootHash()
    {
        // A history-backed scope maintains no trie, so there is no root to recompute.
        if (_trieless) return;

        PauseAndDrainPrewarmer();
        SparseSession.UpdateRootHash();

        if (_configuration.VerifyWithTrie)
        {
            _stateTree!.UpdateRootHash();
            if (_stateTree!.RootHash != SparseSession.RootHash)
            {
                ThrowStateRootMismatch(SparseSession.RootHash, _stateTree!.RootHash, committed: false);
            }
        }

        Interlocked.Increment(ref _hintSequenceId);
        _pausePrewarmer = false;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStateRootMismatch(Hash256 sparseRoot, Hash256 patriciaRoot, bool committed) =>
        throw new TrieException($"Sparse state root {sparseRoot} does not match the {(committed ? "committed " : "")}Patricia root {patriciaRoot}");

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        HintGet(address, account, promote: !isInCurrentSnapshot);

        // A trie-less (history-backed) scope has no trie to verify against — the reader throws on trie-node access,
        // and a historical value verified against the current trie would be wrong anyway.
        if (_configuration.VerifyWithTrie && !_trieless)
        {
            Account? accTrie = _stateTree!.Get(address);
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
            QueueStateTrieWarmup(address, _hintSequenceId);
    }

    // Not reentrant: cancels and replaces the previous hint task unguarded; call only from the block-processing thread.
    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
    {
        CancelHintBal();

        int accountCount = bal.AccountChanges.Count;
        _warmupWriteSet = accountCount == 0 ? null : bal;
        if (accountCount == 0) return Task.CompletedTask;

        // Copy the span into a pooled array so the Task.Run body can capture it.
        ArrayPoolList<ReadOnlyAccountChanges> accountChanges = new(bal.AccountChanges.AsSpan());

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
                // Phase 1: storage-trie prefetch + GetAccount + sink.OnAccountRead. Sink slot reads are
                // deferred to phase 2 so one huge account doesn't bottleneck a single worker.
                void WarmAccount(int i)
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                    ReadOnlyAccountChanges ac = accountChanges[i];
                    Address address = ac.Address;

                    ReadOnlySlotChanges[] storageChanges = ac.StorageChanges;
                    int storageChangeCount = storageChanges.Length;

                    Account? account = _snapshotBundle.GetAccount(address);

                    if (sink is not null && sink.StillNeeded(address, out _))
                        sink.OnAccountRead(address, account);

                    if (account is null) return;
                    Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                    if (storageRoot == Keccak.EmptyTreeHash) return;

                    if (storageChangeCount > 0)
                    {
                        using ArrayPoolList<ValueHash256> keys = new(storageChangeCount, storageChangeCount);
                        for (int j = 0; j < storageChangeCount; j++)
                        {
                            UInt256 slot = storageChanges[j].Key;
                            _snapshotBundle.ShouldQueuePrewarm(address, slot);
                            ValueHash256 key = ValueKeccak.Zero;
                            StorageTree.ComputeKeyWithLookup(slot, ref key);
                            keys[j] = key;
                        }

                        TryPrefetchStorage(address, storageRoot, keys.AsSpan());
                    }

                    if (accounts is not null)
                    {
                        accounts[i] = account;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }
                }

                void PrefetchAccountPaths()
                {
                    if (token.IsCancellationRequested
                        || _hintSequenceId != snapshot
                        || _pausePrewarmer
                        || SparseSession.RootHash == Keccak.EmptyTreeHash)
                    {
                        return;
                    }

                    using ArrayPoolList<ValueHash256> keys = new(accountCount);
                    for (int i = 0; i < accountCount; i++)
                    {
                        ReadOnlyAccountChanges ac = accountChanges[i];
                        // A BAL account that is only read keeps its leaf, so revealing its path is wasted work.
                        if (!ac.HasStateChanges) continue;

                        Address address = ac.Address;
                        _snapshotBundle.ShouldQueuePrewarm(address);
                        keys.Add(address.ToAccountPath);
                    }

                    try
                    {
                        SparseSession.PrefetchState(keys.AsSpan());
                    }
                    catch (Exception ex) when (ex is TrieException or NodeHashMismatchException or ObjectDisposedException)
                    {
                        LogBalPrefetchFailure("state", ex);
                    }
                }

                void TryPrefetchStorage(Address address, Hash256 storageRoot, ReadOnlySpan<ValueHash256> keys)
                {
                    try
                    {
                        ValueHash256 addressHash = address.ToAccountPath;
                        SparseSession.PrefetchStorage(in addressHash, storageRoot.ValueHash256, keys);
                    }
                    catch (Exception ex) when (ex is TrieException or NodeHashMismatchException or ObjectDisposedException)
                    {
                        LogBalPrefetchFailure("storage", ex);
                    }
                }

                void WarmBal(int i)
                {
                    if (i == 0)
                    {
                        PrefetchAccountPaths();
                    }
                    else
                    {
                        WarmAccount(i - 1);
                    }
                }

                // The shared ThreadPool is saturated by the parallel EVM executor
                // during newPayload, so Parallel.For here gets starved exactly when
                // warmup matters. The dedicated reader pool is idle at that point.
                int warmJobCount = accountCount + 1;
                if (_warmReadPool is not null)
                {
                    WarmReadPool pool = _warmReadPool.Value;
                    int workers = Math.Min(pool.MaxConcurrency, Math.Max(2, warmJobCount / 64));
                    pool.Run(warmJobCount, workers, WarmBal, token);
                }
                else
                {
                    Parallel.For(0, warmJobCount, parallelOptions, WarmBal);
                }

                if (sink is not null) RunSinkSlotReads(accountChanges, accounts!, selfDestructIdxs!, sink, parallelOptions);
            }
            catch (OperationCanceledException) { }
            finally
            {
                accountChanges.Dispose();
            }
        });
    }

    private void LogBalPrefetchFailure(string target, Exception ex)
    {
        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
        logger.DebugError($"BAL sparse {target} prefetch failed", ex);
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
                ValueHash256 key = address.ToAccountPath;
                SparseSession.PrefetchState(in key);

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
        if (_snapshotBundle.ShouldQueuePrewarm(address))
            QueueStateTrieWarmup(address.ToAddress(), _hintSequenceId);
    }

    public void HintWarmSlot(in ValueAddress address, in UInt256 index)
    {
        if (IsDisposed || _pausePrewarmer) return;
        if (!_snapshotBundle.ShouldQueuePrewarm(address, index)) return;

        FlatStorageTree? tree = GetOrCreateHintWarmStorageTree(address.ToAddress());
        if (tree is null) return;

        Interlocked.Increment(ref _outstandingWarmups);
        if (!_warmer.PushSlotJobMpmc(tree, index, _hintSequenceId))
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    public void HintCommittedAccount(Address address, Account? account) =>
        SparseSession.EnqueueCommittedAccount(address, account);

    public void HintCommittedStorage(Address address, in UInt256 index, byte[] value)
    {
        ValueHash256 key = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(index, ref key);
        SparseSession.EnqueueCommittedStorage(address, in key, value);
    }

    public void HintCommittedStorageClear(Address address) =>
        SparseSession.EnqueueCommittedStorageClear(address);

    public void CompleteCommittedStateRound() =>
        SparseSession.CompleteCommittedStateRound();

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
        PauseAndDrainPrewarmer();
        SparseSession.DrainConcurrentUpdates();
        // Mutation in progress: the warm generation no longer matches a committed root until the
        // next Commit completes.
        _lastCommitClean = false;
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(ulong blockNumber)
    {
        PauseAndDrainPrewarmer();
        SparseSession.UpdateRootHash();

        if (_configuration.VerifyWithTrie)
        {
            // The diagnostic Patricia trees re-publish the same node bytes the sparse session
            // stages; identical roots guarantee identical nodes, so the overlap is harmless.
            _stateTree!.Commit();
            if (_stateTree!.RootHash != SparseSession.RootHash)
            {
                ThrowStateRootMismatch(SparseSession.RootHash, _stateTree!.RootHash, committed: true);
            }
        }

        // A history-backed scope maintains no trie, so there is nothing to publish.
        if (!_trieless) SparseSession.Publish();

        _storages.Clear();
        _hintWarmStorages?.Clear();

        StateId newStateId = new(blockNumber, RootHash);
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
                cachedResource?.ReleaseLease();
            }
        }

        _currentStateId = newStateId;
        // The warm generation now matches this committed root; it may be admitted at scope dispose.
        _lastCommitClean = true;
        Interlocked.Increment(ref _hintSequenceId);
        _pausePrewarmer = false;
    }

    private void PauseAndDrainPrewarmer()
    {
        _pausePrewarmer = true;
        Interlocked.Increment(ref _hintSequenceId);
        CancelHintBal();
        WaitForOutstandingWarmups();
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
                // Whole-trie sparse roots for the sealed storage jobs; completed roots arrive
                // through MarkDirty and are drained below, preserving OnAccountUpdated timing.
                scope.SparseSession.RunStoragePhase(FinalAccount);

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

                OnAccountUpdated = null;

                // The per-account flat writes above already carry intra-block state for subsequent txs; only a
                // normal scope additionally bulk-applies the dirty accounts into the sparse trie.
                if (!scope._trieless) scope.SparseSession.ApplyStateUpdates(_dirtyAccounts);

                if (scope._configuration.VerifyWithTrie && !scope._trieless)
                {
                    using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree!.BeginSet(_dirtyAccounts.Count);
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

        private Account? FinalAccount(Address address) =>
            _dirtyAccounts.TryGetValue(address, out Account? account) ? account : scope.Get(address);
    }
}
