// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Prometheus;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Per-tier persisted-snapshot store. The codebase wires two instances:
/// <list type="bullet">
///   <item>Small repo: accepts snapshots whose block range
///   <c>To - From &lt; CompactSize</c> as base inputs; its compactor merges
///   them into sub-CompactSize spans (never CompactSize itself).</item>
///   <item>Large repo: accepts snapshots of size exactly <c>CompactSize</c>
///   (written by <c>PersistenceManager</c> at boundary blocks) as base inputs;
///   its compactor merges these into 2×, 4×, ... CompactSize spans.</item>
/// </list>
/// Each instance owns its <c>(ArenaManager, BlobArenaManager,
/// SnapshotCatalog)</c> set. The pool tier is read off the arena manager
/// (<see cref="IArenaManager.Tier"/>) for histogram labelling. Blob arena ids are unique
/// within a repo, not across repos; <c>PersistedSnapshot</c>s only ever resolve <c>NodeRef</c>s
/// through their own repo's blob manager.
/// </summary>
public sealed class PersistedSnapshotRepository(
    IArenaManager arenaManager,
    IBlobArenaManager blobArenaManager,
    IDb catalogDb,
    IFlatDbConfig config,
    PersistedSnapshotBloomFilterManager bloomManager) : IPersistedSnapshotRepository
{
    private readonly IArenaManager _arena = arenaManager;
    private readonly IBlobArenaManager _blobs = blobArenaManager;
    private readonly SnapshotCatalog _catalog = new(catalogDb);
    private readonly int _compactSize = config.CompactSize;
    private readonly bool _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
    private readonly double _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
    private readonly string _tierLabel = arenaManager.Tier.Name;
    // Do NOT iterate these dictionaries on hot or metric paths — entry counts can
    // reach hundreds of thousands in production. Use TryGetValue for point lookups;
    // O(1) aggregates (Base/CompactedSnapshotMemory) are maintained as running totals
    // in the long fields below. Iteration is reserved for one-off lifecycle ops
    // (catalog prune, dispose), which run off the metric / read paths.
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _baseSnapshots = new();
    private readonly ConcurrentDictionary<StateId, PersistedSnapshot> _compactedSnapshots = new();
    // Running totals matching the dictionaries above. Mutated under _catalogLock at
    // every insert/remove site; read lock-free via Interlocked.Read by the Prometheus
    // scrape thread so the metrics stay O(1) regardless of snapshot count. The count
    // counters also let SnapshotCount (consumed by Metrics.PersistedSnapshotCount and a
    // hot compactor guard) avoid ConcurrentDictionary.Count, which acquires every stripe
    // lock and briefly blocks writers.
    private long _baseSnapshotMemoryBytes;
    private long _compactedSnapshotMemoryBytes;
    private long _baseSnapshotCount;
    private long _compactedSnapshotCount;
    // Shared across both per-tier repos. Owned by the DI container, not this repo —
    // see <see cref="Dispose"/> which does NOT dispose the manager.
    private readonly PersistedSnapshotBloomFilterManager _bloomManager = bloomManager;
    private readonly Lock _catalogLock = new();
    // Ordered StateId set + tip — both guarded by `_catalogLock`. Lookups (TryLeaseSnapshotTo,
    // TryLeaseCompactedSnapshotTo, HasBaseSnapshot) stay on the concurrent dictionaries; the
    // ordered set exists purely to expose a self-seed for backward walks
    // (see <see cref="TryGetSnapshotFrom(StateId)"/>).
    private readonly SortedSet<StateId> _orderedStateIds = [];
    private StateId? _lastRegisteredState;

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    public int SnapshotCount =>
        (int)(Interlocked.Read(ref _baseSnapshotCount) + Interlocked.Read(ref _compactedSnapshotCount));
    public long BaseSnapshotMemory => Interlocked.Read(ref _baseSnapshotMemoryBytes);
    public long CompactedSnapshotMemory => Interlocked.Read(ref _compactedSnapshotMemoryBytes);

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

    private void RegisterStateIdLocked(in StateId stateId)
    {
        _orderedStateIds.Add(stateId);
        _lastRegisteredState = stateId;
    }

    private void UnregisterStateIdLocked(in StateId stateId)
    {
        _orderedStateIds.Remove(stateId);
        if (_lastRegisteredState == stateId)
            _lastRegisteredState = _orderedStateIds.Count == 0 ? null : _orderedStateIds.Max;
    }

    /// <summary>
    /// Load this tier's persisted snapshots from its catalog. Routes each
    /// loaded snapshot into the right in-memory dictionary based on its block
    /// range: <c>range &gt; CompactSize</c> ⇒ compacted output, otherwise base
    /// input (covers small-tier <c>&lt; CompactSize</c> entries and the
    /// large-tier's exactly-<c>CompactSize</c> atoms).
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

            foreach (SnapshotCatalog.CatalogEntry entry in entries)
                LoadSnapshot(entry);

            // Delete any blob arena file no loaded snapshot referenced — recoverable
            // orphans from a mid-write crash.
            _blobs.SweepUnreferenced();
        }
    }

    private void LoadSnapshot(SnapshotCatalog.CatalogEntry entry)
    {
        long range = entry.To.BlockNumber - entry.From.BlockNumber;
        ArenaReservation reservation = _arena.Open(entry.Location);

        // The PersistedSnapshot ctor walks its own ref_ids metadata and leases each blob
        // arena file; on partial failure it releases what it took and disposes the
        // reservation lease before rethrowing — no repository-side cleanup needed.
        PersistedSnapshot snapshot = new(entry.From, entry.To, reservation, _blobs, _arena.Tier);

        // One WholeReadSession, one Build call. The bloom covers all key flavours
        // (address / slot / SD / state-trie / storage-trie) in a single filter.
        BloomFilter bloom;
        if (BloomEnabled)
        {
            using WholeReadSession session = snapshot.BeginWholeReadSession();
            bloom = PersistedSnapshotBloomBuilder.Build(session, snapshot, _bloomBitsPerKey);
        }
        else
        {
            bloom = BloomFilter.AlwaysTrue();
        }
        RegisterBlooms(snapshot, bloom);

        if (range > _compactSize)
        {
            _compactedSnapshots[entry.To] = snapshot;
            Interlocked.Add(ref _compactedSnapshotMemoryBytes, snapshot.Size);
            Interlocked.Increment(ref _compactedSnapshotCount);
        }
        else
        {
            _baseSnapshots[entry.To] = snapshot;
            Interlocked.Add(ref _baseSnapshotMemoryBytes, snapshot.Size);
            Interlocked.Increment(ref _baseSnapshotCount);
        }

        // LoadFromCatalog already holds `_catalogLock`. Catalog order is insertion order, so
        // the last entry processed wins as the tip.
        RegisterStateIdLocked(entry.To);
    }

    private readonly Histogram _persistedSnapshotSize = Prometheus.Metrics.CreateHistogram("persisted_snapshot_size", "persisted_snapshot_size", "tier");

    /// <summary>
    /// Persist an in-memory snapshot to this tier as a base input. Caller is
    /// responsible for dispatching to the correct repo (small vs large) — the
    /// repo writes unconditionally to its own <see cref="_arena"/> +
    /// <see cref="_blobs"/> with its configured tags and inserts into
    /// <see cref="_baseSnapshots"/>.
    /// </summary>
    public void ConvertSnapshotToPersistedSnapshot(Snapshot snapshot)
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
            _persistedSnapshotSize.WithLabels(_tierLabel).Observe(arenaWriter.GetWriter().Written);
            (location, reservation) = arenaWriter.Complete();
        }
        blobWriter.Complete();

        // PersistedSnapshot's ctor reads its own ref_ids metadata and leases each blob
        // arena file. The single id written above (blobWriter.BlobArenaId) is the only
        // entry the new metadata carries, so the ctor's iterator yields exactly that id.
        lock (_catalogLock)
        {
            _catalog.Add(new SnapshotCatalog.CatalogEntry(snapshot.From, snapshot.To, location));
            _catalog.Save();

            PersistedSnapshot persisted = new(snapshot.From, snapshot.To, reservation, _blobs, _arena.Tier);
            RegisterBlooms(persisted, bloom);
            if (_validatePersistedSnapshot)
                PersistedSnapshotUtils.ValidatePersistedSnapshot(snapshot, persisted, _bloomManager);
            _baseSnapshots[snapshot.To] = persisted;
            Interlocked.Add(ref _baseSnapshotMemoryBytes, persisted.Size);
            Interlocked.Increment(ref _baseSnapshotCount);
            RegisterStateIdLocked(snapshot.To);
        }

        // Release the metadata writer's creation lease (PersistedSnapshot took its own in
        // the ctor). The blob writer's creation lease is dropped automatically when its
        // `using` scope exits — BlobArenaWriter.Dispose calls BlobArenaFile.Dispose.
        reservation.Dispose();
    }

    /// <summary>
    /// Store a compacted snapshot with a pre-computed location and reservation. The
    /// snapshot's referenced blob arena ids are read off its own metadata HSST by the
    /// <see cref="PersistedSnapshot"/> ctor, which leases each one and rolls back on
    /// partial failure.
    /// </summary>
    public PersistedSnapshot AddCompactedSnapshot(StateId from, StateId to, SnapshotLocation location, ArenaReservation reservation, BloomFilter bloom)
    {
        PersistedSnapshot snapshot;
        lock (_catalogLock)
        {
            _catalog.Add(new SnapshotCatalog.CatalogEntry(from, to, location));
            _catalog.Save();

            snapshot = new PersistedSnapshot(from, to, reservation, _blobs, _arena.Tier);
            RegisterBlooms(snapshot, bloom);

            _compactedSnapshots[to] = snapshot;
            Interlocked.Add(ref _compactedSnapshotMemoryBytes, snapshot.Size);
            Interlocked.Increment(ref _compactedSnapshotCount);
            RegisterStateIdLocked(to);
        }

        // Release the caller's "creation" lease — see ConvertSnapshotToPersistedSnapshot.
        reservation.Dispose();
        return snapshot;
    }

    /// <summary>
    /// Assemble persisted snapshots for compaction, walking backward from toStateId.
    /// If a compacted snapshot spans too far back (below minBlockNumber), fall back to base.
    /// Returns oldest-first list, or empty if fewer than 2 snapshots found.
    /// Mirrors <see cref="SnapshotRepository.AssembleSnapshotsUntil"/>.
    /// </summary>
    public PersistedSnapshotList AssembleSnapshotsForCompaction(StateId toStateId, long minBlockNumber)
    {
        PersistedSnapshotList result = new(0);
        StateId current = toStateId;

        while (true)
        {
            PersistedSnapshot? snapshot;

            // Try compacted first
            if (_compactedSnapshots.TryGetValue(current, out PersistedSnapshot? compacted))
            {
                if (compacted.From.BlockNumber < minBlockNumber)
                {
                    // Compacted spans too far back, try base
                    if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap))
                    {
                        if (baseSnap.From.BlockNumber < minBlockNumber)
                            break; // Base also spans too far
                        snapshot = baseSnap;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    snapshot = compacted;
                }
            }
            else if (_baseSnapshots.TryGetValue(current, out PersistedSnapshot? baseSnap))
            {
                if (baseSnap.From.BlockNumber < minBlockNumber)
                    break;
                snapshot = baseSnap;
            }
            else
            {
                break;
            }

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
        snapshot = null;
        return false;
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
    /// Prune snapshots with To.BlockNumber before the given state. Blob arenas referenced
    /// by surviving compacted snapshots stay alive automatically via the
    /// <see cref="IBlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public int PruneBefore(StateId stateId)
    {
        lock (_catalogLock)
        {
            int pruned = 0;

            using ArrayPoolList<StateId> baseToRemove = new(0);
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber)
                    baseToRemove.Add(kv.Key);
            }
            foreach (StateId key in baseToRemove)
            {
                if (_baseSnapshots.TryRemove(key, out PersistedSnapshot? snapshot))
                {
                    Interlocked.Add(ref _baseSnapshotMemoryBytes, -snapshot.Size);
                    Interlocked.Decrement(ref _baseSnapshotCount);
                    RemoveFromCatalog(snapshot.To);
                    UnregisterStateIdLocked(snapshot.To);
                    snapshot.Dispose();
                    pruned++;
                }
            }

            // Prune compacted snapshots
            using ArrayPoolList<StateId> compactedToRemove = new(0);
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
            {
                if (kv.Value.To.BlockNumber < stateId.BlockNumber)
                    compactedToRemove.Add(kv.Key);
            }
            foreach (StateId key in compactedToRemove)
            {
                if (_compactedSnapshots.TryRemove(key, out PersistedSnapshot? snapshot))
                {
                    Interlocked.Add(ref _compactedSnapshotMemoryBytes, -snapshot.Size);
                    Interlocked.Decrement(ref _compactedSnapshotCount);
                    RemoveFromCatalog(snapshot.To);
                    UnregisterStateIdLocked(snapshot.To);
                    snapshot.Dispose();
                    pruned++;
                }
            }

            _bloomManager.PruneBefore(stateId);

            if (pruned > 0) _catalog.Save();
            return pruned;
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

    private void RemoveFromCatalog(in StateId to)
    {
        SnapshotCatalog.CatalogEntry? entry = _catalog.Find(to);
        if (entry is not null)
            _catalog.Remove(to);
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
            // Dispose snapshots: drops their reservation + blob leases. Files self-clean
            // as their refcount hits zero; the preserve flag set above keeps the on-disk
            // file in place for any snapshot that opted in.
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _baseSnapshots)
                kv.Value.Dispose();
            foreach (KeyValuePair<StateId, PersistedSnapshot> kv in _compactedSnapshots)
                kv.Value.Dispose();
            _baseSnapshots.Clear();
            _compactedSnapshots.Clear();
            Interlocked.Exchange(ref _baseSnapshotMemoryBytes, 0);
            Interlocked.Exchange(ref _compactedSnapshotMemoryBytes, 0);
            Interlocked.Exchange(ref _baseSnapshotCount, 0);
            Interlocked.Exchange(ref _compactedSnapshotCount, 0);
            _orderedStateIds.Clear();
            _lastRegisteredState = null;
            // Drop the managers' dictionary refs; any file still alive cleans up here.
            // Orphans / unreferenced files (no PersistOnShutdown caller) get deleted.
            _arena.Dispose();
            _blobs.Dispose();
            // _bloomManager is shared across tiers; owned and disposed by the DI container.
        }
    }
}
