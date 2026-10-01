// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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

    private ConcurrencyController? _backgroundConcurrency;
    private PatriciaTree? _warmupStateTree;
    private readonly Hash256 _initialStateRoot;
    private StateTree? _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? _hintWarmStorages;
    private bool _isDisposed = false;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private volatile int _hintSequenceId = 0;
    // Counted before each push and uncounted if it is rejected: a job can complete before a later increment lands,
    // and a count below zero would let that increment reach zero with no completion to wake dispose.
    private int _outstandingWarmups = 0;
    // Published by dispose before it waits; the job that brings the count to zero sets it. Never disposed: a job still
    // running past the timeout sets it later, and a slim event takes no kernel handle unless its WaitHandle is read.
    private ManualResetEventSlim? _warmupsDrained;
    private StateId _currentStateId;
    internal volatile bool _pausePrewarmer = false;

    private CancellationTokenSource? _hintBalCts;
    private Task? _hintBalTask;

    private volatile ReadOnlyBlockAccessList? _warmupWriteSet;

    private readonly IdleStorageApplier? _earlyApplier;
    // Closed from the block-end write batch on.
    private volatile bool _earlyApplyClosed;
    // Advanced by every commit, so trees of an earlier block are skipped.
    private volatile int _earlyApplyGeneration;
    private int _earlyAppliedSlots;
    private int _earlyReusedSlots;
    private int _earlyRestoredSlots;
    private int _earlyAbandonedTrees;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    // A history-backed scope is trie-less: flat reads/writes only, no trie node loads, writes or hashing.
    internal bool Trieless => _trieless;

    internal bool BackgroundStorageTrieUpdates => _configuration.BackgroundStorageTrieUpdates
        && !_isReadOnly && !_trieless && _snapshotBundle._usage == ResourcePool.Usage.MainBlockProcessing;

    internal ConcurrencyController BackgroundConcurrency => _backgroundConcurrency ??= new(Math.Max(1, Environment.ProcessorCount / 2) + 1);

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
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;

        _initialStateRoot = currentStateId.StateRoot.ToCommitment();

        _configuration = configuration;
        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
        _trieless = snapshotBundle.IsHistorical;

        // Background storage trie updates take the committed writes instead, so the two never run together.
        if (configuration.ApplyStorageWritesOnIdleThread && !isReadOnly && !_trieless && !configuration.VerifyWithTrie
            && !configuration.BackgroundStorageTrieUpdates && snapshotBundle._usage == ResourcePool.Usage.MainBlockProcessing)
        {
            _earlyApplier = IdleStorageApplier.GetInstance(logManager);
            _earlyApplyClosed = !_earlyApplier.FollowsIdleGap();
        }
    }

    internal bool AppliesStorageWritesEarly => _earlyApplier is not null && !_earlyApplyClosed;

    internal bool EarlyApplyClosed => _earlyApplyClosed;

    internal int EarlyApplyGeneration => _earlyApplyGeneration;

    internal IdleStorageApplier EarlyApplier => _earlyApplier!;

    internal void CountEarlyApplied(int slots) => Interlocked.Add(ref _earlyAppliedSlots, slots);

    /// <summary>This block's early apply counters so far. For tests.</summary>
    internal (int Applied, int Reused, int Restored, int Abandoned) EarlyApplyCounts =>
        (Volatile.Read(ref _earlyAppliedSlots), Volatile.Read(ref _earlyReusedSlots), Volatile.Read(ref _earlyRestoredSlots), Volatile.Read(ref _earlyAbandonedTrees));

    internal void CountEarlyAbandoned() => Interlocked.Increment(ref _earlyAbandonedTrees);

    internal void CountEarlyReconciled(int reused, int restored)
    {
        Interlocked.Add(ref _earlyReusedSlots, reused);
        Interlocked.Add(ref _earlyRestoredSlots, restored);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        _earlyApplyClosed = true;
        // Nothing reads the warmed paths after this, so queued jobs skip their walk and the wait covers only walks in flight.
        Interlocked.Increment(ref _hintSequenceId);
        CancelHintBal();
        WaitForOutstandingWarmups();
        try
        {
            StopBackgroundWrites();
        }
        finally
        {
            _snapshotBundle.Dispose();
            _warmer.OnExitScope();
        }
    }

    private void StopBackgroundWrites()
    {
        if (_backgroundConcurrency is null) return;
        List<Exception>? failures = null;
        foreach (FlatStorageTree? storage in _storages.Values)
        {
            try { storage?.StopBackgroundWrites(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) throw new AggregateException(failures);
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
        Interlocked.Increment(ref _outstandingWarmups);
        if (!_warmer.PushAddressJob(this, address, sequenceId)) CompleteWarmup();
    }

    // Exposed for tests to observe when the wait loop is entered.
    internal Action? OnWaitingForWarmups;
    // Exposed for tests to act just before each blocking wait.
    internal Action? OnBlockingForWarmups;

    private void WaitForOutstandingWarmups()
    {
        if (Volatile.Read(ref _outstandingWarmups) == 0) return;

        OnWaitingForWarmups?.Invoke();

        // Blocking rather than sleeping: a 1 ms sleep can last a whole timer tick (15.6 ms on Windows) past the last walk.
        ManualResetEventSlim drained = new(initialState: false);
        Interlocked.Exchange(ref _warmupsDrained, drained);
        long deadline = Environment.TickCount64 + 1000;
        while (true)
        {
            // Reset before re-reading the count: a job that reached zero before more were queued can set the event late,
            // so a wake only means "re-check", and a completion after the re-read still wakes the wait.
            drained.Reset();
            if (Volatile.Read(ref _outstandingWarmups) == 0) return;
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) break;
            OnBlockingForWarmups?.Invoke();
            drained.Wait((int)remaining);
        }

        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
        if (logger.IsWarn) logger.Warn($"TrieWarmer outstanding jobs ({Volatile.Read(ref _outstandingWarmups)}) did not drain within 1s during scope dispose");
    }

    private void CompleteWarmup()
    {
        if (Interlocked.Decrement(ref _outstandingWarmups) == 0) Volatile.Read(ref _warmupsDrained)?.Set();
    }

    private StateTree StateTree => Volatile.Read(ref _stateTree) ?? CreateStateTree();

    private StateTree CreateStateTree()
    {
        StateTree tree = new(new StateTrieStoreAdapter(_snapshotBundle), _logManager)
        {
            RootHash = _initialStateRoot
        };
        return Interlocked.CompareExchange(ref _stateTree, tree, null) ?? tree;
    }

    public Hash256 RootHash => Volatile.Read(ref _stateTree)?.RootHash ?? _initialStateRoot;

    public void UpdateRootHash()
    {
        if (!_trieless) Volatile.Read(ref _stateTree)?.UpdateRootHash();
    }

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        // Promotion only: a read rewrites nothing at commit, so its trie path needs no warming.
        if (!isInCurrentSnapshot) _snapshotBundle.PromoteAccount(address, account);

        // A trie-less (history-backed) scope has no trie to verify against — the reader throws on trie-node access,
        // and a historical value verified against the current trie would be wrong anyway.
        if (_configuration.VerifyWithTrie && !_trieless)
        {
            Account? accTrie = StateTree.Get(address);
            if (accTrie != account)
            {
                throw new TrieException($"Incorrect account {address}, account hash {address.ToAccountPath}, trie: {accTrie} vs flat: {account}");
            }
        }

        return account;
    }

    public void HintGet(Address address, Account? account) => _snapshotBundle.PromoteAccount(address, account);

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
                // Phase 1: trie warmup + GetAccount + sink.OnAccountRead. Sink slot reads are
                // deferred to phase 2 so one huge account doesn't bottleneck a single worker.
                void WarmAccount(int i)
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                    ReadOnlyAccountChanges ac = accountChanges[i];
                    Address address = ac.Address;

                    if (ac.HasStateChanges && _snapshotBundle.ShouldQueuePrewarm(address))
                    {
                        Interlocked.Increment(ref _outstandingWarmups);
                        if (!_warmer.PushAddressJob(this, address, snapshot)) CompleteWarmup();
                    }

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
                        FlatStorageTree storageWarmer = new(
                            this,
                            _warmer,
                            _snapshotBundle,
                            _configuration,
                            storageRoot,
                            address,
                            _logManager);

                        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                        {
                            UInt256 key = slotChanges.Key;
                            if (!_snapshotBundle.ShouldQueuePrewarm(address, key)) continue;
                            Interlocked.Increment(ref _outstandingWarmups);
                            if (!_warmer.PushSlotJobMpmc(storageWarmer, key, snapshot)) CompleteWarmup();
                        }
                    }

                    if (accounts is not null)
                    {
                        accounts[i] = account;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }
                }

                // The shared ThreadPool is saturated by the parallel EVM executor
                // during newPayload, so Parallel.For here gets starved exactly when
                // warmup matters. The dedicated reader pool is idle at that point.
                if (_warmReadPool is not null)
                {
                    WarmReadPool pool = _warmReadPool.Value;
                    int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, accountCount / 64));
                    pool.Run(accountCount, workers, WarmAccount, token);
                }
                else
                {
                    Parallel.For(0, accountCount, parallelOptions, WarmAccount);
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
        _snapshotBundle.GetSlot(address, in slot, selfDestructIdx, out UInt256? value);
        sink.OnStorageRead(in cell, value.GetValueOrDefault());
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    private PatriciaTree CreateWarmupStateTree()
    {
        PatriciaTree tree = new(new StateTrieStoreWarmerAdapter(_snapshotBundle), _logManager)
        {
            RootHash = _initialStateRoot
        };
        return Interlocked.CompareExchange(ref _warmupStateTree, tree, null) ?? tree;
    }

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
                (Volatile.Read(ref _warmupStateTree) ?? CreateWarmupStateTree()).WarmUpPath(address.ToAccountPath.Bytes);

                return true;
            }
            finally
            {
                _snapshotBundle.ReleaseReadOnlyBundleLease();
            }
        }
        finally
        {
            CompleteWarmup();
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal int OutstandingWarmups => Volatile.Read(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => CompleteWarmup();

    public void HintWarmAccount(Address address)
    {
        if (IsDisposed || _pausePrewarmer) return;
        if (_snapshotBundle.ShouldQueuePrewarm(address))
            QueueStateTrieWarmup(address, _hintSequenceId);
    }

    public void HintWarmSlot(Address address, in UInt256 index)
    {
        if (IsDisposed || _pausePrewarmer) return;
        if (!_snapshotBundle.ShouldQueuePrewarm(address, index)) return;

        FlatStorageTree? tree = GetOrCreateHintWarmStorageTree(address);
        if (tree is null) return;
        Interlocked.Increment(ref _outstandingWarmups);
        if (!_warmer.PushSlotJobMpmc(tree, index, _hintSequenceId)) CompleteWarmup();
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
            storageRoot,
            address,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        CancelHintBal();
        _earlyApplyClosed = true;
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(ulong blockNumber)
    {
        StopBackgroundWrites();
        _pausePrewarmer = true;

        // With DeferStorageTrieCommit the write batches only hashed the storage trees, so their nodes are written here,
        // after the block was reported valid; otherwise the batches already committed them. The nodes must be in the
        // bundle before CollectAndApplySnapshot takes the block's changes. No tree means nothing was written.
        if (!_trieless)
        {
            CommitStorageTrees();
            Volatile.Read(ref _stateTree)?.Commit();
        }

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
        _pausePrewarmer = false;

        if (_earlyApplier is not null) ReportEarlyApply(blockNumber);
    }

    private void ReportEarlyApply(ulong blockNumber)
    {
        int applied = Interlocked.Exchange(ref _earlyAppliedSlots, 0);
        int reused = Interlocked.Exchange(ref _earlyReusedSlots, 0);
        int restored = Interlocked.Exchange(ref _earlyRestoredSlots, 0);
        int abandoned = Interlocked.Exchange(ref _earlyAbandonedTrees, 0);
        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
        if (logger.IsDebug) logger.Debug($"Early storage apply block={blockNumber} applied={applied} reused={reused} restored={restored} abandoned={abandoned}");

        _earlyApplier!.BlockCommitted();
        _earlyApplyGeneration++;
        _earlyApplyClosed = !_earlyApplier.FollowsIdleGap();
    }

    private void CommitStorageTrees()
    {
        if (_storages.Count == 0) return;

        using ArrayPoolList<FlatStorageTree> dirty = new(_storages.Count);
        foreach (FlatStorageTree storage in _storages.Values)
        {
            if (storage.HasUncommittedNodes) dirty.Add(storage);
        }

        if (dirty.Count == 0) return;

        if (dirty.Count == 1 || Core.Cpu.RuntimeInformation.IsSingleProcessor)
        {
            foreach (FlatStorageTree storage in dirty) storage.CommitTree();
            return;
        }

        // Each address writes its own node dictionary in the bundle, so the trees commit independently.
        using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
        ParallelUnbalancedWork.For(0, dirty.Count, Core.Cpu.RuntimeInformation.ParallelOptionsLogicalCores,
            dirty, static (i, trees) =>
            {
                trees[i].CommitTree();
                return trees;
            });
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

            if (account is null)
            {
                // This may not get called by the storage write batch as the worldstate does not try to update storage
                // at all if the end account is null. This is not a problem for trie, but is a problem for flat.
                // Resolve the storage tree before deleting the account from the flat snapshot: creating it reads
                // the account, and with VerifyWithTrie that read is compared against the trie, which only applies
                // this delete on Dispose. Reading after the delete throws for any account the trie still holds,
                // e.g. the EIP-161 clearing of a pre-existing empty account.
                FlatStorageTree storage = scope.CreateStorageTreeImpl(key);
                scope._snapshotBundle.SetAccount(key, account);
                storage.ClearStorage();
                return;
            }

            scope._snapshotBundle.SetAccount(key, account);
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

                OnAccountUpdated = null;

                // The per-account flat writes above already carry intra-block state for subsequent txs; only a
                // normal scope additionally bulk-applies the dirty accounts into the state trie.
                if (!scope._trieless)
                {
                    if (Avx2.IsSupported && _dirtyAccounts.Count >= KeyHashBatch.MinimumBatchSize)
                    {
                        scope.StateTree.SetAccounts(_dirtyAccounts);
                    }
                    else
                    {
                        using StateTree.StateTreeBulkSetter stateSetter = scope.StateTree.BeginSet(_dirtyAccounts.Count);
                        foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                        {
                            stateSetter.Set(kv.Key, kv.Value);
                        }
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
