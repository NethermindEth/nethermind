// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.Core.Attributes;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// The single persisted-snapshot store, holding three buckets keyed by <c>StateId.To</c>:
/// <list type="bullet">
///   <item><c>_base</c> — in-memory snapshots persisted directly. Each owns a
///   contiguous trie-RLP region in one blob arena (<see cref="PersistedSnapshot.BlobRange"/>).</item>
///   <item><c>_compacted</c> — merged (linked) snapshots: sub-<c>CompactSize</c>
///   intermediates and the <c>&gt;CompactSize</c> hierarchical merges. No blob region —
///   <c>NodeRef</c>s reference the base blob arenas via <c>ref_ids</c>.</item>
///   <item><c>_persistable</c> — the <c>CompactSize</c>-wide linked
///   snapshots written to RocksDB by <c>PersistenceManager</c>.</item>
/// </list>
/// </summary>
public sealed class PersistedSnapshotRepository : IPersistedSnapshotRepository
{
    // Below this many catalog entries / bloom picks we skip the progress logger and
    // the heartbeat timer — the cost of one Parallel.ForEach over a tiny input is in
    // the µs range, well below the bookkeeping overhead the logger adds per tick.
    private const int ParallelLoadThreshold = 1024;
    // Heartbeat for the progress logger inside the parallel sections. The logger
    // itself dedups via state-change comparison, so sub-second ticks are cheap.
    private const int ProgressLogIntervalMs = 1000;

    private readonly IArenaManager _arena;
    private readonly BlobArenaManager _blobs;
    private readonly SnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly bool _validatePersistedSnapshot;
    private readonly double _bloomBitsPerKey;
    private readonly StringLabel _tierLabel = new("persisted");
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    // Each bucket is a self-contained, individually-locked store: its To-keyed
    // ConcurrentDictionary (lock-free point lookups), its block-ordered StateId set + running
    // memory/count totals (guarded by the bucket's own lock), and its share of the catalog and
    // global metrics. Do NOT iterate on hot or metric paths — entry counts can reach hundreds of
    // thousands in production; use TryGet for point lookups and the O(1) MemoryBytes/Count
    // aggregates. A `To` can live in more than one bucket (a base and a compacted snapshot can
    // share it), so each keeps its own entry.
    private readonly SnapshotBucket _base;
    private readonly SnapshotBucket _compacted;
    private readonly SnapshotBucket _persistable;

    public PersistedSnapshotRepository(
        IArenaManager arenaManager,
        BlobArenaManager blobArenaManager,
        IDb catalogDb,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _arena = arenaManager;
        _blobs = blobArenaManager;
        _catalog = new(catalogDb);
        _base = new SnapshotBucket(_catalog, SnapshotKind.Base);
        _compacted = new SnapshotBucket(_catalog, SnapshotKind.Compacted);
        _persistable = new SnapshotBucket(_catalog, SnapshotKind.Persistable);
        _compactSize = config.CompactSize;
        _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
        _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<PersistedSnapshotRepository>();
        LoadFromCatalog();
    }

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    public int SnapshotCount => (int)(_base.Count + _compacted.Count + _persistable.Count);
    // Persistable snapshots are compacted (linked) snapshots — count their bytes here too.

