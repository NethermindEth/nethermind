// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core.Buffers;
using Nethermind.Core.Memory;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.PersistedSnapshots;
using Nethermind.State.Pbt.Snapshot;

namespace Nethermind.State.Pbt;

public class PbtDbManager : IPbtDbManager, IAsyncDisposable
{
    private static readonly TimeSpan GatherGiveUpDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CacheSweepInterval = TimeSpan.FromSeconds(15);

    private readonly IPbtTrieNodeCache _trieNodeCache;
    private readonly PbtSnapshotRepository _repository;
    private readonly PbtPersistenceCoordinator _coordinator;
    private readonly IPbtPersistence _persistence;
    private readonly IPbtResourcePool _resourcePool;
    private readonly PbtSnapshotCompactor _compactor;
    private readonly ILogger _logger;
    private readonly Channel<StateId> _persistenceJobs;
    private readonly Channel<StateId> _compactionJobs;
    private readonly Channel<PbtTransientResource> _trieCachePopulationJobs = Channel.CreateBounded<PbtTransientResource>(1);
    private readonly Lock _admissionLock = new();
    private readonly Lock _trieCacheWriteLock = new();
    private readonly bool _inlineCompaction;
    private readonly CancellationToken _processExitToken;
    private readonly bool _recordDetailedMetrics;
    private int _isDisposed;

