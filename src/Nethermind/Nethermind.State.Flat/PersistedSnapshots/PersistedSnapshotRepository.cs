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
public sealed class PersistedSnapshotRepository(
    IArenaManager arenaManager,
    BlobArenaManager blobArenaManager,
    IDb catalogDb,
    IFlatDbConfig config,
    ILogManager logManager) : IPersistedSnapshotRepository
{
    // Below this many catalog entries / bloom picks we skip the progress logger and
    // the heartbeat timer — the cost of one Parallel.ForEach over a tiny input is in
    // the µs range, well below the bookkeeping overhead the logger adds per tick.
    private const int ParallelLoadThreshold = 1024;
    // Heartbeat for the progress logger inside the parallel sections. The logger
    // itself dedups via state-change comparison, so sub-second ticks are cheap.
    private const int ProgressLogIntervalMs = 1000;

    private readonly IArenaManager _arena = arenaManager;
    private readonly BlobArenaManager _blobs = blobArenaManager;
    private readonly SnapshotCatalog _catalog = new(catalogDb);
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly StringLabel _tierLabel = new("persisted");
    private readonly ILogManager _logManager = logManager;
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotRepository>();
    // Each bucket groups its To-keyed ConcurrentDictionary, its block-ordered StateId set, and
    // its running memory/count totals (see SnapshotBucket). Do NOT iterate on hot or metric
    // paths — entry counts can reach hundreds of thousands in production; use TryGet for point
    // lookups and the O(1) MemoryBytes/Count aggregates. The ordered set and totals are mutated
    // under _catalogLock; the dictionary and the totals' reads are lock-free. A `To` can live in
    // more than one bucket (a base and a compacted snapshot can share it), so each keeps its own.
    private readonly SnapshotBucket _base = new();
    private readonly SnapshotBucket _compacted = new();
    private readonly SnapshotBucket _persistable = new();
    private readonly Lock _catalogLock = new();
    private StateId? _lastRegisteredState;

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    public int SnapshotCount => (int)(_base.Count + _compacted.Count + _persistable.Count);
    // Persistable snapshots are compacted (linked) snapshots — count their bytes here too.
    public long CompactedSnapshotMemory => _compacted.MemoryBytes + _persistable.MemoryBytes;

    /// <inheritdoc/>
    public StateId? LastRegisteredState
    {
        get
        {
            lock (_catalogLock)
            {
                return _lastRegisteredState;
            }
        }
    }

    private void RegisterStateIdLocked(SnapshotBucket bucket, in StateId stateId)
    {
        bucket.RegisterOrdered(stateId);
        _lastRegisteredState = stateId;
    }

    /// <summary>Highest <see cref="StateId"/> still registered across the three buckets,
    /// or <c>null</c> when all are empty. Caller holds <see cref="_catalogLock"/>.</summary>
    private StateId? ComputeLastRegisteredLocked()
    {
        StateId? max = null;
        foreach (SnapshotBucket bucket in (ReadOnlySpan<SnapshotBucket>)[_base, _compacted, _persistable])
        {
            SortedSet<StateId> set = bucket.Ordered;
            if (set.Count > 0 && (max is null || set.Max.CompareTo(max.Value) > 0))
                max = set.Max;
        }
        return max;
    }

    /// <summary>
    /// Load the persisted snapshots from the catalog, routing each into its bucket by the
    /// stored <see cref="SnapshotKind"/> (range alone cannot tell a base from a
    /// sub-<c>CompactSize</c> compacted snapshot apart). For catalogs above
    /// <see cref="ParallelLoadThreshold"/> entries, the per-entry arena/blob lease work
    /// runs on <see cref="Parallel.ForEach"/> with a heartbeat <see cref="ProgressLogger"/>;
    /// the non-concurrent <c>SortedSet</c> tip and ordered-id rebuild runs serially after.
    /// </summary>
    public void LoadFromCatalog()
    {
        lock (_catalogLock)
        {
            // Blob arena pool first — rehydrates file lengths so the PersistedSnapshot
            // ctor's TryLeaseFile calls (driven by each snapshot's ref_ids metadata) can
            // resolve the ids. Whole-file reservations are created lazily on first lease.
            _blobs.Initialize();

            _catalog.Load();
            List<SnapshotCatalog.CatalogEntry> entries = [.. _catalog.Entries];
            _arena.Initialize(entries);

            LoadSnapshotsParallel(entries);

            // Serial post-pass: build the SortedSets and the registration tip from the now-
            // populated dicts. The catalog returns entries already sorted by To.BlockNumber
            // ascending, so _lastRegisteredState ends on the highest registered StateId
            // without a separate ComputeLastRegisteredLocked() call.
            foreach (SnapshotCatalog.CatalogEntry entry in entries)
            {
                SnapshotBucket bucket = entry.Kind switch
                {
                    SnapshotKind.Compacted => _compacted,
                    SnapshotKind.Persistable => _persistable,
                    _ => _base,
                };
                RegisterStateIdLocked(bucket, entry.To);
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
    /// Routes a single catalog entry into its bucket dictionary and bumps the matching
    /// metric counters. Safe to call concurrently — only mutates the
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> buckets and <see cref="Interlocked"/>
    /// counters. The non-concurrent <see cref="SortedSet{T}"/> ordered ids and the
    /// <see cref="_lastRegisteredState"/> tip are populated by the serial post-pass in
    /// <see cref="LoadFromCatalog"/>.
    /// </summary>
    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        ArenaReservation reservation = _arena.Open(entry.Location);

        // The PersistedSnapshot ctor walks its own ref_ids metadata and leases each blob
        // arena file; on partial failure it releases what it took and disposes the
        // reservation lease before rethrowing — no repository-side cleanup needed.
        PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, _blobs, entry.BlobRange);

        // Bloom is intentionally NOT built here — each snapshot is constructed with the
        // AlwaysTrue placeholder (correct, but unfiltered). LoadFromCatalog's ReconstructBloom
        // pass replaces it with the snapshot's real bloom once every snapshot is in place.
        switch (entry.Kind)
        {
            case SnapshotKind.Compacted:
                _compacted.Set(entry.To, snapshot);
                Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, snapshot.Size);
                break;
            case SnapshotKind.Persistable:
                _persistable.Set(entry.To, snapshot);
                Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, snapshot.Size);
                break;
            default:
                _base.Set(entry.To, snapshot);
                Interlocked.Add(ref Metrics._persistedSnapshotMemory, snapshot.Size);
                break;
        }
        Interlocked.Increment(ref Metrics._persistedSnapshotCount);
    }


    /// <summary>
    /// Persist an in-memory snapshot as a base input: write its HSST metadata + a contiguous
    /// trie-RLP region into the arena / blob pools, record the region as a
    /// <see cref="BlobRange"/> in the catalog, and insert it into <see cref="_base"/>.
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

        // The base snapshot's trie RLPs occupy one contiguous run in the single blob arena
        // this writer targeted — record it so persistence can prefetch it (a base that wrote
        // no trie nodes has an empty run).
        BlobRange blobRange = blobWriter.Written > blobWriter.StartOffset
            ? new BlobRange(blobWriter.BlobArenaId, blobWriter.StartOffset, blobWriter.Written - blobWriter.StartOffset)
            : BlobRange.None;

        // PersistedSnapshot's ctor reads its own ref_ids metadata and leases each blob
        // arena file. The single id written above (blobWriter.BlobArenaId) is the only
        // entry the new metadata carries, so the ctor's iterator yields exactly that id.
        PersistedSnapshot persisted;
        lock (_catalogLock)
        {
            _catalog.Add(new SnapshotCatalog.CatalogEntry(snapshot.From, snapshot.To, location, blobRange, SnapshotKind.Base));

            persisted = new PersistedSnapshot(snapshot.From, snapshot.To, reservation, _blobs, blobRange, bloom);
            if (_validatePersistedSnapshot)
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted);
            _base.Set(snapshot.To, persisted);
            Interlocked.Add(ref Metrics._persistedSnapshotMemory, persisted.Size);
            Interlocked.Increment(ref Metrics._persistedSnapshotCount);
            RegisterStateIdLocked(_base, snapshot.To);
            // Pre-acquire the caller's lease inside the lock so a racing RemoveStatesUntil can't
            // dispose the dict entry between the unlock and the caller seeing the return.
            persisted.AcquireLease();
        }

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
        PersistedSnapshot snapshot;
        lock (_catalogLock)
        {
            _catalog.Add(new SnapshotCatalog.CatalogEntry(from, to, location, BlobRange.None,
                isPersistable ? SnapshotKind.Persistable : SnapshotKind.Compacted));

            snapshot = new PersistedSnapshot(from, to, reservation, _blobs, bloom: bloom);

            if (isPersistable)
            {
                _persistable.Set(to, snapshot);
                RegisterStateIdLocked(_persistable, to);
            }
            else
            {
                _compacted.Set(to, snapshot);
                RegisterStateIdLocked(_compacted, to);
            }
            Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, snapshot.Size);
            Interlocked.Increment(ref Metrics._persistedSnapshotCount);
            // Pre-acquire the caller's lease inside the lock so a racing RemoveStatesUntil on a
            // background compactor thread can't dispose the dict entry between unlock and
            // the caller seeing the return.
            snapshot.AcquireLease();
        }

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
    /// Find the base snapshot whose <see cref="PersistedSnapshot.From"/> matches <paramref name="fromState"/>,
    /// reaching it via a backward BFS from <paramref name="seedState"/> over the <c>To</c>-keyed dictionaries.
    /// </summary>
    /// <remarks>
    /// The graph is walked by following each visited snapshot's <c>From</c> pointer; compacted entries act as
    /// skip pointers (longer per-hop block ranges) that accelerate convergence but are never returned as the
    /// answer — only entries from <see cref="_base"/> are candidates. <paramref name="seedState"/>
    /// must be a recent (>= <paramref name="fromState"/>) state to walk back from; callers typically pass the
    /// in-memory snapshot repository's earliest <c>StateId</c>.
    /// </remarks>
    internal PersistedSnapshot? TryGetSnapshotFrom(StateId fromState)
    {
        StateId? seed = LastRegisteredState;
        return seed is null ? null : TryGetSnapshotFrom(fromState, seed.Value);
    }

    internal PersistedSnapshot? TryGetSnapshotFrom(StateId fromState, StateId seedState)
    {
        if (seedState.BlockNumber <= fromState.BlockNumber) return null;

        HashSet<StateId> seen = [seedState];
        Queue<StateId> queue = new();
        queue.Enqueue(seedState);

        while (queue.Count > 0)
        {
            StateId current = queue.Dequeue();

            // Skip pointer: compacted edge is navigated through but never returned.
            if (_compacted.TryGet(current, out PersistedSnapshot? compacted))
            {
                StateId next = compacted.From;
                if (next.BlockNumber >= fromState.BlockNumber && seen.Add(next))
                    queue.Enqueue(next);
            }

            // Skip pointer: the CompactSize-wide persistable is navigated but never returned.
            if (_persistable.TryGet(current, out PersistedSnapshot? persistable))
            {
                StateId next = persistable.From;
                if (next.BlockNumber >= fromState.BlockNumber && seen.Add(next))
                    queue.Enqueue(next);
            }

            // Candidate edge: only a base entry whose From matches is a valid answer.
            if (_base.TryGet(current, out PersistedSnapshot? baseSnap))
            {
                if (baseSnap.From == fromState && baseSnap.TryAcquire())
                    return baseSnap;

                StateId next = baseSnap.From;
                if (next.BlockNumber >= fromState.BlockNumber && seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>
    /// Prune snapshots with To.BlockNumber before the given block number. Blob arenas referenced
    /// by surviving compacted snapshots stay alive automatically via the
    /// <see cref="BlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public void RemoveStatesUntil(long blockNumber)
    {
        lock (_catalogLock)
        {
            int pruned =
                PruneBucketBeforeLocked(_base, ref Metrics._persistedSnapshotMemory, blockNumber)
              + PruneBucketBeforeLocked(_compacted, ref Metrics._compactedPersistedSnapshotMemory, blockNumber)
              + PruneBucketBeforeLocked(_persistable, ref Metrics._compactedPersistedSnapshotMemory, blockNumber);

            if (pruned > 0)
            {
                // The registration tip may have been one of the pruned entries.
                if (_lastRegisteredState is { } tip
                    && !_base.Ordered.Contains(tip)
                    && !_compacted.Ordered.Contains(tip)
                    && !_persistable.Ordered.Contains(tip))
                    _lastRegisteredState = ComputeLastRegisteredLocked();
            }
        }
    }

    /// <summary>
    /// Drop one bucket's snapshots whose <c>To.BlockNumber &lt; beforeBlock</c>. The bucket's
    /// sorted set is block-ordered, so the victims are a prefix — walk it until the first
    /// surviving block instead of scanning the dictionary end to end. Caller holds
    /// <see cref="_catalogLock"/>; returns the count removed.
    /// </summary>
    private int PruneBucketBeforeLocked(SnapshotBucket bucket, ref long globalMemory, long beforeBlock)
    {
        // Materialise the prefix first — the removal loop mutates the ordered set.
        using ArrayPoolList<StateId> toRemove = new(0);
        foreach (StateId to in bucket.Ordered)
        {
            if (to.BlockNumber >= beforeBlock) break;
            toRemove.Add(to);
        }

        int pruned = 0;
        foreach (StateId to in toRemove)
        {
            if (RemoveEntryLocked(bucket, to, ref globalMemory))
                pruned++;
        }
        return pruned;
    }

    /// <summary>
    /// Tear down one bucket's entry at <paramref name="to"/>: drop it from the ordered set and
    /// dictionary, release its leases, and update counters/metrics/catalog. Caller holds
    /// <see cref="_catalogLock"/>; returns <c>true</c> when an entry was present.
    /// </summary>
    private bool RemoveEntryLocked(SnapshotBucket bucket, in StateId to, ref long globalMemory)
    {
        // SnapshotBucket.Remove drops the ordered-set + dictionary entry and the bucket totals.
        PersistedSnapshot? snapshot = bucket.Remove(to);
        if (snapshot is null) return false;
        // Capture depth before Dispose — From/To stay valid on the still-alive object,
        // but the underlying reservation/file leases are released by Dispose. The catalog
        // key now scopes the removal to this bucket's entry (the other buckets' entries
        // at the same To carry a different depth and stay put).
        long depth = snapshot.To.BlockNumber - snapshot.From.BlockNumber;
        Interlocked.Add(ref globalMemory, -snapshot.Size);
        Interlocked.Decrement(ref Metrics._persistedSnapshotCount);
        Interlocked.Increment(ref Metrics._persistedSnapshotPrunes);
        _catalog.Remove(to, depth);
        snapshot.Dispose();
        return true;
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
        lock (_catalogLock)
        {
            foreach (SnapshotBucket bucket in (ReadOnlySpan<SnapshotBucket>)[_base, _compacted, _persistable])
            {
                foreach (StateId to in bucket.Ordered.GetViewBetween(min, max))
                    union.Add(to);
            }
        }

        ArrayPoolList<StateId> result = new(union.Count);
        foreach (StateId to in union) result.Add(to);
        return result;
    }

    /// <inheritdoc/>
    public bool RemovePersistedStateExact(in StateId toState)
    {
        lock (_catalogLock)
        {
            // `|` (not `||`): every bucket must be attempted — a `To` can appear in more than one.
            bool removed =
                RemoveEntryLocked(_base, toState, ref Metrics._persistedSnapshotMemory)
              | RemoveEntryLocked(_compacted, toState, ref Metrics._compactedPersistedSnapshotMemory)
              | RemoveEntryLocked(_persistable, toState, ref Metrics._compactedPersistedSnapshotMemory);

            if (removed
                && _lastRegisteredState is { } tip
                && !_base.Ordered.Contains(tip)
                && !_compacted.Ordered.Contains(tip)
                && !_persistable.Ordered.Contains(tip))
                _lastRegisteredState = ComputeLastRegisteredLocked();

            return removed;
        }
    }

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
    /// Invoked from <see cref="LoadFromCatalog"/>; caller holds <c>_catalogLock</c>.
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
        lock (_catalogLock)
        {
            // Mark every loaded snapshot's files as shutdown-preserved before any teardown
            // runs. Snapshots already pruned during this session aren't in these dicts, so
            // their files won't get the flag and will be deleted by the managers' final
            // Dispose below.
            ReadOnlySpan<SnapshotBucket> buckets = [_base, _compacted, _persistable];
            foreach (SnapshotBucket bucket in buckets)
                foreach (PersistedSnapshot snapshot in bucket.Snapshots)
                    snapshot.PersistOnShutdown();
            // Dispose snapshots: drops their reservation + blob leases. Files self-clean
            // as their refcount hits zero; the preserve flag set above keeps the on-disk
            // file in place for any snapshot that opted in.
            foreach (SnapshotBucket bucket in buckets)
                foreach (PersistedSnapshot snapshot in bucket.Snapshots)
                    snapshot.Dispose();

            (long baseMem, long baseCount) = _base.Clear();
            (long compactedMem, long compactedCount) = _compacted.Clear();
            (long persistableMem, long persistableCount) = _persistable.Clear();
            Interlocked.Add(ref Metrics._persistedSnapshotMemory, -baseMem);
            Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, -(compactedMem + persistableMem));
            Interlocked.Add(ref Metrics._persistedSnapshotCount, -(baseCount + compactedCount + persistableCount));
            _lastRegisteredState = null;
            // Drop the managers' dictionary refs; any file still alive cleans up here.
            // Orphans / unreferenced files (no PersistOnShutdown caller) get deleted.
            _arena.Dispose();
            _blobs.Dispose();
        }
    }

    /// <summary>
    /// One snapshot bucket: a <c>To</c>-keyed <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// for lock-free point lookups, a block-ordered <see cref="SortedSet{T}"/> of its <c>To</c>s
    /// (guarded by the repository's <c>_catalogLock</c>), and running memory/count totals
    /// (mutated under the lock, read lock-free via <see cref="Interlocked.Read(ref long)"/>).
    /// </summary>
    private sealed class SnapshotBucket
    {
        private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _byTo = new();
        private readonly SortedSet<StateId> _ordered = [];
        private long _memoryBytes;
        private long _count;

        public long MemoryBytes => Interlocked.Read(ref _memoryBytes);
        public long Count => Interlocked.Read(ref _count);

        /// <summary>Block-ordered <c>To</c> set. All access must hold the repository's catalog lock.</summary>
        public SortedSet<StateId> Ordered => _ordered;

        /// <summary>Live snapshots, for one-off lifecycle iteration (bloom rebuild, dispose).
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
        /// Insert/replace the dictionary entry and bump the bucket totals. Lock-free; the ordered
        /// set is populated separately via <see cref="RegisterOrdered"/> under the catalog lock.
        /// </summary>
        public void Set(in StateId to, PersistedSnapshot snapshot)
        {
            _byTo[to] = snapshot;
            Interlocked.Add(ref _memoryBytes, snapshot.Size);
            Interlocked.Increment(ref _count);
        }

        /// <summary>Record <paramref name="to"/> in the block-ordered set. Caller holds the catalog lock.</summary>
        public void RegisterOrdered(in StateId to) => _ordered.Add(to);

        /// <summary>
        /// Remove the entry at <paramref name="to"/> from the ordered set and dictionary and
        /// decrement the bucket totals. Caller holds the catalog lock. Returns the removed
        /// snapshot (still alive — caller disposes) or <c>null</c> when absent.
        /// </summary>
        public PersistedSnapshot? Remove(in StateId to)
        {
            _ordered.Remove(to);
            if (!_byTo.TryRemove(to, out PersistedSnapshot? snapshot)) return null;
            Interlocked.Add(ref _memoryBytes, -snapshot.Size);
            Interlocked.Decrement(ref _count);
            return snapshot;
        }

        /// <summary>
        /// Clear the dictionary + ordered set and zero the totals, returning the pre-clear
        /// (memory, count) so the caller can roll back the global metric aggregates. Caller holds
        /// the catalog lock.
        /// </summary>
        public (long Memory, long Count) Clear()
        {
            _byTo.Clear();
            _ordered.Clear();
            return (Interlocked.Exchange(ref _memoryBytes, 0), Interlocked.Exchange(ref _count, 0));
        }
    }
}
