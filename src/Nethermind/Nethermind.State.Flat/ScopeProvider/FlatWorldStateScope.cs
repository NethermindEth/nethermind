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

public sealed class FlatWorldStateScope : IWorldStateScopeProvider.IScope, ITrieWarmer.IAddressWarmer,
    IWorldStateScopeProvider.ISparseDeltaSink
{
    private readonly SnapshotBundle _snapshotBundle;
    private readonly IFlatCommitTarget _commitTarget;
    private readonly IFlatDbConfig _configuration;
    private readonly ITrieWarmer _warmer;
    private readonly Lazy<WarmReadPool>? _warmReadPool;
    private readonly ILogManager _logManager;
    private readonly bool _isReadOnly;
    private readonly SparseTrieWorker? _sparseTrieWorker;

    private readonly ConcurrencyController _concurrencyQuota;
    private readonly PatriciaTree _warmupStateTree;
    private readonly StateTree _stateTree;
    private readonly Dictionary<AddressAsKey, FlatStorageTree> _storages = [];
    private ConcurrentDictionary<AddressAsKey, FlatStorageTree?>? _hintWarmStorages;

    private readonly Dictionary<AddressAsKey, Account?> _pendingSparseAccounts = new(AddressAsKey.EqualityComparer);
    private readonly HashSet<AddressAsKey> _provisionalSparseAccounts = new(AddressAsKey.EqualityComparer);
    private readonly List<SparseTrieStorageDelta> _pendingSparseStorage = [];
    private SparseTrieBlockHandle? _sparseBlock;
    private WriteBatch? _activeWriteBatch;
    private WriteBatch? _fallbackJournal;
    private Hash256? _sparseComputedRoot;
    private bool _acceptSparseDeltas;
    private bool _sparseEnabled;
    private bool _sparseRootReturned;
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

    public FlatWorldStateScope(
        StateId currentStateId,
        SnapshotBundle snapshotBundle,
        IWorldStateScopeProvider.ICodeDb codeDb,
        IFlatCommitTarget commitTarget,
        IFlatDbConfig configuration,
        ITrieWarmer trieCacheWarmer,
        SparseTrieWorker? sparseTrieWorker,
        ILogManager logManager,
        Lazy<WarmReadPool>? warmReadPool = null,
        bool isReadOnly = false)
    {
        _currentStateId = currentStateId;
        _snapshotBundle = snapshotBundle;
        CodeDb = codeDb;
        _commitTarget = commitTarget;
        _sparseTrieWorker = sparseTrieWorker;
        _sparseEnabled = sparseTrieWorker is not null;
        _configuration = configuration;
        _snapshotBundle.HashAwareTrieReads = configuration.VerifyWithTrie || sparseTrieWorker is not null;

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

        _warmReadPool = warmReadPool;
        _logManager = logManager;
        _warmer = trieCacheWarmer;

        _warmer.OnEnterScope();
        _isReadOnly = isReadOnly;
        StartSparseBlock(currentStateId.StateRoot.ToCommitment());
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;
        CancelHintBal();
        WaitForOutstandingWarmups();
        AbortSparseBlock();
        _fallbackJournal?.Release();
        _fallbackJournal = null;
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

    public Hash256 RootHash => _sparseComputedRoot ?? _stateTree.RootHash;

    public void UpdateRootHash()
    {
        if (_sparseComputedRoot is null || _fallbackJournal is null)
        {
            _stateTree.UpdateRootHash();
            return;
        }

        if (!_sparseRootReturned)
        {
            _sparseRootReturned = true;
            return;
        }

        ReplayPatriciaFallback(disableSparse: true);
    }

    public bool WantsCommittedDeltas => _sparseBlock is not null && _acceptSparseDeltas;

    public void OnCommittedAccount(Address address, Account? account)
    {
        if (_sparseBlock is not null && _acceptSparseDeltas)
        {
            _pendingSparseAccounts[address] = account;
            _provisionalSparseAccounts.Add(address);
        }
    }

    public void OnCommittedStorage(in StorageCell cell, byte[] value)
    {
        if (_sparseBlock is null || !_acceptSparseDeltas)
            return;

        FlatStorageTree storage = CreateStorageTreeImpl(cell.Address);
        _pendingSparseStorage.Add(new SparseTrieStorageDelta(
            cell.Address,
            storage.ParentRootHash,
            cell.Index,
            value));
    }

    public void OnCommitPhaseCompleted(bool isFinal)
    {
        SparseTrieBlockHandle? block = _sparseBlock;
        if (block is null || !_acceptSparseDeltas)
            return;

        if (_pendingSparseAccounts.Count != 0 || _pendingSparseStorage.Count != 0)
        {
            List<SparseTrieAccountDelta> accounts = new(_pendingSparseAccounts.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in _pendingSparseAccounts)
                accounts.Add(new SparseTrieAccountDelta(entry.Key.Value, entry.Value));

            List<SparseTrieStorageDelta> storage = [.. _pendingSparseStorage];
            _pendingSparseAccounts.Clear();
            _pendingSparseStorage.Clear();

            try
            {
                block.EnqueueDelta(new SparseTriePhaseDelta(accounts, storage));
            }
            catch (Exception exception)
            {
                DisableSparse(exception, "Failed to publish a sparse trie execution delta.");
            }
        }

        if (isFinal)
            _acceptSparseDeltas = false;
    }

    internal bool CollectSparseStorageBatches =>
        _sparseBlock is not null && _activeWriteBatch?.UsesSparse == true;

    internal void RegisterSparseStorageBatch(FlatStorageTree.SparseStorageBatch batch)
    {
        WriteBatch? writeBatch = _activeWriteBatch;
        if (writeBatch is null || !writeBatch.UsesSparse)
            throw new InvalidOperationException("Sparse storage batch has no active world-state write batch.");

        writeBatch.RegisterSparseStorageBatch(batch);
    }

    private void StartSparseBlock(Hash256 parentRoot)
    {
        if (!_sparseEnabled || _sparseTrieWorker is null)
            return;

        try
        {
            _sparseBlock = _sparseTrieWorker.BeginBlock(parentRoot, _snapshotBundle);
            _acceptSparseDeltas = true;
        }
        catch (Exception exception)
        {
            _sparseEnabled = false;
            LogSparseFailure("Failed to start sparse trie block processing.", exception);
        }
    }

    private void DisableSparse(Exception exception, string message)
    {
        Metrics.SparseFailures++;
        LogSparseFailure(message, exception);
        _sparseEnabled = false;
        _acceptSparseDeltas = false;
        _pendingSparseAccounts.Clear();
        _provisionalSparseAccounts.Clear();
        _pendingSparseStorage.Clear();
        AbortSparseBlock();
    }

    private void AbortSparseBlock()
    {
        SparseTrieBlockHandle? block = _sparseBlock;
        _sparseBlock = null;
        _acceptSparseDeltas = false;
        if (block is null)
            return;

        try
        {
            block.AbortAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LogSparseFailure("Failed to abort sparse trie block processing.", exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LogSparseFailure(string message, Exception exception)
    {
        ILogger logger = _logManager.GetClassLogger<FlatWorldStateScope>();
        if (logger.IsWarn)
            logger.Warn($"{message} {exception.Message}");
    }

    private void ReplayPatriciaFallback(bool disableSparse)
    {
        WriteBatch? journal = _fallbackJournal;
        if (journal is null)
            return;

        Metrics.SparseFallbacks++;
        AbortSparseBlock();
        journal.ReplayPatricia();
        journal.Release();

        _fallbackJournal = null;
        _sparseComputedRoot = null;
        _sparseRootReturned = false;
        _provisionalSparseAccounts.Clear();
        if (disableSparse)
            _sparseEnabled = false;
    }

    public Account? Get(Address address)
    {
        Account? account = _snapshotBundle.GetAccount(address);

        HintGet(address, account);

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
        _snapshotBundle.SetAccount(address, account);
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

        if (_fallbackJournal is not null)
            ReplayPatriciaFallback(disableSparse: true);

        WriteBatch writeBatch = new(
            this,
            estimatedAccountNum,
            _logManager.GetClassLogger<WriteBatch>(),
            useSparse: _sparseBlock is not null);

        if (Interlocked.CompareExchange(ref _activeWriteBatch, writeBatch, null) is not null)
            throw new InvalidOperationException("A world-state write batch is already active.");

        return writeBatch;
    }

    private void CompleteWriteBatch(WriteBatch writeBatch)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _activeWriteBatch, null, writeBatch), writeBatch))
            throw new InvalidOperationException("The active world-state write batch changed unexpectedly.");
    }

    public void Commit(ulong blockNumber)
    {
        _pausePrewarmer = true;
        bool sparsePrepared = false;

        try
        {
            if (_sparseBlock is not null && _sparseComputedRoot is not null && _fallbackJournal is not null)
            {
                Hash256 sparseRoot = _sparseComputedRoot;
                try
                {
                    _sparseBlock.PrepareCommitAsync().GetAwaiter().GetResult();
                    if (sparseRoot != _currentStateId.StateRoot.ToCommitment() &&
                        (!_snapshotBundle.TryGetChangedStateNode(TreePath.Empty, out TrieNode? rootNode) ||
                         rootNode.Keccak != sparseRoot))
                    {
                        throw new TrieException(
                            $"Sparse trie prepared root node {rootNode?.Keccak?.ToString() ?? "missing"} " +
                            $"instead of {sparseRoot}.");
                    }
                    _stateTree.SetRootHash(sparseRoot, resetObjects: true);
                    _warmupStateTree.SetRootHash(sparseRoot, resetObjects: true);
                    sparsePrepared = true;
                }
                catch (Exception exception)
                {
                    LogSparseFailure("Sparse trie snapshot preparation failed; replaying Patricia.", exception);
                    ReplayPatriciaFallback(disableSparse: true);
                    if (_stateTree.RootHash != sparseRoot)
                    {
                        throw new TrieException(
                            $"Patricia fallback root {_stateTree.RootHash} differs from the validated sparse root {sparseRoot}.",
                            exception);
                    }
                }
            }

            if (!sparsePrepared)
                _stateTree.Commit();

            _storages.Clear();
            _hintWarmStorages?.Clear();

            StateId newStateId = new(blockNumber, RootHash);
            bool shouldAddSnapshot = !_isReadOnly && _currentStateId != newStateId;
            (Snapshot? newSnapshot, TransientResource? cachedResource) =
                _snapshotBundle.CollectAndApplySnapshot(_currentStateId, newStateId, shouldAddSnapshot);

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

            if (sparsePrepared)
            {
                SparseTrieBlockHandle block = _sparseBlock!;
                try
                {
                    block.AcceptAsync().GetAwaiter().GetResult();
                    _sparseBlock = null;
                }
                catch (Exception exception)
                {
                    DisableSparse(exception, "Failed to retain the accepted sparse trie.");
                }
            }

            _fallbackJournal?.Release();
            _fallbackJournal = null;
            _sparseComputedRoot = null;
            _sparseRootReturned = false;
            _provisionalSparseAccounts.Clear();
            StartSparseBlock(newStateId.StateRoot.ToCommitment());
        }
        catch
        {
            AbortSparseBlock();
            throw;
        }
        finally
        {
            _pausePrewarmer = false;
        }
    }

    // Largely same logic as the the one for TrieStoreScopeProvider, but more confusing when deduplicated.
    // So I just leave it here.
    private sealed class WriteBatch(
        FlatWorldStateScope scope,
        int estimatedAccountCount,
        ILogger logger,
        bool useSparse
    ) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = new(estimatedAccountCount);
        private readonly ConcurrentQueue<(AddressAsKey, Hash256)> _dirtyStorageTree = new();
        private readonly ConcurrentQueue<FlatStorageTree.SparseStorageBatch> _sparseStorageBatches = new();
        private bool _disposed;
        private bool _released;

        public bool UsesSparse { get; } = useSparse;

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

        public void RegisterSparseStorageBatch(FlatStorageTree.SparseStorageBatch batch) =>
            _sparseStorageBatches.Enqueue(batch);

        private void MarkDirty(AddressAsKey address, Hash256 storageTreeRootHash) =>
            _dirtyStorageTree.Enqueue((address, storageTreeRootHash));

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (UsesSparse)
                    FinishSparse();
                else
                {
                    ApplyDirtyStorageRoots(sparseMode: false);
                    ApplyPatriciaAccounts();
                }
            }
            finally
            {
                scope.CompleteWriteBatch(this);
                Interlocked.Increment(ref scope._hintSequenceId);
                if (!ReferenceEquals(scope._fallbackJournal, this))
                    Release();
            }
        }

        private void FinishSparse()
        {
            SparseTrieBlockHandle block = scope._sparseBlock
                ?? throw new InvalidOperationException("Sparse write batch lost its block session.");

            List<FlatStorageTree.SparseStorageBatch> storageBatches = [.. _sparseStorageBatches];
            foreach (FlatStorageTree.SparseStorageBatch storageBatch in storageBatches)
            {
                AddressAsKey address = storageBatch.FinalState.Address;
                if (!_dirtyAccounts.ContainsKey(address))
                    _dirtyAccounts[address] = scope.Get(address.Value);
            }
            foreach (AddressAsKey address in scope._provisionalSparseAccounts)
            {
                if (!_dirtyAccounts.ContainsKey(address))
                    _dirtyAccounts[address] = scope.Get(address.Value);
            }

            List<SparseTrieFinalStorageBatch> finalStorage = new(storageBatches.Count);
            foreach (FlatStorageTree.SparseStorageBatch storageBatch in storageBatches)
                finalStorage.Add(storageBatch.FinalState);

            List<SparseTrieFinalAccount> finalAccounts = new(_dirtyAccounts.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in _dirtyAccounts)
                finalAccounts.Add(new SparseTrieFinalAccount(entry.Key.Value, entry.Value));

            try
            {
                long waitStartedAt = Stopwatch.GetTimestamp();
                SparseTrieBlockResult result;
                try
                {
                    result = block
                        .FinishAsync(new SparseTrieFinalState(finalStorage, finalAccounts))
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    Metrics.SparseForegroundWaitTime.Observe(Stopwatch.GetTimestamp() - waitStartedAt);
                }

                foreach (FlatStorageTree.SparseStorageBatch storageBatch in storageBatches)
                {
                    AddressAsKey address = storageBatch.FinalState.Address;
                    if (!result.StorageRoots.TryGetValue(address, out Hash256? storageRoot) || storageRoot is null)
                        throw new TrieException($"Sparse result omitted storage root for {address}.");

                    storageBatch.ApplySparseRoot(storageRoot);
                }

                ApplyDirtyStorageRoots(sparseMode: true);
                scope._sparseComputedRoot = result.StateRoot;
                scope._sparseRootReturned = false;
                scope._fallbackJournal = this;
            }
            catch (Exception exception)
            {
                scope.DisableSparse(exception, "Sparse trie finalization failed; replaying Patricia.");
                Metrics.SparseFallbacks++;
                ReplayPatricia();
                scope._sparseComputedRoot = null;
                scope._sparseRootReturned = false;
            }
        }

        public void ReplayPatricia()
        {
            bool previousHashAwareReads = scope._snapshotBundle.HashAwareTrieReads;
            scope._snapshotBundle.HashAwareTrieReads = true;
            try
            {
                foreach (FlatStorageTree.SparseStorageBatch storageBatch in _sparseStorageBatches)
                    storageBatch.ReplayFallback();

                ApplyDirtyStorageRoots(sparseMode: false);
                ApplyPatriciaAccounts();
                scope._stateTree.UpdateRootHash();
            }
            finally
            {
                scope._snapshotBundle.HashAwareTrieReads = previousHashAwareReads;
            }
        }

        private void ApplyPatriciaAccounts()
        {
            using StateTree.StateTreeBulkSetter stateSetter = scope._stateTree.BeginSet(_dirtyAccounts.Count);
            foreach (KeyValuePair<AddressAsKey, Account?> entry in _dirtyAccounts)
                stateSetter.Set(entry.Key, entry.Value);
        }

        private void ApplyDirtyStorageRoots(bool sparseMode)
        {
            while (_dirtyStorageTree.TryDequeue(out (AddressAsKey, Hash256) entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                if (!_dirtyAccounts.TryGetValue(key, out Account? account))
                    account = scope.Get(key);

                if (account is null)
                {
                    if (!sparseMode && storageRoot != Keccak.EmptyTreeHash)
                    {
                        using IWorldStateScopeProvider.IStorageWriteBatch writeBatch =
                            CreateStorageWriteBatch(key.Value, 0);
                        writeBatch.Clear();
                    }
                    continue;
                }

                account = account.WithChangedStorageRoot(storageRoot);
                _dirtyAccounts[key] = account;
                scope._snapshotBundle.SetAccount(key, account);

                Address address = key.Value;
                OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
                if (logger.IsTrace)
                    Trace(address, storageRoot, account);
            }
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            _dirtyAccounts.Clear();
            while (_dirtyStorageTree.TryDequeue(out _)) { }
            while (_sparseStorageBatches.TryDequeue(out _)) { }
            OnAccountUpdated = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Trace(Address address, Hash256 storageRoot, Account? account) =>
            logger.Trace($"Update {address} S {account?.StorageRoot} -> {storageRoot}");
    }

}
