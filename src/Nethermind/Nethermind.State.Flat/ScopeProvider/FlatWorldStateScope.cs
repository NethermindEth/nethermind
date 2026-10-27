// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
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
    private readonly bool _warmsTries;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly bool _trieless;

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
    private int _earlyAdoptedSlots;
    private int _earlyAbandonedSlots;

    // int.MaxValue when off, so a storage batch decides with one comparison.
    private readonly int _storageRootPrefetchMinChanges;
    private int _prefetchedContracts;
    private int _prefetchLoads;
    private int _prefetchRounds;
    private long _prefetchTicks;

    // Bench-only (FlatDb.ShadowAccountSplit): the block's account-trie batches, kept for the shadow run at commit.
    private readonly bool _shadowAccountSplit;
    private ShadowAccountBatch? _shadowBatch;
    private static int _shadowFailuresLogged;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

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
        _warmsTries = trieCacheWarmer.IsActive;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
        _trieless = snapshotBundle.IsHistorical;
        _storageRootPrefetchMinChanges = configuration.StorageRootPrefetchMinChanges > 0 && !isReadOnly
            ? configuration.StorageRootPrefetchMinChanges
            : int.MaxValue;

        if (configuration.ApplyStorageWritesOnIdleThread && !isReadOnly && !_trieless && !configuration.VerifyWithTrie
            && snapshotBundle._usage == ResourcePool.Usage.MainBlockProcessing)
        {
            _earlyApplier = IdleStorageApplier.GetInstance(logManager);
            _earlyApplyClosed = !_earlyApplier.FollowsIdleGap();
        }

        _shadowAccountSplit = configuration.ShadowAccountSplit && !isReadOnly && !_trieless;
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

    /// <summary>Bench-only: slots of early trees adopted by the block-end batch, and of trees abandoned mid-pass.</summary>
    internal void CountEarlyAdoptedSlots(int slots) => Interlocked.Add(ref _earlyAdoptedSlots, slots);

    internal void CountEarlyAbandonedSlots(int slots) => Interlocked.Add(ref _earlyAbandonedSlots, slots);

    internal void CountEarlyReconciled(int reused, int restored)
    {
        Interlocked.Add(ref _earlyReusedSlots, reused);
        Interlocked.Add(ref _earlyRestoredSlots, restored);
    }

    /// <summary>Pending writes from which a storage commit prefetches its trie paths; <c>int.MaxValue</c> when off.</summary>
    internal int StorageRootPrefetchMinChanges => _storageRootPrefetchMinChanges;

    /// <summary>The dedicated reader threads, created on first use; <c>null</c> for a read-only provider.</summary>
    internal WarmReadPool? ReadPool => _warmReadPool?.Value;

    internal void CountStorageRootPrefetch(int loads, int rounds, long ticks)
    {
        Interlocked.Increment(ref _prefetchedContracts);
        Interlocked.Add(ref _prefetchLoads, loads);
        Interlocked.Add(ref _prefetchRounds, rounds);
        Interlocked.Add(ref _prefetchTicks, ticks);
    }

    /// <summary>The storage-root prefetches of this scope so far. For tests.</summary>
    internal (int Contracts, int Loads, int Rounds, long Ticks) StorageRootPrefetchCounts =>
        (Volatile.Read(ref _prefetchedContracts), Volatile.Read(ref _prefetchLoads), Volatile.Read(ref _prefetchRounds), Interlocked.Read(ref _prefetchTicks));

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        _earlyApplyClosed = true;
        _shadowBatch = null;
        // Nothing reads the warmed paths after this, so queued jobs skip their walk and the wait covers only walks in flight.
        Interlocked.Increment(ref _hintSequenceId);
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

    public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default) => ScopeBalApplier.Apply(this, bal, _logManager, cancellationToken);

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
                // First, on this task: the backend decides from a few cached probes, so a cached block costs only
                // those, and a cold one has its reads in flight before phase 1 reads them one at a time.
                // Skipped like the warmup below once the hint is cancelled, superseded or paused, so a block that
                // no longer needs it never waits for its key collection and submission in CancelHintBal.
                if (!token.IsCancellationRequested && _hintSequenceId == snapshot && !_pausePrewarmer && SupportsPrefetch)
                    PrefetchBalSafely(accountChanges, token);

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
                            StorageChange[] changes = slotChanges.Changes;
                            bool isDelete = changes.Length > 0 && changes[^1].Value.IsZero;
                            Interlocked.Increment(ref _outstandingWarmups);
                            if (!_warmer.PushSlotJobMpmc(storageWarmer, key, snapshot, isDelete)) CompleteWarmup();
                        }
                    }

                    if (accounts is not null)
                    {
                        accounts[i] = account;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }
                }

                // The shared ThreadPool is saturated by the parallel EVM executor
                // during newPayload. The dedicated reader pool avoids contending
                // with execution workers when warmup matters.
                if (_warmReadPool is not null)
                {
                    WarmReadPool pool = _warmReadPool.Value;
                    int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, accountCount / 64));
                    pool.Run(accountCount, workers, WarmAccount, token);
                }
                else
                {
                    using ParallelUnbalancedWork.WorkerScope workers = ParallelUnbalancedWork.BeginWorkerScope(Environment.ProcessorCount);
                    ParallelUnbalancedWork.For(0, accountCount, parallelOptions, WarmAccount);
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

    /// <summary>
    /// Starts asynchronous batched reads of every account and slot the access list names, so their blocks are in
    /// the RocksDB block cache before the warmup and execution read them one at a time. The reads that follow are
    /// unchanged; on a warm database the prefetch finds the blocks already cached.
    /// </summary>
    // A prefetch only warms caches; a failure must not cost the warmup that follows.
    private void PrefetchBalSafely(ArrayPoolList<ReadOnlyAccountChanges> accountChanges, CancellationToken cancellationToken)
    {
        try
        {
            PrefetchBal(accountChanges, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
            if (logger.IsDebug) logger.Debug($"State prefetch failed: {ex}");
        }
    }

    private void PrefetchBal(ArrayPoolList<ReadOnlyAccountChanges> accountChanges, CancellationToken cancellationToken)
    {
        int slotCount = 0;
        foreach (ReadOnlyAccountChanges changes in accountChanges.AsSpan())
            slotCount += changes.StorageChanges.Length + changes.StorageReads.Length;

        // The one copy of the keys: the access list holds them per account, the batched read needs them contiguous.
        // An account's slots stay adjacent, so its hash is computed once below.
        Address[] accounts = ArrayPool<Address>.Shared.Rent(accountChanges.Count);
        StorageCell[] slots = ArrayPool<StorageCell>.Shared.Rent(slotCount);
        if (CollectBalKeys(accountChanges, accounts, slots, cancellationToken, out int accountCount, out int collectedSlots))
            _snapshotBundle.Prefetch(accounts.AsSpan(0, accountCount), slots.AsSpan(0, collectedSlots), cancellationToken);

        ArrayPool<Address>.Shared.Return(accounts, clearArray: true);
        ArrayPool<StorageCell>.Shared.Return(slots, clearArray: true);
    }

    // How many slots are copied between cancellation checks, so one account with a large slot list still stops soon.
    private const int SlotsPerCancellationCheck = 1024;

    /// <returns>False when <paramref name="cancellationToken"/> is cancelled before every key is copied.</returns>
    internal static bool CollectBalKeys(ArrayPoolList<ReadOnlyAccountChanges> accountChanges, Address[] accounts, StorageCell[] slots,
        CancellationToken cancellationToken, out int accountCount, out int slotCount)
    {
        accountCount = 0;
        slotCount = 0;
        foreach (ReadOnlyAccountChanges changes in accountChanges.AsSpan())
        {
            if (cancellationToken.IsCancellationRequested) return false;

            Address address = changes.Address;
            accounts[accountCount++] = address;
            foreach (ReadOnlySlotChanges slotChanges in changes.StorageChanges)
            {
                if (++slotCount % SlotsPerCancellationCheck == 0 && cancellationToken.IsCancellationRequested) return false;
                slots[slotCount - 1] = new StorageCell(address, slotChanges.Key);
            }

            foreach (UInt256 slot in changes.StorageReads)
            {
                if (++slotCount % SlotsPerCancellationCheck == 0 && cancellationToken.IsCancellationRequested) return false;
                slots[slotCount - 1] = new StorageCell(address, slot);
            }
        }

        return true;
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

    // Without an active warmer a hint could only pin the bundle, mark the dedupe bloom and build a storage tree for a
    // push that is rejected anyway; read-only scopes run on the no-op warmer.
    internal bool WarmsTries => _warmsTries;

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

    public void Prefetch(Span<Address> accounts, Span<StorageCell> slots, CancellationToken cancellationToken) => _snapshotBundle.Prefetch(accounts, slots, cancellationToken);

    public bool SupportsPrefetch => _snapshotBundle.SupportsPrefetch;

    public void HintWarmAccount(Address address)
    {
        if (!_warmsTries || IsDisposed || _pausePrewarmer) return;
        if (_snapshotBundle.ShouldQueuePrewarm(address))
            QueueStateTrieWarmup(address, _hintSequenceId);
    }

    public void HintWarmSlot(Address address, in UInt256 index)
    {
        if (!_warmsTries || IsDisposed || _pausePrewarmer) return;
        if (!_snapshotBundle.ShouldQueuePrewarm(address, index)) return;

        FlatStorageTree? tree = GetOrCreateHintWarmStorageTree(address);
        if (tree is null) return;
        Interlocked.Increment(ref _outstandingWarmups);
        if (!_warmer.PushSlotJobMpmc(tree, index, _hintSequenceId, isDelete: false)) CompleteWarmup();
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
        // Bench-only: what the early apply had done when the block-end batch closes it.
        if (_earlyApplier is not null && BlockDiag.Current is { } diag && diag.Get(BlockDiag.C.RootCommits) <= 1)
        {
            diag.Set(BlockDiag.C.EarlyActive, _earlyApplyClosed ? 0 : 1);
            diag.Set(BlockDiag.C.EarlyAppliedAtBatch, Volatile.Read(ref _earlyAppliedSlots));
        }

        CancelHintBal();
        _earlyApplyClosed = true;
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(ulong blockNumber)
    {
        _pausePrewarmer = true;
        ShadowAccountBatch? shadowBatch = _shadowBatch;
        _shadowBatch = null;

        // With DeferStorageTrieCommit the write batches only hashed the storage trees, so their nodes are written here,
        // after the block was reported valid; otherwise the batches already committed them. The nodes must be in the
        // bundle before CollectAndApplySnapshot takes the block's changes. No tree means nothing was written.
        BlockDiag? diag = _isReadOnly ? null : BlockDiag.Current;
        if (!_trieless)
        {
            using (BlockDiag.Measure(diag, BlockDiag.F.PcStorageTrees))
            {
                CommitStorageTrees();
            }

            // Before the state trie commits, so the bundle still serves only the pre-block state nodes.
            if (shadowBatch is not null && diag is not null && Volatile.Read(ref _stateTree) is { } stateTree)
            {
                RunShadowAccountSplit(shadowBatch, diag, blockNumber, _currentStateId.StateRoot.ToCommitment(), stateTree.RootHash);
            }

            using (BlockDiag.Measure(diag, BlockDiag.F.PcStateTree))
            {
                Volatile.Read(ref _stateTree)?.Commit();
            }
        }

        _storages.Clear();
        _hintWarmStorages?.Clear();

        StateId newStateId = new(blockNumber, RootHash);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        BlockDiag.Section snapshotSection = BlockDiag.Measure(diag, BlockDiag.F.PcSnapshot);
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

        snapshotSection.Dispose();
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
        int adoptedSlots = Interlocked.Exchange(ref _earlyAdoptedSlots, 0);
        int abandonedSlots = Interlocked.Exchange(ref _earlyAbandonedSlots, 0);
        if (BlockDiag.Current is { } diag)
        {
            diag.Set(BlockDiag.C.EarlyAdoptedSlots, adoptedSlots);
            diag.Set(BlockDiag.C.EarlyAbandonedSlots, abandonedSlots);
            diag.Set(BlockDiag.C.EarlyApplied, applied);
            diag.Set(BlockDiag.C.EarlyReused, reused);
            diag.Set(BlockDiag.C.EarlyRestored, restored);
            diag.Set(BlockDiag.C.EarlyAbandoned, abandoned);
        }

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

    /// <summary>
    /// Bench-only: what the block's account-trie batches wrote. Every account as written to the trie, a later batch of
    /// the block overwriting an earlier one's; and the addresses whose storage root the drain wrote, each with the
    /// account the state flush had left for it before the drain, or null when there was none.
    /// </summary>
    private sealed class ShadowAccountBatch
    {
        public Dictionary<AddressAsKey, Account?>? Final;
        public readonly Dictionary<AddressAsKey, Account?> StorageRootChanged = [];

        /// <returns>Whether <paramref name="accounts"/> was taken over rather than copied, so the batch must not clear it.</returns>
        public bool Keep(Dictionary<AddressAsKey, Account?> accounts)
        {
            if (Final is null)
            {
                Final = accounts;
                return true;
            }

            foreach (KeyValuePair<AddressAsKey, Account?> entry in accounts) Final[entry.Key] = entry.Value;
            return false;
        }
    }

    // The shadow run reports into the block's diag, so only a block that carries one keeps its batches.
    private ShadowAccountBatch? GetShadowBatch() =>
        _shadowAccountSplit && BlockDiag.Current is not null ? _shadowBatch ??= new ShadowAccountBatch() : null;

    /// <summary>Whether this block's account-trie batches are kept for the shadow run. For tests.</summary>
    internal bool KeepsShadowBatch => _shadowBatch is not null;

    /// <summary>
    /// Bench-only (<see cref="IFlatDbConfig.ShadowAccountSplit"/>): redoes the block's account-trie update on two
    /// throw-away state tries built on the pre-block root, to bound what writing the settled accounts concurrently
    /// with the storage roots would take off the critical path. Arm X writes every changed account and hashes; arm Y
    /// writes the settled accounts (changed, not deleted, storage root unchanged) and hashes, then the rest and
    /// hashes again. The arm that runs first alternates with the block number. Both roots are checked against the
    /// block's.
    /// </summary>
    /// <remarks>
    /// Arm Y writes the settled accounts as the state flush left them, the only values known before the storage roots,
    /// and leaves them out of its second write; so a storage-changed account taken for settled would end with its old
    /// storage root and fail the check. The tries copy every node they change and are never committed, so nothing
    /// shared is written. A failure is counted in the diag and swallowed.
    /// </remarks>
    private void RunShadowAccountSplit(ShadowAccountBatch batch, BlockDiag diag, ulong blockNumber, Hash256 preBlockRoot, Hash256 blockRoot)
    {
        diag.Set(BlockDiag.C.ShRan, 1);
        bool leased = false;
        try
        {
            Dictionary<AddressAsKey, Account?> final = batch.Final ?? [];
            Dictionary<AddressAsKey, Account?> settled = new(final.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in final)
            {
                bool storageRootChanged = batch.StorageRootChanged.TryGetValue(entry.Key, out Account? beforeDrain);
                // What the first write could have used: the account as the state flush left it.
                Account? flushed = storageRootChanged ? beforeDrain : entry.Value;
                if (flushed is not null && !storageRootChanged) settled[entry.Key] = flushed;
            }

            Dictionary<AddressAsKey, Account?> rest = new(final.Count - settled.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in final)
            {
                if (!settled.ContainsKey(entry.Key)) rest[entry.Key] = entry.Value;
            }

            diag.Set(BlockDiag.C.ShSettled, settled.Count);
            diag.Set(BlockDiag.C.ShRest, rest.Count);

            if (!(leased = _snapshotBundle.TryLeaseReadOnlyBundle())) throw new InvalidOperationException("The snapshot bundle is disposed");

            bool xFirst = (blockNumber & 1) == 0;
            diag.Set(BlockDiag.C.ShXFirst, xFirst ? 1 : 0);
            Hash256 rootX;
            Hash256 rootY;
            if (xFirst)
            {
                rootX = RunShadowArmX(diag, preBlockRoot, final);
                rootY = RunShadowArmY(diag, preBlockRoot, settled, rest);
            }
            else
            {
                rootY = RunShadowArmY(diag, preBlockRoot, settled, rest);
                rootX = RunShadowArmX(diag, preBlockRoot, final);
            }

            diag.Set(BlockDiag.C.ShOk, rootX == blockRoot && rootY == blockRoot ? 1 : 0);
        }
        catch (Exception ex)
        {
            diag.Set(BlockDiag.C.ShErr, 1);
            diag.Set(BlockDiag.C.ShOk, 0);
            ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
            if (logger.IsWarn && Interlocked.Increment(ref _shadowFailuresLogged) <= 10) logger.Warn($"Shadow account split failed at block {blockNumber}: {ex}");
        }
        finally
        {
            if (leased) _snapshotBundle.ReleaseReadOnlyBundleLease();
        }
    }

    // Reads nodes as the block's own state tree does: unlike the warmer's adapter, it sees the earlier blocks of this
    // scope and writes into no cache.
    private StateTree CreateShadowStateTree(Hash256 preBlockRoot) =>
        new(new StateTrieStoreAdapter(_snapshotBundle), _logManager) { RootHash = preBlockRoot };

    private Hash256 RunShadowArmX(BlockDiag diag, Hash256 preBlockRoot, Dictionary<AddressAsKey, Account?> accounts)
    {
        StateTree tree = CreateShadowStateTree(preBlockRoot);
        using (BlockDiag.Measure(diag, BlockDiag.F.ShFull))
        {
            tree.WriteAccounts(accounts);
            tree.UpdateRootHash();
        }

        return tree.RootHash;
    }

    private Hash256 RunShadowArmY(BlockDiag diag, Hash256 preBlockRoot, Dictionary<AddressAsKey, Account?> settled, Dictionary<AddressAsKey, Account?> rest)
    {
        StateTree tree = CreateShadowStateTree(preBlockRoot);
        using (BlockDiag.Measure(diag, BlockDiag.F.ShP1))
        {
            tree.WriteAccounts(settled);
            tree.UpdateRootHash();
        }

        using (BlockDiag.Measure(diag, BlockDiag.F.ShP2))
        {
            tree.WriteAccounts(rest);
            tree.UpdateRootHash();
        }

        return tree.RootHash;
    }

    // Largely same logic as the the one for TrieStoreScopeProvider, but more confusing when deduplicated.
    // So I just leave it here.
    private class WriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly FlatWorldStateScope _scope;
        private readonly ILogger _logger;
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts;
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();
        // One delegate for every storage batch of the block.
        private readonly Action<Address, Hash256> _markDirty;

        public WriteBatch(FlatWorldStateScope scope, int estimatedAccountCount, ILogger logger)
        {
            _scope = scope;
            _logger = logger;
            _dirtyAccounts = new(estimatedAccountCount);
            _markDirty = MarkDirty;
        }

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
                FlatStorageTree storage = _scope.CreateStorageTreeImpl(key);
                _scope._snapshotBundle.SetAccount(key, account);
                storage.ClearStorage();
                return;
            }

            _scope._snapshotBundle.SetAccount(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            _scope
                .CreateStorageTreeImpl(address)
                .CreateWriteBatch(
                    estimatedEntries: estimatedEntries,
                    onRootUpdated: _markDirty);

        private void MarkDirty(Address address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            bool keptForShadow = false;
            try
            {
                ShadowAccountBatch? shadow = _scope.GetShadowBatch();
                while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
                {
                    (AddressAsKey key, Hash256 storageRoot) = entry;
                    bool flushed = _dirtyAccounts.TryGetValue(key, out Account? account);
                    // Bench-only: the account as the state flush left it, before its storage root changes below.
                    shadow?.StorageRootChanged.TryAdd(key, account);
                    if (!flushed) account = _scope.Get(key);
                    if (account is null)
                    {
                        if (storageRoot == Keccak.EmptyTreeHash) continue;
                        BlockDiag.Current?.Add(BlockDiag.C.TrieStorageCleared, 1);
                        using IWorldStateScopeProvider.IStorageWriteBatch wb = CreateStorageWriteBatch(key.Value, 0);
                        wb.Clear();
                        continue;
                    }
                    BlockDiag.Current?.Add(BlockDiag.C.TrieStorageRoots, 1);
                    account = account.WithChangedStorageRoot(storageRoot);
                    _dirtyAccounts[key] = account;

                    _scope._snapshotBundle.SetAccount(key, account);

                    Address address = key.Value;
                    OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                    if (_logger.IsTrace) Trace(address, storageRoot, account);
                }

                OnAccountUpdated = null;
                keptForShadow = shadow?.Keep(_dirtyAccounts) == true;

                BlockDiag? diag = BlockDiag.Current;
                diag?.Add(BlockDiag.C.TrieAccounts, _dirtyAccounts.Count);

                // The per-account flat writes above already carry intra-block state for subsequent txs; only a
                // normal scope additionally bulk-applies the dirty accounts into the state trie.
                // Bench (FlatDb.FusedAccountTrieUpdate): the fused pass also hashes below the root here, so this section
                // then holds what state_root held below the root, and state_root holds the root node.
                using (BlockDiag.Measure(diag, BlockDiag.F.WsAccountTrie))
                {
                    if (diag is not null) diag.CurrentPhase = BlockDiag.Phase.AccountTrie;
                    try
                    {
                        if (!_scope._trieless)
                        {
                            if (!_scope._configuration.FusedAccountTrieUpdate)
                                _scope.StateTree.WriteAccounts(_dirtyAccounts);
                            else if (!_scope.StateTree.WriteAccountsFused(_dirtyAccounts, _scope._configuration.FusedAccountTrieMinAccounts))
                                diag?.Add(BlockDiag.C.FusedFallback, 1);
                        }
                    }
                    finally
                    {
                        if (diag is not null) diag.CurrentPhase = BlockDiag.Phase.None;
                    }
                }
            }
            finally
            {
                // A dictionary the shadow run took over is its own now.
                if (!keptForShadow) _dirtyAccounts.Clear();

                Interlocked.Increment(ref _scope._hintSequenceId);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Trace(Address address, Hash256 storageRoot, Account? account) =>
                _logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
        }
    }
}