    /// <summary>
    /// Load the persisted snapshots from the catalog at construction, routing each into its bucket
    /// by the stored <see cref="SnapshotKind"/> (range alone cannot tell a base from a
    /// sub-<c>CompactSize</c> compacted snapshot apart). For catalogs above
    /// <see cref="ParallelLoadThreshold"/> entries, the per-entry arena/blob lease work
    /// runs on <see cref="Parallel.ForEach"/> with a heartbeat <see cref="ProgressLogger"/>;
    /// the non-concurrent <c>SortedSet</c> tip and ordered-id rebuild runs serially after.
    /// </summary>
    private void LoadFromCatalog()
    {
        // Runs once at construction, before the repository is published — no concurrency.
        // Blob arena pool first — rehydrates file lengths so the PersistedSnapshot ctor's
        // TryLeaseFile calls (driven by each snapshot's ref_ids metadata) can resolve the ids.
        // Whole-file reservations are created lazily on first lease.
        _blobs.Initialize();

        List<SnapshotCatalog.CatalogEntry> entries = [.. _catalog.Load()];
        _arena.Initialize(entries);

        LoadSnapshotsParallel(entries);

        // Serial post-pass: build the ordered sets from the now-populated dicts.
        foreach (SnapshotCatalog.CatalogEntry entry in entries)
        {
            SnapshotBucket bucket = entry.Kind switch
            {
                SnapshotKind.Compacted => _compacted,
                SnapshotKind.Persistable => _persistable,
                _ => _base,
            };
            bucket.RegisterOrdered(entry.To);
        }

        // Delete any blob arena file no loaded snapshot referenced — recoverable
        // orphans from a mid-write crash.
        _blobs.SweepUnreferenced();

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
            loadLog = new ProgressLogger("Persisted snapshot load", _logManager);
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
    /// global memory/count metrics). Safe to call concurrently — <see cref="SnapshotBucket.Set"/>
    /// only mutates the <see cref="ConcurrentDictionary{TKey, TValue}"/> and <see cref="Interlocked"/>
    /// counters. The non-concurrent <see cref="SortedSet{T}"/> ordered ids are populated by the
    /// serial post-pass in <see cref="LoadFromCatalog"/>.
    /// </summary>
    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        ArenaReservation reservation = _arena.Open(entry.Location);

        // The PersistedSnapshot ctor walks its own ref_ids metadata and leases each blob
        // arena file (and reads its blob_range from the same metadata); on partial failure
        // it releases what it took and disposes the reservation lease before rethrowing —
        // no repository-side cleanup needed.
        PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, _blobs);

        // Bloom is intentionally NOT built here — each snapshot is constructed with the
        // AlwaysTrue placeholder (correct, but unfiltered). LoadFromCatalog's ReconstructBloom
        // pass replaces it with the snapshot's real bloom once every snapshot is in place.

