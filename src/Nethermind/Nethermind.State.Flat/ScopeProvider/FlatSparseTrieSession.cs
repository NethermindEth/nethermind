// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Scope-owned sparse root calculation: consumes transaction-committed account and storage
/// updates on a background owner, joins storage root plans under one shared hash frontier,
/// reconciles the final write batch, and publishes every staged node into the bundle at commit.
/// </summary>
/// <remarks>
/// Single mutable owner per trie: storage prefetch and committed settlement serialize on the
/// account's trie, then the final barrier drains them before a flush worker borrows that trie.
/// The state trie is only touched by one CAS-elected owner: sparse-native prefetch, committed
/// account/storage streaming, and the final write batch hand ownership through the same gate. Any
/// final calculation or publication exception poisons the session; every later mutation,
/// calculation, and publication entry point throws, so a poisoned scope can never commit a root
/// that may not match the applied writes.
/// <see cref="RootHash"/> stays readable and always holds the last successfully calculated root
/// (or the anchor).
/// </remarks>
internal sealed class FlatSparseTrieSession : IDisposable
{
    private static readonly AccountDecoder _accountDecoder = new();
    internal const int ConcurrentRootBatchSize = 256;
    private const int ConcurrentAccountQueueCapacity = 2048;
    internal const int ConcurrentStorageBatchSize = 64;
    private const int ConcurrentStorageQueueCapacity = 4096;

    private readonly SnapshotBundle _bundle;
    private readonly FlatTrieNodeReaderContext _readerContext;
    private readonly ILogger _logger;
    private readonly SparseTrieRootWorker? _rootWorker;
    private readonly ConcurrentQueue<PreparedStorageJob> _pendingStorageJobs = new();
    private readonly List<FlatStorageTree> _changedStorageTrees = [];

    // The session's warm storage tries, one per account touched in this scope's lifetime. A trie
    // is borrowed out (removed) while its account's job runs, rolled back in at publication, and
    // dropped on account deletion. It persists across the scope's block commits, so a multi-block
    // branch reuses warm tries directly; at the scope boundary it is handed to the cross-scope
    // retention cache. Parallel storage workers borrow disjoint entries without a global lock.
    private ConcurrentDictionary<ValueHash256, SparseTrie> _storageTries;

    // When false (no retention cache), tries are disposed at each commit so a multi-block scope
    // never accumulates a cross-block working set; when true they roll forward for reuse.
    private readonly bool _retentionEnabled;
    private readonly bool _concurrentRootEnabled;

    private readonly ConcurrentQueue<ValueHash256> _pendingStatePrefetch = new();
    private readonly MpmcRingBuffer<CommittedAccountUpdate>? _pendingCommittedAccounts;
    private readonly ConcurrentQueue<CommittedAccountUpdate> _overflowCommittedAccounts = [];
    private readonly MpmcRingBuffer<CommittedStorageUpdate>? _pendingCommittedStorage;
    private readonly ConcurrentQueue<CommittedStorageUpdate> _overflowCommittedStorage = [];
    private readonly ConcurrentDictionary<ValueHash256, CommittedStorageState> _committedStorageStates = [];
    private readonly PooledDictionary<ValueHash256, CommittedAccountUpdate> _speculativeAccounts = [];
    private int _pendingCommittedAccountCount;
    private int _committedAccountProducers;
    private int _pendingCommittedStorageCount;
    private int _committedStorageProducers;
    private long _nextCommittedAccountSequence;
    private long _nextCommittedStorageSequence;
    private long _completedCommittedAccountSequence;
    private long _completedCommittedStorageSequence;
    private long _drainedCommittedAccountSequence;
    private long _drainedCommittedStorageSequence;
    private int _stateWorkOwner;
    private int _stateWorkerScheduled;
    private SparseTrie? _stateTrie;
    private Hash256 _rootHash;
    private bool _stateDirty;
    private volatile bool _concurrentRootDisabled;
    private volatile bool _poisoned;
    private volatile bool _disposed;
    private bool _generationExtracted;
    private int _concurrentRootCalculationCount;
    private int _concurrentStorageRootCalculationCount;
    private int _concurrentStorageTrieReuseCount;
    private int _updatesSinceConcurrentRootCalculation;

    internal Action? OnConcurrentStateWorkerAcquired;

    /// <param name="checkedOut">A warm generation checked out of the retention cache on an exact
    /// parent-root match, or null for a cold scope. The session reuses and rolls it forward across
    /// its commits and hands the result back through <see cref="ExtractGeneration"/> at the scope
    /// boundary.</param>
    /// <param name="retentionEnabled">When true the scope has a retention cache: tries are kept
    /// warm across commits and offered to the cache at the scope boundary. When false they are
    /// disposed at each commit, so a multi-block scope holds only the current block's working set.</param>
    public FlatSparseTrieSession(
        SnapshotBundle bundle,
        Hash256 anchorStateRoot,
        ILogger logger,
        RetainedGeneration? checkedOut = null,
        bool retentionEnabled = false,
        SparseTrieRootWorker? rootWorker = null)
    {
        _bundle = bundle;
        _logger = logger;
        _rootWorker = rootWorker;
        _rootHash = anchorStateRoot;
        _retentionEnabled = retentionEnabled;
        // The provider decides whether to hand out a root worker; the session follows what it was
        // given rather than re-reading the global switch, so a caller can drive the streamed path
        // (tests, experiments) independently of the default.
        _concurrentRootEnabled = retentionEnabled && rootWorker is not null;
        _readerContext = checkedOut?.ReaderContext ?? new FlatTrieNodeReaderContext(bundle);
        _readerContext.Rebind(bundle);
        if (_concurrentRootEnabled)
        {
            _pendingCommittedAccounts =
                new MpmcRingBuffer<CommittedAccountUpdate>(
                    ConcurrentAccountQueueCapacity,
                    usesArrayPool: true);
            _pendingCommittedStorage =
                new MpmcRingBuffer<CommittedStorageUpdate>(
                    ConcurrentStorageQueueCapacity,
                    usesArrayPool: true);
        }

        _storageTries = checkedOut?.StorageTries ?? [];
        if (checkedOut is not null)
        {
            _stateTrie = checkedOut.StateTrie;
            if (checkedOut.ReaderContext is null)
            {
                _stateTrie.RebindSource(new FlatTrieNodeReader(_readerContext, address: null));
                foreach (KeyValuePair<ValueHash256, SparseTrie> kv in _storageTries)
                {
                    kv.Value.RebindSource(new FlatTrieNodeReader(_readerContext, kv.Key.ToCommitment()));
                }
            }
        }
    }

