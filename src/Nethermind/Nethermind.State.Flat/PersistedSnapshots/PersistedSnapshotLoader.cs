// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <inheritdoc cref="IPersistedSnapshotLoader"/>
/// <remarks>
/// A registered singleton that depends on <see cref="ISnapshotRepository"/> and the arena/blob/catalog
/// stores. Because it depends on the repository, DI disposes it before the repository; and because the
/// compactor depends on this loader, DI disposes the compactor (draining its bucket-touching workers)
/// before it — so <see cref="Dispose"/> tears the persisted tier down only after all such work has stopped.
/// </remarks>
public sealed class PersistedSnapshotLoader(
    ISnapshotRepository repository,
    IArenaManager arena,
    BlobArenaManager blobs,
    ISnapshotCatalog catalog,
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

    private readonly ISnapshotCatalog _catalog = catalog;
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
    /// indexed under its bucket's lock via <see cref="ISnapshotRepository.AddPersistedSnapshot"/>.
    /// </remarks>
    public void Load()
    {
        // Blob arena pool first — rehydrates file lengths so the PersistedSnapshot ctor's TryLeaseFile
        // calls (driven by each snapshot's ref_ids metadata) can resolve the ids. Whole-file
        // reservations are created lazily on first lease.
        blobs.Initialize();

        // Can be millions of entries on a long-running node — materialised once and shared by the
        // arena init and the parallel load below.
        List<CatalogEntry> entries = [.. _catalog.Load()];
        arena.Initialize(entries);

        LoadSnapshotsParallel(entries);

        // Delete any blob arena file no loaded snapshot referenced — recoverable
        // orphans from a mid-write crash.
        blobs.SweepUnreferenced();

        ReconstructBloom();
    }

    private void LoadSnapshotsParallel(List<CatalogEntry> entries)
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
    /// Loads a single catalog entry's snapshot via <see cref="ISnapshotRepository.AddPersistedSnapshot"/>,
    /// which indexes it under the bucket's lock — so this is safe to run from the parallel load.
    /// No catalog write: the entry is already in the catalog (we are reading from it).
    /// </summary>
    private void LoadSnapshot(CatalogEntry entry)
    {
        ArenaReservation reservation = arena.Open(entry.Location);

        // The ctor walks its own ref_ids metadata and leases each blob arena file (rolling back on
        // partial failure) and takes its own lease on the reservation, so we drop ours right after.
        // The bloom is the AlwaysTrue placeholder — ReconstructBloom replaces this snapshot with one
        // carrying the real bloom once every snapshot is in place. The `using` drops the construction
        // lease at the end; the bucket keeps its own.
        using PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, blobs, entry.Tier, RefCountedBloomFilter.AlwaysTrue());
        reservation.Dispose();
        repository.AddPersistedSnapshot(snapshot, entry.Tier);
    }

    /// <summary>
    /// Build the unified bloom for every loaded snapshot and re-register it carrying that bloom,
    /// replacing the AlwaysTrue placeholder each was constructed with. After this pass every snapshot
    /// that can be assembled into a bundle — base, compacted, or CompactSized — carries the precise
    /// bloom built from its own on-disk image, so reads through it are filtered. Each bloom is sized
    /// exactly to its source's key count.
    /// </summary>
    /// <remarks>
    /// Snapshots are built widest-first (largest <c>To - From</c> range) so the heaviest
    /// bloom-builds enter the parallel queue first — LPT-style scheduling that minimises
    /// wallclock when work sizes vary. The build is read-only and independent per snapshot, so it
    /// parallelises freely; the placeholder is then swapped out by re-registering an equivalent
    /// snapshot (over the same reservation) carrying the real bloom — the bloom is fixed at
    /// construction, so there is no in-place mutation.
    /// </remarks>
    private void ReconstructBloom()
    {
        if (!BloomEnabled) return;

        // The catalog is keyed by (To, depth), so a base, a compacted, and a CompactSized can
        // all coexist at the same To across the three buckets — each is an independently
        // assemblable snapshot and gets its own bloom.
        List<PersistedSnapshot> snapshots = [.. repository.PersistedSnapshots];

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
                RefCountedBloomFilter bloom;
                using (WholeReadSession session = snap.BeginWholeReadSession())
                    bloom = new RefCountedBloomFilter(PersistedSnapshotBloomBuilder.Build(session, snap, _bloomBitsPerKey));

                // The bloom is fixed at construction, so swap the AlwaysTrue placeholder by re-registering
                // an equivalent snapshot over the same reservation carrying the real bloom; the placeholder's
                // CleanUp frees its sentinel once it drains. Same reservation → no new mmap, the ctor just
                // re-leases it and the referenced blob arenas.
                using PersistedSnapshot rebuilt = new(snap.From, snap.To, snap.Reservation, blobs, snap.Tier, bloom);
                repository.ReplacePersistedSnapshot(snap.To, rebuilt, snap.Tier);
                if (bloomLog is not null) bloomLog.Update(Interlocked.Increment(ref built));
            });
            bloomLog?.LogProgress();
        }
        finally
        {
            heartbeat?.Dispose();
        }
    }

    /// <inheritdoc/>
    public void ConvertAndRegister(Snapshot snapshot)
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
        // Base snapshots are always sub-CompactSize (single-block window) and read-cold after
        // compaction — pack their metadata into the separate small-arena pool.
        using (ArenaWriter arenaWriter = arena.CreateWriter(estimatedSize, small: true))
        {
            PersistedSnapshotBuilder.Build<ArenaBufferWriter>(
                snapshot, ref arenaWriter.GetWriter(), blobWriter, bloom);
            Metrics.PersistedSnapshotSize.Observe(arenaWriter.GetWriter().Written);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();

        // Durability barrier — fsync both the metadata arena and the blob arena before the
        // catalog records the new entry. A crash between this point and the next persistence
        // checkpoint would otherwise leave the catalog pointing at unsynced pages whose
        // contents are not yet guaranteed to be on disk.
        reservation.Fsync();
        blobWriter.Fsync();

        if (_logger.IsDebug) _logger.Debug($"Persisted snapshot {snapshot.From.BlockNumber}->{snapshot.To.BlockNumber} to disk (arena {location.ArenaId}, {location.Size} bytes)");

        // Build the persisted snapshot (its ctor takes its own reservation + blob leases, so we drop
        // ours), record the catalog entry, then index it. AddPersistedSnapshot takes the bucket's own
        // lease, so we drop this construction lease once indexing (and optional validation) is done.
        PersistedSnapshot persisted = new(snapshot.From, snapshot.To, reservation, blobs, SnapshotTier.PersistedBase, new RefCountedBloomFilter(bloom));
        reservation.Dispose();
        _catalog.Add(new CatalogEntry(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase));
        repository.AddPersistedSnapshot(persisted, SnapshotTier.PersistedBase);

        if (_validatePersistedSnapshot)
        {
            try
            {
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);
            }
            catch (InvalidOperationException ex)
            {
                // Validation runs on a background persistence thread; an unhandled throw here would either
                // be swallowed (looking like a good run) or crash the process with a 128+ code that git
                // bisect treats as "abort". Exit explicitly with a bisect-compatible "bad" code instead.
                if (_logger.IsError) _logger.Error($"Persisted snapshot validation failed for range {snapshot.From.BlockNumber}..{snapshot.To.BlockNumber}. Exiting with code {ExitCodes.GeneralError} for git bisect compatibility.", ex);
                Environment.Exit(ExitCodes.GeneralError);
            }
        }

        persisted.Dispose();
    }

    /// <summary>
    /// Flags the persisted tier's files for shutdown preservation. This is the loader's only teardown
    /// step; the container disposes the rest — the repository (tearing down its buckets) and then the
    /// arena/blob managers it depends on. Because the loader depends on <see cref="ISnapshotRepository"/>,
    /// DI disposes the loader before the repository, so the mark always lands before the buckets are torn down.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        repository.MarkPersistedTierForShutdown();
    }
}
