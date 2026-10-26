// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.Core.Attributes;
using Nethermind.State.Flat.PersistedSnapshots;

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
    private readonly INodeRecordBuffer _nodeRecordBuffer;
    private readonly IResourcePool _resourcePool;

    // Cache for assembling `ReadOnlySnapshotBundle`. Its not actually slow, but its called 1.8k per sec so caching
    // it save a decent amount of CPU.
    private readonly ConcurrentDictionary<StateId, ReadOnlySnapshotBundle> _readonlySnapshotBundleCache = new();

    private readonly Task _compactorTask;
    private readonly Channel<StateId> _compactorJobs;

    private readonly Task _persistenceTask;
    private readonly Channel<StateId> _persistenceJobs;

    private StateId _lastClearedPersistedStateId = StateId.PreGenesis;
    private long _lastClearedRemovedBaseSnapshotCount;

    // Periodically clear the ReadOnlySnapshotBundle cache to prevent stale entries
    private readonly Task _clearBundleCacheTask;

    private readonly int _compactSize;
    private readonly TimeSpan _compactorStallTimeout;

    // For debugging. Do the compaction synchronously
    private readonly bool _inlineCompaction;
    private readonly CancellationToken _processExitToken;
    private readonly CancellationTokenSource _cancelTokenSource;
    private int _isDisposed = 0;
    private readonly bool _enableDetailedMetrics;
    private readonly double _inMemorySlotFilterBitsPerKey;

    public FlatDbManager(
        IResourcePool resourcePool,
        IProcessExitSource processExitSource,
        INodeRecordBuffer nodeRecordBuffer,
        ISnapshotCompactor snapshotCompactor,
        ISnapshotRepository snapshotRepository,
        IPersistenceManager persistenceManager,
        IPersistedSnapshotLoader persistedSnapshotLoader,
        IFlatDbConfig config,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        bool enableDetailedMetrics)
    {
        _nodeRecordBuffer = nodeRecordBuffer;
        _snapshotCompactor = snapshotCompactor;
        _snapshotRepository = snapshotRepository;
        _resourcePool = resourcePool;
        _persistenceManager = persistenceManager;
        _logger = logManager.GetClassLogger<FlatDbManager>();
        _enableDetailedMetrics = enableDetailedMetrics;
        _inMemorySlotFilterBitsPerKey = config.InMemorySnapshotBloomBitsPerKey;

        // Must run before any background worker or read can access the persisted tier.
        persistedSnapshotLoader.Load();

        config.ValidateCompactSize();
        _compactSize = (int)config.CompactSize;

        // We assume that the state must be able to be persisted in half the slot time at the very
        // least. If block processing is stalled for longer than this, persistence is simply too slow
        // for the network. The timeout is 0.5 * blockTime * compactSize because persistence persists
        // compactSize blocks at a time.
        _compactorStallTimeout = TimeSpan.FromSeconds(0.5 * blocksConfig.SecondsPerSlot * _compactSize);
        _inlineCompaction = config.InlineCompaction;

        // Keep worker cancellation under this manager's control so process-exit cancellation cannot preempt the
        // ordered channel drain in DisposeAsync. A job that fails is logged and the worker takes the next one; only
        // a worker that stops, cancelled or failed outside its jobs, cancels the others, so no producer is left
        // waiting for space on a channel nobody reads any more.
        _processExitToken = processExitSource.Token;
        _cancelTokenSource = new();

        _compactorJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _persistenceJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);

        _compactorTask = RunCompactor(_cancelTokenSource.Token);
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
            _cancelTokenSource.Cancel();
        }
        catch
        {
            _cancelTokenSource.Cancel();
            throw;
        }
        finally
        {
            _compactorJobs.Writer.TryComplete();
        }
    }

    private async Task RunCompactJob(StateId stateId, CancellationToken cancellationToken)
    {
        // We do this async because of the lock
        _snapshotRepository.AddStateId(stateId);

        _snapshotCompactor.DoCompactSnapshot(stateId);

        // Trigger persistence job.
        await _persistenceJobs.Writer.WriteAsync(stateId, cancellationToken);
    }

    private async Task RunPersistence(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (StateId stateId in _persistenceJobs.Reader.ReadAllAsync(cancellationToken))
            {
                await NotifyWhenSlow($"Persisting {stateId}", () => PersistIfNeeded(stateId));
            }
        }
        catch (OperationCanceledException)
        {
            _cancelTokenSource.Cancel();
        }
        catch
        {
            _cancelTokenSource.Cancel();
            throw;
        }
        finally
        {
            // No producer may wait for space after the only consumer has stopped.
            _persistenceJobs.Writer.TryComplete();
        }
    }

    private async Task PersistIfNeeded(StateId latestSnapshot)
    {
        await _persistenceManager.AddToPersistence(latestSnapshot);

        StateId currentPersistedStateId = _persistenceManager.GetCurrentPersistedStateId();
        if (currentPersistedStateId == StateId.PreGenesis) return;

        long removedBaseSnapshotCount = _snapshotRepository.RemovedBaseSnapshotCount;
        if (currentPersistedStateId != _lastClearedPersistedStateId
            || removedBaseSnapshotCount != _lastClearedRemovedBaseSnapshotCount)
        {
            _lastClearedPersistedStateId = currentPersistedStateId;
            _lastClearedRemovedBaseSnapshotCount = removedBaseSnapshotCount;
            ClearReadOnlyBundleCache();
        }
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

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) =>
        GatherSnapshotBundle(baseBlock, usage, filterInMemorySlotReads: false);

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage, bool filterInMemorySlotReads)
    {
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");
        return new SnapshotBundle(
            GatherReadOnlySnapshotBundle(baseBlock),
            _nodeRecordBuffer,
            _resourcePool,
            usage: usage,
            filterInMemorySlotReads: filterInMemorySlotReads);
    }

    private static readonly StringLabel _depthInMemoryLabel = new("in_memory");
    private static readonly StringLabel _depthPersistedLabel = new("persisted");

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
        GatherReadOnlySnapshotBundle(baseBlock, ReaderFlags.None);

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags)
    {
        // A linked-list snapshot chain was considered but rejected: the constantly moving chain makes
        // invalidation error-prone.
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");

        // Bundles built with non-default reader flags hold specially-configured readers; keep them out of
        // the shared cache so ordinary consumers never inherit them, and never serve a cached (flagless)
        // bundle for such a request.
        bool shareable = readerFlags == ReaderFlags.None;

        if (baseBlock == StateId.PreGenesis)
        {
            // PreGenesis is a sentinel; Nethermind always generates genesis, so this path is always transient.
            return new ReadOnlySnapshotBundle(new SnapshotPooledList(0), new NoopPersistenceReader(), _enableDetailedMetrics, PersistedSnapshotStack.Empty(_enableDetailedMetrics), bufferPin: null);
        }

        long sw = 0;
        int attempt = 0;
        bool persistedAtBase = false;
        while (true)
        {
            // Fastpath: Share a recently created ReadOnlySnapshotBundle
            if (shareable && _readonlySnapshotBundleCache.TryGetValue(baseBlock, out ReadOnlySnapshotBundle? bundle) && bundle.TryLease()) return bundle;

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

            IPersistence.IPersistenceReader persistenceReader = _persistenceManager.LeaseReader(readerFlags);
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

                // Persistence publishes its pointer only once the write is readable, so while the pointer stays at
                // baseBlock, a reader leased after it got there reads baseBlock. One that doesn't means the pointer is
                // stale against the database (e.g. after the snap-sync wipe), which no retry can fix either.
                bool wasPersistedAtBase = persistedAtBase;
                persistedAtBase = _persistenceManager.GetCurrentPersistedStateId() == baseBlock;
                if (!HasStateForBlock(baseBlock) || (wasPersistedAtBase && persistedAtBase))
                {
                    throw new StateNotRetainedException($"No state available for block {baseBlock.BlockNumber} with state root {baseBlock.StateRoot}");
                }

                attempt++;
                continue;
            }

            if (_logger.IsTrace) _logger.Trace($"Gathered {baseBlock}. Got {assembled.InMemory.Count} known states, {assembled.Persisted.Count} persisted, Reader state: {persistenceReader.CurrentState}. Persistence state: {_persistenceManager.GetCurrentPersistedStateId()}");

            ReportBundleMetrics(assembled);

            // Flagged bundles are one-off full scans (trie verification) that never read through the slot filter, so
            // only shared bundles get bits per key. Nothing is built here: the first filtered read builds the filter.
            ReadOnlySnapshotBundle res = new(assembled.InMemory, persistenceReader, _enableDetailedMetrics,
                new PersistedSnapshotStack(assembled.Persisted, _enableDetailedMetrics), _nodeRecordBuffer.PinAll(),
                slotFilterBitsPerKey: shareable ? _inMemorySlotFilterBitsPerKey : 0);

            if (!shareable) return res;

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
        // PreGenesis (nothing persisted) carries the ulong.MaxValue sentinel, so a raw `<=` would reject
        // every snapshot; only reject when there genuinely is a persisted state at or above this block.
        if (persistedStateId != StateId.PreGenesis && endBlock.BlockNumber <= persistedStateId.BlockNumber)
        {
            if (_logger.IsWarn) _logger.Warn($"Cannot register snapshot earlier than bigcache. Snapshot number {endBlock.BlockNumber}, bigcache number: {persistedStateId}");
            transientResource.ReleaseLease();
            snapshot.Dispose();
            return;
        }

        if (!_snapshotRepository.TryAdd(snapshot, SnapshotTier.InMemoryBase))
        {
            if (_logger.IsWarn) _logger.Warn($"State {snapshot.To} already added");
            transientResource.ReleaseLease();
            snapshot.Dispose();
            return;
        }

        // The latest block the main processing scope committed; used as the head for forced persists.
        _snapshotRepository.SetLastCommittedStateId(endBlock);

        // The final release resets the block's warmed nodes, which is slow, so it is kept off the committing block thread.
        ThreadPool.UnsafeQueueUserWorkItem(static resource => resource.ReleaseLease(), transientResource, preferLocal: false);
        if (_inlineCompaction)
        {
            RunCompactJob(endBlock, _processExitToken).Wait();
        }
        else
        {
            if (!_compactorJobs.Writer.TryWrite(endBlock))
            {
                if (_processExitToken.IsCancellationRequested) return; // When cancelled the queue stop

                // Block processing is now stalled waiting for the compactor to drain the queue; measure how long.
                long stallStart = Stopwatch.GetTimestamp();

                // This wait only occurs after several blocks have already entered the queue without blocking,
                // so attempting to not block here to avoid blocking block processing is redundant.
                TimeSpan delay = _compactorStallTimeout;

                while (true)
                {
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
                        _processExitToken, _cancelTokenSource.Token);
                    cts.CancelAfter(delay);

                    try
                    {
                        _compactorJobs.Writer.WriteAsync(endBlock, cts.Token).AsTask().Wait();
                        break;
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException
                        && !_processExitToken.IsCancellationRequested
                        && !_cancelTokenSource.Token.IsCancellationRequested)
                    {
                        delay = TimeSpan.FromSeconds(5);
                        if (_logger.IsWarn) _logger.Warn("Compactor job stall! Persistence is too slow for the network.");
                    }
                }

                Metrics.CompactorStallTime.Observe(Stopwatch.GetTimestamp() - stallStart);
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

    /// <summary>
    /// Persist every snapshot up to the committed head into RocksDB and clear the caches.
    /// </summary>
    /// <remarks>
    /// Persists every tier up to the committed head, finalized or not, and prunes both tiers behind it.
    /// That collapses the reorg window to zero: the single RocksDB state ends up at an unfinalized block,
    /// and a later reorg below it cannot be served because the branch-point state no longer exists and
    /// <see cref="AddSnapshot"/> rejects snapshots at or below the persisted block. Only for special cases
    /// that need the state at the tip in RocksDB — genesis load and tests — never on the normal
    /// shutdown path, where the persisted-snapshot tier is already durable and reloaded on start.
    /// </remarks>
    public void FlushCache(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("FlatDbManager FlushCache started.");

        StateId persistedState = _persistenceManager.FlushToPersistence(cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;
        if (persistedState == StateId.PreGenesis) return;

        ClearReadOnlyBundleCache();

        if (_logger.IsInfo) _logger.Info($"FlatDbManager FlushCache completed. Persisted to {persistedState}.");
    }

    /// <summary>How long a clean shutdown may spend converting the in-memory tail.</summary>
    /// <remarks>
    /// Spent before the databases close, so a supervisor's stop grace period (Docker's default is 10 s) should leave
    /// room for it. The budget is checked between blocks; an in-flight flush or conversion may run past it.
    /// </remarks>
    internal static readonly TimeSpan ShutdownTailBudget = TimeSpan.FromSeconds(10);

    private void PersistInMemoryTail()
    {
        // Only after a clean drain: a worker that stopped early may have left the queue mid-persist.
        if (_cancelTokenSource.IsCancellationRequested) return;

        long start = Stopwatch.GetTimestamp();
        try
        {
            using CancellationTokenSource budget = new(ShutdownTailBudget);
            int converted = _persistenceManager.PersistInMemoryTailForShutdown(budget.Token);
            if (converted > 0 && _logger.IsInfo)
                _logger.Info($"Kept the state of the last {converted} blocks for the next start in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms.");
        }
        catch (Exception e)
        {
            // Best-effort conversion must not interrupt database disposal; the missing tail is replayed.
            if (_logger.IsWarn) _logger.Warn($"Could not keep the in-memory state for the next start; it will be re-executed. {e}");
        }
    }

    public bool HasStateForBlock(in StateId stateId)
    {
        if (_snapshotRepository.HasState(stateId)) return true;
        if (_persistenceManager.GetCurrentPersistedStateId() == stateId) return true;
        return false;
    }

    public void DropStateNotReachableFrom(in StateId head)
    {
        _persistenceManager.DropStateNotReachableFrom(head);
        // Cached bundles lease the snapshots they were assembled over; without this the pruned ones stay
        // alive until the periodic clear.
        ClearReadOnlyBundleCache();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Drains the queues in feed order — the compactor writes into the persistence queue, so it has to
    /// drain first — before cancelling, so in-flight finality-driven persistence is not lost. It does not
    /// <see cref="FlushCache"/>: that would persist the unfinalized tail and break reorgs across the
    /// restart. Instead the in-memory tail is converted to the persisted-snapshot tier, within
    /// <see cref="ShutdownTailBudget"/>, so the next start resumes from the head; whatever is left is re-executed.
    /// If a worker stops before draining, sibling waits are cancelled instead; a failed job does not stop a worker.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        try
        {
            ClearReadOnlyBundleCache();

            _compactorJobs.Writer.TryComplete();
            await _compactorTask;

            _persistenceJobs.Writer.TryComplete();
            await _persistenceTask;

            PersistInMemoryTail();
        }
        finally
        {
            _compactorJobs.Writer.TryComplete();
            _persistenceJobs.Writer.TryComplete();
            _cancelTokenSource.Cancel();

            try
            {
                await Task.WhenAll(
                    _compactorTask,
                    _persistenceTask,
                    _clearBundleCacheTask);
            }
            finally
            {
                _cancelTokenSource.Dispose();
            }
        }
    }
}
