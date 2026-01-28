// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat;

/// <summary>
/// The main top level FlatDb orchestrator.
/// </summary>
public class FlatDbManager : IFlatDbManager, IAsyncDisposable
{
    private static readonly TimeSpan GatherGiveUpDeadline = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly IPersistenceManager _persistenceManager;
    private readonly ISnapshotCompactor _snapshotCompactor;
    private readonly ISnapshotRepository _snapshotRepository;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly IResourcePool _resourcePool;

    // Cache for assembling `ReadOnlySnapshotBundle`. Its not actually slow, but its called 1.8k per sec so caching
    // it save a decent amount of CPU.
    private readonly ConcurrentDictionary<StateId, ReadOnlySnapshotBundle> _readonlySnapshotBundleCache = new();

    // First it go to here
    private readonly Task _compactorTask;
    private readonly Channel<StateId> _compactorJobs;

    // And here in parallel.
    // The node cache is kinda important for performance, so we want it populated as quickly as possible.
    private readonly Task _populateTrieNodeCacheTask;
    private readonly Channel<TransientResource> _populateTrieNodeCacheJobs;

    // Then eventually a compacted snapshot will be sent here where this will decide what to persist exactly
    private readonly Task _persistenceTask;
    private readonly Channel<StateId> _persistenceJobs;

    private readonly int _compactSize;

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
        IFlatDbConfig config,
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

        _compactSize = config.CompactSize;
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
        // We do this async because of the lock
        _snapshotRepository.AddStateId(stateId);

        if (_snapshotCompactor.DoCompactSnapshot(stateId))
        {
            ClearReadOnlyBundleCache();
        }

        if (stateId.BlockNumber % _compactSize == 0)
        {
            // Trigger persistence job.
            await _persistenceJobs.Writer.WriteAsync(stateId, cancellationToken);
        }
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

        _snapshotRepository.RemoveStatesUntil(currentPersistedStateId);
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
            while (true)
            {
                Task delayTask = Task.Delay(slowTime);
                if (await Task.WhenAny(jobTask, delayTask) == jobTask) break;
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

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock)
    {
        // Note to self: The current verdict on trying to use a linked list of snapshots is that it is error prone and
        // hard to pull of due to the constantly moving chain making invalidation hard.
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        if (baseBlock == StateId.PreGenesis)
        {
            // Special case for pregenesis. Note: nethermind always tries to generate genesis.
            return new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), _enableDetailedMetrics);
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
                    throw new InvalidOperationException($"Unable to gather {nameof(ReadOnlySnapshotBundle)} for block {baseBlock} in {Stopwatch.GetElapsedTime(sw)}");
                }

                int delayMs = Math.Min(1 << attempt, 100);  // 1, 2, 4, 8, 16, 32, 64, 100ms max
                Thread.Sleep(delayMs);
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
                    // Cannot assemble snapshot that reaches the persisted state snapshot. It could be that the snapshots was removed
                    // concurrently. We will retry.
                    snapshots.Dispose();
                    persistenceReader.Dispose();
                    attempt++;
                    continue;
                }
            }

            if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {snapshots.Count} known states, Reader state: {persistenceReader.CurrentState}. Persistence state: {_persistenceManager.GetCurrentPersistedStateId()}");

            ReadOnlySnapshotBundle res = new(snapshots, persistenceReader, _enableDetailedMetrics);

            res.TryLease();
            if (!_readonlySnapshotBundleCache.TryAdd(baseBlock, res))
            {
                res.Dispose();
            }

            Metrics.SnapshotBundleSize = snapshots.Count;
            return res;
        }
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
            return;
        }

        if (!_snapshotRepository.TryAddSnapshot(snapshot))
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
                // Ignore it, just dispose
                transientResource.Dispose();
            }

            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                if (_cancelTokenSource.Token.IsCancellationRequested) return; // When cancelled the queue stop

                if (_logger.IsWarn) _logger.Warn("Compactor job stall! Insufficient reorg depth or too slow persistence!");
                _compactorJobs.Writer.WriteAsync(endBlock).AsTask().Wait();
            }
        }
    }

    private void ClearReadOnlyBundleCache()
    {
        using ArrayPoolListRef<StateId> statesToRemove = new();
        statesToRemove.AddRange(_readonlySnapshotBundleCache.Keys);

        foreach (StateId stateId in statesToRemove)
        {
            if (_readonlySnapshotBundleCache.TryRemove(stateId, out ReadOnlySnapshotBundle? bundle))
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

        _snapshotRepository.RemoveStatesUntil(persistedState);

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