    private readonly Task _persistenceWorker;
    private readonly Task _compactionWorker;
    private readonly Task _trieCachePopulator;
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
        PbtPersistenceCoordinator coordinator,
        IPbtPersistence persistence,
        IPbtResourcePool resourcePool,
        PbtSnapshotCompactor compactor,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        IMetricsConfig metricsConfig,
        IPbtTrieNodeCache trieNodeCache)
        : this(repository, coordinator, persistence, resourcePool, compactor, processExitSource, logManager,
            metricsConfig, trieNodeCache, NullPbtRetainedSnapshotLoader.Instance)
    { }

    internal PbtDbManager(
        PbtSnapshotRepository repository,
        PbtPersistenceCoordinator coordinator,
        IPbtPersistence persistence,
        IPbtResourcePool resourcePool,
        PbtSnapshotCompactor compactor,
        IProcessExitSource processExitSource,
        ILogManager logManager,
        IMetricsConfig metricsConfig,
        IPbtTrieNodeCache trieNodeCache,
        IPbtRetainedSnapshotLoader retainedLoader)
    {
        retainedLoader.Load();
        _trieNodeCache = trieNodeCache;
        _repository = repository;
        _coordinator = coordinator;
        _persistence = persistence;
        _resourcePool = resourcePool;
        _compactor = compactor;
        _logger = logManager.GetClassLogger<PbtDbManager>();
        _processExitToken = processExitSource.Token;
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
        _inlineCompaction = coordinator.Configuration.InlineCompaction;
        _persistenceJobs = Channel.CreateBounded<StateId>(coordinator.Configuration.MaxInFlightCompactJob);
        _compactionJobs = Channel.CreateBounded<StateId>(coordinator.Configuration.MaxInFlightCompactJob);
        _stopSource = new CancellationTokenSource();
        _persistenceWorker = Task.Run(RunPersistenceWorker);
        _compactionWorker = Task.Run(RunCompactionWorker);
        _trieCachePopulator = Task.Run(RunTrieCachePopulator);
        _cacheSweeper = Task.Run(RunCacheSweeper);
    }

    public PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId)
    {
        try { return GatherReadOnlyBundle(stateId); }
        catch (StateNotRetainedException) { return null; }
    }

    public PbtReadOnlySnapshotBundle GatherReadOnlyBundle(in StateId stateId)
    {
        if (stateId == StateId.PreGenesis) return new(new PbtSnapshotPooledList(0), EmptyPersistenceReader.Instance, _recordDetailedMetrics);
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
            PbtSnapshotChain? chain;
            try { chain = _repository.TryLeaseReadChain(stateId, reader.CurrentState); }
            catch { reader.Dispose(); throw; }
            if (chain is null)
            {
                reader.Dispose();
                if (!HasStateForBlock(stateId)) throw new StateNotRetainedException($"No state available for block {stateId.BlockNumber} with state root {stateId.StateRoot}");
                attempt++;
                continue;
            }
            PbtReadOnlySnapshotBundle bundle;
            try
            {
                Metrics.PbtSnapshotBundleSize = chain.Layers.Count;
                Metrics.PbtSnapshotBundleBlockNumberDepth.Observe(chain.Layers.Count > 0
                    ? chain.Layers[^1].To.BlockNumber - chain.Layers[0].From.BlockNumber : 0);
                bundle = new(chain, reader, _recordDetailedMetrics);
            }
            catch { chain.Dispose(); reader.Dispose(); throw; }
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

    public PbtSnapshotBundle? TryGatherBundle(in StateId baseStateId, PbtSnapshotPooledList localSnapshots, PbtResourcePool.Usage usage)
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
            return new PbtSnapshotBundle(localSnapshots, readOnlyBundle, _resourcePool, usage, _trieNodeCache);
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
                snapshot.Dispose();
                transientResource.ReleaseLease();
                return;
            }

            StateId committed = snapshot.To;
            StateId persisted = _coordinator.GetCurrentPersistedStateId();
            if (persisted != StateId.PreGenesis && committed.BlockNumber <= persisted.BlockNumber)
            {
                snapshot.Dispose();
                transientResource.ReleaseLease();
                return;
            }
            if (!_repository.TryAdd(snapshot))
            {
                transientResource.ReleaseLease();
                return;
            }
            if (_inlineCompaction)
            {
                try
                {
                    lock (_trieCacheWriteLock) _trieNodeCache.Add(transientResource);
                    if (_compactor.DoCompactSnapshot(committed)) ClearReadOnlyBundleCache();
                }
                finally { transientResource.ReleaseLease(); }
                EnqueueOrStall(_persistenceJobs.Writer, committed, "persistence");
                return;
            }
            if (!_trieCachePopulationJobs.Writer.TryWrite(transientResource)) transientResource.ReleaseLease();
            if (_logger.IsDebug) _logger.Debug($"Admitted Pbt snapshot {snapshot.From} -> {committed}: persisted={persisted}, snapshots={_repository.Count}, compactedSnapshots={_repository.CompactedCount}, cachedBundles={_readOnlyBundleCache.Count}, managedBytes={GC.GetTotalMemory(false)}");

            EnqueueOrStall(_compactionJobs.Writer, committed, "compaction/persistence");
        }
    }

    /// <summary>Blocks the committing thread until the worker accepts the job; returns false only when the manager is shutting down.</summary>
    private bool EnqueueOrStall<T>(ChannelWriter<T> writer, T job, string workerName)
    {
        if (writer.TryWrite(job)) return true;
        if (_logger.IsWarn) _logger.Warn($"Pbt {workerName} is not keeping up with block processing; stalling the commit until it does.");
        try
        {
            writer.WriteAsync(job, _processExitToken).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) when (_processExitToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _isDisposed) != 0)
        {
            return false;
        }
    }

    public bool HasStateForBlock(in StateId stateId) =>
        stateId == StateId.PreGenesis
        || _repository.HasState(stateId)
        || _coordinator.GetCurrentPersistedStateId() == stateId;

    public void DropStateNotReachableFrom(in StateId head)
    {
        try
        {
            if (_coordinator.DropStateNotReachableFrom(head)) ClearReadOnlyBundleCache();
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
        StateId persisted = _coordinator.FlushToPersistenceState(cancellationToken);
        if (cancellationToken.IsCancellationRequested || persisted == StateId.PreGenesis) return;
        ClearReadOnlyBundleCache();
        lock (_trieCacheWriteLock) _trieNodeCache.Clear();
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
                    if (await _coordinator.CheckPersistenceAsync(stateId)) ClearReadOnlyBundleCache();
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

    private async Task RunTrieCachePopulator()
    {
        try
        {
            await foreach (PbtTransientResource transientResource in _trieCachePopulationJobs.Reader.ReadAllAsync(_stopSource.Token))
            {
                try
                {
                    lock (_trieCacheWriteLock) _trieNodeCache.Add(transientResource);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    if (_logger.IsError) _logger.Error("Pbt trie cache population failed", e);
                }
                finally
                {
                    transientResource.ReleaseLease();
                }
            }
        }
        catch (OperationCanceledException) { await _stopSource.CancelAsync(); }
        catch { await _stopSource.CancelAsync(); throw; }
        finally { _trieCachePopulationJobs.Writer.TryComplete(); }
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
            _trieCachePopulationJobs.Writer.TryComplete();
            await _trieCachePopulator;
            _persistenceJobs.Writer.TryComplete();
            await _persistenceWorker;
        }
        finally
        {
            _compactionJobs.Writer.TryComplete();
            _trieCachePopulationJobs.Writer.TryComplete();
            _persistenceJobs.Writer.TryComplete();
            await _stopSource.CancelAsync();
            try
            {
                await Task.WhenAll(_compactionWorker, _trieCachePopulator, _persistenceWorker, _cacheSweeper);
            }
            finally
            {
                while (_trieCachePopulationJobs.Reader.TryRead(out PbtTransientResource? leftover)) leftover.ReleaseLease();
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
        public bool TryGetCodeLeaf(in PbtPath key, out ValueHash256 value)
        {
            value = default;
            return false;
        }
        public IEnumerator<KeyValuePair<ValueHash256, PbtAccount>> EnumerateAccounts() => ((IEnumerable<KeyValuePair<ValueHash256, PbtAccount>>)[]).GetEnumerator();
        public RefCountingMemory? GetNodeGroup<TPath>(TPath groupKey) where TPath : struct, IPbtNodePath<TPath> => null;

        public void Dispose()
        {
        }
    }
}