    /// <summary>A sealed storage write batch: the final sparse delta for one account.</summary>
    internal readonly record struct StorageJob(
        FlatStorageTree Tree,
        ArrayPoolList<SparseTrieUpdate> Updates,
        bool HasClear,
        Action<Address, Hash256> OnRootUpdated);

    private readonly record struct PreparedStorageJob(
        StorageJob Job,
        SparseTrie.RootCalculation Calculation);

    private readonly record struct CommittedAccountUpdate(
        Address Address,
        ValueHash256 Key,
        Account? Account,
        long Sequence,
        long StorageVersion);

    private sealed class CommittedStorageState(
        Address address,
        in ValueHash256 addressHash,
        in ValueHash256 anchorRoot)
    {
        public readonly Address Address = address;
        public readonly ValueHash256 AddressHash = addressHash;
        public Hash256 Root = anchorRoot.ToCommitment();
        public long CurrentVersion;
        public long SettledVersion;
        public bool HasClear;
    }

    private readonly record struct CommittedStorageUpdate(
        CommittedStorageState State,
        ValueHash256 Key,
        byte[]? Value,
        long Version,
        long Sequence,
        bool IsClear);

    private readonly record struct PreparedCommittedStorage(
        CommittedStorageState State,
        SparseTrie Trie,
        long Version,
        SparseTrie.RootCalculation Calculation,
        bool NeedsCalculation);

    private readonly struct CommittedStorageUpdateComparer : IComparer<CommittedStorageUpdate>
    {
        public int Compare(CommittedStorageUpdate x, CommittedStorageUpdate y)
        {
            int addressComparison = x.State.AddressHash.CompareTo(y.State.AddressHash);
            return addressComparison != 0 ? addressComparison : x.Version.CompareTo(y.Version);
        }
    }

    private readonly struct CommittedStorageUpdateKeyComparer : IComparer<CommittedStorageUpdate>
    {
        public int Compare(CommittedStorageUpdate x, CommittedStorageUpdate y)
        {
            int keyComparison = x.Key.CompareTo(y.Key);
            return keyComparison != 0 ? keyComparison : x.Version.CompareTo(y.Version);
        }
    }

    /// <summary>The parent state root before calculation, the calculated root afterward.</summary>
    public Hash256 RootHash => _rootHash;

    /// <summary>Warm tries currently held (state trie plus per-account storage tries); zero after a
    /// commit when retention is disabled. Test-observable to pin the per-commit disposal policy.</summary>
    internal int RetainedTrieCount => (_stateTrie is null ? 0 : 1) + _storageTries.Count;

    internal int ConcurrentRootCalculationCount => Volatile.Read(ref _concurrentRootCalculationCount);

    internal int ConcurrentStorageRootCalculationCount =>
        Volatile.Read(ref _concurrentStorageRootCalculationCount);

    internal int ConcurrentStorageTrieReuseCount =>
        Volatile.Read(ref _concurrentStorageTrieReuseCount);

    /// <summary>
    /// Applies a sealed storage delta on the parallel flush worker that produced it and queues its
    /// root plan for the shared hash frontier.
    /// </summary>
    public void PrepareStorageJob(in StorageJob job)
    {
        try
        {
            GuardPoisoned();
            bool clearAlreadyApplied = TryAdoptSettledStorageTrie(job);
            SparseTrie trie = job.Tree.ApplySparseJob(job, clearAlreadyApplied);
            _pendingStorageJobs.Enqueue(new PreparedStorageJob(job, trie.PrepareRootCalculation()));
        }
        catch
        {
            job.Tree.DropSparseTrie();
            job.Updates.Dispose();
            _poisoned = true;
            throw;
        }
    }

    private bool TryAdoptSettledStorageTrie(in StorageJob job)
    {
        ValueHash256 addressHash = job.Tree.AddressHash.ValueHash256;
        if (!_committedStorageStates.TryRemove(addressHash, out CommittedStorageState? state))
        {
            return false;
        }

        lock (state)
        {
            long currentVersion = Volatile.Read(ref state.CurrentVersion);
            long settledVersion = Volatile.Read(ref state.SettledVersion);
            if (settledVersion == currentVersion
                && state.HasClear == job.HasClear
                && _storageTries.TryRemove(addressHash, out SparseTrie? trie))
            {
                job.Tree.AdoptSparseTrie(trie);
                Interlocked.Increment(ref _concurrentStorageTrieReuseCount);
                return state.HasClear;
            }

            _storageTries.TryRemove(addressHash, out SparseTrie? stale);
            DisposeStorageTrie(stale);
            return false;
        }
    }

    /// <summary>
    /// Returns the warm storage trie retained for <paramref name="addressHash"/>, rebound to this
    /// scope's reader, or a fresh trie anchored at <paramref name="anchorRoot"/>. A retained trie
    /// whose root does not match the account's committed storage root (e.g. after a clear) is
    /// dropped rather than reused. Runs on the storage-phase worker that owns this account's job.
    /// </summary>
    public SparseTrie AdoptOrCreateStorageTrie(Hash256 addressHash, in ValueHash256 anchorRoot, int hint)
    {
        _storageTries.TryRemove(addressHash.ValueHash256, out SparseTrie? retained);

        if (retained is not null)
        {
            if (retained.RootHash == anchorRoot)
            {
                return retained;
            }

            retained.Dispose();
        }

        return new SparseTrie(new FlatTrieNodeReader(_readerContext, addressHash), anchorRoot, hint);
    }

    /// <summary>Reveals one account path directly into the retained state-trie arena.</summary>
    public void PrefetchState(in ValueHash256 key)
    {
        GuardPoisoned();
        if (Interlocked.CompareExchange(ref _stateWorkOwner, 1, 0) == 0)
        {
            try
            {
                GetOrCreateStateTrie().Prefetch(in key);
                DrainStatePrefetchQueueOwned();
            }
            finally
            {
                ReleaseStateOwner();
            }

            return;
        }

        _pendingStatePrefetch.Enqueue(key);
        EnsureStateWorkerScheduled();
    }

    /// <summary>Reveals account paths as one batched sparse-trie traversal.</summary>
    public void PrefetchState(ReadOnlySpan<ValueHash256> keys)
    {
        GuardPoisoned();
        if (keys.IsEmpty) return;

        if (Interlocked.CompareExchange(ref _stateWorkOwner, 1, 0) == 0)
        {
            try
            {
                GetOrCreateStateTrie().Prefetch(keys);
                DrainStatePrefetchQueueOwned();
            }
            finally
            {
                ReleaseStateOwner();
            }

            return;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            _pendingStatePrefetch.Enqueue(keys[i]);
        }

        EnsureStateWorkerScheduled();
    }

