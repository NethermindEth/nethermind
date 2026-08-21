// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
    /// <summary>Keys a single BAL prewarm job covers, so its DB misses collapse into one batched read.</summary>
    private const int BatchSize = Persistence.BaseFlatPersistence.MultiGetBatchSize;

    private static int ChunkCount(int itemCount) => (itemCount + BatchSize - 1) / BatchSize;

    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly bool _trieless;

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

    private volatile ReadOnlyBlockAccessList? _warmupWriteSet;

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
        _trieless = snapshotBundle.IsHistorical;
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

    private bool NeedsStateTrieWarmup(Address address)
    {
        ReadOnlyBlockAccessList? bal = _warmupWriteSet;
        return bal is null || bal.GetAccountChanges(address)?.HasStateChanges == true;
    }

    private void QueueStateTrieWarmup(Address address, int sequenceId)
    {
        if (NeedsStateTrieWarmup(address)
            && _warmer.PushAddressJob(this, address, sequenceId))
            Interlocked.Increment(ref _outstandingWarmups);
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
        if (!_trieless) _stateTree.UpdateRootHash();
    }

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address, out bool isInCurrentSnapshot);

        HintGet(address, account, promote: !isInCurrentSnapshot);

        // A trie-less (history-backed) scope has no trie to verify against — the reader throws on trie-node access,
        // and a historical value verified against the current trie would be wrong anyway.
        if (_configuration.VerifyWithTrie && !_trieless)
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
                // Phase 1: trie warmup + GetAccount + sink.OnAccountRead. Sink slot reads are
                // deferred to phase 2 so one huge account doesn't bottleneck a single worker.
                // A job is a chunk of accounts rather than one account, so the account reads it
                // misses in the in-memory tiers reach the DB as a single batched read.
                void WarmAccountChunk(int chunkIndex)
                {
                    if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                    int start = chunkIndex * BatchSize;
                    int count = Math.Min(BatchSize, accountCount - start);

                    Address[] addresses = ArrayPool<Address>.Shared.Rent(count);
                    Account?[] chunkAccounts = ArrayPool<Account?>.Shared.Rent(count);
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            ReadOnlyAccountChanges ac = accountChanges[start + i];
                            Address address = ac.Address;
                            addresses[i] = address;

                            if (ac.HasStateChanges
                                && _snapshotBundle.ShouldQueuePrewarm(address)
                                && _warmer.PushAddressJob(this, address, snapshot))
                                Interlocked.Increment(ref _outstandingWarmups);
                        }

                        _snapshotBundle.GetAccounts(addresses.AsSpan(0, count), chunkAccounts.AsSpan(0, count));

                        for (int i = 0; i < count; i++)
                        {
                            if (token.IsCancellationRequested || _hintSequenceId != snapshot || _pausePrewarmer) return;

                            int j = start + i;
                            ReadOnlyAccountChanges ac = accountChanges[j];
                            Address address = ac.Address;
                            Account? account = chunkAccounts[i];

                            if (sink is not null && sink.StillNeeded(address, out _))
                                sink.OnAccountRead(address, account);

                            if (account is null) continue;
                            Hash256 storageRoot = account.StorageRoot ?? Keccak.EmptyTreeHash;
                            if (storageRoot == Keccak.EmptyTreeHash) continue;

                            ReadOnlySlotChanges[] storageChanges = ac.StorageChanges;
                            if (storageChanges.Length > 0)
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
                                accounts[j] = account;
                                selfDestructIdxs![j] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<Address>.Shared.Return(addresses, clearArray: true);
                        ArrayPool<Account?>.Shared.Return(chunkAccounts, clearArray: true);
                    }
                }

                int accountChunks = ChunkCount(accountCount);

                // The shared ThreadPool is saturated by the parallel EVM executor
                // during newPayload, so Parallel.For here gets starved exactly when
                // warmup matters. The dedicated reader pool is idle at that point.
                if (_warmReadPool is not null)
                {
                    WarmReadPool pool = _warmReadPool.Value;
                    // Worker count still scales with accounts, not chunks: batching removes per-key
                    // overhead, it does not add I/O parallelism, which is what these workers provide.
                    int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, accountCount / 64));
                    pool.Run(accountChunks, workers, WarmAccountChunk, token);
                }
                else
                {
                    Parallel.For(0, accountChunks, parallelOptions, WarmAccountChunk);
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
        // Worker count still scales with slots, not chunks: batching removes per-key overhead, it
        // does not add I/O parallelism, which is what these workers provide.
        int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, idx / 64));

        pool.Run(ChunkCount(idx), workers, c => ReadSlotChunkToSink(sink, jobs, c, idx), parallelOptions.CancellationToken);
    }

    /// <summary>
    /// Serves one chunk of the sink's slot reads: drops the cells the sink no longer needs, reads what
    /// remains as a single batch, then reports each value.
    /// </summary>
    private void ReadSlotChunkToSink(
        IWorldStateScopeProvider.IAsyncBalReaderSink sink,
        ArrayPoolList<(Address Address, int SelfDestructIdx, UInt256 Slot)> jobs,
        int chunkIndex,
        int jobCount)
    {
        if (_pausePrewarmer) return;

        int start = chunkIndex * BatchSize;
        int count = Math.Min(BatchSize, jobCount - start);

        Address[] addresses = ArrayPool<Address>.Shared.Rent(count);
        UInt256[] slots = ArrayPool<UInt256>.Shared.Rent(count);
        int[] selfDestructIdxs = ArrayPool<int>.Shared.Rent(count);
        byte[]?[] values = ArrayPool<byte[]?>.Shared.Rent(count);
        try
        {
            // Filter before the batched read, not after: a cell the sink has already satisfied must not
            // cost a lookup.
            int needed = 0;
            for (int i = 0; i < count; i++)
            {
                (Address address, int selfDestructIdx, UInt256 slot) = jobs[start + i];
                StorageCell cell = new(address, in slot);
                if (!sink.StillNeeded(in cell)) continue;

                addresses[needed] = address;
                slots[needed] = slot;
                selfDestructIdxs[needed++] = selfDestructIdx;
            }

            if (needed == 0) return;

            _snapshotBundle.GetSlots(
                addresses.AsSpan(0, needed), slots.AsSpan(0, needed),
                selfDestructIdxs.AsSpan(0, needed), values.AsSpan(0, needed));

            if (_pausePrewarmer) return;

            for (int i = 0; i < needed; i++)
            {
                StorageCell cell = new(addresses[i], in slots[i]);
                byte[]? raw = values[i];
                sink.OnStorageRead(in cell, raw is null || raw.Length == 0 ? StorageTree.ZeroBytes : raw);
            }
        }
        finally
        {
            ArrayPool<Address>.Shared.Return(addresses, clearArray: true);
            ArrayPool<UInt256>.Shared.Return(slots);
            ArrayPool<int>.Shared.Return(selfDestructIdxs);
            ArrayPool<byte[]?>.Shared.Return(values, clearArray: true);
        }
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
        if (_snapshotBundle.ShouldQueuePrewarm(address))
            QueueStateTrieWarmup(address.ToAddress(), _hintSequenceId);
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

        // Storage tree commits already happened during WriteBatch.Dispose() via
        // StorageTreeBulkWriteBatch(commit: true). Only the state tree needs committing here.
        if (!_trieless) _stateTree.Commit();

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

                OnAccountUpdated = null;

                // The per-account flat writes above already carry intra-block state for subsequent txs; only a
                // normal scope additionally bulk-applies the dirty accounts into the state trie.
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
