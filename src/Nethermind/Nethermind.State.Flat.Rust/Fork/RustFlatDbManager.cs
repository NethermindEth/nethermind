// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// <see cref="IFlatDbManager"/> of <see cref="FlatDbBackend.Rust"/>: the snapshots, their compaction and persistence,
/// and the bundles read through them live in the native library; this shell keeps what C# owns around them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddSnapshot"/> adds the native snapshot the bundle committed, with no copy, and keeps the trie nodes it
/// set for the <c>IsPersisted</c> replay. The <see cref="TransientResource"/> goes to the trie node cache populator
/// before the compaction is queued, which may stall, as in <c>FlatDbManager</c>.
/// </para>
/// <para>
/// A gathered <see cref="SnapshotBundle"/> takes the native scope opened together with its read-only bundle, in one
/// call; a gathered <see cref="ReadOnlySnapshotBundle"/> is the native one. Both read the in-memory snapshots, then
/// the persistence, natively, <c>PreGenesis</c> as an empty state.
/// </para>
/// <para>
/// <see cref="FlushCache"/> and <see cref="DropStateNotReachableFrom"/> go through the injected
/// <see cref="IPersistenceManager"/>, so its decorators see them, then clear the native bundle cache.
/// </para>
/// </remarks>
internal sealed class RustFlatDbManager : IFlatDbManager, IAsyncDisposable
{
    private readonly NativeFlatManager _native;
    private readonly IPersistenceManager _persistenceManager;
    private readonly ITrieNodeCache _trieNodeCache;
    private readonly IResourcePool _resourcePool;
    private readonly ILogger _logger;
    private readonly bool _inlineCompaction;

    // The node cache is kinda important for performance, so we want it populated as quickly as possible.
    private readonly Channel<TransientResource> _populateTrieNodeCacheJobs = Channel.CreateBounded<TransientResource>(1);
    private readonly Task _populateTrieNodeCacheTask;
    private int _isDisposed;

    public RustFlatDbManager(
        NativeFlatManager native,
        IPersistenceManager persistenceManager,
        ITrieNodeCache trieNodeCache,
        IResourcePool resourcePool,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _native = native;
        _persistenceManager = persistenceManager;
        _trieNodeCache = trieNodeCache;
        _resourcePool = resourcePool;
        _inlineCompaction = config.InlineCompaction;
        // The lines this shell logs are those of FlatDbManager, as are the native manager's.
        _logger = logManager.GetLogger(ForkInternals.FlatDbManagerLoggerName);
        _populateTrieNodeCacheTask = Task.Run(RunTrieCachePopulator);
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        StateId from = snapshot.From;
        StateId to = snapshot.To;
        bool queued = false;
        try
        {
            // Kept before the add, as the native persistence can persist the snapshot before the add returns.
            PersistedNodeReplay.Addition addition = _native.Replay.Record(from, to, snapshot.TakeNodes());
            // A failed call leaves unknown whether the snapshot was added, so its nodes stay kept until persisted past.
            bool added = true;
            try
            {
                added = _native.AddSnapshot(snapshot.Handle) == FdbStatus.Ok;
            }
            finally
            {
                _native.Replay.Settle(addition, added);
            }

            if (!added) return;

            if (_inlineCompaction)
            {
                // A failure propagates before the compaction is queued, as from FlatDbManager.RunCompactJobSync.
                queued = true;
                PopulateTrieNodeCache(transientResource);
            }
            else
            {
                queued = _populateTrieNodeCacheJobs.Writer.TryWrite(transientResource);
            }

            long stallNs = _native.QueueCompaction(to);
            if (stallNs >= 0) Metrics.CompactorStallTime.Observe(RustMetricsBridge.ToTicks(stallNs));
        }
        finally
        {
            // Queue full, rejected or failed: return to pool instead of leaking.
            if (!queued) transientResource.ReleaseLease();
            snapshot.Dispose();
        }
    }

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage) =>
        GatherSnapshotBundle(baseBlock, usage, filterInMemorySlotReads: false);

    public SnapshotBundle GatherSnapshotBundle(in StateId baseBlock, ResourcePool.Usage usage, bool filterInMemorySlotReads)
    {
        if (_logger.IsTrace) _logger.Trace($"Gathering {baseBlock}.");
        ReadOnlySnapshotBundle view = _native.OpenScope(baseBlock, ReaderFlags.None, new FfiScopeFlags(filterInMemorySlotReads, usage));
        try
        {
            return new SnapshotBundle(view, _trieNodeCache, _resourcePool, usage, filterInMemorySlotReads: filterInMemorySlotReads);
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
        GatherReadOnlySnapshotBundle(baseBlock, ReaderFlags.None);

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags) =>
        _native.GatherReadOnly(baseBlock, readerFlags);

    /// <inheritdoc cref="Nethermind.State.Flat.FlatDbManager.FlushCache"/>
    public void FlushCache(CancellationToken cancellationToken)
    {
        if (_logger.IsInfo) _logger.Info("FlatDbManager FlushCache started.");

        StateId persistedState = _persistenceManager.FlushToPersistence(cancellationToken);

        if (cancellationToken.IsCancellationRequested) return;
        if (persistedState == StateId.PreGenesis) return;

        _native.ClearBundleCache();
        _trieNodeCache.Clear();

        if (_logger.IsInfo) _logger.Info($"FlatDbManager FlushCache completed. Persisted to {persistedState}.");
    }

    public bool HasStateForBlock(in StateId stateId) => _native.HasState(stateId);

    public void DropStateNotReachableFrom(in StateId head)
    {
        _persistenceManager.DropStateNotReachableFrom(head);
        // Cached bundles lease the snapshots they were assembled over; without this the pruned ones stay
        // alive until the periodic clear.
        _native.ClearBundleCache();
    }

    /// <inheritdoc cref="Nethermind.State.Flat.FlatDbManager.DisposeAsync"/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1) return;

        try
        {
            _native.Shutdown();
        }
        finally
        {
            _populateTrieNodeCacheJobs.Writer.TryComplete();
            await _populateTrieNodeCacheTask;
            while (_populateTrieNodeCacheJobs.Reader.TryRead(out TransientResource? cachedResource)) cachedResource.ReleaseLease();
        }
    }

    private async Task RunTrieCachePopulator()
    {
        await foreach (TransientResource cachedResource in _populateTrieNodeCacheJobs.Reader.ReadAllAsync())
        {
            await NotifyWhenSlow("Populating trie node cache", () => PopulateTrieNodeCache(cachedResource));
        }
    }

    private void PopulateTrieNodeCache(TransientResource transientResource)
    {
        try
        {
            _trieNodeCache.Add(transientResource);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }

    /// <summary>
    /// Port of <c>FlatDbManager.NotifyWhenSlow</c> for the one job the shell runs: logs the job's failure and the time it
    /// took, and warns every two seconds while it runs.
    /// </summary>
    private async Task NotifyWhenSlow(string name, Action job)
    {
        TimeSpan slowTime = TimeSpan.FromSeconds(2);

        Task jobTask = Task.Run(() =>
        {
            long sw = Stopwatch.GetTimestamp();
            try
            {
                job();
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
}
