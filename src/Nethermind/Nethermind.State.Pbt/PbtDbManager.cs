// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using Nethermind.Config;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Monitoring.Config;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

public class PbtDbManager : IPbtDbManager, IAsyncDisposable
{
    private const int GatherRetryLimit = 16;
    private const int MaxInFlightCompactionJobs = 32;
    private static readonly TimeSpan CacheSweepInterval = TimeSpan.FromSeconds(15);

    private readonly PbtSnapshotRepository _repository;
    private readonly PbtPersistenceCoordinator _coordinator;
    private readonly IPbtPersistence _persistence;
    private readonly IPbtResourcePool _resourcePool;
    private readonly PbtSnapshotCompactor _compactor;
    private readonly ILogger _logger;
    private readonly bool _recordDetailedMetrics;
    // Persistence is idempotent — it re-reads the head every time — so a dropped nudge costs nothing
    // and one pending signal is enough.
    private readonly Channel<bool> _workSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // Compaction is not: each block is its own window at its own width, and a dropped one is a level
    // that never merges and never comes back. Committing waits rather than drops when this backs up.
    private readonly Channel<StateId> _compactionJobs = Channel.CreateBounded<StateId>(new BoundedChannelOptions(MaxInFlightCompactionJobs) { FullMode = BoundedChannelFullMode.Wait });

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
        PbtPersistenceCoordinator coordinator,
        IPbtPersistence persistence,
        IPbtResourcePool resourcePool,
        PbtSnapshotCompactor compactor,
        IProcessExitSource processExitSource,
        IMetricsConfig metricsConfig,
        ILogManager logManager)
    {
        _repository = repository;
        _coordinator = coordinator;
        _persistence = persistence;
        _resourcePool = resourcePool;
        _compactor = compactor;
        _recordDetailedMetrics = metricsConfig.EnableDetailedMetric;
        _logger = logManager.GetClassLogger<PbtDbManager>();
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(processExitSource.Token);
        _persistenceWorker = Task.Run(RunPersistenceWorker);
        _compactionWorker = Task.Run(RunCompactionWorker);
        _cacheSweeper = Task.Run(RunCacheSweeper);
    }

    public PbtReadOnlySnapshotBundle? TryGatherReadOnlyBundle(in StateId stateId)
    {
        // the pre-genesis state is empty by definition, whatever is on disk, and is never cached:
        // there is nothing to amortise and nothing to sweep
        if (stateId == StateId.PreGenesis) return new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), EmptyPersistenceReader.Instance, _recordDetailedMetrics);

        // a sweep may have released the entry between the lookup and the lease, in which case fall
        // through and assemble; a failure here must not consume an assembly attempt, or a state that
        // exists would report unavailable once the attempts ran out
        if (_readOnlyBundleCache.TryGetValue(stateId, out PbtReadOnlySnapshotBundle? cached) && cached.TryLease()) return cached;

        // reader first, chain second: if persistence advances in between, the chain walk to the
        // reader's (stale) floor fails and we retry with a fresh reader; leased layers pruned
        // after assembly stay readable through their leases
        for (int attempt = 0; attempt < GatherRetryLimit; attempt++)
        {
            IPbtPersistence.IReader reader = _persistence.CreateReader();
            PbtSnapshotPooledList chain = new(1);
            if (_repository.TryLeaseChain(stateId, reader.CurrentState, chain))
            {
                ReportBundleMetrics(chain);

                // ownership of the chain and the reader passes to the bundle
                PbtReadOnlySnapshotBundle bundle = new(chain, reader, _recordDetailedMetrics);

                // lease before publishing, never after: a sweep landing between the publish and the
                // lease would release the only lease and hand back a dead bundle
                bundle.TryLease();
                if (!_readOnlyBundleCache.TryAdd(stateId, bundle)) bundle.Dispose();
                return bundle;
            }

            // a broken walk leaves the partial leases on the chain: disposing it releases them
            chain.Dispose();
            reader.Dispose();
        }

        return null;
    }

    /// <remarks>
    /// Only assembled chains are reported: a cache hit returns a view whose shape was already
    /// observed when it was built.
    /// </remarks>
    private static void ReportBundleMetrics(PbtSnapshotPooledList chain)
    {
        Metrics.PbtSnapshotBundleSize = chain.Count;

        // PreGenesis sits at the top of the block-number range, so it subtracts as -1 would and the
        // span of a chain reaching back to it comes out right without a special case.
        Metrics.PbtSnapshotBundleBlockNumberDepth.Observe(
            chain.Count > 0 ? chain[^1].To.BlockNumber - chain[0].From.BlockNumber : 0);
    }

    /// <remarks>
    /// Removes before releasing, so a concurrent gather either leases the entry while it is still
    /// live or misses it and assembles its own. Bundles a scope is still reading stay alive on that
    /// scope's own lease.
    /// </remarks>
    private void ClearReadOnlyBundleCache()
    {
        foreach ((StateId stateId, PbtReadOnlySnapshotBundle bundle) in _readOnlyBundleCache)
        {
            if (_readOnlyBundleCache.TryRemove(new KeyValuePair<StateId, PbtReadOnlySnapshotBundle>(stateId, bundle)))
            {
                bundle.Dispose();
            }
        }
    }

    public PbtSnapshotBundle? TryGatherBundle(in StateId stateId, PbtResourcePool.Usage usage)
    {
        if (TryGatherReadOnlyBundle(stateId) is not { } readOnlyBundle) return null;

        try
        {
            // ownership of the shared bundle's lease passes to the writable one
            return new PbtSnapshotBundle(new PbtSnapshotPooledList(1), readOnlyBundle, _resourcePool, usage);
        }
        catch
        {
            readOnlyBundle.Dispose();
            throw;
        }
    }

    public void AddSnapshot(PbtSnapshot snapshot)
    {
        StateId committed = snapshot.To;
        _repository.TryAdd(snapshot);

        // Backing up this far means compaction is not keeping pace with the chain, which persistence
        // sits downstream of.
        if (!_compactionJobs.Writer.TryWrite(committed))
        {
            if (_logger.IsWarn) _logger.Warn("Pbt compaction is not keeping up with block processing; stalling the commit until it does.");
            _compactionJobs.Writer.WriteAsync(committed, _stopSource.Token).AsTask().GetAwaiter().GetResult();
        }
    }

    public bool HasStateForBlock(in StateId stateId) =>
        stateId == StateId.PreGenesis
        || _repository.HasState(stateId)
        || _coordinator.GetCurrentPersistedStateId() == stateId;

    public void FlushCache(CancellationToken cancellationToken)
    {
        _coordinator.FlushToPersistence();
        ClearReadOnlyBundleCache();
    }

    /// <summary>Persists up to <paramref name="seed"/> on behalf of an external clock, sweeping the bundle cache if it advanced.</summary>
    /// <inheritdoc cref="PbtPersistenceCoordinator.PersistUpTo" path="/remarks"/>
    public void PersistUpTo(in StateId seed)
    {
        if (_coordinator.PersistUpTo(seed)) ClearReadOnlyBundleCache();
    }

    private async Task RunPersistenceWorker()
    {
        try
        {
            await foreach (bool _ in _workSignal.Reader.ReadAllAsync(_stopSource.Token))
            {
                try
                {
                    // only sweep once persistence has actually advanced, and only after the layers it
                    // superseded are pruned: sweeping earlier would re-cache a view assembled from
                    // layers about to go, pinning them all over again
                    if (_coordinator.CheckPersistence()) ClearReadOnlyBundleCache();
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Pbt persistence failed", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error("Pbt compaction failed", e);
                }

                _workSignal.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    public async ValueTask DisposeAsync()
    {
        _workSignal.Writer.TryComplete();
        _compactionJobs.Writer.TryComplete();
        await _stopSource.CancelAsync();
        await _persistenceWorker;
        await _compactionWorker;
        await _cacheSweeper;
        ClearReadOnlyBundleCache();
        _stopSource.Dispose();
    }

    private sealed class EmptyPersistenceReader : IPbtPersistence.IReader
    {
        public static readonly EmptyPersistenceReader Instance = new();

        private EmptyPersistenceReader()
        {
        }

        public StateId CurrentState => StateId.PreGenesis;

        // the empty tree's EIP-8297 root is 32 zero bytes
        public ValueHash256 CurrentTreeRoot => default;

        public RefCountingMemory? GetLeafBlob(in Stem stem) => null;
        public RefCountingMemory? GetTrieNode(in TrieNodeKey key) => null;

        public void Dispose()
        {
        }
    }
}
