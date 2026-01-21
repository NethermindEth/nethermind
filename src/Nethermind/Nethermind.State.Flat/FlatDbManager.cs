// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;
using Prometheus;

namespace Nethermind.State.Flat;

public class FlatDbManager : IFlatDbManager, IAsyncDisposable
{
    private const int MaxGatherAttempts = 16;

    private readonly ILogger _logger;
    private readonly PersistenceManager _persistenceManager;
    private readonly SnapshotCompactor _snapshotCompactor;
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly TrieNodeCache _trieNodeCache;
    private readonly ResourcePool _resourcePool;

    // Cache for assembling `ReadOnlySnapshotBundle`. Its not actually slow, but its called 1.8k per sec so caching
    // it save a decent amount of CPU.
    private readonly ConcurrentDictionary<StateId, ReadOnlySnapshotBundle> _readonlySnapshotBundleCache = new();

    // First it go to here
    private readonly Task _compactorTask;
    private Channel<StateId> _compactorJobs;

    // And here in parallel.
    // The node cache is kinda important for performance, so we want it populated as quickly as possible.
    private readonly Task _populateTrieNodeCacheTask;
    private Channel<TransientResource> _populateTrieNodeCacheJobs;

    // Then eventually a compacted snapshot will be sent here where this will decide what to compact exactly
    private readonly Task _persistenceTask;
    private Channel<StateId> _persistenceJobs;


    private readonly int _compactSize;
    private readonly int _midCompactSize;

    // For debugging. Do the compaction synchronously
    private readonly bool _inlineCompaction;
    private readonly CancellationTokenSource _cancelTokenSource;
    private int _isDisposed = 0;

    internal static Histogram _flatdiffimes = DevMetric.Factory.CreateHistogram("flatdiff_times", "aha", new HistogramConfiguration()
    {
        LabelNames = new[] { "category", "type" },
        // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
        Buckets = [1]
    });
    private static Histogram _knownStatesSize = DevMetric.Factory.CreateHistogram("flatdiff_known_state_size", "timer",
        new HistogramConfiguration()
        {
            LabelNames = ["part"],
            // Buckets = Histogram.LinearBuckets(0, 1, 100)
            Buckets = [1]
        });