        // Route by the stored Kind, not by the To-From distance: a base and a sub-CompactSize
        // compacted snapshot can span the same number of blocks, so range alone cannot tell
        // them apart.
        SnapshotBucket bucket = entry.Kind switch
        {
            SnapshotKind.Compacted => _compacted,
            SnapshotKind.Persistable => _persistable,
            _ => _base,
        };
        bucket.Set(entry.To, snapshot);
    }


    /// <summary>
    /// Persist an in-memory snapshot as a base input: write its HSST metadata + a contiguous
    /// trie-RLP region into the arena / blob pools (the region is recorded in the metadata
    /// HSST's <c>blob_range</c> key by the builder), and insert it into <see cref="_base"/>.
    /// </summary>
    public PersistedSnapshot ConvertSnapshotToPersistedSnapshot(Snapshot snapshot)
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
        using BlobArenaWriter blobWriter = _blobs.CreateWriter(estimatedSize);
        using (ArenaWriter arenaWriter = _arena.CreateWriter(estimatedSize))
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

        // PersistedSnapshot's ctor reads its own ref_ids metadata and leases each blob
        // arena file, and reads its contiguous blob run from the blob_range metadata key the
        // builder wrote. The single id written above (blobWriter.BlobArenaId) is the only
        // entry the new metadata carries, so the ctor's iterator yields exactly that id.
        PersistedSnapshot persisted = new(snapshot.From, snapshot.To, reservation, _blobs, bloom);
        if (_validatePersistedSnapshot)
            PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);
        // Add records the catalog entry, indexes the snapshot, and pre-acquires the caller's
        // lease under the bucket's lock so a racing RemoveStatesUntil can't dispose the entry
        // between insert and the caller seeing the return.
        _base.Add(snapshot.From, snapshot.To, location, persisted);

        // Release the metadata writer's creation lease (PersistedSnapshot took its own in
        // the ctor). The blob writer's creation lease is dropped automatically when its
        // `using` scope exits — BlobArenaWriter.Dispose calls BlobArenaFile.Dispose.
        reservation.Dispose();
        return persisted;
    }

    /// <summary>
    /// Store a compacted snapshot with a pre-computed location and reservation. The
    /// snapshot's referenced blob arena ids are read off its own metadata HSST by the
    /// <see cref="PersistedSnapshot"/> ctor, which leases each one and rolls back on
    /// partial failure. <paramref name="isPersistable"/> routes a <c>CompactSize</c>-wide
    /// merge into <see cref="_persistable"/> (the RocksDB-bound bucket);
    /// otherwise it lands in <see cref="_compacted"/>.
    /// </summary>
    public PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false)
    {
        PersistedSnapshot snapshot = new(from, to, reservation, _blobs, bloom: bloom);
        // Add records the catalog entry (with the bucket's own SnapshotKind), indexes the
        // snapshot, and pre-acquires the caller's lease under the bucket's lock so a racing
        // RemoveStatesUntil on a background compactor thread can't dispose it between insert
        // and the caller seeing the return.
        (isPersistable ? _persistable : _compacted).Add(from, to, location, snapshot);

        // Release the caller's "creation" lease — see ConvertSnapshotToPersistedSnapshot.
        reservation.Dispose();
        return snapshot;
    }

    /// <summary>
    /// Assemble persisted snapshots for compaction, walking backward from toStateId.
    /// At each hop the widest snapshot that does not span past minBlockNumber is chosen —
    /// compacted, then the CompactSize-wide persistable, then base.
    /// Returns oldest-first list, or empty if fewer than 2 snapshots found.
    /// Mirrors <see cref="SnapshotRepository.AssembleSnapshotsUntil"/>.
    /// </summary>
    public PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber)
    {
        PersistedSnapshotList result = new(0);
        StateId current = toStateId;

        while (true)
        {
            PersistedSnapshot? snapshot = SelectForCompaction(current, minBlockNumber);
            if (snapshot is null)
                break;

            if (!snapshot.TryAcquire())
            {
                result.Dispose();
                return PersistedSnapshotList.Empty();
            }

            result.Add(snapshot);

            if (snapshot.From == current)
                break; // Prevent infinite loop

            if (snapshot.From.BlockNumber == minBlockNumber)
                break;

            current = snapshot.From;
        }

        if (result.Count < 2)
        {
            result.Dispose();
            return PersistedSnapshotList.Empty();
        }

        result.Reverse(); // oldest-first
        return result;
    }

    /// <summary>
    /// Pick the widest snapshot ending at <paramref name="current"/> whose <c>From</c> does
    /// not span past <paramref name="minBlockNumber"/>: compacted, then the CompactSize-wide
    /// persistable, then base. The persistable tier MUST be walked — it is the only source
    /// the &gt;CompactSize boundary compaction has.
    /// </summary>
    private PersistedSnapshot? SelectForCompaction(StateId current, long minBlockNumber)
    {
        if (_compacted.TryGet(current, out PersistedSnapshot? compacted)
            && compacted.From.BlockNumber >= minBlockNumber)
            return compacted;
        if (_persistable.TryGet(current, out PersistedSnapshot? persistable)
            && persistable.From.BlockNumber >= minBlockNumber)
            return persistable;
        if (_base.TryGet(current, out PersistedSnapshot? baseSnap)
            && baseSnap.From.BlockNumber >= minBlockNumber)
            return baseSnap;
        return null;
    }

    public bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_base.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    public bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_compacted.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        if (_persistable.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>
    /// Lease the <c>CompactSize</c>-wide persistable snapshot ending at <paramref name="toState"/>
    /// — the candidate <c>PersistenceManager</c> writes to RocksDB.
    /// </summary>
    public bool TryLeasePersistableCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_persistable.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>
    /// Lease every base snapshot tiling <c>(from, to]</c>, walking <c>From</c> pointers back
    /// from <paramref name="to"/>. Used to bulk-prefetch the base blob-RLP regions before a
    /// linked persistable is scanned. Best-effort — stops at the first gap. Caller disposes
    /// the returned list.
    /// </summary>
    public PersistedSnapshotList LeaseBaseSnapshotsInRange(StateId from, StateId to)
    {
        PersistedSnapshotList result = new(0);
        StateId current = to;
        while (current != from && current.BlockNumber > from.BlockNumber)
        {
            if (!_base.TryGet(current, out PersistedSnapshot? snapshot) || !snapshot.TryAcquire())
                break;
            result.Add(snapshot);
            if (snapshot.From == current)
                break; // Prevent infinite loop
            current = snapshot.From;
        }
        return result;
    }

    /// <summary>
    /// Prune snapshots with To.BlockNumber before the given block number. Blob arenas referenced
    /// by surviving compacted snapshots stay alive automatically via the
    /// <see cref="BlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public void RemoveStatesUntil(long blockNumber)
    {
        _base.PruneBefore(blockNumber);
        _compacted.PruneBefore(blockNumber);
        _persistable.PruneBefore(blockNumber);
    }

    /// <inheritdoc/>
    public ArrayPoolList<StateId> GetPersistedStatesInRange(long startBlockInclusive, long endBlockInclusive)
    {
        if (endBlockInclusive < startBlockInclusive) return ArrayPoolList<StateId>.Empty();

        StateId min = new(startBlockInclusive, ValueKeccak.Zero);
        StateId max = new(endBlockInclusive, ValueKeccak.MaxValue);

        // A `To` can live in more than one bucket (a base and a compacted snapshot can share it),
        // so dedupe across the three block-ordered sets.
        HashSet<StateId> union = [];
        _base.CollectRange(min, max, union);
        _compacted.CollectRange(min, max, union);
        _persistable.CollectRange(min, max, union);

        ArrayPoolList<StateId> result = new(union.Count);
        foreach (StateId to in union) result.Add(to);
        return result;
    }

    /// <inheritdoc/>
    // `|` (not `||`): every bucket must be attempted — a `To` can appear in more than one.
    public bool RemovePersistedStateExact(in StateId toState) =>
        _base.RemoveExact(toState) | _compacted.RemoveExact(toState) | _persistable.RemoveExact(toState);

    public bool HasBaseSnapshot(in StateId stateId) => _base.ContainsKey(stateId);

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
    /// Invoked from <see cref="LoadFromCatalog"/> at construction.
    /// </remarks>
    private void ReconstructBloom()
    {
        if (!BloomEnabled) return;

        // The catalog is keyed by (To, depth), so a base, a compacted, and a persistable can
        // all coexist at the same To across the three buckets — each is an independently
        // assemblable snapshot and gets its own bloom.
        List<PersistedSnapshot> snapshots = [];
        foreach (SnapshotBucket bucket in (ReadOnlySpan<SnapshotBucket>)[_base, _compacted, _persistable])
            foreach (PersistedSnapshot snap in bucket.Snapshots)
                snapshots.Add(snap);

        // Widest-first so the big merges (slowest to scan) lead the parallel queue.
        snapshots.Sort(static (a, b) =>
            (b.To.BlockNumber - b.From.BlockNumber).CompareTo(a.To.BlockNumber - a.From.BlockNumber));

        ProgressLogger? bloomLog = null;
        Timer? heartbeat = null;
        if (snapshots.Count > ParallelLoadThreshold && _logger.IsInfo)
        {
            bloomLog = new ProgressLogger("Persisted snapshot bloom rebuild", _logManager);
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

    public void Dispose()
    {
        // Mark every loaded snapshot's files as shutdown-preserved before any teardown runs.
        // Snapshots already pruned during this session aren't in the buckets, so their files
        // won't get the flag and will be deleted by the managers' final Dispose below. This
        // pass must complete for every bucket before any disposal — a file shared between a base
        // and a compacted snapshot must be flagged before either of them is torn down.
        _base.PersistAllOnShutdown();
        _compacted.PersistAllOnShutdown();
        _persistable.PersistAllOnShutdown();

        // Dispose snapshots (drops their reservation + blob leases) and roll back each bucket's
        // share of the global metrics. Files self-clean as their refcount hits zero; the preserve
        // flag set above keeps the on-disk file in place for any snapshot that opted in.
        _base.DisposeAndClear();
        _compacted.DisposeAndClear();
        _persistable.DisposeAndClear();

        // Drop the managers' dictionary refs; any file still alive cleans up here.
        // Orphans / unreferenced files (no PersistOnShutdown caller) get deleted.
        _arena.Dispose();
        _blobs.Dispose();
    }

    /// <summary>
    /// One self-contained snapshot bucket for a single <see cref="SnapshotKind"/>: a <c>To</c>-keyed
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> for lock-free point lookups, a block-ordered
    /// <see cref="SortedSet{T}"/> of its <c>To</c>s, and running memory/count totals — all guarded by
    /// the bucket's own <see cref="Lock"/>. The bucket owns its share of the shared catalog and the
    /// process-wide memory/count metrics, so insert/prune/remove are end-to-end here.
    /// </summary>
    /// <remarks>
    /// Totals are read lock-free via <see cref="Interlocked.Read(ref long)"/>; the dictionary serves
    /// point lookups lock-free. The lock only serialises ordered-set mutation, catalog writes, and
    /// the lease/dispose handoff so a racing prune cannot dispose an entry between insert and return.
    /// </remarks>
    private sealed class SnapshotBucket(SnapshotCatalog catalog, SnapshotKind kind)
    {
        private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _byTo = new();
        private readonly SortedSet<StateId> _ordered = [];
        private readonly Lock _lock = new();
        private long _memoryBytes;
        private long _count;

        public long MemoryBytes => Interlocked.Read(ref _memoryBytes);
        public long Count => Interlocked.Read(ref _count);

        // The process-wide memory gauge for this bucket's tier: base snapshots and the
        // compacted/persistable tiers are tracked under separate aggregates.
        private ref long GlobalMemory => ref (kind == SnapshotKind.Base
            ? ref Metrics._persistedSnapshotMemory
            : ref Metrics._compactedPersistedSnapshotMemory);

        /// <summary>Live snapshots, for one-off lifecycle iteration (bloom rebuild) at construction.
        /// Enumerates the dictionary directly — does not allocate a Values snapshot.</summary>
        public IEnumerable<PersistedSnapshot> Snapshots
        {
            get
            {
                foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
                    yield return kv.Value;
            }
        }

        public bool TryGet(in StateId to, [NotNullWhen(true)] out PersistedSnapshot? snapshot) =>
            _byTo.TryGetValue(to, out snapshot);

        public bool ContainsKey(in StateId to) => _byTo.ContainsKey(to);

        /// <summary>
        /// Insert the dictionary entry and bump this bucket's + the global memory/count totals.
        /// Lock-free (used by the parallel catalog load); the ordered set is populated separately
        /// via <see cref="RegisterOrdered"/>.
        /// </summary>
        public void Set(in StateId to, PersistedSnapshot snapshot)
        {
            _byTo[to] = snapshot;
            Interlocked.Add(ref _memoryBytes, snapshot.Size);
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref GlobalMemory, snapshot.Size);
            Interlocked.Increment(ref Metrics._persistedSnapshotCount);
        }

        /// <summary>Record <paramref name="to"/> in the block-ordered set, under this bucket's lock.
        /// Used by the serial post-pass of the catalog load.</summary>
        public void RegisterOrdered(in StateId to)
        {
            lock (_lock) _ordered.Add(to);
        }

        /// <summary>
        /// Runtime insert of a freshly persisted snapshot: write its catalog entry (tagged with this
        /// bucket's <see cref="SnapshotKind"/>), index it (dictionary + ordered set + totals), and
        /// pre-acquire the caller's lease — all under this bucket's lock so a racing prune cannot
        /// dispose the entry between insert and the caller seeing the return.
        /// </summary>
        public void Add(in StateId from, in StateId to, in SnapshotLocation location, PersistedSnapshot snapshot)
        {
            lock (_lock)
            {
                catalog.Add(new SnapshotCatalog.CatalogEntry(from, to, location, kind));
                Set(to, snapshot);
                _ordered.Add(to);
                snapshot.AcquireLease();
            }
        }

        /// <summary>Remove the entry at <paramref name="to"/> (catalog + index + leases) under this
        /// bucket's lock. Returns <c>true</c> when an entry was present.</summary>
        public bool RemoveExact(in StateId to)
        {
            lock (_lock) return RemoveLocked(to);
        }

        /// <summary>
        /// Prune the block-ordered prefix whose <c>To.BlockNumber &lt; beforeBlock</c>, removing each
        /// entry (catalog + index + leases) under this bucket's lock.
        /// </summary>
        public void PruneBefore(long beforeBlock)
        {
            lock (_lock)
            {
                // Materialise the prefix first — the removal loop mutates the ordered set.
                using ArrayPoolList<StateId> toRemove = new(0);
                foreach (StateId to in _ordered)
                {
                    if (to.BlockNumber >= beforeBlock) break;
                    toRemove.Add(to);
                }
                foreach (StateId to in toRemove) RemoveLocked(to);
            }
        }

        /// <summary>Copy this bucket's <c>To</c>s in the inclusive [<paramref name="min"/>,
        /// <paramref name="max"/>] range into <paramref name="into"/>, under this bucket's lock.</summary>
        public void CollectRange(in StateId min, in StateId max, ISet<StateId> into)
        {
            lock (_lock)
                foreach (StateId to in _ordered.GetViewBetween(min, max))
                    into.Add(to);
        }

        /// <summary>Mark every live snapshot's files shutdown-preserved, under this bucket's lock.
        /// Must complete across all buckets before any <see cref="DisposeAndClear"/>.</summary>
        public void PersistAllOnShutdown()
        {
            lock (_lock)
                foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
                    kv.Value.PersistOnShutdown();
        }

        /// <summary>Dispose every live snapshot, clear the index, and roll back this bucket's
        /// contribution to the global memory/count gauges. Under this bucket's lock.</summary>
        public void DisposeAndClear()
        {
            lock (_lock)
            {
                foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _byTo)
                    kv.Value.Dispose();
                _byTo.Clear();
                _ordered.Clear();
                Interlocked.Add(ref GlobalMemory, -Interlocked.Exchange(ref _memoryBytes, 0));
                Interlocked.Add(ref Metrics._persistedSnapshotCount, -Interlocked.Exchange(ref _count, 0));
            }
        }

        /// <summary>
        /// Remove <paramref name="to"/> from the index + catalog, dispose its leases, and roll back
        /// the bucket and global totals (bumping the prune metric). This bucket's lock must be held.
        /// </summary>
        private bool RemoveLocked(in StateId to)
        {
            _ordered.Remove(to);
            if (!_byTo.TryRemove(to, out PersistedSnapshot? snapshot)) return false;
            // Capture depth before Dispose — From/To stay valid on the still-alive object, but the
            // underlying reservation/file leases are released by Dispose. The catalog key scopes the
            // removal to this bucket's entry (the other buckets' entries at the same To carry a
            // different depth and stay put).
            long depth = to.BlockNumber - snapshot.From.BlockNumber;
            Interlocked.Add(ref _memoryBytes, -snapshot.Size);
            Interlocked.Decrement(ref _count);
            Interlocked.Add(ref GlobalMemory, -snapshot.Size);
            Interlocked.Decrement(ref Metrics._persistedSnapshotCount);
            Interlocked.Increment(ref Metrics._persistedSnapshotPrunes);
            catalog.Remove(to, depth);
            snapshot.Dispose();
            return true;
        }
    }
}
