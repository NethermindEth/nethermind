// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Prometheus;
using Metrics = Prometheus.Metrics;

namespace Nethermind.State.Flat;

public class FlatDiffRepository : IFlatDiffRepository, IAsyncDisposable
{
    private ReaderWriterLockSlim _repoLock = new ReaderWriterLockSlim(); // Note: lock is for proteccting in memory and compacted states only
    private readonly ICanonicalStateRootFinder _stateRootFinder;
    private Dictionary<StateId, Snapshot> _compactedKnownStates = new();
    private InMemorySnapshotStore _inMemorySnapshotStore;
    private ResourcePool _resourcePool;
    private List<(Hash256AsKey, TreePath)> _trieNodesSortBuffer = new List<(Hash256AsKey, TreePath)>(); // Presort make it faster
    private readonly Task _compactorTask;

    private Lock _readerCacheLock = new Lock();
    private RefCountingPersistenceReader? _cachedReader = null;
    private readonly TrieNodeCache _trieNodeCache;
    private readonly Task _persistenceTask;

    private IPersistence _persistence;
    private int _boundary;

    private Channel<(StateId, CachedResource)> _compactorJobs;
    private Channel<StateId> _persistenceJob;
    private long _compactSize;
    private long _compactEveryBlockNum;
    private readonly bool _inlineCompaction;
    private ILogger _logger;
    private StateId _currentPersistedState;

