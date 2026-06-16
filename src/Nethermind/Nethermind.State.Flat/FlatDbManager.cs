// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Core.Attributes;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

public class FlatDbManager : IFlatDbManager, IAsyncDisposable
{
    private static readonly TimeSpan GatherGiveUpDeadline = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly IPersistenceManager _persistenceManager;
    private readonly ISnapshotCompactor _snapshotCompactor;
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly IResourcePool _resourcePool;

    // Assembling a ReadOnlySnapshotBundle is called ~1.8k/sec; caching saves meaningful CPU even though each
    // individual assembly is fast.
    private readonly ConcurrentDictionary<StateId, ReadOnlySnapshotBundle> _readonlySnapshotBundleCache = new();

    // Pipeline stage 1: compaction. Runs concurrently with stage 2.
    private readonly Task _compactorTask;
    private readonly Channel<StateId> _compactorJobs;

    // Pipeline stage 2 (parallel with stage 1): populate trie node cache as quickly as possible
    // because it is critical for read performance.
    private readonly Task _populateTrieNodeCacheTask;
    private readonly Channel<TransientResource> _populateTrieNodeCacheJobs;

    // Pipeline stage 3: decide what to actually persist once a compacted snapshot is ready.
    private readonly Task _persistenceTask;
    private readonly Channel<StateId> _persistenceJobs;

    private readonly Task _clearBundleCacheTask;

    private readonly int _compactSize;
    private readonly TimeSpan _compactorStallTimeout;

    // For debugging. Do the compaction synchronously
    private readonly bool _inlineCompaction;
    private readonly CancellationTokenSource _cancelTokenSource;
    private int _isDisposed = 0;
    private readonly bool _enableDetailedMetrics;

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public FlatDbManager(
        IResourcePool resourcePool,
        IProcessExitSource processExitSource,
        ITrieNodeCache trieNodeCache,
        ISnapshotCompactor snapshotCompactor,
        ISnapshotRepository snapshotRepository,
        IPersistenceManager persistenceManager,
        IPersistedSnapshotLoader persistedSnapshotLoader,
        IFlatDbConfig config,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        bool enableDetailedMetrics)
    {
        _trieNodeCache = trieNodeCache;
        _snapshotCompactor = snapshotCompactor;
        _snapshotRepository = snapshotRepository;
        _resourcePool = resourcePool;
        _persistenceManager = persistenceManager;
        _logger = logManager.GetClassLogger<FlatDbManager>();
        _enableDetailedMetrics = enableDetailedMetrics;

        // Must run before any background worker or read can access the persisted tier.
        persistedSnapshotLoader.Load();

        _compactSize = config.CompactSize;

        // Persistence must complete within half a slot time per compactSize blocks to keep up with the network.
        // Timeout = 0.5 * slotTime * compactSize.
        _compactorStallTimeout = TimeSpan.FromSeconds(0.5 * blocksConfig.SecondsPerSlot * _compactSize);
        _inlineCompaction = config.InlineCompaction;

        _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _populateTrieNodeCacheJobs = Channel.CreateBounded<TransientResource>(1);
        _persistenceJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

        _compactorTask = RunCompactor(_cancelTokenSource.Token);
        _populateTrieNodeCacheTask = RunTrieCachePopulator(_cancelTokenSource.Token);
        _persistenceTask = RunPersistence(_cancelTokenSource.Token);
        _clearBundleCacheTask = RunClearBundleCache(_cancelTokenSource.Token);
    }

    private async Task RunCompactor(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId stateId in _compactorJobs.Reader.ReadAllAsync(cancellationToken))
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
        // AddStateId acquires a lock; running via async avoids blocking the caller.
        _snapshotRepository.AddStateId(stateId);

        if (_snapshotCompactor.DoCompactSnapshot(stateId))
        {
            ClearReadOnlyBundleCache();
        }

