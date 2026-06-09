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
///   <item><c>_baseSnapshots</c> — in-memory snapshots persisted directly. Each owns a
///   contiguous trie-RLP region in one blob arena (<see cref="PersistedSnapshot.BlobRange"/>).</item>
///   <item><c>_compactedSnapshots</c> — merged (linked) snapshots: sub-<c>CompactSize</c>
///   intermediates and the <c>&gt;CompactSize</c> hierarchical merges. No blob region —
///   <c>NodeRef</c>s reference the base blob arenas via <c>ref_ids</c>.</item>
///   <item><c>_persistableCompactedSnapshots</c> — the <c>CompactSize</c>-wide linked
///   snapshots written to RocksDB by <c>PersistenceManager</c>.</item>
/// </list>
/// </summary>
public sealed class PersistedSnapshotRepository(
    IArenaManager arenaManager,
    IBlobArenaManager blobArenaManager,
    IDb catalogDb,
    IFlatDbConfig config,
    PersistedSnapshotBloomFilterManager bloomManager,
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
    private readonly IBlobArenaManager _blobs = blobArenaManager;
    private readonly SnapshotCatalog _catalog = new(catalogDb);
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly StringLabel _tierLabel = new(arenaManager.Tier.Name);
    private readonly ILogManager _logManager = logManager;
    private readonly ILogger _logger = logManager.GetClassLogger<PersistedSnapshotRepository>();
    // Do NOT iterate these dictionaries on hot or metric paths — entry counts can
    // reach hundreds of thousands in production. Use TryGetValue for point lookups;
    // O(1) aggregates (Base/CompactedSnapshotMemory) are maintained as running totals
    // in the long fields below. Iteration is reserved for one-off lifecycle ops
    // (catalog prune, dispose), which run off the metric / read paths.
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _baseSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _persistableCompactedSnapshots = new();
    // Running totals matching the dictionaries above. Mutated under _catalogLock at
    // every insert/remove site; read lock-free via Interlocked.Read by the Prometheus
    // scrape thread so the metrics stay O(1) regardless of snapshot count. The count
    // counters also let SnapshotCount (consumed by Metrics.PersistedSnapshotCount and a
    // hot compactor guard) avoid ConcurrentDictionary.Count, which acquires every stripe
    // lock and briefly blocks writers.
    private long _baseSnapshotMemoryBytes;
    private long _compactedSnapshotMemoryBytes;
    private long _persistableSnapshotMemoryBytes;
    private long _baseSnapshotCount;
    private long _compactedSnapshotCount;
    private long _persistableSnapshotCount;
    // Shared across both per-tier repos. Owned by the DI container, not this repo —
    // see <see cref="Dispose"/> which does NOT dispose the manager.
    private readonly PersistedSnapshotBloomFilterManager _bloomManager = bloomManager;
    private readonly Lock _catalogLock = new();
    // One block-ordered StateId set per bucket + the registration tip — all guarded by
    // `_catalogLock`. Lookups (TryLeaseSnapshotTo, TryLeaseCompactedSnapshotTo,
    // HasBaseSnapshot) stay on the concurrent dictionaries; the ordered sets expose a
    // self-seed for backward walks (see TryGetSnapshotFrom) and let RemoveStatesUntil drop each
    // bucket's block-ordered prefix without scanning the dictionaries end to end. A `To` can
    // live in more than one bucket (a base and a compacted snapshot can share it), so each
    // bucket keeps its own set.
    private readonly SortedSet<StateId> _baseStateIds = [];
    private readonly SortedSet<StateId> _compactedStateIds = [];
    private readonly SortedSet<StateId> _persistableStateIds = [];
    private StateId? _lastRegisteredState;

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    public int SnapshotCount =>
        (int)(Interlocked.Read(ref _baseSnapshotCount)
            + Interlocked.Read(ref _compactedSnapshotCount)
            + Interlocked.Read(ref _persistableSnapshotCount));
    public long BaseSnapshotMemory => Interlocked.Read(ref _baseSnapshotMemoryBytes);
    // Persistable snapshots are compacted (linked) snapshots — count their bytes here too.
    public long CompactedSnapshotMemory =>
        Interlocked.Read(ref _compactedSnapshotMemoryBytes) + Interlocked.Read(ref _persistableSnapshotMemoryBytes);

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

    private void RegisterStateIdLocked(SortedSet<StateId> ordered, in StateId stateId)
    {
        ordered.Add(stateId);
        _lastRegisteredState = stateId;
    }

    /// <summary>Highest <see cref="StateId"/> still registered across the three buckets,
    /// or <c>null</c> when all are empty. Caller holds <see cref="_catalogLock"/>.</summary>
    private StateId? ComputeLastRegisteredLocked()
    {
        StateId? max = null;
        foreach (SortedSet<StateId> set in (ReadOnlySpan<SortedSet<StateId>>)
                 [_baseStateIds, _compactedStateIds, _persistableStateIds])
        {
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
                SortedSet<StateId> set = entry.Kind switch
                {
                    SnapshotKind.Compacted => _compactedStateIds,
                    SnapshotKind.Persistable => _persistableStateIds,
                    _ => _baseStateIds,
                };
                RegisterStateIdLocked(set, entry.To);
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
            loadLog = new ProgressLogger($"Persisted snapshot load ({_arena.Tier.Name})", _logManager);
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
        PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, _blobs, _arena.Tier, entry.BlobRange);

        // Bloom is intentionally NOT built here — the bloom subsystem starts empty after
        // LoadFromCatalog. Callers must invoke ReconstructBloom() before queries to get
        // bloom filtering. Until then, LeaseOrSentinel returns the AlwaysTrue sentinel —
        // correct (no false negatives) but unfiltered.
        switch (entry.Kind)
        {
            case SnapshotKind.Compacted:
                _compactedSnapshots[entry.To] = snapshot;
                Interlocked.Add(ref _compactedSnapshotMemoryBytes, snapshot.Size);
                Interlocked.Increment(ref _compactedSnapshotCount);
                Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, snapshot.Size);
                break;
            case SnapshotKind.Persistable:
                _persistableCompactedSnapshots[entry.To] = snapshot;
                Interlocked.Add(ref _persistableSnapshotMemoryBytes, snapshot.Size);
                Interlocked.Increment(ref _persistableSnapshotCount);
                Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, snapshot.Size);
                break;
            default:
                _baseSnapshots[entry.To] = snapshot;
                Interlocked.Add(ref _baseSnapshotMemoryBytes, snapshot.Size);
                Interlocked.Increment(ref _baseSnapshotCount);
                Interlocked.Add(ref Metrics._persistedSnapshotMemory, snapshot.Size);
                break;
        }
        Interlocked.Increment(ref Metrics._persistedSnapshotCount);
    }


    /// <summary>
    /// Persist an in-memory snapshot as a base input: write its HSST metadata + a contiguous
    /// trie-RLP region into the arena / blob pools, record the region as a
    /// <see cref="BlobRange"/> in the catalog, and insert it into <see cref="_baseSnapshots"/>.
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
            PersistedSnapshotBuilder.Build<ArenaBufferWriter, ArenaBufferReader, NoOpPin>(
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

            persisted = new PersistedSnapshot(snapshot.From, snapshot.To, reservation, _blobs, _arena.Tier, blobRange);
            RegisterBlooms(persisted, bloom);
            if (_validatePersistedSnapshot)
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, _bloomManager);
            _baseSnapshots[snapshot.To] = persisted;
            Interlocked.Add(ref _baseSnapshotMemoryBytes, persisted.Size);
            Interlocked.Increment(ref _baseSnapshotCount);
            Interlocked.Add(ref Metrics._persistedSnapshotMemory, persisted.Size);
            Interlocked.Increment(ref Metrics._persistedSnapshotCount);
            RegisterStateIdLocked(_baseStateIds, snapshot.To);
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
    /// merge into <see cref="_persistableCompactedSnapshots"/> (the RocksDB-bound bucket);
    /// otherwise it lands in <see cref="_compactedSnapshots"/>.
    /// </summary>
    public PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom, bool isPersistable = false)
    {
        PersistedSnapshot snapshot;
        lock (_catalogLock)
        {
            _catalog.Add(new SnapshotCatalog.CatalogEntry(from, to, location, BlobRange.None,
                isPersistable ? SnapshotKind.Persistable : SnapshotKind.Compacted));

            snapshot = new PersistedSnapshot(from, to, reservation, _blobs, _arena.Tier);
            RegisterBlooms(snapshot, bloom);

            if (isPersistable)
            {
                _persistableCompactedSnapshots[to] = snapshot;
                Interlocked.Add(ref _persistableSnapshotMemoryBytes, snapshot.Size);
                Interlocked.Increment(ref _persistableSnapshotCount);
                RegisterStateIdLocked(_persistableStateIds, to);
            }
            else
            {
                _compactedSnapshots[to] = snapshot;
                Interlocked.Add(ref _compactedSnapshotMemoryBytes, snapshot.Size);
                Interlocked.Increment(ref _compactedSnapshotCount);
                RegisterStateIdLocked(_compactedStateIds, to);
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
        if (_compactedSnapshots.TryGetValue(current, out PersistedSnapshot? compacted)
            && compacted.From.BlockNumber >= minBlockNumber)
            return compacted;
        if (_persistableCompactedSnapshots.TryGetValue(current, out PersistedSnapshot? persistable)
            && persistable.From.BlockNumber >= minBlockNumber)
            return persistable;
        if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap)
            && baseSnap.From.BlockNumber >= minBlockNumber)
            return baseSnap;
        return null;
    }

    public bool TryLeaseSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_baseSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    public bool TryLeaseCompactedSnapshotTo(StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (_compactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        if (_persistableCompactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
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
        if (_persistableCompactedSnapshots.TryGetValue(toState, out snapshot) && snapshot.TryAcquire())
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
            if (!_baseSnapshots.TryGetValue(current, out PersistedSnapshot? snapshot) || !snapshot.TryAcquire())
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
    /// answer — only entries from <see cref="_baseSnapshots"/> are candidates. <paramref name="seedState"/>
    /// must be a recent (>= <paramref name="fromState"/>) state to walk back from; callers typically pass the
    /// in-memory snapshot repository's earliest <c>StateId</c>.
    /// </remarks>
    /// <inheritdoc/>
    public PersistedSnapshot? TryGetSnapshotFrom(StateId fromState)
    {
        StateId? seed = LastRegisteredState;
        return seed is null ? null : TryGetSnapshotFrom(fromState, seed.Value);
    }

    public PersistedSnapshot? TryGetSnapshotFrom(StateId fromState, StateId seedState)
    {
        if (seedState.BlockNumber <= fromState.BlockNumber) return null;

        HashSet<StateId> seen = [seedState];
        Queue<StateId> queue = new();
        queue.Enqueue(seedState);

        while (queue.Count > 0)
        {
            StateId current = queue.Dequeue();

            // Skip pointer: compacted edge is navigated through but never returned.
            if (_compactedSnapshots.TryGetValue(current, out PersistedSnapshot? compacted))
            {
                StateId next = compacted.From;
                if (next.BlockNumber >= fromState.BlockNumber && seen.Add(next))
                    queue.Enqueue(next);
            }

            // Skip pointer: the CompactSize-wide persistable is navigated but never returned.
            if (_persistableCompactedSnapshots.TryGetValue(current, out PersistedSnapshot? persistable))
            {
                StateId next = persistable.From;
                if (next.BlockNumber >= fromState.BlockNumber && seen.Add(next))
                    queue.Enqueue(next);
            }

            // Candidate edge: only a base entry whose From matches is a valid answer.
            if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap))
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
    /// <see cref="IBlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public void RemoveStatesUntil(long blockNumber)
    {
        lock (_catalogLock)
        {
            int pruned =
                PruneBucketBeforeLocked(_baseSnapshots, _baseStateIds,
                    ref _baseSnapshotMemoryBytes, ref _baseSnapshotCount,
                    ref Metrics._persistedSnapshotMemory, blockNumber)
              + PruneBucketBeforeLocked(_compactedSnapshots, _compactedStateIds,
                    ref _compactedSnapshotMemoryBytes, ref _compactedSnapshotCount,
                    ref Metrics._compactedPersistedSnapshotMemory, blockNumber)
              + PruneBucketBeforeLocked(_persistableCompactedSnapshots, _persistableStateIds,
                    ref _persistableSnapshotMemoryBytes, ref _persistableSnapshotCount,
                    ref Metrics._compactedPersistedSnapshotMemory, blockNumber);

            if (pruned > 0)
            {
                // The registration tip may have been one of the pruned entries.
                if (_lastRegisteredState is { } tip
                    && !_baseStateIds.Contains(tip)
                    && !_compactedStateIds.Contains(tip)
                    && !_persistableStateIds.Contains(tip))
                    _lastRegisteredState = ComputeLastRegisteredLocked();
            }

            _bloomManager.PruneBefore(blockNumber);
        }
    }

    /// <summary>
    /// Drop one bucket's snapshots whose <c>To.BlockNumber &lt; beforeBlock</c>. The bucket's
    /// sorted set is block-ordered, so the victims are a prefix — walk it until the first
    /// surviving block instead of scanning the dictionary end to end. Caller holds
    /// <see cref="_catalogLock"/>; returns the count removed.
    /// </summary>
    private int PruneBucketBeforeLocked(
        ConcurrentDictionary<StateId, PersistedSnapshot> dict,
        SortedSet<StateId> ordered,
        ref long bucketMemory,
        ref long bucketCount,
        ref long globalMemory,
        long beforeBlock)
    {
        // Materialise the prefix first — the removal loop mutates `ordered`.
        using ArrayPoolList<StateId> toRemove = new(0);
        foreach (StateId to in ordered)
        {
            if (to.BlockNumber >= beforeBlock) break;
            toRemove.Add(to);
        }

        int pruned = 0;
        foreach (StateId to in toRemove)
        {
            if (RemoveEntryLocked(dict, ordered, to, ref bucketMemory, ref bucketCount, ref globalMemory))
                pruned++;
        }
        return pruned;
    }

    /// <summary>
    /// Tear down one bucket's entry at <paramref name="to"/>: drop it from the ordered set and
    /// dictionary, release its leases, and update counters/metrics/catalog. Caller holds
    /// <see cref="_catalogLock"/>; returns <c>true</c> when an entry was present.
    /// </summary>
    private bool RemoveEntryLocked(
        ConcurrentDictionary<StateId, PersistedSnapshot> dict,
        SortedSet<StateId> ordered,
        in StateId to,
        ref long bucketMemory,
        ref long bucketCount,
        ref long globalMemory)
    {
        ordered.Remove(to);
        if (!dict.TryRemove(to, out PersistedSnapshot? snapshot)) return false;
        // Capture depth before Dispose — From/To stay valid on the still-alive object,
        // but the underlying reservation/file leases are released by Dispose. The catalog
        // key now scopes the removal to this bucket's entry (the other buckets' entries
        // at the same To carry a different depth and stay put).
        long depth = snapshot.To.BlockNumber - snapshot.From.BlockNumber;
        Interlocked.Add(ref bucketMemory, -snapshot.Size);
        Interlocked.Decrement(ref bucketCount);
        Interlocked.Add(ref globalMemory, -snapshot.Size);
        Interlocked.Decrement(ref Metrics._persistedSnapshotCount);
        Interlocked.Increment(ref Metrics._persistedSnapshotPrunes);
        RemoveFromCatalog(to, depth);
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
            foreach (SortedSet<StateId> set in (ReadOnlySpan<SortedSet<StateId>>)
                     [_baseStateIds, _compactedStateIds, _persistableStateIds])
            {
                foreach (StateId to in set.GetViewBetween(min, max))
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
                RemoveEntryLocked(_baseSnapshots, _baseStateIds, toState,
                    ref _baseSnapshotMemoryBytes, ref _baseSnapshotCount,
                    ref Metrics._persistedSnapshotMemory)
              | RemoveEntryLocked(_compactedSnapshots, _compactedStateIds, toState,
                    ref _compactedSnapshotMemoryBytes, ref _compactedSnapshotCount,
                    ref Metrics._compactedPersistedSnapshotMemory)
              | RemoveEntryLocked(_persistableCompactedSnapshots, _persistableStateIds, toState,
                    ref _persistableSnapshotMemoryBytes, ref _persistableSnapshotCount,
                    ref Metrics._compactedPersistedSnapshotMemory);

            if (removed
                && _lastRegisteredState is { } tip
                && !_baseStateIds.Contains(tip)
                && !_compactedStateIds.Contains(tip)
                && !_persistableStateIds.Contains(tip))
                _lastRegisteredState = ComputeLastRegisteredLocked();

            // The bloom slot for `toState` is left in place: it self-prunes via PruneBefore once
            // the block falls below the persisted frontier, and a stale slot only yields a
            // correctness-safe false positive (the follow-up TryLease* miss).
            return removed;
        }
    }

    public bool HasBaseSnapshot(in StateId stateId) => _baseSnapshots.ContainsKey(stateId);

    /// <summary>
    /// Register the supplied bloom with the bloom manager. Pure handoff — the caller
    /// is responsible for producing the filter (either built from the on-disk image
    /// via <see cref="PersistedSnapshotBloomBuilder"/>, populated inline by the writer /
    /// merger, or a <see cref="BloomFilter.AlwaysTrue"/> sentinel when the bloom feature
    /// is off).
    /// </summary>
    private void RegisterBlooms(PersistedSnapshot snapshot, BloomFilter bloom) =>
        _bloomManager.Register(new PersistedSnapshotBloom(snapshot.From, snapshot.To, bloom));

    /// <summary>
    /// Build and register blooms for every loaded snapshot, matching the manager's
    /// end-state after a long-running session's compactions: blocks covered by a
    /// compacted/persistable snapshot use that snapshot's merged bloom; blocks not
    /// covered by any compaction use a per-base bloom.
    /// </summary>
    /// <remarks>
    /// Three phases: (A) walk the union of every bucket's <c>To</c> ids newest→oldest,
    /// simulating <see cref="PersistedSnapshotBloomFilterManager.Register"/>'s chain
    /// walk locally so we know exactly which slots each pick would fill — that lets us
    /// skip subsequent <c>To</c>s already covered by a wider pick. (B) reverse the
    /// collected picks so the bigger snapshots (older <c>To</c>s, where persistables
    /// and hierarchical merges accumulate) sit at the front of the parallel queue —
    /// LPT-style scheduling minimises wallclock when work sizes vary. (C) parallel
    /// bloom-build + register; <c>_blooms</c> is a <see cref="ConcurrentDictionary{TKey, TValue}"/>
    /// and Register's chain walk is CAS-based, and the picks have disjoint slot ranges
    /// by construction.
    /// Invoked from <see cref="LoadFromCatalog"/>; caller holds <c>_catalogLock</c>.
    /// </remarks>
    private void ReconstructBloom()
    {
        if (!BloomEnabled) return;

        // Snapshot the base StateId graph once so the parentLookup closure (shared by
        // both the local skip simulation and Register inside the parallel section) is a
        // cheap dict probe. Bases are usually contiguous by block number, but RemoveStatesUntil
        // can leave gaps — missing predecessor blocks are surfaced as a default StateId,
        // which Register treats as "anchor the chain here" via its own boundary check.
        Dictionary<long, StateId> parentByBlock = new(_baseStateIds.Count);
        foreach (StateId id in _baseStateIds) parentByBlock[id.BlockNumber] = id;
        Func<long, StateId> parentLookup = block =>
            parentByBlock.TryGetValue(block, out StateId id) ? id : default;

        // Phase A — serial collect.
        // The catalog is keyed by (To, depth), so a persistable / compacted entry at the
        // same To as a base round-trips independently. Walk the union of every bucket's
        // To id to ensure no slot is missed. coveredSlots mirrors Register's actual fill
        // set, so we don't redundantly pick a snapshot whose slot a wider pick already
        // owns.
        SortedSet<StateId> allTos = [.. _baseStateIds, .. _compactedStateIds, .. _persistableStateIds];
        HashSet<StateId> coveredSlots = new(allTos.Count);
        List<PersistedSnapshot> picks = [];

        foreach (StateId to in allTos.Reverse())
        {
            if (coveredSlots.Contains(to)) continue;

            PersistedSnapshot? snap = PickWidest(
                _baseSnapshots.TryGetValue(to, out PersistedSnapshot? b) ? b : null,
                _compactedSnapshots.TryGetValue(to, out PersistedSnapshot? c) ? c : null,
                _persistableCompactedSnapshots.TryGetValue(to, out PersistedSnapshot? p) ? p : null);
            if (snap is null) continue;

            picks.Add(snap);
            SimulateRegisterFill(snap, parentLookup, coveredSlots);
        }

        // Phase B — reverse for LPT scheduling. Phase A produces newest→oldest; the
        // older end holds the wider (and thus slower-to-build) persistables and
        // hierarchical merges. Putting them first in the parallel queue stops a
        // single big bloom-build from dominating the tail.
        picks.Reverse();

        // Phase C — parallel bloom-build + Register.
        ProgressLogger? bloomLog = null;
        Timer? heartbeat = null;
        if (picks.Count > ParallelLoadThreshold && _logger.IsInfo)
        {
            bloomLog = new ProgressLogger($"Persisted snapshot bloom rebuild ({_arena.Tier.Name})", _logManager);
            bloomLog.Reset(0, picks.Count);
            heartbeat = new Timer(ProgressLogIntervalMs);
            heartbeat.Elapsed += (_, _) => bloomLog.LogProgress();
            heartbeat.Start();
        }

        try
        {
            long built = 0;
            Parallel.ForEach(picks, snap =>
            {
                BloomFilter bloom = BuildBloomFor(snap);
                _bloomManager.Register(new PersistedSnapshotBloom(snap.From, snap.To, bloom), parentLookup);
                if (bloomLog is not null) bloomLog.Update(Interlocked.Increment(ref built));
            });
            bloomLog?.LogProgress();
        }
        finally
        {
            heartbeat?.Dispose();
        }
    }

    // Mirror PersistedSnapshotBloomFilterManager.Register's chain walk for the
    // ReconstructBloom path: start at snap.To, step back via parentLookup, mark each
    // visited StateId as covered. Terminates on the same `cur.BlockNumber > fromBlock`
    // boundary Register uses, so the covered set matches the slots Register will actually
    // fill (including the early exit when parentLookup returns default(StateId) past a
    // pruned gap).
    private static void SimulateRegisterFill(
        PersistedSnapshot snap, Func<long, StateId> parentLookup, HashSet<StateId> coveredSlots)
    {
        long fromBlock = snap.From.BlockNumber;
        StateId cur = snap.To;
        while (cur.BlockNumber > fromBlock)
        {
            coveredSlots.Add(cur);
            cur = cur.BlockNumber - 1 > fromBlock
                ? parentLookup(cur.BlockNumber - 1)
                : snap.From;
        }
    }

    private BloomFilter BuildBloomFor(PersistedSnapshot snap)
    {
        using WholeReadSession session = snap.BeginWholeReadSession();
        return PersistedSnapshotBloomBuilder.Build(session, snap, _bloomBitsPerKey);
    }

    // Pick the snapshot with the largest (To - From) range across the three buckets.
    // After a reload, only one of the three is non-null at a given <c>To</c> (the
    // catalog overwrites at that key); during a running session there can be a base
    // alongside a compacted / persistable at the same <c>To</c>. The compacted bucket
    // can hold either sub-CompactSize sub-merges or hierarchical (>CompactSize) merges,
    // so the widest is decided by range, not by bucket precedence.
    private static PersistedSnapshot? PickWidest(
        PersistedSnapshot? baseSnap, PersistedSnapshot? compacted, PersistedSnapshot? persistable)
    {
        PersistedSnapshot? best = null;
        long bestRange = -1;
        foreach (PersistedSnapshot? cand in (ReadOnlySpan<PersistedSnapshot?>)[baseSnap, compacted, persistable])
        {
            if (cand is null) continue;
            long range = cand.To.BlockNumber - cand.From.BlockNumber;
            if (range > bestRange) { best = cand; bestRange = range; }
        }
        return best;
    }

    private void RemoveFromCatalog(in StateId to, long depth)
    {
        SnapshotCatalog.CatalogEntry? entry = _catalog.Find(to, depth);
        if (entry is not null)
            _catalog.Remove(to, depth);
    }

    public void Dispose()
    {
        lock (_catalogLock)
        {
            // Mark every loaded snapshot's files as shutdown-preserved before any teardown
            // runs. Snapshots already pruned during this session aren't in these dicts, so
            // their files won't get the flag and will be deleted by the managers' final
            // Dispose below.
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
                kv.Value.PersistOnShutdown();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
                kv.Value.PersistOnShutdown();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _persistableCompactedSnapshots)
                kv.Value.PersistOnShutdown();
            // Dispose snapshots: drops their reservation + blob leases. Files self-clean
            // as their refcount hits zero; the preserve flag set above keeps the on-disk
            // file in place for any snapshot that opted in.
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
                kv.Value.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
                kv.Value.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _persistableCompactedSnapshots)
                kv.Value.Dispose();
            _baseSnapshots.Clear();
            _compactedSnapshots.Clear();
            _persistableCompactedSnapshots.Clear();
            long baseMem = Interlocked.Exchange(ref _baseSnapshotMemoryBytes, 0);
            long compactedMem = Interlocked.Exchange(ref _compactedSnapshotMemoryBytes, 0);
            long persistableMem = Interlocked.Exchange(ref _persistableSnapshotMemoryBytes, 0);
            long baseCount = Interlocked.Exchange(ref _baseSnapshotCount, 0);
            long compactedCount = Interlocked.Exchange(ref _compactedSnapshotCount, 0);
            long persistableCount = Interlocked.Exchange(ref _persistableSnapshotCount, 0);
            Interlocked.Add(ref Metrics._persistedSnapshotMemory, -baseMem);
            Interlocked.Add(ref Metrics._compactedPersistedSnapshotMemory, -(compactedMem + persistableMem));
            Interlocked.Add(ref Metrics._persistedSnapshotCount, -(baseCount + compactedCount + persistableCount));
            _baseStateIds.Clear();
            _compactedStateIds.Clear();
            _persistableStateIds.Clear();
            _lastRegisteredState = null;
            // Drop the managers' dictionary refs; any file still alive cleans up here.
            // Orphans / unreferenced files (no PersistOnShutdown caller) get deleted.
            _arena.Dispose();
            _blobs.Dispose();
            // _bloomManager is shared across tiers; owned and disposed by the DI container.
        }
    }
}
