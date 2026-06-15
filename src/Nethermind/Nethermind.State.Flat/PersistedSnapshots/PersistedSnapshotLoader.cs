// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Loads the persisted snapshot tier from the catalog into <see cref="SnapshotRepository"/>'s
/// buckets at construction: rehydrates the arena/blob stores, constructs each
/// <see cref="PersistedSnapshot"/> into its tier bucket, then rebuilds the per-snapshot blooms.
/// </summary>
/// <remarks>
/// Runs once, before the repository is published, so the only concurrency is the parallel fan-out
/// it drives explicitly. The buckets it fills are owned by the repository and outlive the loader.
/// </remarks>
internal sealed class PersistedSnapshotLoader(
    IArenaManager arena,
    BlobArenaManager blobs,
    SnapshotCatalog catalog,
    SnapshotRepository.SnapshotBucket @base,
    SnapshotRepository.SnapshotBucket compacted,
    SnapshotRepository.SnapshotBucket persistable,
    double bloomBitsPerKey,
    ILogManager logManager)
{
    // Below this many catalog entries / bloom picks we skip the progress logger and
    // the heartbeat timer — the cost of one Parallel.ForEach over a tiny input is in
    // the µs range, well below the bookkeeping overhead the logger adds per tick.
    private const int ParallelLoadThreshold = 1024;
    // Heartbeat for the progress logger inside the parallel sections. The logger
    // itself dedups via state-change comparison, so sub-second ticks are cheap.
    private const int ProgressLogIntervalMs = 1000;

    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotLoader>();

    private bool BloomEnabled => bloomBitsPerKey > 0;

    /// <summary>
    /// Load the persisted snapshots from the catalog, routing each into its bucket by the stored
    /// <see cref="SnapshotTier"/> (range alone cannot tell a base from a sub-<c>CompactSize</c>
    /// compacted snapshot apart). For catalogs above <see cref="ParallelLoadThreshold"/> entries,
    /// the per-entry arena/blob lease work runs on <see cref="Parallel.ForEach"/> with a heartbeat
    /// <see cref="ProgressLogger"/>; the non-concurrent <c>SortedSet</c> tip and ordered-id rebuild
    /// runs serially after.
    /// </summary>
    public void Load()
    {
        // Runs once at construction, before the repository is published — no concurrency.
        // Blob arena pool first — rehydrates file lengths so the PersistedSnapshot ctor's
        // TryLeaseFile calls (driven by each snapshot's ref_ids metadata) can resolve the ids.
        // Whole-file reservations are created lazily on first lease.
        blobs.Initialize();

        List<SnapshotCatalog.CatalogEntry> entries = [.. catalog.Load()];
        arena.Initialize(entries);

        LoadSnapshotsParallel(entries);

        // Serial post-pass: build the ordered sets from the now-populated dicts.
        foreach (SnapshotCatalog.CatalogEntry entry in entries)
        {
            BucketFor(entry.Tier).RegisterOrdered(entry.To);
        }

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
    /// Routes a single catalog entry into its bucket dictionary (which bumps the bucket and
    /// global memory/count metrics). Safe to call concurrently — <see cref="SnapshotRepository.SnapshotBucket.Set"/>
    /// only mutates the <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// and <see cref="Interlocked"/> counters. The non-concurrent <see cref="System.Collections.Generic.SortedSet{T}"/>
    /// ordered ids are populated by the serial post-pass in <see cref="Load"/>.
    /// </summary>
    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        ArenaReservation reservation = arena.Open(entry.Location);

        // The PersistedSnapshot ctor walks its own ref_ids metadata and leases each blob
        // arena file (and reads its blob_range from the same metadata); on partial failure
        // it releases what it took and disposes the reservation lease before rethrowing —
        // no repository-side cleanup needed.
        PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, blobs);

        // Bloom is intentionally NOT built here — each snapshot is constructed with the
        // AlwaysTrue placeholder (correct, but unfiltered). The ReconstructBloom pass
        // replaces it with the snapshot's real bloom once every snapshot is in place.

        // Route by the stored tier, not by the To-From distance: a base and a sub-CompactSize
        // compacted snapshot can span the same number of blocks, so range alone cannot tell
        // them apart.
        BucketFor(entry.Tier).Set(entry.To, snapshot);
    }

    /// <summary>
    /// Build and attach the unified bloom for every loaded snapshot across all three buckets,
    /// replacing the AlwaysTrue placeholder each was constructed with. After this pass every
    /// snapshot that can be assembled into a bundle — base, compacted, or persistable —
    /// carries the precise bloom built from its own on-disk image, so reads through it are
    /// filtered. Each bloom is sized exactly to its source's key count.
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
        List<PersistedSnapshot> snapshots = [];
        foreach (SnapshotRepository.SnapshotBucket bucket in (ReadOnlySpan<SnapshotRepository.SnapshotBucket>)[@base, compacted, persistable])
            foreach (PersistedSnapshot snap in bucket.Snapshots)
                snapshots.Add(snap);

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
        return PersistedSnapshotBloomBuilder.Build(session, snap, bloomBitsPerKey);
    }

    private SnapshotRepository.SnapshotBucket BucketFor(SnapshotTier tier) => tier switch
    {
        SnapshotTier.PersistedBase => @base,
        SnapshotTier.PersistedCompacted => compacted,
        SnapshotTier.PersistedPersistable => persistable,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only persisted tiers are valid here."),
    };
}
