// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat;

public class FlatDiffRepository : IFlatDiffRepository, IAsyncDisposable
{
    private ReaderWriterLockSlim _repoLock = new ReaderWriterLockSlim(); // Note: lock is for proteccting in memory and compacted states only
    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedKnownStates = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _knownStates = new();
    private SortedSet<StateId> _sortedKnownStates = new();

    private ResourcePool _resourcePool;

    private readonly Task _compactorTask;

    private RefCountingPersistenceReader? _cachedReader = null;
    private Lock _readerCacheLock = new Lock();
    private readonly TrieNodeCache _trieNodeCache;
    private readonly Task _persistenceTask;

    private IPersistence _persistence;

    private Channel<(StateId, CachedResource)> _compactorJobs;
    private Channel<StateId> _persistenceJob;
    private long _compactSize;
    private readonly bool _inlineCompaction;
    private ILogger _logger;
    private readonly PersistenceRunner _persistenceRunner;
    private readonly SnapshotCompactor _snapshotCompactor;

    internal static Histogram _flatdiffimes = DevMetric.Factory.CreateHistogram("flatdiff_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "category", "type" },
        // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
        Buckets = [1]
    });

    private static Gauge _knownStatesMemory = DevMetric.Factory.CreateGauge("flatdiff_knownstates_memory", "memory", "category");
    private static Gauge _compactedMemory = DevMetric.Factory.CreateGauge("flatdiff_compacted_memory", "memory", "category");

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public record Configuration(
        int MaxInFlightCompactJob = 32,
        int CompactSize = 64,
        int CompactInterval = 4,
        int ConcurrentCompactor = 4,
        int Boundary = 128,
        int ForcedPruningBoundary = 1024,
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
        IFinalizedStateProvider finalizedStateProvider,
        IPersistence persistedPersistence,
        ResourcePool resourcePool,
        ILogManager logManager,
        Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _persistence = persistedPersistence;
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
        _resourcePool = resourcePool;
        _logger = logManager.GetClassLogger<FlatDiffRepository>();

        _persistenceRunner = new PersistenceRunner(this, config, finalizedStateProvider, persistedPersistence, logManager);
        _snapshotCompactor = new SnapshotCompactor(this, config, resourcePool, logManager);

        _compactorJobs = Channel.CreateBounded<(StateId, CachedResource)>(config.MaxInFlightCompactJob);
        _persistenceJob = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

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

    internal void ClearReaderCache()
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
                    PopulateTrieNodeCache(cachedResource);

                    _snapshotCompactor.CompactLevel(stateId, cachedResource);
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

    private void RunCompactJob(StateId stateId, CachedResource cachedResource)
    {
        _snapshotCompactor.CompactLevel(stateId, cachedResource);
        PersistIfNeeded().Wait();
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

    internal RepolockReadExiter EnterRepolockReadOnly()
    {
        _repoLock.EnterReadLock();

        return new RepolockReadExiter(_repoLock, true);
    }

    internal RepolockReadExiter EnterRepolock()
    {
        _repoLock.EnterWriteLock();

        return new RepolockReadExiter(_repoLock, false);
    }

    internal ArrayPoolList<StateId> GetStatesAfterBlock(long blockNumber)
    {
        _repoLock.EnterReadLock();

        StateId min = new StateId(blockNumber + 1, ValueKeccak.Zero);
        StateId max = new StateId(long.MaxValue, ValueKeccak.Zero);

        return _sortedKnownStates.GetViewBetween(min, max).ToPooledList(0);
    }

    internal ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        _repoLock.EnterReadLock();

        StateId min = new StateId(blockNumber, ValueKeccak.Zero);
        StateId max = new StateId(blockNumber, ValueKeccak.MaxValue);

        return _sortedKnownStates.GetViewBetween(min, max).ToPooledList(0);
    }

    internal ref struct RepolockReadExiter(ReaderWriterLockSlim @lock, bool read) : IDisposable
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

    internal StateId? GetLastSnapshotId()
    {
        _repoLock.EnterReadLock();

        if (_sortedKnownStates.Count == 0)
            return null;
        return _sortedKnownStates.Max;
    }

    private void PopulateTrieNodeCache(CachedResource cachedResource)
    {
        Snapshot lastSnapshot;
        using (EnterRepolockReadOnly())
        {
            StateId? last = GetLastSnapshotId();
            if (last == null) return;
            if (!TryLeaseState(last.Value, out lastSnapshot)) return;
        }

        using var _ = lastSnapshot; // Dispose

        var memory = lastSnapshot.EstimateMemory(); // Note: This is slow, do it outside.
        foreach (var keyValuePair in memory)
        {
            _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
        }
        _knownStatesMemory.WithLabels("count").Inc(1);

        long sw = Stopwatch.GetTimestamp();
        _trieNodeCache.Add(lastSnapshot, cachedResource);
        _flatdiffimes.WithLabels("compaction", "add_to_trienode_cache").Observe(Stopwatch.GetTimestamp() - sw);

        _resourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, cachedResource);
        return;
    }

    internal StateId GetCurrentPersistedStateId()
    {
        using var reader = LeaseReader();
        return reader.CurrentState;
    }

    public SnapshotBundle? GatherReaderAtBaseBlock(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        // TODO: Throw if not enough or return null
        return GatherSnapshotBundle(baseBlock, usage);
    }

    private static Histogram _knownStatesSize = DevMetric.Factory.CreateHistogram("flatdiff_known_state_size", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part"],
            // Buckets = Histogram.LinearBuckets(0, 1, 100)
            Buckets = [1]
        });

    private SnapshotBundle GatherSnapshotBundle(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        // The current verdict on trying to use a linked list of snapshots is that it is error prone and hard to pull of
        long sw =  Stopwatch.GetTimestamp();
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        if (baseBlock == StateId.PreGenesis)
        {
            // Special case for pregenesis. Note: nethermind always try to generate genesis.
            return new SnapshotBundle(
                new ArrayPoolList<Snapshot>(0),
                new NoopPersistenceReader(),
                _trieNodeCache,
                _resourcePool,
                usage: usage);
        }

        StateId persistedState = GetCurrentPersistedStateId();

        StateId current = baseBlock;
        ArrayPoolList<Snapshot> snapshots = new(Math.Max(1, (int)(_knownStates.Count / _compactSize)));
        while(TryLeaseCompactedState(current, out Snapshot? snapshot) || TryLeaseState(current, out snapshot))
        {
            if (_logger.IsTrace) _logger.Trace($"Got {snapshot.From} -> {snapshot.To}");

            snapshots.Add(snapshot);
            if (snapshot.From == current) {
                break; // Some test commit two block with the same id, so we dont know the parent anymore.
            }

            current = snapshot.From;
            if (snapshot.From == persistedState)
            {
                break;
            }

            if (snapshot.From.blockNumber < persistedState.blockNumber)
            {
                break;
            }
        }

        _flatdiffimes.WithLabels("gather_cache", "gather").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();
        _knownStatesSize.Observe(snapshots.Count);

        // Note: By the time the previous loop finished checking all state, the persistencc may have added new state and removed some
        // entry in `_inMemorySnapshotStore`. Meaning, this need to be here instead of before the loop.
        IPersistence.IPersistenceReader persistenceReader = LeaseReader();
        if (current != baseBlock && persistenceReader.CurrentState.blockNumber != -1 && current.blockNumber > persistenceReader.CurrentState.blockNumber)
        {
            throw new Exception($"Non consecutive snapshots. Current {current} vs {persistenceReader.CurrentState}, {persistedState}, {baseBlock}, {_knownStates.TryGetValue(current, out var snapshot)}, ");
        }

        if (persistenceReader.CurrentState.blockNumber > baseBlock.blockNumber)
        {
            persistenceReader.Dispose();
            throw new InvalidOperationException($"Unable to prepare state before persisted state. Persisted state: {persistenceReader.CurrentState}, requested state: {baseBlock}");
        }

        // TODO: Measure this
        snapshots.Reverse();
        _flatdiffimes.WithLabels("gather_cache", "reverse").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {snapshots.Count} known states, {GetCurrentPersistedStateId()}");
        var res = new SnapshotBundle(
            snapshots,
            persistenceReader,
            _trieNodeCache,
            _resourcePool,
            usage: usage);
        _flatdiffimes.WithLabels("gather_cache", "done").Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public bool TryLeaseCompactedState(StateId stateId, out Snapshot entry)
    {
        int attempt = 0;
        while (_compactedKnownStates.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;
            attempt++;
            if (attempt > 10_000) throw new Exception($"Unable to acquire lease on compacted state {stateId}");
        }
        return false;
    }

    public bool TryLeaseState(StateId stateId, out Snapshot entry)
    {
        int attempt = 0;
        while (_knownStates.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;
            attempt++;
            if (attempt > 10_000) throw new Exception($"Unable to acquire lease on state {stateId}");
        }
        return false;
    }

    public void AddSnapshot(Snapshot snapshot, CachedResource cachedResource)
    {
        long sw = Stopwatch.GetTimestamp();

        StateId startingBlock = snapshot.From;
        StateId endBlock = snapshot.To;
        _flatdiffimes.WithLabels("add_snapshot", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.blockNumber} to {endBlock.blockNumber}");
        if (endBlock.blockNumber <= GetCurrentPersistedStateId().blockNumber)
        {
            _logger.Warn(
                $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {GetCurrentPersistedStateId()}");
            return;
        }

        if (_knownStates.TryAdd(endBlock, snapshot))
        {
            using (EnterRepolock())
            {
                _sortedKnownStates.Add(endBlock);
            }
        }

        _flatdiffimes.WithLabels("add_snapshot", "add_block").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

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
        await NotifyWhenSlow("add to bigcache", () => _persistenceRunner.AddToPersistence());
    }

    public void RemoveAndReleaseCompactedKnownState(StateId stateId)
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
        if (_knownStates.TryRemove(stateId, out var existingState))
        {
            using (EnterRepolock())
            {
                _sortedKnownStates.Remove(stateId);
            }
            var memory = existingState.EstimateMemory();
            foreach (var keyValuePair in memory)
            {
                _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Dec(keyValuePair.Value);
            }
            _knownStatesMemory.WithLabels("count").Dec();

            existingState.Dispose(); // After memory
        }
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Flush cache not implemented");
    }

    public bool HasStateForBlock(StateId stateId)
    {
        if (_knownStates.TryGetValue(stateId, out var snapshot))
        {
            return true;
        }

        if (GetCurrentPersistedStateId() == stateId) return true;
        return false;
    }

    public StateId? FindStateIdForStateRoot(Hash256 stateRoot)
    {
        using (EnterRepolockReadOnly())
        {
            foreach (var stateId in _sortedKnownStates)
            {
                if (stateId.stateRoot == stateRoot) return stateId;
            }
        }

        StateId? currentPersistedIdx = GetCurrentPersistedStateId();
        if (currentPersistedIdx?.stateRoot == stateRoot)
        {
            return currentPersistedIdx.Value;
        }

        return null;
    }

    public StateId? FindLatestAvailableState()
    {
        using (EnterRepolockReadOnly())
        {
            StateId? lastInMemory = GetLastSnapshotId();
            if (lastInMemory != null) return lastInMemory;

            return GetCurrentPersistedStateId();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _compactorTask;
        await _persistenceTask;
        ClearReaderCache();

        return;
    }

    public void OnStatePersisted(StateId stateId)
    {
        ArrayPoolList<StateId> statesBeforeStateId;
        using (EnterRepolock())
        {
            statesBeforeStateId = _sortedKnownStates
                .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(stateId.blockNumber, Keccak.MaxValue))
                .ToPooledList(0);
        }
        using var _ = statesBeforeStateId;

        foreach (var stateToRemove in statesBeforeStateId)
        {
            RemoveAndReleaseCompactedKnownState(stateToRemove);
            RemoveAndReleaseKnownState(stateToRemove);
        }

        ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(stateId.blockNumber));
    }

    public bool AddCompactedSnapshot(StateId stateId, Snapshot compactedSnapshot)
    {
        return _compactedKnownStates.TryAdd(stateId, compactedSnapshot);
    }
}
