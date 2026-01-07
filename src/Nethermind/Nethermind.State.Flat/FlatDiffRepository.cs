// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNext.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat;

public class FlatDiffRepository : IFlatDiffRepository, IAsyncDisposable
{
    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedKnownStates = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _knownStates = new();
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedKnownStates = new(new SortedSet<StateId>());
    private readonly TrieNodeCache _trieNodeCache;
    private readonly ResourcePool _resourcePool;

    private readonly ConcurrentDictionary<StateId, ReadOnlySnapshotBundle> _readonlySnapshotBundleCache = new();

    private readonly Task _compactorTask;
    private readonly Task _persistenceTask;
    private readonly Task _populateTrieNodeCacheTask;

    private Channel<StateId> _compactorJobs;
    private Channel<CachedResource> _populateTrieNodeCacheJob;
    private Channel<StateId> _persistenceJob;

    private long _compactSize;
    private readonly bool _inlineCompaction;
    private ILogger _logger;
    private readonly PersistenceManager _persistenceManager;
    private readonly SnapshotCompactor _snapshotCompactor;
    private readonly IProcessExitSource _processExitSource;

    internal static Histogram _flatdiffimes = DevMetric.Factory.CreateHistogram("flatdiff_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "category", "type" },
        // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
        Buckets = [1]
    });

    private static Gauge _knownStatesMemory = DevMetric.Factory.CreateGauge("flatdiff_knownstates_memory", "memory", "category");
    private static Gauge _compactedMemory = DevMetric.Factory.CreateGauge("flatdiff_compacted_memory", "memory", "category");
    private static Gauge _snapshotCount = DevMetric.Factory.CreateGauge("flatdiff_snapshot_count", "memory", "category");

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
        bool DisableTrieWarmer = false,
        bool WarmUpPersistence = false)
    {
    }

    public FlatDiffRepository(
        IProcessExitSource exitSource,
        IFinalizedStateProvider finalizedStateProvider,
        IPersistence persistedPersistence,
        ResourcePool resourcePool,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        Configuration? config = null)
    {
        if (config is null) config = new Configuration();
        _compactSize = config.CompactSize;
        _inlineCompaction = config.InlineCompaction;
        _resourcePool = resourcePool;
        _processExitSource = processExitSource;
        _logger = logManager.GetClassLogger<FlatDiffRepository>();

        if (config.WarmUpPersistence)
        {
            persistedPersistence.WarmUpWhole(processExitSource.Token);
        }

        _persistenceManager = new PersistenceManager(
            this,
            config,
            finalizedStateProvider,
            persistedPersistence,
            processExitSource,
            logManager);
        _snapshotCompactor = new SnapshotCompactor(this, config, resourcePool, logManager);

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _populateTrieNodeCacheJob = Channel.CreateBounded<CachedResource>(1);
        _persistenceJob = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

        _trieNodeCache = new TrieNodeCache(config.TrieCacheMemoryTarget, logManager);

        _compactorTask = RunCompactor(exitSource.Token);
        _populateTrieNodeCacheTask = RunTrieCachePopulator(exitSource.Token);
        _persistenceTask = RunPersistence(exitSource.Token);
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
            {
                await RunCompactJob(stateId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunTrieCachePopulator(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var cachedResource in _populateTrieNodeCacheJob.Reader.ReadAllAsync(cancellationToken))
            {
                PopulateTrieNodeCache(cachedResource);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunCompactJob(StateId stateId, CancellationToken cancellationToken)
    {
        try
        {
            // We do this async because of the lock
            using (var _ = _sortedKnownStates.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Add(stateId);
            }

            _snapshotCompactor.CompactLevel(stateId);
            if (stateId.blockNumber % _compactSize == 0)
            {
                _snapshotCount.WithLabels("snapshots").Set(_knownStates.Count);
                _snapshotCount.WithLabels("compacted_snapshots").Set(_compactedKnownStates.Count);
                await _persistenceJob.Writer.WriteAsync(stateId, cancellationToken);
            }

            if (_readonlySnapshotBundleCache.TryRemove(stateId, out ReadOnlySnapshotBundle? bundle))
            {
                bundle.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error("Compact job failed", ex);
            throw;
        }
    }

    private async Task RunCompactJobSync(StateId stateId, CachedResource cachedResource, CancellationToken cancellationToken)
    {
        PopulateTrieNodeCache(cachedResource);

        await RunCompactJob(stateId, cancellationToken);
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

    internal ArrayPoolList<StateId> GetStatesAfterBlock(long blockNumber)
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new StateId(blockNumber + 1, ValueKeccak.Zero);
        StateId max = new StateId(long.MaxValue, ValueKeccak.Zero);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    internal ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new StateId(blockNumber, ValueKeccak.Zero);
        StateId max = new StateId(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    internal StateId? GetLastSnapshotId()
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        if (sortedSnapshots.Count == 0)
            return null;
        return sortedSnapshots.Max;
    }

    private void PopulateTrieNodeCache(CachedResource cachedResource)
    {
        // TODO: When unable to populate, the cached resource need to have its node unresolved or it will memleak.

        StateId? last = GetLastSnapshotId();
        if (last == null) return;

        Snapshot? lastSnapshot;
        if (!TryLeaseState(last.Value, out lastSnapshot)) return;

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
    }

    public SnapshotBundle GatherReaderAtBaseBlock(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        // TODO: Throw if not enough or return null
        return GatherSnapshotBundle(baseBlock, usage);
    }

    public ReadOnlySnapshotBundle GatherReadOnlyReaderAtBaseBlock(StateId baseBlock)
    {
        return GatherReadOnlySnapshotBundle(baseBlock);
    }

    private static Histogram _knownStatesSize = DevMetric.Factory.CreateHistogram("flatdiff_known_state_size", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part"],
            // Buckets = Histogram.LinearBuckets(0, 1, 100)
            Buckets = [1]
        });

    private ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(StateId baseBlock)
    {
        // The current verdict on trying to use a linked list of snapshots is that it is error prone and hard to pull of
        long sw =  Stopwatch.GetTimestamp();
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        if (baseBlock == StateId.PreGenesis)
        {
            // Special case for pregenesis. Note: nethermind always try to generate genesis.
            return new ReadOnlySnapshotBundle(new ArrayPoolList<Snapshot>(0), new NoopPersistenceReader());
        }

        if (_readonlySnapshotBundleCache.TryGetValue(baseBlock, out ReadOnlySnapshotBundle? bundle) && bundle.TryLease())
        {
            return bundle;
        }

        StateId persistedState = _persistenceManager.GetCurrentPersistedStateId();

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

        _flatdiffimes.WithLabels("gather_readonly_cache", "gather").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();
        _knownStatesSize.Observe(snapshots.Count);

        // Note: By the time the previous loop finished checking all state, the persistencc may have added new state and removed some
        // entry in `_inMemorySnapshotStore`. Meaning, this need to be here instead of before the loop.
        IPersistence.IPersistenceReader persistenceReader = _persistenceManager.LeaseReader();
        if (current != baseBlock && persistenceReader.CurrentState.blockNumber != -1 && current.blockNumber > persistenceReader.CurrentState.blockNumber)
        {
            persistenceReader.Dispose();
            throw new Exception($"Non consecutive snapshots. Current {current} vs {persistenceReader.CurrentState}, {persistedState}, {baseBlock}, {_knownStates.TryGetValue(current, out var snapshot)}, ");
        }

        if (persistenceReader.CurrentState.blockNumber > baseBlock.blockNumber)
        {
            persistenceReader.Dispose();
            throw new InvalidOperationException($"Unable to prepare state before persisted state. Persisted state: {persistenceReader.CurrentState}, requested state: {baseBlock}");
        }

        // TODO: Measure this
        snapshots.Reverse();
        _flatdiffimes.WithLabels("gather_readonly_cache", "reverse").Observe(Stopwatch.GetTimestamp() - sw);
        sw =  Stopwatch.GetTimestamp();

        if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {snapshots.Count} known states, {_persistenceManager.GetCurrentPersistedStateId()}");
        var res = new ReadOnlySnapshotBundle(
            snapshots,
            persistenceReader);
        _flatdiffimes.WithLabels("gather_readonly_cache", "done").Observe(Stopwatch.GetTimestamp() - sw);

        res.TryLease();
        if (!_readonlySnapshotBundleCache.TryAdd(baseBlock, res))
        {
            res.Dispose();
        }

        return res;
    }

    private SnapshotBundle GatherSnapshotBundle(StateId baseBlock, IFlatDiffRepository.SnapshotBundleUsage usage)
    {
        // The current verdict on trying to use a linked list of snapshots is that it is error prone and hard to pull of
        long sw =  Stopwatch.GetTimestamp();
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");
        ReadOnlySnapshotBundle readOnlySnapshotBundle = GatherReadOnlySnapshotBundle(baseBlock);
        _flatdiffimes.WithLabels("gather_cache", "gather_readonly").Observe(Stopwatch.GetTimestamp() - sw);

        if (baseBlock == StateId.PreGenesis)
        {
            // Special case for pregenesis. Note: nethermind always try to generate genesis.
            return new SnapshotBundle(
                readOnlySnapshotBundle,
                new ArrayPoolList<Snapshot>(1),
                _trieNodeCache,
                _resourcePool,
                usage: usage);
        }

        sw =  Stopwatch.GetTimestamp();

        var res = new SnapshotBundle(
            readOnlySnapshotBundle,
            new ArrayPoolList<Snapshot>(1),
            _trieNodeCache,
            _resourcePool,
            usage: usage);

        _flatdiffimes.WithLabels("gather_cache", "done").Observe(Stopwatch.GetTimestamp() - sw);
        return res;
    }

    public bool TryLeaseCompactedState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
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

    public bool TryLeaseState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
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
        StateId persistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (endBlock.blockNumber <= persistedStateId.blockNumber)
        {
            _logger.Warn(
                $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.blockNumber}, bigcache number: {persistedStateId}");
            return;
        }

        if (!_knownStates.TryAdd(endBlock, snapshot))
        {
            _logger.Warn($"State {snapshot.To} already added");
            _resourcePool.ReturnCachedResource(IFlatDiffRepository.SnapshotBundleUsage.MainBlockProcessing, cachedResource);
            snapshot.Dispose();
            return;
        }

        _flatdiffimes.WithLabels("add_snapshot", "add_block").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        if (_inlineCompaction)
        {
            RunCompactJobSync(endBlock, cachedResource, _processExitSource.Token).Wait();
        }
        else
        {
            while (!_populateTrieNodeCacheJob.Writer.TryWrite(cachedResource))
            {
                if (_processExitSource.Token.IsCancellationRequested) break; // When cancelled the queue stop

                _logger.Warn("Trie node cache job stall");
                _populateTrieNodeCacheJob.Writer.WaitToWriteAsync().Wait();
            }

            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                if (_processExitSource.Token.IsCancellationRequested) return; // When cancelled the queue stop

                _flatdiffimes.WithLabels("add_snapshot", "try_write_failed").Observe(Stopwatch.GetTimestamp() - sw);
                sw = Stopwatch.GetTimestamp();
                _logger.Warn("Compactor job stall!");
                _compactorJobs.Writer.WriteAsync(endBlock).AsTask().Wait();
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
        await NotifyWhenSlow("add to bigcache", () =>
        {
            _persistenceManager.AddToPersistence();
            ClearReadOnlyBundleCache();
        });
    }

    private void ClearReadOnlyBundleCache()
    {
        using ArrayPoolListRef<StateId> states = new ArrayPoolListRef<StateId>();
        foreach (var kv in _readonlySnapshotBundleCache)
        {
            states.Add(kv.Key);
        }

        foreach (var stateId in states)
        {
            if (_readonlySnapshotBundleCache.TryRemove(stateId, out ReadOnlySnapshotBundle? bundle))
            {
                bundle.Dispose();
            }
        }
    }

    public void RemoveAndReleaseCompactedKnownState(StateId stateId)
    {
        if (_compactedKnownStates.TryRemove(stateId, out var existingState))
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
        if (_knownStates.TryRemove(stateId, out var existingState))
        {
            using (var _ = _sortedKnownStates.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
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

        if (_persistenceManager.GetCurrentPersistedStateId() == stateId) return true;
        return false;
    }

    public StateId? FindStateIdForStateRoot(Hash256 stateRoot)
    {
        using (var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots))
        {
            foreach (var stateId in sortedSnapshots)
            {
                if (stateId.stateRoot == stateRoot) return stateId;
            }
        }

        StateId? currentPersistedIdx = _persistenceManager.GetCurrentPersistedStateId();
        if (currentPersistedIdx?.stateRoot == stateRoot)
        {
            return currentPersistedIdx.Value;
        }

        return null;
    }

    public StateId? FindLatestAvailableState()
    {
        StateId? lastInMemory = GetLastSnapshotId();
        if (lastInMemory != null) return lastInMemory;

        return _persistenceManager.GetCurrentPersistedStateId();
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        return _persistenceManager.LeaseReader();
    }

    public async ValueTask DisposeAsync()
    {
        await _compactorTask;
        await _persistenceTask;
        await _populateTrieNodeCacheTask;
        await _persistenceManager.DisposeAsync();
    }

    public void OnStatePersisted(StateId stateId)
    {
        ArrayPoolList<StateId> statesBeforeStateId;
        using (var _s = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapashots))
        {
            statesBeforeStateId = sortedSnapashots
                .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(stateId.blockNumber, Keccak.MaxValue))
                .ToPooledList(0);
        }
        using var _ = statesBeforeStateId; // auto dispose

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