    private static Gauge _snapshotCount = DevMetric.Factory.CreateGauge("flatdiff_snapshot_count", "memory", "category");

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public FlatDbManager(
        ResourcePool resourcePool,
        IProcessExitSource processExitSource,
        TrieNodeCache trieNodeCache,
        SnapshotCompactor snapshotCompactor,
        SnapshotRepository snapshotRepository,
        PersistenceManager persistenceManager,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _trieNodeCache = trieNodeCache;
        _snapshotCompactor = snapshotCompactor;
        _snapshotRepository = snapshotRepository;
        _resourcePool = resourcePool;
        _persistenceManager = persistenceManager;
        _logger = logManager.GetClassLogger<FlatDbManager>();

        _compactSize = config.CompactSize;
        _midCompactSize = config.MidCompactSize;
        _inlineCompaction = config.InlineCompaction;

        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _populateTrieNodeCacheJobs = Channel.CreateBounded<TransientResource>(1);
        _persistenceJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

        _compactorTask = RunCompactor(_cancelTokenSource.Token);
        _populateTrieNodeCacheTask = RunTrieCachePopulator(_cancelTokenSource.Token);
        _persistenceTask = RunPersistence(_cancelTokenSource.Token);
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
            {
                await NotifyWhenSlow($"Compacting {stateId}", async () =>
                {
                    await RunCompactJob(stateId, cancellationToken);
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunCompactJobSync(StateId stateId, TransientResource transientResource, CancellationToken cancellationToken)
    {
        PopulateTrieNodeCache(transientResource);
        await RunCompactJob(stateId, cancellationToken);
    }

    private async Task RunCompactJob(StateId stateId, CancellationToken cancellationToken)
    {
        long sw = Stopwatch.GetTimestamp();
        // We do this async because of the lock
        _snapshotRepository.AddStateId(stateId);
        _flatdiffimes.WithLabels("compact", "add_state_id").Observe(Stopwatch.GetTimestamp() - sw);

        sw = Stopwatch.GetTimestamp();
        if (_snapshotRepository.TryLeaseState(stateId, out Snapshot? snapshot))
        {
            using var _ = snapshot; // dispose

            // Actually do the compaction
            _snapshotCompactor.DoCompactSnapshot(snapshot);

            if (stateId.BlockNumber % _compactSize == 0 || stateId.BlockNumber % _midCompactSize == 0)
            {
                ClearReadOnlyBundleCache();
            }

            if (stateId.BlockNumber % _compactSize == 0)
            {
                _flatdiffimes.WithLabels("compact", "do_compact_full").Observe(Stopwatch.GetTimestamp() - sw);
            }
            else
            {
                if (stateId.BlockNumber % _midCompactSize == 0)
                {
                    _flatdiffimes.WithLabels("compact", "do_mid_compact").Observe(Stopwatch.GetTimestamp() - sw);
                }
                else
                {
                    _flatdiffimes.WithLabels("compact", "do_compact").Observe(Stopwatch.GetTimestamp() - sw);
                }
            }
        }

        sw = Stopwatch.GetTimestamp();
        if (stateId.BlockNumber % _compactSize == 0)
        {
            _snapshotCount.WithLabels("snapshots").Set(_snapshotRepository.SnapshotCount);
            _snapshotCount.WithLabels("compacted_snapshots").Set(_snapshotRepository.CompactedSnapshotCount);
            // Trigger persistence job.
            await _persistenceJobs.Writer.WriteAsync(stateId, cancellationToken);
            _flatdiffimes.WithLabels("compact", "persist_queue_signal").Observe(Stopwatch.GetTimestamp() - sw);
        }
    }

    private async Task RunPersistence(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var stateId in _persistenceJobs.Reader.ReadAllAsync(cancellationToken))
            {
                await NotifyWhenSlow($"Persisting {stateId}", () =>
                {
                    PersistIfNeeded(stateId);
                    return Task.CompletedTask;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PersistIfNeeded(StateId latestSnapshot)
    {
        _persistenceManager.AddToPersistence(latestSnapshot);

        StateId currentPersistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (currentPersistedStateId == StateId.PreGenesis) return;

        _snapshotRepository.RemoveStatesUntil(currentPersistedStateId);
        ClearReadOnlyBundleCache();
        ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(currentPersistedStateId.BlockNumber));
    }

    private async Task RunTrieCachePopulator(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var cachedResource in _populateTrieNodeCacheJobs.Reader.ReadAllAsync(cancellationToken))
            {
                await NotifyWhenSlow("Populating trie node cache", () =>
                {
                    PopulateTrieNodeCache(cachedResource);
                    return Task.CompletedTask;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void PopulateTrieNodeCache(TransientResource transientResource)
    {
        _trieNodeCache.Add(transientResource);
        _resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
    }

    private async Task NotifyWhenSlow(string name, Func<Task> closure)
    {
        TimeSpan slowTime = TimeSpan.FromSeconds(2);

        Task jobTask = Task.Run(async () =>
        {
            long sw = Stopwatch.GetTimestamp();
            try
            {
                await closure();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Error on {name}", ex);
            }
            if (_logger.IsTrace) _logger.Trace($"{name} took {Stopwatch.GetElapsedTime(sw)}");
        });

        _ = Task.Run(async () =>
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                Task delayTask = Task.Delay(slowTime);
                if (await Task.WhenAny(jobTask, delayTask) == jobTask) break;
                _logger.Warn($"Slow task \"{name}\". Took {sw.Elapsed}");
            }
        });

        await jobTask;
    }

    public SnapshotBundle GatherReaderAtBaseBlock(StateId baseBlock, ResourcePool.Usage usage)
    {
        return GatherSnapshotBundle(baseBlock, usage);
    }

    public ReadOnlySnapshotBundle GatherReadOnlyReaderAtBaseBlock(StateId baseBlock)
    {
        return GatherReadOnlySnapshotBundle(baseBlock);
    }

    private ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(StateId baseBlock)
    {
        // The current verdict on trying to use a linked list of snapshots is that it is error prone and hard to pull of
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        if (baseBlock == StateId.PreGenesis)
        {
            // Special case for pregenesis. Note: nethermind always try to generate genesis.
            return new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader());
        }

        // Fastpath: Share recently created ReadOnlySnapshotBundle
        if (_readonlySnapshotBundleCache.TryGetValue(baseBlock, out ReadOnlySnapshotBundle? bundle) && bundle.TryLease())
        {
            return bundle;
        }

        int attempt = 0;
        while (attempt < MaxGatherAttempts)
        {
            if (attempt != 0)
            {
                Thread.Yield();
            }

            IPersistence.IPersistenceReader persistenceReader = _persistenceManager.LeaseReader();
            SnapshotPooledList snapshots;
            try
            {
                snapshots = _snapshotRepository.AssembleSnapshots(
                    baseBlock,
                    persistenceReader.CurrentState,
                    estimatedSize: Math.Max(1, _snapshotRepository.SnapshotCount / _compactSize));
            }
            catch (Exception)
            {
                persistenceReader.Dispose();
                throw;
            }
            _knownStatesSize.Observe(snapshots.Count);

            if (snapshots.Count == 0)
            {
                if (persistenceReader.CurrentState != baseBlock)
                {
                    persistenceReader.Dispose();
                    throw new InvalidOperationException($"Unable to gather snapshots for state {baseBlock}.");
                }
            }
            else
            {
                if (snapshots[0].From != persistenceReader.CurrentState)
                {
                    // Cannot assemble snapshot that reach the persisted state snapshot. It could be that the snapshots was removed
                    // concurrently. We will retry.
                    snapshots.Dispose();
                    persistenceReader.Dispose();
                    attempt++;
                    continue;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {snapshots.Count} known states, Reader state: {persistenceReader.CurrentState}. Persistence state: {_persistenceManager.GetCurrentPersistedStateId()}");

            ReadOnlySnapshotBundle res = new ReadOnlySnapshotBundle(
                snapshots,
                persistenceReader);

            res.TryLease();
            if (!_readonlySnapshotBundleCache.TryAdd(baseBlock, res))
            {
                res.Dispose();
            }

            return res;
        }

        throw new InvalidOperationException($"Unable to gather {nameof(ReadOnlySnapshotBundle)} for block {baseBlock}");
    }

    private SnapshotBundle GatherSnapshotBundle(StateId baseBlock, ResourcePool.Usage usage)
    {
        // The current verdict on trying to use a linked list of snapshots is that it is error prone and hard to pull of
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");
        ReadOnlySnapshotBundle readOnlySnapshotBundle = GatherReadOnlySnapshotBundle(baseBlock);
        return new SnapshotBundle(
            readOnlySnapshotBundle,
            _trieNodeCache,
            _resourcePool,
            usage: usage);
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        long sw = Stopwatch.GetTimestamp();

        StateId startingBlock = snapshot.From;
        StateId endBlock = snapshot.To;
        _flatdiffimes.WithLabels("add_snapshot", "repolock").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.BlockNumber} to {endBlock.BlockNumber}");
        StateId persistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (endBlock.BlockNumber <= persistedStateId.BlockNumber)
        {
            _logger.Warn(
                $"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.BlockNumber}, bigcache number: {persistedStateId}");
            return;
        }

        if (!_snapshotRepository.TryAddSnapshot(snapshot))
        {
            _logger.Warn($"State {snapshot.To} already added");
            _resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
            snapshot.Dispose();
            return;
        }

        _flatdiffimes.WithLabels("add_snapshot", "add_block").Observe(Stopwatch.GetTimestamp() - sw);
        sw = Stopwatch.GetTimestamp();

        if (_inlineCompaction)
        {
            RunCompactJobSync(endBlock, transientResource, _cancelTokenSource.Token).Wait();
        }
        else
        {
            if (!_populateTrieNodeCacheJobs.Writer.TryWrite(transientResource))
            {
                // Ignore it, just dispose
                transientResource.Dispose();
            }

            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                if (_cancelTokenSource.Token.IsCancellationRequested) return; // When cancelled the queue stop

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

    private void ClearReadOnlyBundleCache()
    {
        using ArrayPoolListRef<StateId> statesToRemove = new ArrayPoolListRef<StateId>();
        statesToRemove.AddRange(_readonlySnapshotBundleCache.Keys);

        foreach (var stateId in statesToRemove)
        {
            if (_readonlySnapshotBundleCache.TryRemove(stateId, out ReadOnlySnapshotBundle? bundle))
            {
                bundle.Dispose();
            }
        }
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("Flush cache not implemented");
    }

    public bool HasStateForBlock(StateId stateId)
    {
        if (_snapshotRepository.HasState(stateId)) return true;
        if (_persistenceManager.GetCurrentPersistedStateId() == stateId) return true;
        return false;
    }

    public IPersistence.IPersistenceReader CreateReader()
    {
        return _persistenceManager.LeaseReader();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        _cancelTokenSource.Cancel();

        _compactorJobs.Writer.Complete();
        _populateTrieNodeCacheJobs.Writer.Complete();
        _persistenceJobs.Writer.Complete();

        await _compactorTask;
        await _populateTrieNodeCacheTask;
        await _persistenceTask;

        _cancelTokenSource.Dispose();
    }
}
