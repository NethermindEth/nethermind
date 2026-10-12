// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Channels;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Rust.Interop;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// <see cref="IFlatDbManager"/> of <see cref="FlatDbBackend.Rust"/>: the snapshots, their compaction and persistence,
/// and the bundles read through them live in the native library; this shell keeps what C# owns around them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddSnapshot"/> serializes the block's snapshot into the native manager and keeps the trie nodes it holds
/// for the <c>IsPersisted</c> replay. The <see cref="TransientResource"/> goes to the trie node cache populator before
/// the compaction is queued, which may stall, as in <c>FlatDbManager</c>; the C# snapshot is released at once, its
/// writes now living natively.
/// </para>
/// <para>
/// A gathered bundle is a C# <see cref="ReadOnlySnapshotBundle"/> without snapshots whose persistence reader is the
/// native bundle, which reads the snapshots and then the persistence and applies the self-destruct cut-off itself. The
/// slot filter runs natively for the shared bundles of <see cref="GatherReadOnlySnapshotBundle(in StateId)"/> and for
/// <see cref="GatherSnapshotBundle(in StateId, ResourcePool.Usage, bool)"/> when asked for. <c>PreGenesis</c> is served
/// without the native manager, as <c>FlatDbManager</c> serves it.
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
    private readonly bool _enableDetailedMetrics = Db.Metrics.DetailedMetricsEnabled;

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
        _logger = logManager.GetClassLogger<RustFlatDbManager>();
        _populateTrieNodeCacheTask = Task.Run(RunTrieCachePopulator);
    }

    public void AddSnapshot(Snapshot snapshot, TransientResource transientResource)
    {
        StateId from = snapshot.From;
        StateId to = snapshot.To;
        bool queued = false;
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Registering {from.BlockNumber} to {to.BlockNumber}");
            nint builder = SnapshotSerializer.Build(snapshot, out ArrayPoolList<ShellNode> nodes);

            // Kept before the add, as the native persistence can persist the snapshot before the add returns.
            PersistedNodeReplay.Addition addition = _native.Replay.Record(from, to, nodes);
            // A failed call leaves unknown whether the snapshot was added, so its nodes stay kept until persisted past.
            bool added = true;
            try
            {
                added = _native.AddSnapshot(builder) == FdbStatus.Ok;
            }
            finally
            {
                _native.Replay.Settle(addition, added);
            }

            if (!added) return;

            if (_inlineCompaction)
            {
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
        return new SnapshotBundle(
            Gather(baseBlock, ReaderFlags.None, filterInMemorySlotReads),
            _trieNodeCache,
            _resourcePool,
            usage: usage,
            filterInMemorySlotReads: filterInMemorySlotReads);
    }

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock) =>
        GatherReadOnlySnapshotBundle(baseBlock, ReaderFlags.None);

    public ReadOnlySnapshotBundle GatherReadOnlySnapshotBundle(in StateId baseBlock, ReaderFlags readerFlags) =>
        Gather(baseBlock, readerFlags, filtered: readerFlags == ReaderFlags.None);

    private ReadOnlySnapshotBundle Gather(in StateId baseBlock, ReaderFlags readerFlags, bool filtered)
    {
        // PreGenesis is a sentinel; Nethermind always generates genesis, so this path is always transient.
        IPersistence.IPersistenceReader reader = baseBlock == StateId.PreGenesis
            ? new NoopPersistenceReader()
            : _native.Gather(baseBlock, readerFlags, filtered);
        return new ReadOnlySnapshotBundle(new SnapshotPooledList(0), reader, _enableDetailedMetrics, PersistedSnapshotStack.Empty(_enableDetailedMetrics));
    }

    /// <inheritdoc cref="FlatDbManager.FlushCache"/>
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

    /// <inheritdoc cref="FlatDbManager.DisposeAsync"/>
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
            PopulateTrieNodeCache(cachedResource);
        }
    }

    private void PopulateTrieNodeCache(TransientResource transientResource)
    {
        try
        {
            _trieNodeCache.Add(transientResource);
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Error on Populating trie node cache", exception);
        }
        finally
        {
            transientResource.ReleaseLease();
        }
    }
}
