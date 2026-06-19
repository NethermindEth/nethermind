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

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly PreservedPatriciaTrie? _preservedPatriciaTrie;
    private readonly PreservedPatriciaTrie.Rebinder? _stateTreeRebinder;
    private readonly PreservedStorageTries? _preservedStorageTries;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private List<FlatStorageTree>? _preservableStorages;
    private bool _isDisposed = false;
    private Hash256? _lastCommittedStateRoot;

    // The sequence id is for stopping trie warmer for doing work while committing. Incrementing this value invalidates
    // tasks within the trie warmer's ring buffer.
    private volatile int _hintSequenceId = 0;
    private int _outstandingWarmups = 0;
    private StateId _currentStateId;
    internal volatile bool _pausePrewarmer = false;

    private CancellationTokenSource? _hintBalCts;
    private Task? _hintBalTask;

    internal bool IsDisposed => Volatile.Read(ref _isDisposed);

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        ILogManager logManager,
        Lazy<WarmReadPool>? warmReadPool = null,
        PreservedPatriciaTrie? preservedPatriciaTrie = null,
        PreservedStorageTries? preservedStorageTries = null,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;
        _preservedPatriciaTrie = preservedPatriciaTrie;
        _preservedStorageTries = preservedStorageTries;

        _concurrencyQuota = new ConcurrencyController(Environment.ProcessorCount); // Used during tree commit.
        Hash256 stateRoot = currentStateId.StateRoot.ToCommitment();
        if (preservedPatriciaTrie is not null && preservedPatriciaTrie.TryTake(stateRoot, snapshotBundle, _concurrencyQuota, out StateTree reusedTree))
        {
            _stateTree = reusedTree;
        }
        else
        {
            StateTrieStoreAdapter adapter = new(snapshotBundle, _concurrencyQuota);
            _stateTreeRebinder = preservedPatriciaTrie is not null ? adapter.Rebind : null;
            _stateTree = new(adapter, logManager)
            {
                RootHash = stateRoot
            };
        }

        _warmupStateTree = new(
            new StateTrieStoreWarmerAdapter(snapshotBundle),
            logManager
        )
        {
            RootHash = stateRoot
        };

        _configuration = configuration;
        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        CancelHintBal();
        WaitForOutstandingWarmups();
        ReturnPreservedStorageTries();
        ReturnPreservedPatriciaTrie();
        _snapshotBundle.Dispose();
        _warmer.OnExitScope();
    }

    private void ReturnPreservedStorageTries()
    {
        if (_preservableStorages is null) return;

        foreach (FlatStorageTree storage in _preservableStorages)
        {
            storage.Preserve();
        }

        _preservableStorages.Clear();
    }

    private void ReturnPreservedPatriciaTrie()
    {
        if (_preservedPatriciaTrie is null) return;

        if (_lastCommittedStateRoot is not null && _stateTree.RootHash == _lastCommittedStateRoot)
        {
            _preservedPatriciaTrie.StoreAnchored(_stateTree, _stateTreeRebinder, _lastCommittedStateRoot);
            return;
        }

        _preservedPatriciaTrie.TryStoreCleared();
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
    public void UpdateRootHash() => _stateTree.UpdateRootHash();

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address);

        HintPrewarm(address);

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

    public void HintGet(Address address, Account? account)
    {
        _snapshotBundle.CacheAccount(address, account);
        HintPrewarm(address);
    }

    private void HintPrewarm(Address address)
    {
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

            int[]? selfDestructIdxs = sink is null ? null : new int[accountCount];
            bool[]? shouldReadSlots = sink is null ? null : new bool[accountCount];

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

                    Account? account = null;
                    bool accountStillNeeded = sink is null || sink.StillNeeded(address, out account);

                    if (accountStillNeeded && (sink is not null || storageChangeCount != 0))
                    {
                        account = _snapshotBundle.GetAccount(address);
                    }

                    if (sink is not null && accountStillNeeded)
                    {
                        sink.OnAccountRead(address, account);
                    }

                    if (sink is not null && (accountStillNeeded || account is not null))
                    {
                        shouldReadSlots![i] = true;
                        selfDestructIdxs![i] = _snapshotBundle.DetermineSelfDestructSnapshotIdx(address);
                    }

                    if (account is null)
                        return;
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
                            preservedStorageTries: null,
                            _logManager);

                        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                        {
                            UInt256 key = slotChanges.Key;
                            if (_snapshotBundle.ShouldQueuePrewarm(address, key)
                                && _warmer.PushSlotJobMpmc(storageWarmer, key, snapshot))
                                Interlocked.Increment(ref _outstandingWarmups);
                        }
                    }
                });

                if (sink is not null) RunSinkSlotReads(accountChanges, shouldReadSlots!, selfDestructIdxs!, sink, parallelOptions);
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
        bool[] shouldReadSlots,
        int[] selfDestructIdxs,
        IWorldStateScopeProvider.IAsyncBalReaderSink sink,
        ParallelOptions parallelOptions)
    {
        // Read-only providers have no pool; sinks are only passed on the writable block-processing path.
        if (_warmReadPool is null) return;

        int totalSlots = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (!shouldReadSlots[i]) continue;
            totalSlots += accountChanges[i].StorageChanges.Length
                       + accountChanges[i].StorageReads.Length;
        }

        if (totalSlots == 0) return;

        using ArrayPoolList<(Address Address, ValueHash256 AccountPath, int SelfDestructIdx, UInt256 Slot)> jobs = new(totalSlots, totalSlots);
        int idx = 0;
        for (int i = 0; i < accountChanges.Count; i++)
        {
            if (!shouldReadSlots[i]) continue;
            ReadOnlyAccountChanges ac = accountChanges[i];
            Address address = ac.Address;
            ValueHash256 accountPath = address.ToAccountPath;
            int selfDestructIdx = selfDestructIdxs[i];
            foreach (ReadOnlySlotChanges slotChanges in ac.StorageChanges)
                jobs[idx++] = (address, accountPath, selfDestructIdx, slotChanges.Key);
            foreach (UInt256 readKey in ac.StorageReads)
                jobs[idx++] = (address, accountPath, selfDestructIdx, readKey);
        }

        // Lazy materialisation: this is the only call site that needs the pool, so chains/forks
        // that never see a BAL never allocate the dedicated reader threads.
        WarmReadPool pool = _warmReadPool.Value;
        int workers = Math.Min(pool.MaxConcurrency, Math.Max(1, idx / 64));

        pool.Run(idx, workers, j =>
        {
            if (_pausePrewarmer) return;
            (Address address, ValueHash256 accountPath, int selfDestructIdx, UInt256 slot) = jobs[j];
            ReadSlotToSink(sink, address, in accountPath, in slot, selfDestructIdx);
        }, parallelOptions.CancellationToken);
    }

    private void ReadSlotToSink(IWorldStateScopeProvider.IAsyncBalReaderSink sink, Address address, in ValueHash256 accountPath, in UInt256 slot, int selfDestructIdx)
    {
        StorageCell cell = new(address, in slot);
        if (!sink.StillNeeded(in cell)) return;
        byte[]? raw = _snapshotBundle.GetSlot(address, in accountPath, in slot, selfDestructIdx);
        sink.OnStorageRead(in cell, raw is null || raw.Length == 0 ? StorageTree.ZeroBytes : raw);
    }

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public int HintSequenceId => _hintSequenceId; // Called by FlatStorageTree

    public bool WarmUpStateTrie(Address address, int sequenceId)
    {
        try
        {
            if (_hintSequenceId != sequenceId || _pausePrewarmer) return false;

            // Note: tree root not changed after writing batch. Also, not cleared. So the result is not correct.
            // this is just for warming up
            _warmupStateTree.WarmUpPath(address.ToAccountPath.Bytes);

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _outstandingWarmups);
        }
    }

    internal void IncrementOutstandingWarmups() => Interlocked.Increment(ref _outstandingWarmups);

    internal void DecrementOutstandingWarmups() => Interlocked.Decrement(ref _outstandingWarmups);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => CreateStorageTreeImpl(address);

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address, Hash256 storageRoot) => CreateStorageTreeImpl(address, storageRoot);

    private FlatStorageTree CreateStorageTreeImpl(Address address)
        => CreateStorageTreeImpl(address, Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash);

    private FlatStorageTree CreateStorageTreeImpl(Address address, Hash256 storageRoot)
    {
        ref FlatStorageTree? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (exists) return storage!;

        storage = new FlatStorageTree(
            this,
            _warmer,
            _snapshotBundle,
            _configuration,
            _concurrencyQuota,
            storageRoot,
            address,
            _preservedStorageTries,
            _logManager);

        return storage;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
    {
        CancelHintBal();
        return new WriteBatch(this, estimatedAccountNum, _logManager.GetClassLogger<WriteBatch>());
    }

    public void Commit(long blockNumber)
    {
        _pausePrewarmer = true;

        // Storage tree commits already happened during WriteBatch.Dispose() via
        // StorageTreeBulkWriteBatch(commit: true). Only the state tree needs committing here.
        _stateTree.Commit();

        StateId newStateId = new(blockNumber, RootHash);
        bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
        if (shouldAddSnapshot && _preservedStorageTries is not null && _storages.Count != 0)
        {
            _preservableStorages ??= new List<FlatStorageTree>(_storages.Count);
            _preservableStorages.AddRange(_storages.Values);
        }

        _storages.Clear();
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
        _lastCommittedStateRoot = newStateId.StateRoot.ToCommitment();
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

                using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
                foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
                {
                    stateSetter.Set(kv.Key, kv.Value);
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
