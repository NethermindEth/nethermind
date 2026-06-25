// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
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

        ReconstructBloom(entries);
    }

    private void LoadSnapshotsParallel(List<CatalogEntry> entries)
    {
        if (entries.Count == 0) return;

        // Load the first snapshot sequentially and verify its format (the last byte). All snapshots in a
        // directory share one format, so a mismatch here is a single clean throw that pre-empts the rest.
        LoadSnapshot(entries[0], verifyFormat: true);

        ProgressLogger? loadLog = null;
        Timer? heartbeat = null;
        if (entries.Count > ParallelLoadThreshold && _logger.IsInfo)
        {
            loadLog = new ProgressLogger("Persisted snapshot load", logManager);
            loadLog.Reset(0, (ulong)entries.Count);
            heartbeat = new Timer(ProgressLogIntervalMs);
            heartbeat.Elapsed += (_, _) => loadLog.LogProgress();
            heartbeat.Start();
        }

        try
        {
            ulong loaded = 1;
            Parallel.For(1, entries.Count, i =>
            {
                LoadSnapshot(entries[i]);
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
    private void LoadSnapshot(CatalogEntry entry, bool verifyFormat = false)
    {
        ArenaReservation reservation = arena.Open(entry.Location);

        // The last byte of the snapshot is its sorted-table format version; a mismatch means the whole
        // directory predates the current on-disk format. Read it off the open reservation (a plain mmap
        // read — no lease change) before consuming the reservation below.
        if (verifyFormat && !FormatMatches(reservation, out byte onDisk))
        {
            reservation.Dispose();
            throw new InvalidOperationException(
                $"Persisted snapshot format mismatch: on-disk v{onDisk}, runtime expects v{SortedTable.FormatVersion}. " +
                "The persisted_snapshot/ directory has an incompatible layout — wipe and resync.");
        }

        // The ctor walks its own ref_ids metadata and leases each blob arena file (rolling back on
        // partial failure) and takes its own lease on the reservation, so we drop ours right after.
        // The bloom is the AlwaysTrue placeholder — ReconstructBloom replaces this snapshot with one
        // carrying the real bloom once every snapshot is in place. The `using` drops the construction
        // lease at the end; the bucket keeps its own.
        using PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, blobs, entry.Tier, RefCountedBloomFilter.AlwaysTrue());
        reservation.Dispose();
        repository.AddPersistedSnapshot(snapshot, entry.Tier);
    }

    private static bool FormatMatches(ArenaReservation reservation, out byte onDisk)
    {
        ArenaByteReader reader = reservation.CreateReader();
        Span<byte> versionByte = stackalloc byte[1];
        onDisk = reader.TryRead(reservation.Size - 1, versionByte) ? versionByte[0] : (byte)0;
        return onDisk == SortedTable.FormatVersion;
    }

    /// <summary>
    /// Rebuild a bloom only for each widest snapshot covering the persisted tier and share it across its
    /// range, so the narrower contained snapshots adopt it instead of each carrying its own — mirroring
    /// the runtime layout a large compaction leaves behind. Snapshots no widest one covers keep their
    /// AlwaysTrue placeholder (correct — never a false negative — just unfiltered).
    /// </summary>
    /// <remarks>
    /// Assembles the widest-first chain via the main read-path <see cref="ISnapshotRepository.AssembleSnapshots"/>
    /// (its <c>EdgePriority</c> leads with the large skip-pointers), so the chain tiles
    /// <c>(committed, head]</c> with the fewest, widest snapshots. The committed base it targets is the
    /// oldest loaded snapshot's <c>From</c>. The few wide blooms are rebuilt in parallel; chain ranges are
    /// disjoint, so the per-range <see cref="ISnapshotRepository.ShareBloomAcrossRange"/> calls don't collide.
    /// </remarks>
    private void ReconstructBloom(List<CatalogEntry> entries)
    {
        if (!BloomEnabled || entries.Count == 0) return;
        if (repository.GetLastSnapshotId() is not StateId head) return;

        // The persisted tier sits on the committed base — the oldest loaded snapshot's From.
        StateId committed = entries[0].From;
        foreach (CatalogEntry e in entries)
            if (e.From.BlockNumber < committed.BlockNumber) committed = e.From;
        if (head == committed) return;

        // Widest-first chain from head down to the committed base; .InMemory is empty at reload.
        int estimatedSize = (int)Math.Clamp(head.BlockNumber - committed.BlockNumber, 4, 4096);
        AssembledSnapshotResult assembled = repository.AssembleSnapshots(head, committed, estimatedSize);
        assembled.InMemory.Dispose();
        using PersistedSnapshotList widest = assembled.Persisted;

        // Build the (few, wide) blooms in parallel and share each across its range. A fresh bloom
        // (refcount 1) is leased by each snapshot ShareBloomAcrossRange re-registers; the local lease is
        // released on dispose, leaving the shared snapshots holding theirs.
        Parallel.ForEach(widest, snap =>
        {
            RefCountedBloomFilter bloom;
            using (WholeReadSession session = snap.BeginWholeReadSession())
                bloom = new RefCountedBloomFilter(PersistedSnapshotBloomBuilder.Build(session, snap, _bloomBitsPerKey));
            using (bloom)
                repository.ShareBloomAcrossRange(snap.From, snap.To, bloom, blobs);
        });
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
        try
        {
            // Catalog first (durable record) for crash recovery; if the in-memory index then throws,
            // roll the catalog row back so a later load can't resolve an entry whose snapshot the
            // finally below cleans up.
            _catalog.Add(new CatalogEntry(snapshot.From, snapshot.To, location, SnapshotTier.PersistedBase));
            try
            {
                repository.AddPersistedSnapshot(persisted, SnapshotTier.PersistedBase);
            }
            catch
            {
                _catalog.Remove(snapshot.To, (long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber));
                throw;
            }

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
        }
        finally
        {
            // Drop the construction lease. On success the bucket holds its own lease so the snapshot
            // survives; if catalog/repository indexing threw first, this is the last lease and the
            // snapshot (with its reservation + blob leases) is cleaned up instead of leaked.
            persisted.Dispose();
        }
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