        await _persistenceJobs.Writer.WriteAsync(stateId, cancellationToken);
    }

    private async Task RunPersistence(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId stateId in _persistenceJobs.Reader.ReadAllAsync(cancellationToken))
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

    private void PersistIfNeeded(in StateId latestSnapshot)
    {
        _persistenceManager.AddToPersistence(latestSnapshot);

        StateId currentPersistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (currentPersistedStateId == StateId.PreGenesis) return;

        ClearReadOnlyBundleCache();
        ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(currentPersistedStateId.BlockNumber));
    }

    private async Task RunTrieCachePopulator(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TransientResource cachedResource in _populateTrieNodeCacheJobs.Reader.ReadAllAsync(cancellationToken))
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
                if (_logger.IsError) _logger.Error($"Error on {name}", ex);
            }
            if (_logger.IsTrace) _logger.Trace($"{name} took {Stopwatch.GetElapsedTime(sw)}");
        });

        _ = Task.Run(async () =>
        {
            long sw = Stopwatch.GetTimestamp();
            using CancellationTokenSource cts = new();
            while (true)
            {
                Task delayTask = Task.Delay(slowTime, cts.Token);
                if (await Task.WhenAny(jobTask, delayTask) == jobTask)
                {
                    await cts.CancelAsync();
                    break;
                }
                if (_logger.IsWarn) _logger.Warn($"Slow task \"{name}\". Took {Stopwatch.GetElapsedTime(sw)}");
            }
        });

        await jobTask;
    }

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage)
    {
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");
        return new SnapshotBundle(
            GatherReadOnlySnapshotBundle(baseBlock),
            _trieNodeCache,
            _resourcePool,
            usage: usage);
    }

    private static readonly StringLabel _depthInMemoryLabel = new("in_memory");
    private static readonly StringLabel _depthPersistedLabel = new("persisted");

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        // A linked-list snapshot chain was considered but rejected: the constantly moving chain makes
        // invalidation error-prone.
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        if (baseBlock == StateId.PreGenesis)
        {
            // PreGenesis is a sentinel; Nethermind always generates genesis, so this path is always transient.
            return new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), _enableDetailedMetrics, PersistedSnapshotStack.Empty(_enableDetailedMetrics));
        }

        long sw = 0;
        int attempt = 0;
        while (true)
        {
            // Fastpath: Share a recently created ReadOnlySnapshotBundle
            if (_readonlySnapshotBundleCache.TryGetValue(baseBlock, out ReadOnlySnapshotBundle? bundle) && bundle.TryLease()) return bundle;

            if (attempt == 1) sw = Stopwatch.GetTimestamp();
            if (attempt != 0)
            {
                if (Stopwatch.GetElapsedTime(sw) > GatherGiveUpDeadline)
                {
                    throw new InvalidOperationException($"Timed out gathering {nameof(ReadOnlySnapshotBundle)} for block {baseBlock} after {attempt} retries over {Stopwatch.GetElapsedTime(sw)}");
                }

                int delayMs = Math.Min(1 << Math.Min(attempt, 30), 100);  // 1, 2, 4, 8, 16, 32, 64, 100ms max
                Thread.Sleep(delayMs);
            }

            IPersistence.IPersistenceReader persistenceReader = _persistenceManager.LeaseReader();
            AssembledSnapshotResult assembled;
            try
            {
                assembled = _snapshotRepository.AssembleSnapshots(
                    baseBlock,
                    persistenceReader.CurrentState,
                    estimatedSize: Math.Max(1, _snapshotRepository.SnapshotCount / _compactSize));
            }
            catch (Exception)
            {
                persistenceReader.Dispose();
                throw;
            }

            // Empty result + reader not at baseBlock means the path was removed concurrently;
            // retry unless baseBlock itself was pruned (orphaned), which no retry can recover.
            if (assembled.SnapshotCount == 0 && persistenceReader.CurrentState != baseBlock)
            {
                assembled.Dispose();
                persistenceReader.Dispose();

                if (!_snapshotRepository.HasState(baseBlock))
                {
                    throw new InvalidOperationException($"State {baseBlock} no longer exists; concurrently removed.");
                }

                attempt++;
                continue;
            }

            if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {assembled.InMemory.Count} known states, {assembled.Persisted.Count} persisted, Reader state: {persistenceReader.CurrentState}. Persistence state: {_persistenceManager.GetCurrentPersistedStateId()}");

            ReportBundleMetrics(assembled);

            ReadOnlySnapshotBundle res = new(assembled.InMemory, persistenceReader, _enableDetailedMetrics,
                new PersistedSnapshotStack(assembled.Persisted, _enableDetailedMetrics));

            res.TryLease();
            if (!_readonlySnapshotBundleCache.TryAdd(baseBlock, res))
            {
                res.Dispose();
            }

            return res;
        }
    }

    private static void ReportBundleMetrics(in AssembledSnapshotResult assembled)
    {
        int inMemoryDepth = assembled.InMemory.Count > 0
            ? (int)(assembled.InMemory[^1].To.BlockNumber - assembled.InMemory[0].From.BlockNumber) : 0;
        int persistedDepth = assembled.Persisted.Count > 0
            ? (int)(assembled.Persisted[^1].To.BlockNumber - assembled.Persisted[0].From.BlockNumber) : 0;
        Metrics.SnapshotBundleBlockNumberDepth.Observe(inMemoryDepth, _depthInMemoryLabel);
        Metrics.SnapshotBundleBlockNumberDepth.Observe(persistedDepth, _depthPersistedLabel);

        Metrics.SnapshotBundleSize = assembled.InMemory.Count;
        Metrics.SnapshotBundlePersistedSnapshotSize = assembled.Persisted.Count;

        long persistedBytes = 0;
        for (int i = 0; i < assembled.Persisted.Count; i++)
            persistedBytes += assembled.Persisted[i].Size;
        Metrics.SnapshotBundlePersistedSnapshotMemory = persistedBytes;
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        StateId startingBlock = snapshot.From;
        StateId endBlock = snapshot.To;

        if (_logger.IsTrace) _logger.Trace($"Registering {startingBlock.BlockNumber} to {endBlock.BlockNumber}");
        StateId persistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (endBlock.BlockNumber <= persistedStateId.BlockNumber)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.BlockNumber}, bigcache number: {persistedStateId}");
            _resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
            snapshot.Dispose();
            return;
        }

        if (!_snapshotRepository.TryAdd(snapshot, SnapshotTier.InMemoryBase))
        {
            if (_logger.IsWarn) _logger.Warn($"State {snapshot.To} already added");
            _resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
            snapshot.Dispose();
            return;
        }

        if (_inlineCompaction)
        {
            RunCompactJobSync(endBlock, transientResource, _cancelTokenSource.Token).Wait();
        }
        else
        {
            if (!_populateTrieNodeCacheJobs.Writer.TryWrite(transientResource))
            {
                // Queue full, return to pool instead of leaking
                _resourcePool.ReturnCachedResource(ResourcePool.Usage.MainBlockProcessing, transientResource);
            }

            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                if (_cancelTokenSource.Token.IsCancellationRequested) return; // Channel is completed on cancellation; no point waiting

                // This wait only occurs after several blocks have already entered the queue without blocking,
                // so attempting to not block here to avoid blocking block processing is redundant.
                TimeSpan delay = _compactorStallTimeout;

                while (true)
                {
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cancelTokenSource.Token);
                    cts.CancelAfter(delay);

                    try
                    {
                        _compactorJobs.Writer.WriteAsync(endBlock, cts.Token).AsTask().Wait();
                        break;
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException && !_cancelTokenSource.Token.IsCancellationRequested)
                    {
                        delay = TimeSpan.FromSeconds(5);
                        if (_logger.IsWarn) _logger.Warn("Compactor job stall! Persistence is too slow for the network.");
                    }
                }
            }
        }
    }

    private async Task RunClearBundleCache(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ClearReadOnlyBundleCache();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearReadOnlyBundleCache()
    {
        foreach (KeyValuePair<StateId, ReadOnlySnapshotBundle> entry in _readonlySnapshotBundleCache)
        {
            if (_readonlySnapshotBundleCache.TryRemove(entry.Key, out ReadOnlySnapshotBundle? bundle))
            {
                bundle.Dispose();
            }
        }
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("FlatDbManager FlushCache started.");

        StateId persistedState = _persistenceManager.FlushToPersistence();

        if (cancellationToken.IsCancellationRequested) return;
        if (persistedState.BlockNumber < 0) return;

        ClearReadOnlyBundleCache();
        _trieNodeCache.Clear();

        if (_logger.IsInfo) _logger.Info($"FlatDbManager FlushCache completed. Persisted to {persistedState}.");
    }

    public bool HasStateForBlock(in StateId stateId)
    {
        if (_snapshotRepository.HasState(stateId)) return true;
        if (_persistenceManager.GetCurrentPersistedStateId() == stateId) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        ClearReadOnlyBundleCache();
        _cancelTokenSource.Cancel();

        _compactorJobs.Writer.Complete();
        _populateTrieNodeCacheJobs.Writer.Complete();
        _persistenceJobs.Writer.Complete();

        await _compactorTask;
        await _populateTrieNodeCacheTask;
        await _persistenceTask;
        await _clearBundleCacheTask;

        _cancelTokenSource.Dispose();
    }
}
