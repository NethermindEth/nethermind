// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Api.Monitoring;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.NodeBuffer;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt;

public class PbtDbManager : IPbtDbManager, IAsyncDisposable
{
    private static readonly TimeSpan GatherGiveUpDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheSweepInterval = TimeSpan.FromSeconds(15);

    private readonly PbtSnapshotRepository _repository;
    private readonly PbtPersistenceManager _persistenceManager;
    private readonly IPbtPersistence _persistence;
    private readonly IPbtResourcePool _resourcePool;
    private readonly IPbtGroupRecordBuffer _groupRecordBuffer;
    private readonly PbtSnapshotCompactor _compactor;
    private readonly ILogger _logger;
    private readonly Channel<StateId> _persistenceJobs;
    private readonly Channel<StateId> _compactionJobs;
    private readonly Lock _admissionLock = new();
    private readonly bool _inlineCompaction;
    private readonly CancellationToken _processExitToken;
    private readonly bool _recordDetailedMetrics;
    private readonly double _inMemorySlotFilterBitsPerKey;
    private int _isDisposed;

    private readonly Task _persistenceWorker;
    private readonly Task _compactionWorker;
    private readonly Task _cacheSweeper;
    private readonly CancellationTokenSource _stopSource;

    // Gathering is on the hot path of every RPC read, and every scope at one state assembles the very
    // same immutable view, so it is shared rather than rebuilt. A cached entry is only ever a lifetime
    // cost, never a correctness one: it owns its layer leases and its own database snapshot. What it
    // must not do is keep answering forever — the leases hold layer contents out of the pool and the
    // reader pins SST files on disk — hence the sweeps.
    private readonly ConcurrentDictionary<StateId, PbtReadOnlySnapshotBundle> _readOnlyBundleCache = new();

    public PbtDbManager(
        PbtSnapshotRepository repository,
        PbtPersistenceManager persistenceManager,
        IPbtPersistence persistence,
        IPbtResourcePool resourcePool,
        IPbtGroupRecordBuffer groupRecordBuffer,
        PbtSnapshotCompactor compactor,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        IMetricsConfig metricsConfig,
        IPbtPersistedSnapshotLoader persistedLoader,
        IPbtConfig config)
    {
        persistedLoader.Load();
        _repository = repository;
        _persistenceManager = persistenceManager;
        _persistence = persistence;
        _resourcePool = resourcePool;
        _groupRecordBuffer = groupRecordBuffer;
        _compactor = compactor;
        _logger = logManager.GetClassLogger<PbtDbManager>();
        _processExitToken = processExitSource.Token;
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
        _inlineCompaction = config.InlineCompaction;
        _inMemorySlotFilterBitsPerKey = config.InMemorySnapshotBloomBitsPerKey;
        _persistenceJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _compactionJobs = Channel.CreateBounded<StateId>(config.MaxInFlightCompactJob);
        _stopSource = new CancellationTokenSource();
        _persistenceWorker = Task.Run(RunPersistenceWorker);
        _compactionWorker = Task.Run(RunCompactionWorker);
        _cacheSweeper = Task.Run(RunCacheSweeper);
    }