    private static Histogram _flatdiffimes = Metrics.CreateHistogram("flatdiff_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "category", "type" },
        Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
    });

    private static Gauge _knownStatesMemory = Metrics.CreateGauge("flatdiff_knownstates_memory", "memory", "category");
    private static Gauge _compactedMemory = Metrics.CreateGauge("flatdiff_compacted_memory", "memory", "category");

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public record Configuration(
        int MaxInFlightCompactJob = 32,
        int CompactSize = 64,
        int CompactInterval = 4,
        int ConcurrentCompactor = 4,
        int Boundary = 128,
        long TrieCacheMemoryTarget = 2_000_000_000,
        bool VerifyWithTrie = false,
        bool ReadWithTrie = false,
        bool InlineCompaction = false,
        bool DisableTrieWarmer = false
    )
    {
    }

    public FlatDiffRepository(
        IProcessExitSource exitSource,
        ICanonicalStateRootFinder stateRootFinder,
        IPersistence persistedPersistence,
        ResourcePool resourcePool,
        ILogManager logManager,
        Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _inMemorySnapshotStore = new InMemorySnapshotStore();
        _persistence = persistedPersistence;
        _compactSize = config.CompactSize;
        _compactEveryBlockNum = config.CompactInterval;
        _inlineCompaction = config.InlineCompaction;
        _stateRootFinder = stateRootFinder;
        _resourcePool = resourcePool;
        _logger = logManager.GetClassLogger<FlatDiffRepository>();

        _compactorJobs = Channel.CreateBounded<(StateId, CachedResource)>(config.MaxInFlightCompactJob);
        _persistenceJob = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _boundary = config.Boundary;

        using var reader = LeaseReader();
        _currentPersistedState = reader.CurrentState;
        _trieNodeCache = new TrieNodeCache(config.TrieCacheMemoryTarget, logManager);

        _compactorTask = RunCompactor(exitSource.Token);
        _persistenceTask = RunPersistence(exitSource.Token);
    }


    public IPersistence.IPersistenceReader LeaseReader()
    {
        using var _ = _readerCacheLock.EnterScope();
        var cachedReader = _cachedReader;
        if (cachedReader is null)
        {
            _cachedReader = cachedReader = new RefCountingPersistenceReader(
                _persistence.CreateReader(),
                _logger
            );
        }

        cachedReader.AcquireLease();
        return cachedReader;
    }

    private void ClearReaderCache()
    {
        using var _ = _readerCacheLock.EnterScope();
        RefCountingPersistenceReader? cachedReader = _cachedReader;
        _cachedReader = null;
        cachedReader?.Dispose();
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (stateId, cachedResource) in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    CompactLevel(stateId, cachedResource);
                    if (stateId.blockNumber % _compactSize == 0)
                    {
                        await _persistenceJob.Writer.WriteAsync(stateId, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Compact job failed", ex);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPersistence(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _persistenceJob.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await PersistIfNeeded();
                }
                catch (Exception ex)
                {
                    _logger.Error("Persistence job failed", ex);
                    throw;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NotifyWhenSlow(string name, Action closure)
    {
        Task jobTask = Task.Run(() =>
        {
            try
            {
                closure();
            }
            catch (Exception ex)
            {
                _logger.Error($"job {name} failed", ex);
                Environment.Exit(1);
                throw;
            }
        });
        Task waiterTask = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(1000);
                if (jobTask.IsCompleted) break;
                _logger.Info($"Task {name} took {sw.Elapsed}");
            }
        });

        await Task.WhenAny(jobTask, waiterTask);
    }

    private void RunCompactJob(StateId stateId, CachedResource cachedResource)
    {
        CompactLevel(stateId, cachedResource);
        PersistIfNeeded().Wait();
    }

    private RepolockReadExiter EnterRepolockReadOnly()
    {
        _repoLock.EnterReadLock();

        return new RepolockReadExiter(_repoLock, true);
    }

    private RepolockReadExiter EnterRepolock()
    {
        _repoLock.EnterWriteLock();

        return new RepolockReadExiter(_repoLock, false);
    }

    private ref struct RepolockReadExiter(ReaderWriterLockSlim @lock, bool read) : IDisposable
    {
        public void Dispose()
        {
            if (read)
            {
                @lock.ExitReadLock();
            }
            else
            {
                @lock.ExitWriteLock();
            }
        }
    }

    private void CompactLevel(StateId stateId, CachedResource cachedResource)
    {
        try
        {
            if (PopulateTrieNodeCache(cachedResource)) return;

            if (_compactSize <= 1) return; // Disabled
            long blockNumber = stateId.blockNumber;
            if (blockNumber == 0) return;
            if (blockNumber % _compactSize != 0)
            {
                using (EnterRepolockReadOnly())
                {
                    StateId? last = _inMemorySnapshotStore.GetLast();
                    if (last != null && last.Value.blockNumber - blockNumber > 1)
                    {
                        // To slow. Just skip this block number.
                        return;
                    }
                }

                if (blockNumber % _compactEveryBlockNum != 0) return;
            }

            long startingBlockNumber = ((blockNumber - 1) / _compactSize) * _compactSize;

            using SnapshotBundle gatheredCache = GatherCache(stateId, IFlatDiffRepository.SnapshotBundleUsage.Compactor, startingBlockNumber);
            if (gatheredCache.SnapshotCount == 1)
            {
                return;
            }

            if (_logger.IsDebug) _logger.Debug($"Compacting {stateId}");
            long sw = Stopwatch.GetTimestamp();
            Snapshot snapshot = gatheredCache.CompactToKnownState();
            _flatdiffimes.WithLabels("compaction", "compact_to_known_state").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
            Dictionary<MemoryType, long> memory = snapshot.EstimateMemory();

            using (EnterRepolock())
            {
                _flatdiffimes.WithLabels("compaction", "add_repolock").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();

                if (_logger.IsDebug) _logger.Debug($"Compacted {gatheredCache.SnapshotCount} to {stateId}");

                if (_compactedKnownStates.TryAdd(stateId, snapshot))
                {
                    foreach (var keyValuePair in memory)
                    {
                        _compactedMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
                    }
                    _compactedMemory.WithLabels("count").Inc(1);
                }
                else
                {
                    snapshot.Dispose();
                }

                _flatdiffimes.WithLabels("compaction", "add_and_measure").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();

                if (stateId.blockNumber % _compactSize != 0)
                {
                    // Save memory
                    foreach (var id in _inMemorySnapshotStore.GetStatesAtBlockNumber(stateId.blockNumber - _compactSize))
                    {
                        RemoveAndReleaseCompactedKnownState(id);
                    }
                }

                _flatdiffimes.WithLabels("compaction", "cleanup_compacted").Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
        catch (Exception e)
        {
            _logger.Error($"Compactor failed {e}");
        }
    }

    private bool PopulateTrieNodeCache(CachedResource cachedResource)
    {
        Snapshot lastSnapshot;
        using (EnterRepolockReadOnly())
        {
            StateId? last = _inMemorySnapshotStore.GetLast();
            if (last == null) return true;
            if (!TryLeaseState(last.Value, out lastSnapshot)) return true;
        }

        try
        {
            var memory = lastSnapshot.EstimateMemory(); // Note: This is slow, do it outside.
            foreach (var keyValuePair in memory)
            {
                _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
            }
            _knownStatesMemory.WithLabels("count").Inc(1);

            _trieNodeCache.Add(lastSnapshot, cachedResource);
            _resourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, cachedResource);
            return false;
        }
        finally
        {
            lastSnapshot.Dispose();
        }
    }

    public SnapshotBundle? GatherReaderAtBaseBlock(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        // TODO: Throw if not enough or return null
        return GatherCache(baseBlock, usage, null);
    }

    private static Histogram _knownStatesSize = Metrics.CreateHistogram("flatdiff_known_state_size", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part"],
            Buckets = Histogram.LinearBuckets(0, 1, 100)
        });

    private SnapshotBundle GatherCache(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage, long? earliestExclusive = null) {
        long sw = Stopwatch.GetTimestamp();
        using var _ = EnterRepolockReadOnly();
        _flatdiffimes.WithLabels("gather_cache", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();

        ArrayPoolList<Snapshot> knownStates = new(Math.Max(1, (int)(_inMemorySnapshotStore.KnownStatesCount / _compactSize)));

        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}. Earliest is {earliestExclusive}");

        StateId bigCacheState = _currentPersistedState;

        // TODO: Determine if using a linked list of snapshot make more sense. Measure the impact of this loop and the
        // dispose loop.
        string exitReason = "";
        StateId current = baseBlock;
        while(TryLeaseCompactedState(current, out var entry) || TryLeaseState(current, out entry))
        {
            Snapshot state = entry;
            if (_logger.IsTrace) _logger.Trace($"Got {state.From} -> {state.To}");
            knownStates.Add(state);
            if (state.From == current) {
                exitReason = "cycle";
                break; // Some test commit two block with the same id, so we dont know the parent anymore.
            }
            current = state.From;

            if (state.To.blockNumber <= bigCacheState.blockNumber)
            {
                exitReason = $"First {state.From} to {bigCacheState}";
                break; // Or equal?
            }
            if (state.From.blockNumber <= earliestExclusive) break;
        }

        _flatdiffimes.WithLabels("gather_cache", "gather").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();
        _knownStatesSize.Observe(knownStates.Count);

        // Note: By the time the previous loop finished checking all state, the big cache may have added new state and removed some
        // entry in `_inMemorySnapshotStore`. Meaning, this need to be here instead oof before the loop.
        IPersistence.IPersistenceReader bigCacheReader = LeaseReader();
        if (current != baseBlock && earliestExclusive is null && bigCacheReader.CurrentState.blockNumber != -1 && current.blockNumber > bigCacheReader.CurrentState.blockNumber)
        {
            throw new Exception($"Non consecutive snappshots. Current {current} vs {bigCacheReader.CurrentState}, {bigCacheState}, {baseBlock}, {_inMemorySnapshotStore.TryGetValue(current, out var snapshot)}, {exitReason}");
        }

        if (bigCacheReader.CurrentState.blockNumber > baseBlock.blockNumber)
        {
            _logger.Warn("Big cache too early");
            bigCacheReader.Dispose();
            bigCacheReader = new NoopPersistenceReader();
        }

        // TODO: Measure this
        knownStates.Reverse();
        _flatdiffimes.WithLabels("gather_cache", "reverse").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Earliest is {earliestExclusive}, Got {knownStates.Count} known states, {_currentPersistedState}");
        var res = new SnapshotBundle(
            knownStates,
            bigCacheReader,
            _trieNodeCache,
            _resourcePool,
            usage: usage);
        _flatdiffimes.WithLabels("gather_cache", "done").Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public bool TryLeaseCompactedState(StateId stateId, out Snapshot entry)
    {
        if (!_compactedKnownStates.TryGetValue(stateId, out entry)) return false;
        if (!entry.TryAcquire()) return false;
        return true;
    }

    public bool TryLeaseState(StateId stateId, out Snapshot entry)
    {
        if (!_inMemorySnapshotStore.TryGetValue(stateId, out entry)) return false;
        if (!entry.TryAcquire()) return false;
        return true;
    }

    public void AddSnapshot(Snapshot snapshot, CachedResource cachedResource)
    {
        long sw = Stopwatch.GetTimestamp();

        StateId startingBlock = snapshot.From;
        StateId endBlock = snapshot.To;
        using (EnterRepolock())
        {
            _flatdiffimes.WithLabels("add_snapshot", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
            if (endBlock.blockNumber <= _currentPersistedState.blockNumber)
            {
                _logger.Warn(
                    $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {_currentPersistedState}");
                return;
            }

            // snapshot should have 2 lease here
            _inMemorySnapshotStore.AddBlock(endBlock, snapshot);

            _flatdiffimes.WithLabels("add_snapshot", "add_block").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
        }

        if (_inlineCompaction)
        {
            RunCompactJob(endBlock, cachedResource);
        }
        else
        {
            if (!_compactorJobs.Writer.TryWrite((endBlock, cachedResource)))
            {
                _flatdiffimes.WithLabels("add_snapshot", "try_write_failed").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();
                _logger.Warn("Compactor job stall!");
                _compactorJobs.Writer.WriteAsync((endBlock, cachedResource)).AsTask().Wait();
                _flatdiffimes.WithLabels("add_snapshot", "write_async").Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                _flatdiffimes.WithLabels("add_snapshot", "try_write_ok").Observe(Stopwatch.GetTimestamp() - sw);
            }
        }
    }

    private async Task PersistIfNeeded()
    {
        await NotifyWhenSlow("add to bigcache", () => AddToPersistence());
    }

    private void AddToPersistence()
    {
        // Attempt to add snapshots into bigcache
        while (true)
        {
            Snapshot pickedState;
            StateId? pickedSnapshot = null;
            List<StateId> toRemoveStates = new List<StateId>();
            long sw = Stopwatch.GetTimestamp();
            using (EnterRepolock())
            {
                _flatdiffimes.WithLabels("add_to_persistence", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();
                long lastSnapshotNumber = _inMemorySnapshotStore.GetLast()?.blockNumber ?? 0;
                StateId currentState = _currentPersistedState;
                if (lastSnapshotNumber - currentState.blockNumber <= (_boundary + _compactSize))
                {
                    break;
                }

                List<StateId> candidateToAdd = new List<StateId>();

                long? blockNumber = null;
                bool persistCompactedStates = false;
                //  Note: Need to verify that this is finalized
                foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber + _compactSize - 1))
                {
                    if (stateId.blockNumber > currentState.blockNumber + _compactSize)
                    {
                        break;
                    }
                    if (_compactedKnownStates.TryGetValue(stateId, out var existingState))
                    {
                        if (blockNumber is null)
                        {
                            if (existingState.From != currentState)
                            {
                                if (_logger.IsDebug) _logger.Debug($"Not using compacted state. Mismatch. {existingState.From}, query {stateId} vs {currentState}");
                                break;
                            }

                            if (_logger.IsDebug) _logger.Debug($"Setting compacted state");
                            persistCompactedStates = true;
                            blockNumber = stateId.blockNumber;
                            candidateToAdd.Add(stateId);
                        }
                        else if (blockNumber == stateId.blockNumber)
                        {
                            candidateToAdd.Add(stateId);
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        if (_logger.IsDebug) _logger.Debug($"Cancelling setting compacted state, {stateId}");
                        persistCompactedStates = false;
                        candidateToAdd.Clear();
                        blockNumber = null;
                        break;
                    }
                }

                if (persistCompactedStates)
                {
                    if (_logger.IsDebug) _logger.Debug($"Using compacted state. {blockNumber}, vs {currentState}");
                }

                if (blockNumber is null)
                {
                    foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber))
                    {
                        if (blockNumber is null)
                        {
                            blockNumber = stateId.blockNumber;
                            candidateToAdd.Add(stateId);
                        }
                        else if (blockNumber == stateId.blockNumber)
                        {
                            candidateToAdd.Add(stateId);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                Debug.Assert(candidateToAdd.Count > 0);

                if (candidateToAdd.Count > 1)
                {
                    Hash256? canonicalStateRoot = _stateRootFinder.GetCanonicalStateRootAtBlock(blockNumber.Value);
                    if (canonicalStateRoot is null)
                    {
                        _logger.Warn($"Canonical state root for block {blockNumber} not known");
                        return;
                    }

                    foreach (var stateId in candidateToAdd)
                    {
                        if (stateId.stateRoot == canonicalStateRoot)
                        {
                            pickedSnapshot = stateId;
                        }
                    }
                }
                else
                {
                    pickedSnapshot = candidateToAdd[0];
                }

                if (!pickedSnapshot.HasValue)
                {
                    // Ah, probably filter the compacted state here instead
                    _logger.Warn($"Unable to determine canonicaal snapshot");
                    return;
                }

                // Remove non-canon snapshots
                foreach (var stateId in candidateToAdd)
                {
                    if (stateId != pickedSnapshot)
                    {
                        RemoveAndReleaseCompactedKnownState(stateId);
                        RemoveAndReleaseKnownState(stateId);
                    }
                }

                if (persistCompactedStates)
                {
                    _compactedKnownStates.TryGetValue(pickedSnapshot.Value, out pickedState);
                    pickedState.AcquireLease();
                    if (_logger.IsDebug) _logger.Debug($"Picking compacted state {pickedState.From} to {pickedState.To}");

                    foreach (var stateId in _inMemorySnapshotStore.GetStatesAfterBlock(currentState.blockNumber))
                    {
                        if (stateId.blockNumber < pickedSnapshot.Value.blockNumber)
                        {
                            toRemoveStates.Add(stateId);
                        }
                    }
                }
                else
                {
                    _inMemorySnapshotStore.TryGetValue(pickedSnapshot.Value, out pickedState);
                    pickedState.AcquireLease();
                }
            }
            _flatdiffimes.WithLabels("add_to_persistence", "state_picked").Observe(Stopwatch.GetTimestamp() - sw);

            // Add the canon snapshot
            Add(pickedState);
            pickedState.Dispose();

            sw = Stopwatch.GetTimestamp();
            // And we remove it
            using (EnterRepolock())
            {
                RemoveAndReleaseCompactedKnownState(pickedSnapshot.Value);
                RemoveAndReleaseKnownState(pickedSnapshot.Value);

                foreach (var stateId in toRemoveStates)
                {
                    RemoveAndReleaseCompactedKnownState(stateId);
                    RemoveAndReleaseKnownState(stateId);
                }
            }
            _flatdiffimes.WithLabels("add_to_persistence", "cleanup").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();

            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(pickedSnapshot.Value.blockNumber));

            _flatdiffimes.WithLabels("add_to_persistence", "reorg_boundary").Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    private void RemoveAndReleaseCompactedKnownState(StateId stateId)
    {
        if (_compactedKnownStates.Remove(stateId, out var existingState))
        {
            var memory = existingState.EstimateMemory();
            foreach (var keyValuePair in memory)
            {
                _compactedMemory.WithLabels(keyValuePair.Key.ToString()).Dec(keyValuePair.Value);
            }
            _compactedMemory.WithLabels("count").Dec(1);

            existingState.Dispose();
        }
    }

    private void RemoveAndReleaseKnownState(StateId stateId)
    {
        if (!_repoLock.IsWriteLockHeld) throw new InvalidOperationException("Must hold write lock to repolock to change snapshot store");
        if (_inMemorySnapshotStore.TryGetValue(stateId, out var existingState))
        {
            _inMemorySnapshotStore.Remove(stateId);
            var memory = existingState.EstimateMemory();
            foreach (var keyValuePair in memory)
            {
                _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Dec(keyValuePair.Value);
            }
            _knownStatesMemory.WithLabels("count").Dec();

            existingState.Dispose(); // After memory
        }
    }

    public void Add(Snapshot snapshot)
    {
        if (snapshot.To.blockNumber - snapshot.From.blockNumber != _compactSize) _logger.Warn($"Snapshot size write is {snapshot.To.blockNumber - snapshot.From.blockNumber}");
        long sw = Stopwatch.GetTimestamp();
        using (var batch = _persistence.CreateWriteBatch(snapshot.From, snapshot.To))
        {
            _flatdiffimes.WithLabels("persistence", "start_batch").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();
            int counter = 0;
            foreach (var toSelfDestructStorage in snapshot.SelfDestructedStorageAddresses)
            {
                if (toSelfDestructStorage.Value)
                {
                    /*
                    int deleted = batch.SelfDestruct(toSelfDestructStorage.Key.Value);
                    if (toSelfDestructStorage.Key.Value == FlatWorldStateScope.DebugAddress)
                    {
                        Console.Error.WriteLine($"Selfdestruct should skip {toSelfDestructStorage.Key}");
                    }
                    if (deleted > 0)
                    {
                        _logger.Warn($"Should selfdestruct {toSelfDestructStorage.Key}. Deleted {deleted}. Snapshot range {snapshot.From} {snapshot.To}");
                        throw new Exception($"Should sefl destruct not called properly {toSelfDestructStorage.Key}");
                    }
                    */
                    continue;
                }

                int num = batch.SelfDestruct(toSelfDestructStorage.Key.Value);
                if (toSelfDestructStorage.Key.Value == FlatWorldStateScope.DebugAddress)
                {
                    using var r = LeaseReader();
                    bool _ = r.TryGetSlot(FlatWorldStateScope.DebugAddress, FlatWorldStateScope.DebugSlot, out var value);
                    Console.Error.WriteLine($"Selfdestructed {toSelfDestructStorage.Key} {num}, {value?.ToHexString()}");
                }
                counter++;
            }
            _flatdiffimes.WithLabels("persistence", "self_destruct").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Accounts)
            {
                (Address addr, Account? account) = kv;
                if (account is null)
                    batch.RemoveAccount(addr);
                else
                    batch.SetAccount(addr, account);
            }
            _flatdiffimes.WithLabels("persistence", "accounts").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            foreach (var kv in snapshot.Storages)
            {
                ((Address addr, UInt256 slot), byte[] value) = kv;

                if (value is null || Bytes.AreEqual(value, StorageTree.ZeroBytes))
                {
                    batch.RemoveStorage(addr, slot);
                }
                else
                {
                    batch.SetStorage(addr, slot, value);
                }
            }
            _flatdiffimes.WithLabels("persistence", "storages").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StateNodeKeys.Select<TreePath, (Hash256AsKey, TreePath)>((path) => (null, path)));
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort_state").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
            {
                (_, TreePath path) = k;

                snapshot.TryGetStateNode(path, out TrieNode node);

                if (node.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetTrieNodes(null, path, node);

                node.IsPersisted = true;
            }
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            _trieNodesSortBuffer.Clear();
            _trieNodesSortBuffer.AddRange(snapshot.StorageTrieNodeKeys);
            _trieNodesSortBuffer.Sort();
            _flatdiffimes.WithLabels("persistence", "trienode_sort").Observe(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            // foreach (var tn in snapshot.TrieNodes)
            foreach (var k in _trieNodesSortBuffer)
            {
                (Hash256AsKey address, TreePath path) = k;

                snapshot.TryGetStorageNode(address, path, out TrieNode node);

                if (node.FullRlp.Length == 0)
                {
                    // TODO: Need to double check this case. Does it need a rewrite or not?
                    if (node.NodeType == NodeType.Unknown)
                    {
                        continue;
                    }
                }

                // Note: Even if the node already marked as persisted, we still re-persist it
                batch.SetTrieNodes(address, path, node);

                node.IsPersisted = true;
            }
            _flatdiffimes.WithLabels("persistence", "trienodes").Observe(Stopwatch.GetTimestamp() - sw);

            sw = Stopwatch.GetTimestamp();
        }
        _flatdiffimes.WithLabels("persistence", "dispose").Observe(Stopwatch.GetTimestamp() - sw);

        _currentPersistedState = snapshot.To;
        ClearReaderCache();
    }


    public void FlushCache(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Flush cache not implemented");
    }

    public bool HasStateForBlock(StateId stateId)
    {
        if (_inMemorySnapshotStore.TryGetValue(stateId, out var snapshot))
        {
            return true;
        }

        if (_currentPersistedState == stateId) return true;
        return false;
    }

    public StateId? FindStateIdForStateRoot(Hash256 stateRoot)
    {
        using (EnterRepolockReadOnly())
        {
            foreach (var stateId in _inMemorySnapshotStore.GetKeysBetween(new StateId(0, Hash256.Zero), new StateId(long.MaxValue, Keccak.MaxValue)))
            {
                if (stateId.stateRoot == stateRoot) return stateId;
            }

            if (_currentPersistedState.stateRoot == stateRoot)
            {
                return _currentPersistedState;
            }
        }

        return null;
    }

    public StateId? FindLatestAvailableState()
    {
        using (EnterRepolockReadOnly())
        {
            StateId? lastInMemory = _inMemorySnapshotStore.GetLast();
            if (lastInMemory != null) return lastInMemory;

            return _currentPersistedState;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _compactorTask;
        await _persistenceTask;
        ClearReaderCache();

        return;
    }
}