    private SparseTrie GetOrCreateStateTrie() =>
        _stateTrie ??= new SparseTrie(new FlatTrieNodeReader(_readerContext, address: null), _rootHash.ValueHash256);

    /// <summary>
    /// Queues a transaction-committed account value for the single background state owner. The
    /// block-final write batch still reapplies or rolls back every streamed value before commit.
    /// </summary>
    public void EnqueueCommittedAccount(Address address, Account? account)
    {
        if (!_concurrentRootEnabled || _concurrentRootDisabled || _disposed || _poisoned) return;

        Interlocked.Increment(ref _committedAccountProducers);
        try
        {
            if (_concurrentRootDisabled || _disposed || _poisoned) return;

            ValueHash256 key = address.ToAccountPath;
            long sequence = Interlocked.Increment(ref _nextCommittedAccountSequence);
            long storageVersion = _committedStorageStates.TryGetValue(key, out CommittedStorageState? storageState)
                ? Volatile.Read(ref storageState.CurrentVersion)
                : 0;
            Interlocked.Increment(ref _pendingCommittedAccountCount);
            try
            {
                CommittedAccountUpdate update = new(address, key, account, sequence, storageVersion);
                if (!_pendingCommittedAccounts!.TryEnqueue(in update))
                {
                    _overflowCommittedAccounts.Enqueue(update);
                }
            }
            catch
            {
                Interlocked.Decrement(ref _pendingCommittedAccountCount);
                throw;
            }

            if (_concurrentRootDisabled || _disposed)
            {
                DiscardPendingCommittedAccounts();
                return;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _committedAccountProducers);
        }
    }

    public void EnqueueCommittedStorage(
        Address address,
        in ValueHash256 key,
        byte[] value) =>
        EnqueueCommittedStorage(address, in key, value, isClear: false);

    public void EnqueueCommittedStorageClear(Address address)
    {
        ValueHash256 key = default;
        EnqueueCommittedStorage(address, in key, value: null, isClear: true);
    }

    private void EnqueueCommittedStorage(
        Address address,
        in ValueHash256 key,
        byte[]? value,
        bool isClear)
    {
        if (!_concurrentRootEnabled || _concurrentRootDisabled || _disposed || _poisoned) return;

        Interlocked.Increment(ref _committedStorageProducers);
        try
        {
            if (_concurrentRootDisabled || _disposed || _poisoned) return;

            ValueHash256 addressHash = address.ToAccountPath;
            CommittedStorageState state = _committedStorageStates.GetOrAdd(
                addressHash,
                static (_, args) =>
                {
                    Hash256 anchorRoot =
                        args.Session._bundle.GetAccount(args.Address)?.StorageRoot
                        ?? Keccak.EmptyTreeHash;
                    return new CommittedStorageState(
                        args.Address,
                        args.AddressHash,
                        anchorRoot.ValueHash256);
                },
                (Session: this, Address: address, AddressHash: addressHash));
            long version = Interlocked.Increment(ref state.CurrentVersion);
            long sequence = Interlocked.Increment(ref _nextCommittedStorageSequence);
            Interlocked.Increment(ref _pendingCommittedStorageCount);
            try
            {
                CommittedStorageUpdate update = new(state, key, value, version, sequence, isClear);
                if (!_pendingCommittedStorage!.TryEnqueue(in update))
                {
                    _overflowCommittedStorage.Enqueue(update);
                }
            }
            catch
            {
                Interlocked.Decrement(ref _pendingCommittedStorageCount);
                throw;
            }

            if (_concurrentRootDisabled || _disposed)
            {
                DiscardPendingCommittedStorage();
                return;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _committedStorageProducers);
        }
    }

    public void CompleteCommittedStateRound()
    {
        if (!_concurrentRootEnabled || _concurrentRootDisabled || _disposed || _poisoned) return;

        WaitForCommittedProducers();
        CaptureCompletedSequences();
        EnsureStateWorkerScheduled();
    }

