// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Owns the lifecycle of the <see cref="ISnapshotRepository"/>'s persisted tier: loads it from the
/// catalog at startup (<see cref="Load"/>) and tears it down at shutdown (<see cref="IDisposable.Dispose"/>).
/// </summary>
public interface IPersistedSnapshotLoader : IDisposable
{
    /// <summary>Rehydrate the arena/blob stores, construct every persisted snapshot from the catalog
    /// into the repository's tier buckets, and rebuild their blooms. Drives the repository's persisted
    /// tier from empty to fully populated; called once at startup.</summary>
    void Load();

    /// <summary>
    /// Persist an in-memory <see cref="Snapshot"/> as a base entry in the persisted tier: build its
    /// HSST metadata + contiguous trie-RLP region into the shared arena/blob pools, fsync for
    /// durability, then store it in the repository's base bucket. The returned snapshot is pre-leased —
    /// the caller owns the lease and MUST dispose it.
    /// </summary>
    PersistedSnapshot Convert(Snapshot snapshot);
}

/// <inheritdoc cref="IPersistedSnapshotLoader"/>
/// <remarks>
/// A registered singleton that depends on <see cref="ISnapshotRepository"/> and the arena/blob/catalog
/// stores. Because it depends on the repository, DI disposes it before the repository, and the manager
/// (which depends on this loader and awaits its background workers on shutdown) is disposed before it —
/// so <see cref="Dispose"/> tears the persisted tier down only after all bucket-touching work has stopped.
/// </remarks>
public sealed class PersistedSnapshotLoader(
    ISnapshotRepository repository,
    IArenaManager arena,
    BlobArenaManager blobs,
    [KeyFilter(DbNames.PersistedSnapshotCatalog)] IDb catalogDb,
    IFlatDbConfig config,
    ILogManager logManager) : IPersistedSnapshotLoader
{
    // Below this many catalog entries / bloom picks we skip the progress logger and
    // the heartbeat timer — the cost of one Parallel.ForEach over a tiny input is in
    // the µs range, well below the bookkeeping overhead the logger adds per tick.
    private const int ParallelLoadThreshold = 1024;
    // Heartbeat for the progress logger inside the parallel sections. The logger
    // itself dedups via state-change comparison, so sub-second ticks are cheap.
    private const int ProgressLogIntervalMs = 1000;

    private static readonly StringLabel _tierLabel = new("persisted");

    private readonly SnapshotCatalog _catalog = new(catalogDb);
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotLoader>();
    private int _disposed;

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    /// <inheritdoc/>
    /// <remarks>
    /// Routes each catalog entry into its bucket by the stored <see cref="SnapshotTier"/> (range alone
    /// cannot tell a base from a sub-<c>CompactSize</c> compacted snapshot apart). For catalogs above
    /// <see cref="ParallelLoadThreshold"/> entries, the per-entry arena/blob lease work runs on
    /// <see cref="Parallel.ForEach"/> with a heartbeat <see cref="ProgressLogger"/>; each entry is then
    /// indexed under its bucket's lock via <see cref="ISnapshotRepository.LoadPersistedSnapshot"/>.
    /// </remarks>
    public void Load()
    {
        // Runs once at startup, before the repository serves any read — no concurrency beyond the
        // parallel fan-out below. Blob arena pool first — rehydrates file lengths so the
        // PersistedSnapshot ctor's TryLeaseFile calls (driven by each snapshot's ref_ids metadata)
        // can resolve the ids. Whole-file reservations are created lazily on first lease.
        blobs.Initialize();

        List<SnapshotCatalog.CatalogEntry> entries = [.. _catalog.Load()];
        arena.Initialize(entries);

        LoadSnapshotsParallel(entries);

        // Delete any blob arena file no loaded snapshot referenced — recoverable
        // orphans from a mid-write crash.
        blobs.SweepUnreferenced();

        // Build blooms only for the maximal-covering snapshot in each contiguous
        // range. The catalog-load itself stays cheap; this pass produces the same
        // end-state as the runtime would after all of its compactions, while
        // building only one bloom per uncovered slot instead of one per snapshot.
        ReconstructBloom();
    }

    private void LoadSnapshotsParallel(List<SnapshotCatalog.CatalogEntry> entries)
    {
        ProgressLogger? loadLog = null;
        Timer? heartbeat = null;
        if (entries.Count > ParallelLoadThreshold && _logger.IsInfo)
        {
            loadLog = new ProgressLogger("Persisted snapshot load", logManager);
            loadLog.Reset(0, entries.Count);
            heartbeat = new Timer(ProgressLogIntervalMs);
            heartbeat.Elapsed += (_, _) => loadLog.LogProgress();
            heartbeat.Start();
        }

        try
        {
            long loaded = 0;
            Parallel.ForEach(entries, entry =>
            {
                LoadSnapshot(entry);
                if (loadLog is not null) loadLog.Update(Interlocked.Increment(ref loaded));
            });
            loadLog?.LogProgress();
        }
        finally
        {
            heartbeat?.Dispose();
        }
    }

    /// <summary>
    /// Re-indexes a single catalog entry's snapshot via <see cref="ISnapshotRepository.AddPersistedSnapshot"/>,
    /// which builds it from the reservation and indexes it under the bucket's lock — so this is safe to run
    /// from the parallel load. No catalog write: the entry is already in the catalog (we are reading from it).
    /// </summary>
    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        ArenaReservation reservation = arena.Open(entry.Location);

        // AddPersistedSnapshot builds the snapshot (its ctor walks its own ref_ids metadata and leases
        // each blob arena file, rolling back on partial failure), indexes it by the stored tier, disposes
        // the reservation, and returns it pre-leased. The bloom is the AlwaysTrue placeholder here —
        // ReconstructBloom replaces it once every snapshot is in place — and we drop the returned
        // creation lease immediately; the bucket keeps its own.
        using PersistedSnapshot _ = repository.AddPersistedSnapshot(
            entry.From, entry.To, reservation, BloomFilter.AlwaysTrue(), entry.Tier);
    }

    /// <summary>
    /// Build and attach the unified bloom for every loaded snapshot, replacing the AlwaysTrue
    /// placeholder each was constructed with. After this pass every snapshot that can be assembled
    /// into a bundle — base, compacted, or persistable — carries the precise bloom built from its own
    /// on-disk image, so reads through it are filtered. Each bloom is sized exactly to its source's key count.
    /// </summary>
    /// <remarks>
    /// Snapshots are built widest-first (largest <c>To - From</c> range) so the heaviest
    /// bloom-builds enter the parallel queue first — LPT-style scheduling that minimises
    /// wallclock when work sizes vary. The build is read-only and independent per snapshot,
    /// so it parallelises freely; <see cref="PersistedSnapshot.SetBloom"/> is the only mutation
    /// and touches just the snapshot it is called on.
    /// </remarks>
    private void ReconstructBloom()
    {
        if (!BloomEnabled) return;

        // The catalog is keyed by (To, depth), so a base, a compacted, and a persistable can
        // all coexist at the same To across the three buckets — each is an independently
        // assemblable snapshot and gets its own bloom.
        List<PersistedSnapshot> snapshots = [.. repository.PersistedSnapshots];

        // Widest-first so the big merges (slowest to scan) lead the parallel queue.
        snapshots.Sort(static (a, b) =>
            (b.To.BlockNumber - b.From.BlockNumber).CompareTo(a.To.BlockNumber - a.From.BlockNumber));

        ProgressLogger? bloomLog = null;
        Timer? heartbeat = null;
        if (snapshots.Count > ParallelLoadThreshold && _logger.IsInfo)
        {
            bloomLog = new ProgressLogger("Persisted snapshot bloom rebuild", logManager);
            bloomLog.Reset(0, snapshots.Count);
            heartbeat = new Timer(ProgressLogIntervalMs);
            heartbeat.Elapsed += (_, _) => bloomLog.LogProgress();
            heartbeat.Start();
        }

        try
        {
            long built = 0;
            Parallel.ForEach(snapshots, snap =>
            {
                snap.SetBloom(BuildBloomFor(snap));
                if (bloomLog is not null) bloomLog.Update(Interlocked.Increment(ref built));
            });
            bloomLog?.LogProgress();
        }
        finally
        {
            heartbeat?.Dispose();
        }
    }

    private BloomFilter BuildBloomFor(PersistedSnapshot snap)
    {
        using WholeReadSession session = snap.BeginWholeReadSession();
        return PersistedSnapshotBloomBuilder.Build(session, snap, _bloomBitsPerKey);
    }

    /// <inheritdoc/>
    public PersistedSnapshot Convert(Snapshot snapshot)
    {
        // One unified bloom covering account/slot/SD keys + state-trie + storage-trie paths.
        // Sized as the union of both expected key counts at the configured bits-per-key.
        BloomFilter bloom;
        if (BloomEnabled)
        {
            long capacity = (long)snapshot.AccountsCount
                          + snapshot.Content.SelfDestructedStorageAddresses.Count
                          + 2L * snapshot.StoragesCount
                          + snapshot.StateNodesCount
                          + snapshot.StorageNodesCount;
            bloom = new BloomFilter(Math.Max(capacity, 1), _bloomBitsPerKey);
        }
        else
        {
            bloom = BloomFilter.AlwaysTrue();
        }

        long estimatedSize = PersistedSnapshotBuilder.EstimateSize(snapshot);

        SnapshotLocation location;
        ArenaReservation reservation;
        using BlobArenaWriter blobWriter = blobs.CreateWriter(estimatedSize);
        using (ArenaWriter arenaWriter = arena.CreateWriter(estimatedSize))
        {
            PersistedSnapshotBuilder.Build<ArenaBufferWriter>(
                snapshot, ref arenaWriter.GetWriter(), blobWriter, bloom);
            Metrics.PersistedSnapshotSize.Observe(arenaWriter.GetWriter().Written, _tierLabel);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();

        // Durability barrier — fsync both the metadata arena and the blob arena before the
        // catalog records the new entry. A crash between this point and the next persistence
        // checkpoint would otherwise leave the catalog pointing at unsynced pages whose
        // contents are not yet guaranteed to be on disk.
        reservation.Fsync();
        blobWriter.Fsync();

        // Record the catalog entry, then index the snapshot. AddPersistedSnapshot indexes it,
        // pre-acquires the caller's lease under the bucket's lock, and disposes the reservation.
        _catalog.Add(new SnapshotCatalog.CatalogEntry(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase));
        PersistedSnapshot persisted = repository.AddPersistedSnapshot(
            snapshot.From, snapshot.To, reservation, bloom, SnapshotTier.PersistedBase);

        if (_validatePersistedSnapshot)
            PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);

        return persisted;
    }

    /// <summary>
    /// Flags the persisted tier's files for shutdown preservation. This is the loader's only teardown
    /// step; the actual disposal of the repository (its buckets) and the arena/blob managers is left to
    /// DI. Because the loader depends on <see cref="ISnapshotRepository"/>, DI disposes it before the
    /// repository, so the mark always lands before the buckets are torn down; and because the repository
    /// depends on the arena/blob managers, they are disposed after it — buckets drop their reservation
    /// and blob leases before the stores they point into go.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        repository.MarkPersistedTierForShutdown();
    }
}