    public PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId)
    {
        try { return GatherReadOnlyBundle(stateId); }
        catch (StateNotRetainedException) { return null; }
    }

    public PbtReadOnlySnapshotBundle GatherReadOnlyBundle(in StateId stateId)
    {
        if (stateId == StateId.PreGenesis) return new(new PbtSnapshotPooledList(0), EmptyPersistenceReader.Instance, _recordDetailedMetrics, new PbtPersistedSnapshotList(0), _groupRecordBuffer.PinAll(), slotFilterBitsPerKey: 0);
        long started = 0;
        int attempt = 0;
        while (true)
        {
            if (_readOnlyBundleCache.TryGetValue(stateId, out PbtReadOnlySnapshotBundle? cached) && cached.TryLease()) return cached;
            if (attempt == 1) started = Stopwatch.GetTimestamp();
            if (attempt != 0)
            {
                if (Stopwatch.GetElapsedTime(started) > GatherGiveUpDeadline)
                    throw new InvalidOperationException($"Timed out gathering PBT bundle for {stateId} after {attempt} retries.");
                Thread.Sleep(Math.Min(1 << Math.Min(attempt, 30), 100));
            }
            IPbtPersistence.IReader reader = _persistence.CreateReader();
            PbtAssembledSnapshotResult assembled;
            try { assembled = _repository.TryLeaseReadChain(stateId, reader.CurrentState); }
            catch { reader.Dispose(); throw; }
            // Empty with the reader elsewhere means no path reached the reader's state.
            if (assembled.SnapshotCount == 0 && reader.CurrentState != stateId)
            {
                assembled.Dispose();
                reader.Dispose();
                if (!HasStateForBlock(stateId)) throw new StateNotRetainedException($"No state available for block {stateId.BlockNumber} with state root {stateId.StateRoot}");
                attempt++;
                continue;
            }
            PbtReadOnlySnapshotBundle bundle;
            try
            {
                Metrics.PbtSnapshotBundleSize = assembled.SnapshotCount;
                // The tiers are contiguous, so the chain spans the sum of their spans.
                Metrics.PbtSnapshotBundleBlockNumberDepth.Observe(
                    (assembled.InMemory.Count > 0 ? assembled.InMemory[^1].To.BlockNumber - assembled.InMemory[0].From.BlockNumber : 0)
                    + (assembled.Persisted.Count > 0 ? assembled.Persisted[^1].To.BlockNumber - assembled.Persisted[0].From.BlockNumber : 0));
                // Nothing is built here: the first filtered read builds the filter.
                bundle = new(assembled.InMemory, reader, _recordDetailedMetrics, assembled.Persisted, _groupRecordBuffer.PinAll(), _inMemorySlotFilterBitsPerKey);
            }
            catch { assembled.Dispose(); reader.Dispose(); throw; }
            bundle.TryLease();
            if (!_readOnlyBundleCache.TryAdd(stateId, bundle)) bundle.Dispose();
            return bundle;
        }
    }

    /// <remarks>
    /// Removes before releasing, so a concurrent gather either leases the entry while it is still
    /// live or misses it and assembles its own. Bundles a scope is still reading stay alive on that
    /// scope's own lease.
    /// </remarks>
    private void ClearReadOnlyBundleCache()
    {
        if (_logger.IsDebug) _logger.Debug($"Clearing Pbt read-only bundle cache: bundles={_readOnlyBundleCache.Count}, snapshots={_repository.Count}, compactedSnapshots={_repository.CompactedCount}, managedBytes={GC.GetTotalMemory(false)}");
        foreach ((StateId stateId, PbtReadOnlySnapshotBundle bundle) in _readOnlyBundleCache)
        {
            if (_readOnlyBundleCache.TryRemove(new KeyValuePair<StateId, PbtReadOnlySnapshotBundle>(stateId, bundle)))
            {
                bundle.Dispose();
            }
        }
    }

    public PbtSnapshotBundle? TryGatherBundle(in StateId stateId, PbtResourcePool.Usage usage) =>
        TryGatherBundle(stateId, new PbtSnapshotPooledList(1), usage, filterInMemorySlotReads: false, _groupRecordBuffer);

    public PbtSnapshotBundle? TryGatherBundle(in StateId baseStateId, PbtSnapshotPooledList localSnapshots, PbtResourcePool.Usage usage, bool filterInMemorySlotReads,
        IPbtGroupRecordBuffer groupRecordBuffer)
    {
        PbtReadOnlySnapshotBundle? readOnlyBundle = null;
        try
        {
            readOnlyBundle = TryGatherReadOnlyBundle(baseStateId);
            if (readOnlyBundle is null)
            {
                localSnapshots.Dispose();
                return null;
            }

            // ownership of the shared bundle's lease passes to the writable one
            return new PbtSnapshotBundle(localSnapshots, readOnlyBundle, groupRecordBuffer, _resourcePool, usage, filterInMemorySlotReads);
        }
        catch
        {
            readOnlyBundle?.Dispose();
            localSnapshots.Dispose();
            throw;
        }
    }

    public void AddSnapshot(PbtSnapshot snapshot, PbtTransientResource transientResource)
    {
        lock (_admissionLock)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _processExitToken.IsCancellationRequested)
            {
                transientResource.ReleaseLease();
                snapshot.Dispose();
                return;
            }

            StateId committed = snapshot.To;
            StateId persisted = _persistenceManager.GetCurrentPersistedStateId();
            if (persisted != StateId.PreGenesis && committed.BlockNumber <= persisted.BlockNumber)
            {
                transientResource.ReleaseLease();
                snapshot.Dispose();
                return;
            }
            bool added = _repository.TryAdd(snapshot);
            transientResource.ReleaseLease();
            if (!added) return;
            if (_inlineCompaction)
            {
                if (_compactor.DoCompactSnapshot(committed)) ClearReadOnlyBundleCache();
                EnqueueOrStall(_persistenceJobs.Writer, committed, "persistence");
                return;
            }
            if (_logger.IsDebug) _logger.Debug($"Admitted Pbt snapshot {snapshot.From} -> {committed}: persisted={persisted}, snapshots={_repository.Count}, compactedSnapshots={_repository.CompactedCount}, cachedBundles={_readOnlyBundleCache.Count}, managedBytes={GC.GetTotalMemory(false)}");

            EnqueueOrStall(_compactionJobs.Writer, committed, "compaction/persistence");
        }
    }

    /// <summary>Blocks the committing thread until the worker accepts the job, or the manager shuts down.</summary>
    private void EnqueueOrStall<T>(ChannelWriter<T> writer, T job, string workerName)
    {
        if (writer.TryWrite(job)) return;
        if (_logger.IsWarn) _logger.Warn($"Pbt {workerName} is not keeping up with block processing; stalling the commit until it does.");
        try
        {
            writer.WriteAsync(job, _processExitToken).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_processExitToken.IsCancellationRequested) { }
        catch (ChannelClosedException) when (Volatile.Read(ref _isDisposed) != 0) { }
    }

    public bool HasStateForBlock(in StateId stateId) =>
        stateId == StateId.PreGenesis
        || _repository.HasState(stateId)
        || _persistenceManager.GetCurrentPersistedStateId() == stateId;

    public void DropStateNotReachableFrom(in StateId head)
    {
        try
        {
            if (_persistenceManager.DropStateNotReachableFrom(head)) ClearReadOnlyBundleCache();
        }
        catch
        {
            // A catalog failure can leave a partially pruned graph; future gathers must not reuse it.
            ClearReadOnlyBundleCache();
            throw;
        }
    }

    public void FlushCache(CancellationToken cancellationToken)
    {
        StateId persisted = _persistenceManager.FlushToPersistenceState(cancellationToken);
        if (cancellationToken.IsCancellationRequested || persisted == StateId.PreGenesis) return;
        ClearReadOnlyBundleCache();
    }

    private async Task RunPersistenceWorker()
    {
        try
        {
            await foreach (StateId stateId in _persistenceJobs.Reader.ReadAllAsync(_stopSource.Token))
            {
                try
                {
                    // only sweep once persistence has actually advanced, and only after the layers it
                    // superseded are pruned: sweeping earlier would re-cache a view assembled from
                    // layers about to go, pinning them all over again
                    if (await _persistenceManager.CheckPersistenceAsync(stateId)) ClearReadOnlyBundleCache();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (_logger.IsError) _logger.Error("Pbt persistence failed", e);
                }
            }
        }
        catch (OperationCanceledException) { await _stopSource.CancelAsync(); }
        catch { await _stopSource.CancelAsync(); throw; }
        finally { _persistenceJobs.Writer.TryComplete(); }
    }

    /// <remarks>
    /// Compaction is upstream of persistence: a block is merged into whatever level its number calls
    /// for, and only then is persistence nudged, so it always finds the widest layer available.
    /// </remarks>
    private async Task RunCompactionWorker()
    {
        try
        {
            await foreach (StateId stateId in _compactionJobs.Reader.ReadAllAsync(_stopSource.Token))
            {
                try
                {
                    // a published layer changes what a walk at any state above it would assemble, so
                    // the cached views have to go before anything reads through them again — and
                    // before persistence is nudged, or the boundary sweep would race this one
                    if (_compactor.DoCompactSnapshot(stateId)) ClearReadOnlyBundleCache();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (_logger.IsError) _logger.Error("Pbt compaction failed", e);
                }

                await _persistenceJobs.Writer.WriteAsync(stateId, _stopSource.Token);
            }
        }
        catch (OperationCanceledException) { await _stopSource.CancelAsync(); }
        catch { await _stopSource.CancelAsync(); throw; }
        finally { _compactionJobs.Writer.TryComplete(); }
    }

    /// <remarks>
    /// The boundary sweeps only fire when persistence advances, which it never does while finality
    /// lags. This is what bounds the pinning until it resumes.
    /// </remarks>
    private async Task RunCacheSweeper()
    {
        try
        {
            using PeriodicTimer timer = new(CacheSweepInterval);
            while (await timer.WaitForNextTickAsync(_stopSource.Token))
            {
                ClearReadOnlyBundleCache();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <remarks>
    /// Drains the workers so in-flight persistence is not lost, but does not <see cref="FlushCache"/>: that would
    /// persist the unfinalized tail and break reorgs across the restart. The in-memory tier is re-executed from the
    /// persisted state on the next start.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;

        try
        {
            ClearReadOnlyBundleCache();
            _compactionJobs.Writer.TryComplete();
            // Closing admission releases a blocked producer; wait for its repository insertion before draining.
            lock (_admissionLock) { }
            await _compactionWorker;
            _persistenceJobs.Writer.TryComplete();
            await _persistenceWorker;
        }
        finally
        {
            _compactionJobs.Writer.TryComplete();
            _persistenceJobs.Writer.TryComplete();
            await _stopSource.CancelAsync();
            try
            {
                await Task.WhenAll(_compactionWorker, _persistenceWorker, _cacheSweeper);
            }
            finally
            {
                ClearReadOnlyBundleCache();
                _repository.RemoveStatesUntil(ulong.MaxValue);
                _stopSource.Dispose();
            }
        }
    }

    private sealed class EmptyPersistenceReader : IPbtPersistence.IReader
    {
        public static readonly EmptyPersistenceReader Instance = new();

        private EmptyPersistenceReader()
        {
        }

        public StateId CurrentState => StateId.PreGenesis;

        public ValueHash256 CurrentRoot => default;
        public PbtAccount? GetAccount(in ValueHash256 addressHash) => null;
        public PackedSlotRun GetSlotRun<TKey>(in TKey runKey) where TKey : struct, IPbtKey<TKey> => SlotRun.Empty;
        public CodeInfo? GetCode(in ValueHash256 codeHash) => null;
        public bool TryLoadNodeGroup<TPath, TArg>(in TPath groupKey, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TPath : struct, IPbtNodePath<TPath> where TArg : allows ref struct => false;

        public void Dispose()
        {
        }
    }
}