    private void WaitForCommittedProducers()
    {
        SpinWait spinWait = new();
        while (Volatile.Read(ref _committedAccountProducers) != 0
            || Volatile.Read(ref _committedStorageProducers) != 0)
        {
            // Never escalate to Thread.Sleep(1): the producers finish in microseconds, while one
            // 1 ms sleep is a measurable slice of the whole block budget.
            spinWait.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void CaptureCompletedSequences()
    {
        Volatile.Write(
            ref _completedCommittedStorageSequence,
            Volatile.Read(ref _nextCommittedStorageSequence));
        Volatile.Write(
            ref _completedCommittedAccountSequence,
            Volatile.Read(ref _nextCommittedAccountSequence));
    }

    private void EnsureStateWorkerScheduled()
    {
        if (_disposed || !HasSchedulableStateWork()
            || Interlocked.CompareExchange(ref _stateWorkerScheduled, 1, 0) != 0)
        {
            return;
        }

        bool scheduled = _rootWorker?.TrySchedule(this)
            ?? ThreadPool.UnsafeQueueUserWorkItem(
                static session => session.RunStateWorker(),
                this,
                preferLocal: false);
        if (!scheduled)
        {
            Volatile.Write(ref _stateWorkerScheduled, 0);
        }
    }

    private bool HasSchedulableStateWork() =>
        !_pendingStatePrefetch.IsEmpty
        || (!_concurrentRootDisabled
            && (Volatile.Read(ref _completedCommittedAccountSequence)
                    - Volatile.Read(ref _drainedCommittedAccountSequence)
                    >= ConcurrentRootBatchSize
                || Volatile.Read(ref _completedCommittedStorageSequence)
                    - Volatile.Read(ref _drainedCommittedStorageSequence)
                    >= ConcurrentStorageBatchSize));

    internal void RunStateWorker()
    {
        if (Interlocked.CompareExchange(ref _stateWorkOwner, 1, 0) != 0)
        {
            Volatile.Write(ref _stateWorkerScheduled, 0);
            if (Volatile.Read(ref _stateWorkOwner) == 0) EnsureStateWorkerScheduled();
            return;
        }

        try
        {
            OnConcurrentStateWorkerAcquired?.Invoke();
            if (_disposed)
            {
                DiscardPendingStateWorkOwned();
                return;
            }

            DrainStatePrefetchQueueOwned();
            long completedAccountSequence =
                Volatile.Read(ref _completedCommittedAccountSequence);
            long completedStorageSequence =
                Volatile.Read(ref _completedCommittedStorageSequence);
            int applied = DrainCommittedStorageOwned(completedStorageSequence);
            applied += DrainCommittedAccountsOwned(completedAccountSequence);
            DrainStatePrefetchQueueOwned();

            _updatesSinceConcurrentRootCalculation += applied;
            if (_updatesSinceConcurrentRootCalculation >= ConcurrentRootBatchSize)
            {
                if (_stateTrie!.IsDirty)
                {
                    _stateTrie.CalculateRoot(canBeParallel: false);
                    _concurrentRootCalculationCount++;
                }

                _updatesSinceConcurrentRootCalculation = 0;
            }
        }
        catch (Exception ex)
        {
            DisableConcurrentRootOwned(ex);
        }
        finally
        {
            Volatile.Write(ref _stateWorkOwner, 0);
            Volatile.Write(ref _stateWorkerScheduled, 0);
            EnsureStateWorkerScheduled();
        }
    }

    private int DrainCommittedStorageOwned(long completedSequence)
    {
        long drainedSequence = Volatile.Read(ref _drainedCommittedStorageSequence);
        if (_concurrentRootDisabled || completedSequence <= drainedSequence)
        {
            return 0;
        }

        int count = (int)Math.Min(
            Volatile.Read(ref _pendingCommittedStorageCount),
            completedSequence - drainedSequence);
        using ArrayPoolList<CommittedStorageUpdate> pending = new(count);
        while (TryDequeueCommittedStorage(completedSequence, out CommittedStorageUpdate update))
        {
            pending.Add(update);
            Interlocked.Decrement(ref _pendingCommittedStorageCount);
        }

        Volatile.Write(ref _drainedCommittedStorageSequence, completedSequence);
        if (pending.Count == 0) return 0;

        pending.AsSpan().Sort(default(CommittedStorageUpdateComparer));
        using ArrayPoolList<PreparedCommittedStorage> prepared =
            new(Math.Min(pending.Count, ConcurrentStorageBatchSize));
        try
        {
            int start = 0;
            while (start < pending.Count)
            {
                CommittedStorageState state = pending[start].State;
                int end = start + 1;
                while (end < pending.Count && pending[end].State.AddressHash == state.AddressHash)
                {
                    end++;
                }

                PreparedCommittedStorage item = PrepareCommittedStorage(
                    state,
                    pending.AsSpan().Slice(start, end - start));
                try
                {
                    prepared.Add(item);
                }
                catch
                {
                    ReleasePreparedStorage(item);
                    throw;
                }

                start = end;
            }

            CalculateCommittedStorageRoots(prepared.AsSpan());

            int appliedAccounts = 0;
            for (int i = 0; i < prepared.Count; i++)
            {
                PreparedCommittedStorage item = prepared[i];
                item.State.Root = item.Trie.RootHash.ToCommitment();
                Volatile.Write(ref item.State.SettledVersion, item.Version);
                appliedAccounts += ApplySettledStorageRootOwned(item.State);
            }

            return appliedAccounts;
        }
        finally
        {
            for (int i = prepared.Count - 1; i >= 0; i--)
            {
                ReleasePreparedStorage(prepared[i]);
            }
        }
    }

    private int DrainCommittedStorageOwned() =>
        DrainCommittedStorageOwned(Volatile.Read(ref _completedCommittedStorageSequence));

    private static void ReleasePreparedStorage(in PreparedCommittedStorage prepared)
    {
        Monitor.Exit(prepared.Trie);
        Monitor.Exit(prepared.State);
    }

    private PreparedCommittedStorage PrepareCommittedStorage(
        CommittedStorageState state,
        Span<CommittedStorageUpdate> pending)
    {
        Monitor.Enter(state);
        SparseTrie? trie = null;
        try
        {
            long settledVersion = pending[^1].Version;
            int firstUpdate = 0;
            for (int i = pending.Length - 1; i >= 0; i--)
            {
                if (pending[i].IsClear)
                {
                    firstUpdate = i + 1;
                    state.HasClear = true;
                    _storageTries.TryRemove(state.AddressHash, out SparseTrie? oldTrie);
                    DisposeStorageTrie(oldTrie);
                    state.Root = Keccak.EmptyTreeHash;
                    break;
                }
            }

            trie = GetOrCreatePrefetchStorageTrie(
                state.AddressHash,
                state.Root.ValueHash256);
            Monitor.Enter(trie);
            Span<CommittedStorageUpdate> writes = pending[firstUpdate..];
            writes.Sort(default(CommittedStorageUpdateKeyComparer));
            using ArrayPoolListRef<SparseTrieUpdate> updates = new(writes.Length);
            int writeIndex = 0;
            while (writeIndex < writes.Length)
            {
                int nextKey = writeIndex + 1;
                while (nextKey < writes.Length && writes[nextKey].Key == writes[writeIndex].Key)
                {
                    nextKey++;
                }

                ref readonly CommittedStorageUpdate update = ref writes[nextKey - 1];
                ValueHash256 key = update.Key;
                PatriciaTree.BulkSetEntry entry =
                    StorageTree.CreateBulkSetEntry(in key, update.Value);
                updates.Add(new SparseTrieUpdate(entry.Path, entry.Value));
                writeIndex = nextKey;
            }

            if (updates.Count > 0) trie.Apply(updates.AsSpan());
            return new PreparedCommittedStorage(
                state,
                trie,
                settledVersion,
                updates.Count > 0 ? trie.PrepareRootCalculation() : default,
                NeedsCalculation: updates.Count > 0);
        }
        catch
        {
            if (trie is not null && Monitor.IsEntered(trie)) Monitor.Exit(trie);
            Monitor.Exit(state);
            throw;
        }
    }

    private void CalculateCommittedStorageRoots(ReadOnlySpan<PreparedCommittedStorage> prepared)
    {
        int calculationCount = 0;
        for (int i = 0; i < prepared.Length; i++)
        {
            if (prepared[i].NeedsCalculation) calculationCount++;
        }

        if (calculationCount == 0) return;

        SparseTrie.RootCalculation[] calculations =
            SafeArrayPool<SparseTrie.RootCalculation>.Shared.Rent(calculationCount);
        try
        {
            int calculationIndex = 0;
            for (int i = 0; i < prepared.Length; i++)
            {
                if (prepared[i].NeedsCalculation)
                {
                    calculations[calculationIndex++] = prepared[i].Calculation;
                }
            }

            SparseTrie.CalculateRoots(calculations, calculationCount);
            Interlocked.Add(ref _concurrentStorageRootCalculationCount, calculationCount);
        }
        finally
        {
            calculations.AsSpan(0, calculationCount).Clear();
            SafeArrayPool<SparseTrie.RootCalculation>.Shared.Return(calculations);
        }
    }

    private int ApplySettledStorageRootOwned(CommittedStorageState state)
    {
        long settledVersion = Volatile.Read(ref state.SettledVersion);
        if (_speculativeAccounts.TryGetValue(
                state.AddressHash,
                out CommittedAccountUpdate existing))
        {
            if (existing.Account is null || existing.StorageVersion > settledVersion) return 0;

            Account settledAccount = existing.Account.WithChangedStorageRoot(state.Root);
            if (settledAccount == existing.Account && existing.StorageVersion == settledVersion)
            {
                return 0;
            }

            CommittedAccountUpdate settled = existing with
            {
                Account = settledAccount,
                StorageVersion = settledVersion
            };
            ApplyAccountUpdate(settled);
            _speculativeAccounts[state.AddressHash] = settled;
            return 1;
        }

        Account? parentAccount = _bundle.GetAccount(state.Address);
        if (parentAccount is null) return 0;

        CommittedAccountUpdate synthetic = new(
            state.Address,
            state.AddressHash,
            parentAccount.WithChangedStorageRoot(state.Root),
            Volatile.Read(ref _nextCommittedAccountSequence),
            settledVersion);
        ApplyAccountUpdate(synthetic);
        _speculativeAccounts[state.AddressHash] = synthetic;
        return 1;
    }

    private void DrainStatePrefetchQueueOwned()
    {
        int count = _pendingStatePrefetch.Count;
        if (count == 0) return;

        using ArrayPoolList<ValueHash256> keys = new(count);
        while (_pendingStatePrefetch.TryDequeue(out ValueHash256 key))
        {
            keys.Add(key);
        }

        if (keys.Count > 0)
        {
            GetOrCreateStateTrie().Prefetch(keys.AsSpan());
        }
    }

    private int DrainCommittedAccountsOwned(long completedSequence)
    {
        long drainedSequence = Volatile.Read(ref _drainedCommittedAccountSequence);
        if (_concurrentRootDisabled || completedSequence <= drainedSequence)
        {
            return 0;
        }

        int count = (int)Math.Min(
            Volatile.Read(ref _pendingCommittedAccountCount),
            completedSequence - drainedSequence);
        using PooledDictionary<ValueHash256, CommittedAccountUpdate> latest = new(count);
        while (TryDequeueCommittedAccount(completedSequence, out CommittedAccountUpdate update))
        {
            if (update.Account is not null
                && update.StorageVersion != 0
                && _committedStorageStates.TryGetValue(update.Key, out CommittedStorageState? storageState)
                && Volatile.Read(ref storageState.SettledVersion) >= update.StorageVersion)
            {
                update = update with
                {
                    Account = update.Account.WithChangedStorageRoot(storageState.Root)
                };
            }

            if (!latest.TryGetValue(update.Key, out CommittedAccountUpdate existing)
                || update.Sequence > existing.Sequence)
            {
                latest[update.Key] = update;
            }

            Interlocked.Decrement(ref _pendingCommittedAccountCount);
        }

        Volatile.Write(ref _drainedCommittedAccountSequence, completedSequence);
        if (latest.Count == 0) return 0;

        ApplyAccountUpdates(latest);
        int applied = latest.Count;
        foreach (KeyValuePair<ValueHash256, CommittedAccountUpdate> kv in latest)
        {
            if (!_speculativeAccounts.TryGetValue(kv.Key, out CommittedAccountUpdate existing)
                || kv.Value.Sequence > existing.Sequence)
            {
                _speculativeAccounts[kv.Key] = kv.Value;
            }
        }

        return applied;
    }

    private int DrainCommittedAccountsOwned() =>
        DrainCommittedAccountsOwned(Volatile.Read(ref _completedCommittedAccountSequence));

    private void ApplyAccountUpdates(PooledDictionary<ValueHash256, CommittedAccountUpdate> accounts)
    {
        using ArrayPoolList<SparseTrieUpdate> updates = new(accounts.Count);
        foreach (KeyValuePair<ValueHash256, CommittedAccountUpdate> kv in accounts)
        {
            AddAccountUpdate(updates, kv.Key, kv.Value.Account);
        }

        ApplyAccountUpdates(updates);
    }

    private void ApplyAccountUpdates(ArrayPoolList<SparseTrieUpdate> updates)
    {
        GetOrCreateStateTrie().Apply(updates.AsSpan());
        _stateDirty = true;
    }

    private void ApplyAccountUpdate(in CommittedAccountUpdate account)
    {
        using ArrayPoolList<SparseTrieUpdate> updates = new(1);
        AddAccountUpdate(updates, account.Key, account.Account);
        ApplyAccountUpdates(updates);
    }

    private static void AddAccountUpdate(
        ArrayPoolList<SparseTrieUpdate> updates,
        in ValueHash256 key,
        Account? account)
    {
        byte[]? accountBytes = account is null ? null
            : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp.Bytes
            : _accountDecoder.EncodeAsBytes(account);
        updates.Add(new SparseTrieUpdate(in key, accountBytes));
    }

    private void DisableConcurrentRootOwned(Exception exception)
    {
        if (_logger.IsWarn)
        {
            _logger.Warn($"Concurrent sparse state-root calculation failed; rebuilding from the committed parent: {exception}");
        }

        _concurrentRootDisabled = true;
        DiscardPendingStateWorkOwned();
        _speculativeAccounts.Clear();
        foreach (KeyValuePair<ValueHash256, CommittedStorageState> kv in _committedStorageStates)
        {
            lock (kv.Value)
            {
                _storageTries.TryRemove(kv.Key, out SparseTrie? speculativeTrie);
                DisposeStorageTrie(speculativeTrie);
            }
        }

        _committedStorageStates.Clear();
        _stateTrie?.Dispose();
        _stateTrie = null;
        _stateDirty = false;
        _updatesSinceConcurrentRootCalculation = 0;
    }

    private void DiscardPendingStateWorkOwned()
    {
        while (_pendingStatePrefetch.TryDequeue(out _)) { }
        DiscardPendingCommittedAccounts();
        DiscardPendingCommittedStorage();
    }

    private void DiscardPendingCommittedAccounts()
    {
        while (TryDequeueCommittedAccount(out _))
        {
            Interlocked.Decrement(ref _pendingCommittedAccountCount);
        }
    }

    private bool TryDequeueCommittedAccount(out CommittedAccountUpdate update) =>
        (_pendingCommittedAccounts?.TryDequeue(out update) ?? false)
        || _overflowCommittedAccounts.TryDequeue(out update);

    private bool TryDequeueCommittedAccount(
        long completedSequence,
        out CommittedAccountUpdate update)
    {
        while (TryPeekCommittedAccount(out CommittedAccountUpdate next, out bool fromRing))
        {
            if (next.Sequence > completedSequence)
            {
                update = default;
                return false;
            }

            bool dequeued = fromRing
                ? _pendingCommittedAccounts!.TryDequeue(out update)
                : _overflowCommittedAccounts.TryDequeue(out update);
            if (dequeued) return true;
        }

        update = default;
        return false;
    }

    private bool TryPeekCommittedAccount(
        out CommittedAccountUpdate update,
        out bool fromRing)
    {
        CommittedAccountUpdate ring = default;
        bool hasRing =
            _pendingCommittedAccounts?.TryPeekSingleConsumer(out ring) ?? false;
        bool hasOverflow =
            _overflowCommittedAccounts.TryPeek(out CommittedAccountUpdate overflow);
        fromRing = hasRing && (!hasOverflow || ring.Sequence <= overflow.Sequence);
        update = fromRing ? ring : overflow;
        return hasRing || hasOverflow;
    }

    private void DiscardPendingCommittedStorage()
    {
        while (TryDequeueCommittedStorage(out _))
        {
            Interlocked.Decrement(ref _pendingCommittedStorageCount);
        }
    }

    private bool TryDequeueCommittedStorage(out CommittedStorageUpdate update) =>
        (_pendingCommittedStorage?.TryDequeue(out update) ?? false)
        || _overflowCommittedStorage.TryDequeue(out update);

    private bool TryDequeueCommittedStorage(
        long completedSequence,
        out CommittedStorageUpdate update)
    {
        while (TryPeekCommittedStorage(out CommittedStorageUpdate next, out bool fromRing))
        {
            if (next.Sequence > completedSequence)
            {
                update = default;
                return false;
            }

            bool dequeued = fromRing
                ? _pendingCommittedStorage!.TryDequeue(out update)
                : _overflowCommittedStorage.TryDequeue(out update);
            if (dequeued) return true;
        }

        update = default;
        return false;
    }

    private bool TryPeekCommittedStorage(
        out CommittedStorageUpdate update,
        out bool fromRing)
    {
        CommittedStorageUpdate ring = default;
        bool hasRing =
            _pendingCommittedStorage?.TryPeekSingleConsumer(out ring) ?? false;
        bool hasOverflow =
            _overflowCommittedStorage.TryPeek(out CommittedStorageUpdate overflow);
        fromRing = hasRing && (!hasOverflow || ring.Sequence <= overflow.Sequence);
        update = fromRing ? ring : overflow;
        return hasRing || hasOverflow;
    }

    private void AcquireStateOwner()
    {
        SpinWait spinWait = new();
        while (Interlocked.CompareExchange(ref _stateWorkOwner, 1, 0) != 0)
        {
            // The owner holds the trie for one bounded batch, so yield rather than sleep: this runs
            // on the block-processing thread at commit, where a 1 ms sleep dwarfs the wait itself.
            spinWait.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void ReleaseStateOwner()
    {
        Volatile.Write(ref _stateWorkOwner, 0);
        EnsureStateWorkerScheduled();
    }

    public void DrainConcurrentUpdates()
    {
        if (!_concurrentRootEnabled || _concurrentRootDisabled) return;

        AcquireStateOwner();
        try
        {
            WaitForCommittedProducers();
            CaptureCompletedSequences();

            try
            {
                DrainStatePrefetchQueueOwned();
                DrainCommittedStorageOwned();
                DrainCommittedAccountsOwned();
            }
            catch (Exception ex)
            {
                DisableConcurrentRootOwned(ex);
            }
        }
        finally
        {
            ReleaseStateOwner();
        }
    }

    /// <summary>Reveals one slot path directly into the retained arena for its storage trie.</summary>
    public void PrefetchStorage(in ValueHash256 addressHash, in ValueHash256 anchorRoot, in ValueHash256 key)
    {
        GuardPoisoned();
        if (_committedStorageStates.TryGetValue(addressHash, out CommittedStorageState? state))
        {
            lock (state)
            {
                SparseTrie settlingTrie =
                    GetOrCreatePrefetchStorageTrie(addressHash, state.Root.ValueHash256);
                lock (settlingTrie)
                {
                    settlingTrie.Prefetch(in key);
                }
            }

            return;
        }

        SparseTrie trie = GetOrCreatePrefetchStorageTrie(addressHash, in anchorRoot);
        lock (trie)
        {
            trie.Prefetch(in key);
        }
    }

    /// <summary>Reveals one account's slot paths as one batched sparse-trie traversal.</summary>
    public void PrefetchStorage(in ValueHash256 addressHash, in ValueHash256 anchorRoot, ReadOnlySpan<ValueHash256> keys)
    {
        GuardPoisoned();
        if (_committedStorageStates.TryGetValue(addressHash, out CommittedStorageState? state))
        {
            lock (state)
            {
                SparseTrie settlingTrie =
                    GetOrCreatePrefetchStorageTrie(addressHash, state.Root.ValueHash256);
                lock (settlingTrie)
                {
                    settlingTrie.Prefetch(keys);
                }
            }

            return;
        }

        SparseTrie trie = GetOrCreatePrefetchStorageTrie(addressHash, in anchorRoot);
        lock (trie)
        {
            trie.Prefetch(keys);
        }
    }

    private SparseTrie GetOrCreatePrefetchStorageTrie(in ValueHash256 addressHash, in ValueHash256 anchorRoot)
    {
        while (true)
        {
            if (_storageTries.TryGetValue(addressHash, out SparseTrie? retained)
                && retained.RootHash == anchorRoot)
            {
                return retained;
            }

            SparseTrie replacement =
                new(new FlatTrieNodeReader(_readerContext, addressHash.ToCommitment()), anchorRoot);
            bool stored = retained is null
                ? _storageTries.TryAdd(addressHash, replacement)
                : _storageTries.TryUpdate(addressHash, replacement, retained);
            if (stored)
            {
                DisposeStorageTrie(retained);
                return replacement;
            }

            replacement.Dispose();
        }
    }

    private static void DisposeStorageTrie(SparseTrie? trie)
    {
        if (trie is null) return;
        lock (trie)
        {
            trie.Dispose();
        }
    }

    /// <summary>Rolls a published storage trie back into the warm set for reuse by later blocks.</summary>
    private void ReturnStorageTrie(Hash256 addressHash, SparseTrie trie)
    {
        if (!_storageTries.TryAdd(addressHash.ValueHash256, trie))
        {
            trie.Dispose();
            ThrowStorageTrieAlreadyReturned();
        }
    }

    /// <summary>Drops any warm storage trie held for a now-deleted or cleared account.</summary>
    public void DiscardRetainedStorage(Hash256 addressHash)
    {
        ValueHash256 key = addressHash.ValueHash256;
        if (_committedStorageStates.TryRemove(key, out CommittedStorageState? state))
        {
            lock (state)
            {
                _storageTries.TryRemove(key, out SparseTrie? settlingTrie);
                DisposeStorageTrie(settlingTrie);
            }

            return;
        }

        _storageTries.TryRemove(key, out SparseTrie? retained);
        DisposeStorageTrie(retained);
    }

    /// <summary>Marks the session unusable after a failure outside its own guarded paths.</summary>
    internal void Poison() => _poisoned = true;

    /// <summary>
    /// Drains prepared storage jobs, calculates every retained storage root together, and reports
    /// each completed root through its job's callback.
    /// </summary>
    /// <param name="getFinalAccount">The batch-final account per address; a job whose final
    /// account is deleted is discarded (the Flat clear and account deletion are already
    /// recorded) so its root is never calculated.</param>
    public void RunStoragePhase(Func<Address, Account?> getFinalAccount)
    {
        GuardPoisoned();
        if (_pendingStorageJobs.IsEmpty) return;

        using ArrayPoolList<PreparedStorageJob> jobs = new(_pendingStorageJobs.Count);
        try
        {
            while (_pendingStorageJobs.TryDequeue(out PreparedStorageJob prepared))
            {
                bool keep;
                try
                {
                    keep = getFinalAccount(prepared.Job.Tree.Address) is not null;
                }
                catch
                {
                    // The in-flight job is in neither the queue nor the tracked list yet.
                    prepared.Job.Updates.Dispose();
                    throw;
                }

                if (keep)
                {
                    jobs.Add(prepared);
                }
                else
                {
                    prepared.Job.Updates.Dispose();
                }
            }

            if (jobs.Count == 0) return;

            SparseTrie.RootCalculation[] calculations =
                SafeArrayPool<SparseTrie.RootCalculation>.Shared.Rent(jobs.Count);
            try
            {
                for (int i = 0; i < jobs.Count; i++)
                {
                    calculations[i] = jobs[i].Calculation;
                }

                SparseTrie.CalculateRoots(calculations, jobs.Count);
                for (int i = 0; i < jobs.Count; i++)
                {
                    jobs[i].Job.Tree.CompleteSparseJob(jobs[i].Job);
                }
            }
            finally
            {
                calculations.AsSpan(0, jobs.Count).Clear();
                SafeArrayPool<SparseTrie.RootCalculation>.Shared.Return(calculations);
            }
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].Job.Tree.MarkInChangedSet()) _changedStorageTrees.Add(jobs[i].Job.Tree);
                jobs[i].Job.Updates.Dispose();
            }
        }
    }

    /// <summary>
    /// Settles the background account stream against the batch-final state. Matching values remain
    /// pre-hashed; changed values and net-zero accounts are corrected before final calculation.
    /// </summary>
    public void ApplyStateUpdates(Dictionary<AddressAsKey, Account?> dirtyAccounts)
    {
        GuardPoisoned();
        AcquireStateOwner();

        try
        {
            try
            {
                WaitForCommittedProducers();
                CaptureCompletedSequences();
                DrainStatePrefetchQueueOwned();
                DrainCommittedStorageOwned();
                DrainCommittedAccountsOwned();
            }
            catch (Exception ex)
            {
                // Concurrent work is advisory. Discard its arena and rebuild from the committed
                // parent below so a worker failure costs latency, never consensus correctness.
                DisableConcurrentRootOwned(ex);
            }

            if (dirtyAccounts.Count == 0 && _speculativeAccounts.Count == 0) return;

            using ArrayPoolList<SparseTrieUpdate> finalUpdates =
                new(dirtyAccounts.Count + _speculativeAccounts.Count);

            foreach (KeyValuePair<ValueHash256, CommittedAccountUpdate> kv in _speculativeAccounts)
            {
                CommittedAccountUpdate speculative = kv.Value;
                Account? finalAccount = dirtyAccounts.TryGetValue(speculative.Address, out Account? dirtyAccount)
                    ? dirtyAccount
                    : _bundle.GetAccount(speculative.Address);
                if (speculative.Account != finalAccount)
                {
                    AddAccountUpdate(finalUpdates, kv.Key, finalAccount);
                }
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in dirtyAccounts)
            {
                Address address = kv.Key.Value;
                ValueHash256 key = address.ToAccountPath;
                if (!_speculativeAccounts.ContainsKey(key))
                {
                    AddAccountUpdate(finalUpdates, key, kv.Value);
                }
            }

            if (finalUpdates.Count > 0)
            {
                ApplyAccountUpdates(finalUpdates);
            }

            _speculativeAccounts.Clear();
            _updatesSinceConcurrentRootCalculation = 0;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            ReleaseStateOwner();
        }
    }

    /// <summary>Recalculates the state root when writes intervened since the last calculation.</summary>
    public void UpdateRootHash()
    {
        GuardPoisoned();
        AcquireStateOwner();

        try
        {
            DrainStatePrefetchQueueOwned();
            if (Volatile.Read(ref _pendingCommittedAccountCount) != 0
                || Volatile.Read(ref _pendingCommittedStorageCount) != 0
                || _speculativeAccounts.Count != 0)
            {
                _poisoned = true;
                ThrowUnsettledUpdates(
                    Volatile.Read(ref _pendingCommittedAccountCount),
                    Volatile.Read(ref _pendingCommittedStorageCount),
                    _speculativeAccounts.Count);
            }

            if (!_stateDirty) return;

            _rootHash = _stateTrie!.CalculateRoot(canBeParallel: true).ToCommitment();
            _stateDirty = false;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            ReleaseStateOwner();
        }
    }

    /// <summary>
    /// Publishes every staged storage and state node into the bundle's changed-node maps, ahead of
    /// snapshot collection. The tries are left intact so the committed generation can either be
    /// retained (see <see cref="ExtractGeneration"/>) or disposed with the session.
    /// </summary>
    public void Publish()
    {
        GuardPoisoned();
        if (!_pendingStorageJobs.IsEmpty)
        {
            _poisoned = true;
            ThrowStorageJobsSealed();
        }

        AcquireStateOwner();
        try
        {
            foreach (FlatStorageTree tree in _changedStorageTrees)
            {
                tree.PublishSparseNodes();
                // With retention, roll the warm trie back into the session set so the next block
                // in this scope reuses it; without it, dispose per commit so a multi-block scope
                // holds no cross-block working set. A deleted account's tree yields null.
                SparseTrie? trie = tree.TakeSparseTrie();
                if (trie is null) continue;
                if (_retentionEnabled) ReturnStorageTrie(tree.AddressHash, trie);
                else trie.Dispose();
            }

            _changedStorageTrees.Clear();
            _committedStorageStates.Clear();

            if (!_retentionEnabled)
            {
                foreach (KeyValuePair<ValueHash256, SparseTrie> kv in _storageTries)
                {
                    kv.Value.Dispose();
                }

                _storageTries.Clear();
            }

            if (_stateTrie is not null)
            {
                using ArrayPoolList<SparseTrieStagedNode> staged = new(_stateTrie.UnpublishedNodeCapacityHint);
                _stateTrie.DrainUnpublished(staged);
                if (staged.Count > 0)
                {
                    using ArrayPoolListRef<(TreePath, TrieNode)> buffer = BuildPublicationBuffer(staged.AsSpan());
                    _bundle.PublishStateNodes(buffer.AsSpan());
                }

                if (!_retentionEnabled)
                {
                    _stateTrie.Dispose();
                    _stateTrie = null;
                }
            }

            _stateDirty = false;
        }
        catch
        {
            _poisoned = true;
            throw;
        }
        finally
        {
            ReleaseStateOwner();
        }
    }

    /// <summary>
    /// Transfers the warm tries out of the session into a retainable generation keyed by
    /// <paramref name="newStateRoot"/> (the last committed state root): the state trie plus every
    /// warm storage trie. Called once at the scope boundary after a clean commit; the session then
    /// owns nothing, so its dispose is a no-op and the cache (or its rejection path) owns the tries.
    /// </summary>
    public RetainedGeneration ExtractGeneration(in ValueHash256 newStateRoot)
    {
        GuardPoisoned();
        AcquireStateOwner();
        try
        {
            if (Volatile.Read(ref _pendingCommittedAccountCount) != 0
                || Volatile.Read(ref _pendingCommittedStorageCount) != 0
                || _speculativeAccounts.Count != 0)
            {
                _poisoned = true;
                ThrowUnsettledUpdates(
                    Volatile.Read(ref _pendingCommittedAccountCount),
                    Volatile.Read(ref _pendingCommittedStorageCount),
                    _speculativeAccounts.Count);
            }

            // No state change ever materialized a trie; anchor an empty one so the generation is
            // still valid for the next block (which will reveal from the committed reader).
            _stateTrie ??= new SparseTrie(new FlatTrieNodeReader(_readerContext, address: null), newStateRoot);

            RetainedGeneration generation = new(newStateRoot, _stateTrie, _storageTries, _readerContext);
            _stateTrie = null;
            _storageTries = [];
            _generationExtracted = true;
            return generation;
        }
        finally
        {
            ReleaseStateOwner();
        }
    }

    /// <summary>Converts staged records into the sealed <see cref="TrieNode"/> publication shape.</summary>
    /// <remarks>The staged array is already the final owned RLP; the explicit
    /// <see cref="CappedArray{T}"/> binds the adopting <see cref="TrieNode"/> constructor —
    /// a bare <c>byte[]</c> would bind the <see cref="ReadOnlySpan{T}"/> overload, which
    /// copies every array again.</remarks>
    internal static ArrayPoolListRef<(TreePath, TrieNode)> BuildPublicationBuffer(ReadOnlySpan<SparseTrieStagedNode> staged)
    {
        ArrayPoolListRef<(TreePath, TrieNode)> buffer = new(staged.Length);
        try
        {
            for (int i = 0; i < staged.Length; i++)
            {
                ref readonly SparseTrieStagedNode node = ref staged[i];
                buffer.Add((node.Path, new TrieNode(NodeType.Unknown, node.Hash.ToCommitment(), new CappedArray<byte>(node.Rlp))));
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private void GuardPoisoned()
    {
        if (_poisoned) ThrowPoisoned();

        [DoesNotReturn, StackTraceHidden]
        static void ThrowPoisoned() =>
            throw new InvalidOperationException("Sparse trie session is poisoned by an earlier calculation failure");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageJobsSealed() =>
        throw new InvalidOperationException("Storage jobs sealed after the storage phase would never be calculated");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowUnsettledUpdates(int accountCount, int storageCount, int speculativeCount) =>
        throw new InvalidOperationException(
            $"Concurrent sparse updates were not settled by the final write batch: {accountCount} accounts, {storageCount} storage updates, {speculativeCount} speculative accounts");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowStorageTrieAlreadyReturned() =>
        throw new InvalidOperationException("A sparse storage trie was returned more than once");

    public void Dispose()
    {
        _disposed = true;
        AcquireStateOwner();
        try
        {
            if (_pendingCommittedAccounts is not null)
            {
                SpinWait spinWait = new();
                while (Volatile.Read(ref _committedAccountProducers) != 0
                    || Volatile.Read(ref _committedStorageProducers) != 0
                    || Volatile.Read(ref _stateWorkerScheduled) != 0)
                {
                    spinWait.SpinOnce();
                }
            }

            DiscardPendingStateWorkOwned();
            _speculativeAccounts.Clear();
            _speculativeAccounts.Dispose();
            _pendingCommittedAccounts?.ReturnPooledArrays();
            _pendingCommittedStorage?.ReturnPooledArrays();
            _committedStorageStates.Clear();

            while (_pendingStorageJobs.TryDequeue(out PreparedStorageJob job)) job.Job.Updates.Dispose();

            // A generation transferred to the cache owns its tries; the session must not touch them.
            // Otherwise (no cache, or a scope that never reached a clean commit) the session owns every
            // trie it built or checked out and releases them here: those still held by a tree touched
            // in an aborted, not-yet-published block, plus the warm set and the state trie.
            if (_generationExtracted) return;

            foreach (FlatStorageTree tree in _changedStorageTrees)
            {
                tree.DropSparseTrie();
            }

            _changedStorageTrees.Clear();
            _stateTrie?.Dispose();
            _stateTrie = null;

            foreach (KeyValuePair<ValueHash256, SparseTrie> kv in _storageTries)
            {
                kv.Value.Dispose();
            }

            _storageTries.Clear();
        }
        finally
        {
            Volatile.Write(ref _stateWorkOwner, 0);
        }
    }
}
