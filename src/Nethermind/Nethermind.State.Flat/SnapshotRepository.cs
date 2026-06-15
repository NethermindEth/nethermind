// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Autofac.Features.AttributeFilters;
using Collections.Pooled;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat;

/// <summary>
/// The single snapshot repository owning both tiers: the in-memory snapshots (base + compacted
/// dictionaries) and the persisted tier (three <see cref="SnapshotBucket"/>s over the
/// arena/blob/catalog stores). Two-tier graph walks, persistence, and compaction-assembly all
/// live here so they operate on the buckets directly.
/// </summary>
public class SnapshotRepository : ISnapshotRepository, IDisposable
{
    // ---- Edge-priority tables: the parent-edge expansion/lease order for the graph walks, one per
    // walk mode. Every order is explicit — it does NOT track SnapshotTier's numeric order.

    // ParentCursor full expansion: in-RAM-tier-first, widest-first within a tier. PersistedPersistable
    // is never expanded here (only leased explicitly via FindSnapshotToPersist).
    private static readonly SnapshotTier[] FullExpansionPriority =
    [
        SnapshotTier.InMemoryCompacted,
        SnapshotTier.InMemoryBase,
        SnapshotTier.PersistedCompacted,
        SnapshotTier.PersistedBase,
    ];

    // includePersisted == false: only the in-memory edges.
    private static readonly SnapshotTier[] InMemoryExpansionPriority =
    [
        SnapshotTier.InMemoryCompacted,
        SnapshotTier.InMemoryBase,
    ];

    // fromPersistedEdge == true: `to` was reached over a persisted edge, so persisted snapshots only
    // chain back to other persisted snapshots — the in-memory edges are guaranteed misses and skipped.
    private static readonly SnapshotTier[] PersistedContinuationPriority =
    [
        SnapshotTier.PersistedCompacted,
        SnapshotTier.PersistedBase,
    ];

    // FindSnapshotToPersist lease order: persistable, persisted base, in-memory compacted/base, then
    // the >CompactSize persisted compacted (traversed as a skip pointer, never a returnable candidate).
    private static readonly SnapshotTier[] PersistEdgePriority =
    [
        SnapshotTier.PersistedPersistable,
        SnapshotTier.PersistedBase,
        SnapshotTier.InMemoryCompacted,
        SnapshotTier.InMemoryBase,
        SnapshotTier.PersistedCompacted,
    ];

    // Persisted-only, widest-first compaction expansion: compacted, then the CompactSize-wide
    // persistable (the only source >CompactSize boundary compaction has), then base. Used by the
    // compaction mode of ParentCursor / WalkParents.
    private static readonly SnapshotTier[] CompactionEdgePriority =
    [
        SnapshotTier.PersistedCompacted,
        SnapshotTier.PersistedPersistable,
        SnapshotTier.PersistedBase,
    ];

    private readonly ILogger _logger;

    // ---- Persisted tier: three buckets keyed by StateId.To, plus the arena/blob/catalog stores.
    // Each bucket is a self-contained, individually-locked store: its To-keyed ConcurrentDictionary
    // (lock-free point lookups), its block-ordered StateId set + running memory/count totals
    // (guarded by the bucket's own lock), and its share of the catalog and global metrics. A `To`
    // can live in more than one bucket (a base and a compacted snapshot can share it).
    private readonly IArenaManager _arena;
    private readonly BlobArenaManager _blobs;
    private readonly IDb _catalogDb;
    private readonly SnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly SnapshotBucket _base;
    private readonly SnapshotBucket _compacted;
    private readonly SnapshotBucket _persistable;
    private int _disposed;

    // ---- In-memory tier.
    // Do NOT iterate these dictionaries: entry counts can reach hundreds of thousands
    // in production. Use TryGetValue / TryLease* for point lookups. Aggregates (the
    // SnapshotCount / CompactedSnapshotCount properties below, plus the static
    // Metrics.Snapshot* gauges) are maintained as running totals at the TryAdd* /
    // RemoveAndRelease* sites so the repo doesn't pay ConcurrentDictionary.Count's
    // all-stripe-lock cost on every read.
    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new([]);
    private long _snapshotCount;
    private long _compactedSnapshotCount;
    // Always guarded by `_sortedSnapshotStateIds`'s lock.
    private StateId? _lastRegisteredState;

    public SnapshotRepository(
        IArenaManager arenaManager,
        BlobArenaManager blobArenaManager,
        [KeyFilter(DbNames.PersistedSnapshotCatalog)] IDb catalogDb,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _arena = arenaManager;
        _blobs = blobArenaManager;
        _catalogDb = catalogDb;
        _catalog = new(catalogDb);
        _base = new SnapshotBucket(_catalog, SnapshotTier.PersistedBase);
        _compacted = new SnapshotBucket(_catalog, SnapshotTier.PersistedCompacted);
        _persistable = new SnapshotBucket(_catalog, SnapshotTier.PersistedPersistable);
        _compactSize = config.CompactSize;
        _logger = logManager.GetClassLogger<SnapshotRepository>();
    }

    public int SnapshotCount => (int)Interlocked.Read(ref _snapshotCount);
    // Test-only observability; not part of ISnapshotRepository.
    internal int CompactedSnapshotCount => (int)Interlocked.Read(ref _compactedSnapshotCount);

    // Test-only: lets tests build a loader/compactor over the same shared arena/blob managers and
    // catalog db the repository reads through (the compactor records its compacted entries in this
    // same catalog so a reload sees them).
    internal IArenaManager ArenaManager => _arena;
    internal BlobArenaManager BlobArenaManager => _blobs;
    internal IDb CatalogDb => _catalogDb;

    public int PersistedSnapshotCount => (int)(_base.Count + _compacted.Count + _persistable.Count);

    /// <summary>
    /// Tip used as the seed for backward walks over the snapshot graph
    /// (see <see cref="PersistenceManager"/>'s persist-finding paths).
    /// Tracks call order of <see cref="AddStateId"/>, not block-number max —
    /// the most-recent registration wins even if it lowers the block number.
    /// </summary>
    public StateId? LastRegisteredState
    {
        get
        {
            using ReadWriteLockBox<SortedSet<StateId>>.Lock readLock = _sortedSnapshotStateIds.EnterReadLock(out _);
            return _lastRegisteredState;
        }
    }

    public void AddStateId(in StateId stateId)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots);
        sortedSnapshots.Add(stateId);
        _lastRegisteredState = stateId;
    }

    // Dual-tier path BFS: each node has up to 4 edges (compacted/base × in-memory/persisted); once on a
    // persisted edge further in-memory edges are not explored. The cursor's in-mem-base-before-persisted-
    // base priority matters: a persisted-base win would lock the rest of the BFS into the persisted tier
    // (via the enqueue), barring any wider in-mem compacted skip-pointer downstream.
    private struct AssembleVisitor(StateId target, PooledSet<StateId> seen,
        ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited) : IParentWalkVisitor
    {
        public int WinnerIndex = -1;

        public WalkAction Visit(IDisposable snapshot, in StateId from, bool viaPersisted, int parentIndex, ref PooledQueue<WalkNode> queue)
        {
            if (from.BlockNumber < target.BlockNumber)
            {
                // In-memory snapshots are persistence-granular; overshoot means unusable edge. Persisted
                // (especially compacted) snapshots can span past the target — accept as the terminal
                // element without enqueuing further.
                if (!viaPersisted) { snapshot.Dispose(); return WalkAction.Continue; }
                WinnerIndex = visited.Count;
                visited.Add((snapshot, parentIndex));
                return WalkAction.Stop;
            }

            if (!seen.Add(from)) { snapshot.Dispose(); return WalkAction.Continue; } // cycle

            int idx = visited.Count;
            visited.Add((snapshot, parentIndex));
            if (from == target || from.BlockNumber == target.BlockNumber)
            {
                WinnerIndex = idx;
                return WalkAction.Stop;
            }
            queue.Enqueue(new WalkNode(from, viaPersisted, idx));
            return WalkAction.Continue;
        }
    }

    public AssembledSnapshotResult AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
    {
        if (baseBlock == targetState) return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        using ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited = new(estimatedSize);
        using PooledSet<StateId> seen = new();
        using PooledQueue<WalkNode> queue = new();
        try
        {
            seen.Add(baseBlock);
            AssembleVisitor visitor = new(targetState, seen, visited);
            WalkParents(baseBlock, startViaPersisted: false, includePersisted: true, ref visitor, queue);

            if (visitor.WinnerIndex < 0)
                return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

            // Reconstruct winning path and double-lease those snapshots so they
            // survive the finally block which disposes all visited entries.
            HashSet<int> pathIndices = [];
            int walk = visitor.WinnerIndex;
            while (walk >= 0)
            {
                pathIndices.Add(walk);
                walk = visited[walk].parentIndex;
            }

            SnapshotPooledList inMemory = new(estimatedSize);
            PersistedSnapshotList persistedList = new(0);
            for (int i = 0; i < visited.Count; i++)
            {
                if (!pathIndices.Contains(i)) continue;

                switch (visited[i].snapshot)
                {
                    case PersistedSnapshot ps:
                        ps.TryAcquire();
                        persistedList.Add(ps);
                        break;
                    case Snapshot s:
                        s.TryAcquire();
                        inMemory.Add(s);
                        break;
                }
            }

            inMemory.Reverse();
            persistedList.Reverse();
            return new AssembledSnapshotResult(inMemory, persistedList);
        }
        finally
        {
            for (int i = 0; i < visited.Count; i++)
                visited[i].snapshot.Dispose();
        }
    }

    // In-memory-only path BFS: up to 2 edges per node, widest-jump first (in-memory compacted then base).
    // Edges below minBlockNumber are pruned, so a wide compacted jump that overshoots is discarded for the
    // narrower base edge. Wins at the first node reaching minBlockNumber.
    // Holds an ArrayPoolListRef, so it must be a ref struct.
    private ref struct AssembleBfsVisitor(long minBlockNumber, PooledSet<StateId> seen, int estimatedSize) : IParentWalkVisitor
    {
        public int WinnerIndex = -1;
        public ArrayPoolListRef<(Snapshot Snapshot, int ParentIndex)> Visited = new(estimatedSize);

        public WalkAction Visit(IDisposable leased, in StateId from, bool viaPersisted, int parentIndex, ref PooledQueue<WalkNode> queue)
        {
            // In-memory-only expansion — the lease is always a Snapshot.
            Snapshot snapshot = (Snapshot)leased;

            if (from.BlockNumber < minBlockNumber || !seen.Add(from)) { snapshot.Dispose(); return WalkAction.Continue; }

            int index = Visited.Count;
            Visited.Add((snapshot, parentIndex));
            if (from.BlockNumber == minBlockNumber)
            {
                WinnerIndex = index;
                return WalkAction.Stop;
            }
            queue.Enqueue(new WalkNode(from, viaPersisted, index)); // viaPersisted always false here
            return WalkAction.Continue;
        }
    }

    /// <summary>
    /// BFS over the snapshot graph from <paramref name="baseBlock"/> back toward
    /// <paramref name="minBlockNumber"/>, returning the in-memory snapshots along the winning path in
    /// ascending order (<c>result[0].From</c> is the terminus, <c>result[^1].To == baseBlock</c>).
    /// Returns an empty list when no path reaches the terminus.
    /// </summary>
    /// <remarks>
    /// Each StateId node has up to 2 edges, explored widest-jump first - the in-memory compacted
    /// snapshot, then the in-memory base snapshot. Edges dropping below <paramref name="minBlockNumber"/>
    /// are pruned, so a wide compacted jump that overshoots is discarded in favour of the narrower base
    /// edge. The path wins at the first node reaching <paramref name="minBlockNumber"/>. `visited` owns a
    /// lease on every leased snapshot; the winning path is re-leased before the finally releases all of them.
    /// </remarks>
    public SnapshotPooledList AssembleInMemorySnapshotsForCompaction(in StateId baseBlock, long minBlockNumber, int estimatedSize)
    {
        using PooledQueue<WalkNode> queue = new();
        using PooledSet<StateId> seen = new();
        AssembleBfsVisitor visitor = new(minBlockNumber, seen, estimatedSize);
        try
        {
            seen.Add(baseBlock);
            WalkParents(baseBlock, startViaPersisted: false, includePersisted: false, ref visitor, queue);

            if (visitor.WinnerIndex < 0) return SnapshotPooledList.Empty();

            // Walk winner -> root: yields ascending order directly (result[0].From == terminus,
            // result[^1].To == baseBlock).
            SnapshotPooledList result = new(estimatedSize);
            for (int walk = visitor.WinnerIndex; walk >= 0; walk = visitor.Visited[walk].ParentIndex)
            {
                // `Visited` still holds a lease, so re-acquire cannot fail; assert flags future
                // Snapshot lifecycle changes that could break this invariant.
                bool acquired = visitor.Visited[walk].Snapshot.TryAcquire();
                Debug.Assert(acquired, "TryAcquire failed despite held lease");
                result.Add(visitor.Visited[walk].Snapshot);
            }
            return result;
        }
        finally
        {
            for (int i = 0; i < visitor.Visited.Count; i++)
                visitor.Visited[i].Snapshot.Dispose();
            visitor.Visited.Dispose();
        }
    }

    /// <summary>
    /// Edge seam over the two-tier snapshot DAG: given a node, leases the snapshot backing one of
    /// its parent (<c>From</c>) edges in the given <paramref name="tier"/>. Callers own every lease
    /// and must dispose it on all paths.
    /// </summary>
    /// <remarks>The persisted-tier mapping is not 1:1 with the buckets: <see cref="SnapshotTier.PersistedCompacted"/>
    /// leases from the compacted then the persistable bucket, so it doubles as the skip-pointer edge.</remarks>
    private bool TryLeaseParent(in StateId to, SnapshotTier tier, [NotNullWhen(true)] out IDisposable? snapshot, out StateId from)
    {
        if (tier.IsPersisted())
        {
            if (TryLeasePersistedState(to, tier, out PersistedSnapshot? persisted))
            {
                (snapshot, from) = (persisted, persisted.From);
                return true;
            }
        }
        else if (TryLeaseInMemoryState(to, tier, out Snapshot? inMemory))
        {
            (snapshot, from) = (inMemory, inMemory.From);
            return true;
        }

        (snapshot, from) = (null, default);
        return false;
    }

    private struct ParentCursor
    {
        private readonly SnapshotRepository _repo;
        private readonly StateId _to;
        private readonly SnapshotTier[] _priority;
        private int _next;

        internal ParentCursor(SnapshotRepository repo, in StateId to, bool fromPersistedEdge, bool includePersisted, bool compaction)
        {
            _repo = repo;
            _to = to;
            // fromPersistedEdge is only ever passed together with includePersisted: true, so the
            // persisted continuation always reaches the full persisted depth. The compaction mode is
            // persisted-only and includes the CompactSize-wide persistable as a source.
            _priority = compaction ? CompactionEdgePriority
                : fromPersistedEdge ? PersistedContinuationPriority
                : includePersisted ? FullExpansionPriority
                : InMemoryExpansionPriority;
            _next = 0;
        }

        /// <summary>Leases the next available parent edge in priority order. The caller owns the lease.</summary>
        public bool TryLeaseNext([NotNullWhen(true)] out IDisposable? snapshot, out StateId from, out bool viaPersistedEdge)
        {
            while (_next < _priority.Length)
            {
                SnapshotTier tier = _priority[_next++];
                if (_repo.TryLeaseParent(_to, tier, out snapshot, out from))
                {
                    viaPersistedEdge = tier.IsPersisted();
                    return true;
                }
            }

            (snapshot, from, viaPersistedEdge) = (null, default, false);
            return false;
        }
    }

    private readonly struct WalkNode(in StateId current, bool viaPersisted, int parentIndex)
    {
        public readonly StateId Current = current;
        public readonly bool ViaPersisted = viaPersisted;
        public readonly int ParentIndex = parentIndex;
    }

    private enum WalkAction { Continue, Stop }

    /// <summary>
    /// Per-edge policy for <see cref="WalkParents{TVisitor}"/>. The visitor OWNS the lease handed to it:
    /// dispose it and return <see cref="WalkAction.Continue"/> to skip the edge; retain it (e.g. in a
    /// visited list) and enqueue the child via <paramref name="queue"/> to expand; or retain/dispose per
    /// its own bookkeeping and return <see cref="WalkAction.Stop"/> to end the whole walk. The driver
    /// never disposes a lease — there is exactly one owner at all times.
    /// </summary>
    private interface IParentWalkVisitor
    {
        WalkAction Visit(IDisposable snapshot, in StateId from, bool viaPersisted, int parentIndex, ref PooledQueue<WalkNode> queue);
    }

    /// <summary>
    /// Generic backward BFS over parent (<c>From</c>) edges via <see cref="ParentCursor"/>. Owns only
    /// the frontier and the edge-expansion loop; <typeparamref name="TVisitor"/> owns cycle detection,
    /// pruning, the win condition, lease retention, and result building. <paramref name="queue"/> is
    /// supplied by the caller (and cleared here) so a hot prune loop can reuse one instance.
    /// </summary>
    private void WalkParents<TVisitor>(in StateId start, bool startViaPersisted, bool includePersisted,
            ref TVisitor visitor, PooledQueue<WalkNode> queue, bool compaction = false)
        where TVisitor : struct, IParentWalkVisitor, allows ref struct
    {
        queue.Clear();
        queue.Enqueue(new WalkNode(start, startViaPersisted, -1));

        while (queue.Count > 0)
        {
            WalkNode node = queue.Dequeue();
            ParentCursor edges = new(this, node.Current, node.ViaPersisted, includePersisted, compaction);
            while (edges.TryLeaseNext(out IDisposable? snapshot, out StateId from, out bool edgePersisted))
            {
                if (visitor.Visit(snapshot!, from, edgePersisted, node.ParentIndex, ref queue) == WalkAction.Stop)
                    return;
            }
        }
    }

    /// <summary>
    /// Phase 1 BFS — walks backward over the snapshot graph from <paramref name="seed"/> via
    /// <see cref="Snapshot.From"/> pointers, returning the first snapshot whose <c>From</c> equals
    /// <paramref name="currentPersistedState"/>. At each visited <c>StateId</c> the candidate
    /// sources are tried in the fixed <see cref="PersistEdgePriority"/> order:
    /// <list type="number">
    ///   <item><see cref="SnapshotTier.PersistedPersistable"/> — the CompactSize-wide
    ///   persistable (one persist covers the whole window)</item>
    ///   <item><see cref="SnapshotTier.PersistedBase"/> — a persisted base (fallback when the
    ///   persistable for this window has not been compacted yet)</item>
    ///   <item><see cref="SnapshotTier.InMemoryCompacted"/> filtered to depth == <paramref name="compactSize"/> —
    ///   in-memory boundary compacted</item>
    ///   <item><see cref="SnapshotTier.InMemoryBase"/> — in-memory base, depth == 1</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// &gt;CompactSize compacted persisted entries (<see cref="SnapshotTier.PersistedCompacted"/>,
    /// last in <see cref="PersistEdgePriority"/>) and non-boundary in-memory compacted entries
    /// are not returnable candidates; they are still traversed for navigation, acting as skip
    /// pointers that jump multiple blocks per hop and shorten the path to a candidate.
    /// </remarks>
    public (PersistedSnapshot? Persisted, Snapshot? InMemory) FindSnapshotToPersist(
        in StateId seed, in StateId currentPersistedState, int compactSize)
    {
        if (seed.BlockNumber <= currentPersistedState.BlockNumber) return (null, null);

        HashSet<StateId> visited = [seed];
        Queue<StateId> queue = new();
        queue.Enqueue(seed);

        while (queue.TryDequeue(out StateId current))
        {
            foreach (SnapshotTier tier in PersistEdgePriority)
            {
                if (!TryLeaseParent(current, tier, out IDisposable? snapshot, out StateId from)) continue;

                if (from == currentPersistedState && IsPersistCandidate(tier, current, from, compactSize))
                {
                    return snapshot is PersistedSnapshot persistedSnapshot
                        ? (persistedSnapshot, null)
                        : (null, (Snapshot)snapshot);
                }

                if (from.BlockNumber > currentPersistedState.BlockNumber && visited.Add(from))
                    queue.Enqueue(from);
                snapshot.Dispose();
            }
        }

        return (null, null);
    }

    private static bool IsPersistCandidate(SnapshotTier tier, in StateId to, in StateId from, int compactSize) => tier switch
    {
        SnapshotTier.PersistedCompacted => false,
        SnapshotTier.InMemoryCompacted => to.BlockNumber - from.BlockNumber == compactSize,
        _ => true,
    };

    // Best-effort persisted compaction tiling over the WalkParents driver (compaction edge set):
    // prunes edges overshooting minBlockNumber, and tracks the deepest (lowest-block) node reached.
    // Widest-first expansion + BFS means the first path to each depth is the widest one. The window
    // need not be fully populated — a partial chain (whatever reaches the deepest block >= min) still
    // merges, and a reachable full window wins immediately at min.
    private ref struct PersistedCompactionVisitor(long minBlockNumber, PooledSet<StateId> seen, int estimatedSize) : IParentWalkVisitor
    {
        public ArrayPoolListRef<(PersistedSnapshot Snapshot, int ParentIndex)> Visited = new(estimatedSize);
        public int WinnerIndex = -1;
        private long _winnerBlock = long.MaxValue;

        public WalkAction Visit(IDisposable leased, in StateId from, bool viaPersisted, int parentIndex, ref PooledQueue<WalkNode> queue)
        {
            // Compaction expansion is persisted-only — the lease is always a PersistedSnapshot.
            PersistedSnapshot snapshot = (PersistedSnapshot)leased;
            if (from.BlockNumber < minBlockNumber || !seen.Add(from)) { snapshot.Dispose(); return WalkAction.Continue; }

            int index = Visited.Count;
            Visited.Add((snapshot, parentIndex));
            if (from.BlockNumber < _winnerBlock)
            {
                _winnerBlock = from.BlockNumber;
                WinnerIndex = index;
            }

            if (from.BlockNumber == minBlockNumber) return WalkAction.Stop; // window start — deepest possible
            queue.Enqueue(new WalkNode(from, viaPersisted, index));
            return WalkAction.Continue;
        }
    }

    /// <summary>
    /// Best-effort backward BFS over the persisted tier from <paramref name="toStateId"/>, returning the
    /// contiguous chain reaching the deepest block <c>&gt;= </c><paramref name="minBlockNumber"/>
    /// (oldest-first). The window need not be fully populated; returns empty when fewer than two
    /// snapshots are found.
    /// </summary>
    public PersistedSnapshotList AssemblePersistedSnapshotsForCompaction(in StateId toStateId, long minBlockNumber)
    {
        int estimatedSize = (int)Math.Clamp(toStateId.BlockNumber - minBlockNumber, 4, 4096);
        using PooledQueue<WalkNode> queue = new();
        using PooledSet<StateId> seen = new();
        PersistedCompactionVisitor visitor = new(minBlockNumber, seen, estimatedSize);
        try
        {
            seen.Add(toStateId);
            WalkParents(toStateId, startViaPersisted: true, includePersisted: true, ref visitor, queue, compaction: true);

            if (visitor.WinnerIndex < 0) return PersistedSnapshotList.Empty();

            // Walk winner -> root: oldest-first (result[0].From == deepest terminus, result[^1].To == toStateId).
            PersistedSnapshotList result = new(estimatedSize);
            for (int walk = visitor.WinnerIndex; walk >= 0; walk = visitor.Visited[walk].ParentIndex)
            {
                bool acquired = visitor.Visited[walk].Snapshot.TryAcquire();
                Debug.Assert(acquired, "TryAcquire failed despite held lease");
                result.Add(visitor.Visited[walk].Snapshot);
            }

            if (result.Count < 2)
            {
                result.Dispose();
                return PersistedSnapshotList.Empty();
            }
            return result;
        }
        finally
        {
            for (int i = 0; i < visitor.Visited.Count; i++)
                visitor.Visited[i].Snapshot.Dispose();
            visitor.Visited.Dispose();
        }
    }

    public bool TryLeaseInMemoryState(in StateId stateId, SnapshotTier tier, [NotNullWhen(true)] out Snapshot? entry)
    {
        tier.EnsureInMemory();
        ConcurrentDictionary<StateId, Snapshot> snapshots = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
        SpinWait sw = new();
        while (snapshots.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;

            sw.SpinOnce();
        }
        return false;
    }

    public bool TryAdd(Snapshot snapshot, SnapshotTier tier)
    {
        tier.EnsureInMemory();
        if (tier == SnapshotTier.InMemoryBase)
        {
            if (_snapshots.TryAdd(snapshot.To, snapshot))
            {
                Interlocked.Increment(ref _snapshotCount);
                Metrics.SnapshotCount++;

                long totalBytes = snapshot.EstimateMemory();
                Metrics.SnapshotMemory += totalBytes;
                Metrics.TotalSnapshotMemory += totalBytes;

                return true;
            }

            return false;
        }

        if (_compactedSnapshots.TryAdd(snapshot.To, snapshot))
        {
            Interlocked.Increment(ref _compactedSnapshotCount);
            Metrics.CompactedSnapshotCount++;

            long compactedBytes = snapshot.Content.EstimateCompactedMemory();
            Metrics.CompactedSnapshotMemory += compactedBytes;
            Metrics.TotalSnapshotMemory += compactedBytes;

            return true;
        }

        return false;
    }

    public ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new(blockNumber, ValueKeccak.Zero);
        StateId max = new(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    private bool HasForkAt(long blockNumber)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new(blockNumber, ValueKeccak.Zero);
        StateId max = new(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).Count > 1;
    }

    public StateId? GetLastSnapshotId()
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);
        return sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;
    }

    public bool RemoveAndReleaseInMemoryKnownState(in StateId stateId, SnapshotTier tier)
    {
        tier.EnsureInMemory();
        if (tier == SnapshotTier.InMemoryCompacted)
        {
            if (_compactedSnapshots.TryRemove(stateId, out Snapshot? existingState))
            {
                Interlocked.Decrement(ref _compactedSnapshotCount);
                Metrics.CompactedSnapshotCount--;

                long compactedBytes = existingState.Content.EstimateCompactedMemory();
                Metrics.CompactedSnapshotMemory -= compactedBytes;
                Metrics.TotalSnapshotMemory -= compactedBytes;

                existingState.Dispose();

                return true;
            }

            return false;
        }

        if (_snapshots.TryRemove(stateId, out Snapshot? existing))
        {
            Interlocked.Decrement(ref _snapshotCount);
            Metrics.SnapshotCount--;

            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
                if (_lastRegisteredState == stateId)
                    _lastRegisteredState = sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;
            }

            long totalBytes = existing.EstimateMemory();
            Metrics.SnapshotMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            existing.Dispose(); // After memory

            return true;
        }

        return false;
    }

    public bool HasState(in StateId stateId)
    {
        if (_snapshots.ContainsKey(stateId)) return true;
        if (HasBaseSnapshot(stateId)) return true;
        return false;
    }

    public ArrayPoolList<StateId> GetStatesUpToBlock(long blockNumber)
    {
        if (blockNumber < 0)
            return ArrayPoolList<StateId>.Empty();

        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        return sortedSnapshots
            .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(blockNumber, Keccak.MaxValue))
            .ToPooledList(0);
    }

    public void RemoveStatesUntil(long blockNumber)
    {
        using ArrayPoolList<StateId> statesUpToBlock = GetStatesUpToBlock(blockNumber);
        foreach (StateId stateToRemove in statesUpToBlock)
        {
            // A To can exist in both in-memory tiers — remove from each.
            RemoveAndReleaseInMemoryKnownState(stateToRemove, SnapshotTier.InMemoryCompacted);
            RemoveAndReleaseInMemoryKnownState(stateToRemove, SnapshotTier.InMemoryBase);
        }
    }

    private const int PruneBatchSize = 1000;

    public void RemoveSiblingAndDescendents(in StateId canonicalStateId)
    {
        long canonicalBlock = canonicalStateId.BlockNumber;

        // Fast-fail when the persisted block has no sibling state in either tier: with a single
        // state at the block, every state above it chains down through the canonical one, so
        // nothing above it can be orphaned. A non-canonical sibling may live in-memory or — if it
        // was converted before the reorg pruned it — in the persisted tier.
        if (!HasForkAt(canonicalBlock) && !HasPersistedForkAt(canonicalStateId)) return;

        // The in-memory tier always sits at or above the persisted tier, so its highest block
        // bounds the orphan walk across both.
        long maxBlock = GetLastSnapshotId()?.BlockNumber ?? long.MinValue;
        if (maxBlock <= canonicalBlock) return;

        long batchStart = canonicalBlock + 1;
        int totalPruned = 0;

        using PooledQueue<WalkNode> queue = new();
        using PooledSet<StateId> seen = new();

        while (batchStart <= maxBlock)
        {
            long batchEnd = Math.Min(batchStart + PruneBatchSize - 1, maxBlock);

            // In-memory orphans above the persisted block.
            using (ArrayPoolListRef<StateId> inMemory = GetStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in inMemory)
                {
                    if (!CanReachState(stateId, canonicalStateId, queue, seen))
                    {
                        // A To can exist in both in-memory tiers — remove from each.
                        RemoveAndReleaseInMemoryKnownState(stateId, SnapshotTier.InMemoryCompacted);
                        RemoveAndReleaseInMemoryKnownState(stateId, SnapshotTier.InMemoryBase);
                        totalPruned++;
                    }
                }
            }

            // Persisted-tier orphans above the persisted block — e.g. non-canonical siblings
            // converted into the tier (DoConvert applies no canonicality filter) before the
            // reorg orphaned them, which the in-memory pass above can no longer reach.
            using (ArrayPoolList<StateId> persisted = GetPersistedStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in persisted)
                {
                    if (!CanReachState(stateId, canonicalStateId, queue, seen)
                        && RemovePersistedStateExact(stateId))
                    {
                        totalPruned++;
                    }
                }
            }

            batchStart = batchEnd + 1;
        }

        if (totalPruned > 0 && _logger.IsInfo)
        {
            _logger.Info($"Pruned {totalPruned} orphaned non-canonical snapshot(s) above persisted state {canonicalStateId}.");
        }
    }

    /// <summary>True when the persisted tier holds a state at <paramref name="canonicalStateId"/>'s
    /// block that is not the canonical state itself — a fork the canonical persist orphans.</summary>
    private bool HasPersistedForkAt(in StateId canonicalStateId)
    {
        using ArrayPoolList<StateId> atBlock =
            GetPersistedStatesInRange(canonicalStateId.BlockNumber, canonicalStateId.BlockNumber);
        foreach (StateId stateId in atBlock)
            if (stateId != canonicalStateId) return true;
        return false;
    }

    /// <remarks>
    /// Walks parent (<c>From</c>) edges from <paramref name="from"/> toward <paramref name="target"/>
    /// across both tiers via the same <see cref="ParentCursor"/> expansion as
    /// <see cref="AssembleSnapshots"/>. Each lease is read for its <c>From</c> then disposed immediately. Crossing into the persisted
    /// tier is required so a canonical in-memory state whose ancestry descends through a converted
    /// snapshot is not mistaken for an orphan.
    /// </remarks>
    // Reachability only reads each parent's From, never retains a lease. BFS (the order is irrelevant
    // for a boolean reachability result).
    private struct CanReachVisitor(StateId target, PooledSet<StateId> seen) : IParentWalkVisitor
    {
        public bool Reached = false;

        public WalkAction Visit(IDisposable snapshot, in StateId from, bool viaPersisted, int parentIndex, ref PooledQueue<WalkNode> queue)
        {
            snapshot.Dispose();

            if (from == target) { Reached = true; return WalkAction.Stop; }
            if (from.BlockNumber > target.BlockNumber && seen.Add(from))
                queue.Enqueue(new WalkNode(from, viaPersisted, parentIndex));
            return WalkAction.Continue;
        }
    }

    private bool CanReachState(in StateId from, in StateId target, PooledQueue<WalkNode> queue, PooledSet<StateId> seen)
    {
        if (from == target) return true;
        if (from.BlockNumber <= target.BlockNumber) return false;

        seen.Clear();
        seen.Add(from);
        CanReachVisitor visitor = new(target, seen);
        WalkParents(from, startViaPersisted: false, includePersisted: true, ref visitor, queue);
        return visitor.Reached;
    }

    private ArrayPoolListRef<StateId> GetStatesInRange(long blockStartInclusive, long blockEndInclusive)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        SortedSet<StateId> view = sortedSnapshots.GetViewBetween(
            new StateId(blockStartInclusive, Hash256.Zero),
            new StateId(blockEndInclusive, Keccak.MaxValue));

        ArrayPoolListRef<StateId> result = new(view.Count);
        foreach (StateId stateId in view) result.Add(stateId);
        return result;
    }

    // ===================== Persisted tier =====================

    /// <summary>
    /// Build a persisted snapshot from <paramref name="reservation"/> and index it into the bucket
    /// selected by <paramref name="tier"/>, returning it pre-leased (caller disposes the lease). Does
    /// NOT write the catalog — the caller records the catalog entry (a freshly persisted/compacted
    /// snapshot writes one; a snapshot reloaded from the catalog does not). The snapshot's referenced
    /// blob arena ids are read off its own metadata HSST by the <see cref="PersistedSnapshot"/> ctor,
    /// which leases each one and rolls back on partial failure.
    /// </summary>
    public PersistedSnapshot AddPersistedSnapshot(StateId from, StateId to, ArenaReservation reservation, BloomFilter bloom, SnapshotTier tier)
    {
        PersistedSnapshot snapshot = new(from, to, reservation, _blobs, bloom: bloom);
        // Index the snapshot and pre-acquire the caller's lease under the bucket's lock so a racing
        // RemovePersistedStatesUntil on a background compactor thread can't dispose it between insert
        // and the caller seeing the return.
        BucketFor(tier).Add(to, snapshot);

        // Release the caller's "creation" lease — the bucket pre-acquired its own above.
        reservation.Dispose();
        return snapshot;
    }

    /// <summary>
    /// Lease the persisted snapshot ending at <paramref name="toState"/> from the bucket(s) backing
    /// <paramref name="tier"/>. <see cref="SnapshotTier.PersistedCompacted"/> spans both the compacted
    /// and persistable buckets (it doubles as the skip-pointer edge); the other two map to a single
    /// bucket. <paramref name="tier"/> must be a <c>Persisted*</c> value. Caller disposes the lease.
    /// </summary>
    public bool TryLeasePersistedState(in StateId toState, SnapshotTier tier, [NotNullWhen(true)] out PersistedSnapshot? snapshot) => tier switch
    {
        SnapshotTier.PersistedBase => TryLeaseFrom(_base, toState, out snapshot),
        SnapshotTier.PersistedCompacted => TryLeaseFrom(_compacted, toState, out snapshot) || TryLeaseFrom(_persistable, toState, out snapshot),
        SnapshotTier.PersistedPersistable => TryLeaseFrom(_persistable, toState, out snapshot),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only persisted tiers are valid here."),
    };

    private static bool TryLeaseFrom(SnapshotBucket bucket, in StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (bucket.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>The single bucket owning a persisted-tier catalog entry. Each entry carries exactly
    /// one <c>Persisted*</c> tier, so this is a 1:1 map (unlike leasing, where the compacted edge
    /// spans two buckets).</summary>
    private SnapshotBucket BucketFor(SnapshotTier tier) => tier switch
    {
        SnapshotTier.PersistedBase => _base,
        SnapshotTier.PersistedCompacted => _compacted,
        SnapshotTier.PersistedPersistable => _persistable,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Only persisted tiers are valid here."),
    };

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
    /// Prune persisted snapshots with To.BlockNumber before the given block number. Blob arenas
    /// referenced by surviving compacted snapshots stay alive automatically via the
    /// <see cref="BlobArenaManager"/> refcount — no explicit "referenced base id"
    /// check is needed at this layer.
    /// </summary>
    public void RemovePersistedStatesUntil(long blockNumber)
    {
        _base.PruneBefore(blockNumber);
        _compacted.PruneBefore(blockNumber);
        _persistable.PruneBefore(blockNumber);
    }

    /// <summary>
    /// Enumerate persisted <c>To</c>-StateIds across all buckets whose <c>To.BlockNumber</c> is in
    /// <c>[startBlockInclusive, endBlockInclusive]</c>, deduped. Caller disposes the returned list.
    /// </summary>
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

    /// <summary>
    /// Remove the persisted snapshot(s) at exactly <paramref name="toState"/> from every bucket it
    /// appears in, releasing their leases. Returns <c>true</c> when anything was removed.
    /// </summary>
    // `|` (not `||`): every bucket must be attempted — a `To` can appear in more than one.
    public bool RemovePersistedStateExact(in StateId toState) =>
        _base.RemoveExact(toState) | _compacted.RemoveExact(toState) | _persistable.RemoveExact(toState);

    public bool HasBaseSnapshot(in StateId stateId) => _base.ContainsKey(stateId);

    public IEnumerable<PersistedSnapshot> PersistedSnapshots
    {
        get
        {
            foreach (PersistedSnapshot snap in _base.Snapshots) yield return snap;
            foreach (PersistedSnapshot snap in _compacted.Snapshots) yield return snap;
            foreach (PersistedSnapshot snap in _persistable.Snapshots) yield return snap;
        }
    }

    public void MarkPersistedTierForShutdown()
    {
        // Mark every loaded snapshot's files as shutdown-preserved before any teardown runs.
        // Snapshots already pruned during this session aren't in the buckets, so their files
        // won't get the flag and will be deleted when the arena/blob managers are disposed. This
        // pass must complete for every bucket before Dispose tears any bucket down — a file shared
        // between a base and a compacted snapshot must be flagged before either of them is disposed.
        _base.PersistAllOnShutdown();
        _compacted.PersistAllOnShutdown();
        _persistable.PersistAllOnShutdown();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Dispose snapshots (drops their reservation + blob leases) and roll back each bucket's
        // share of the global metrics. Files self-clean as their refcount hits zero; the preserve
        // flag set by MarkPersistedTierForShutdown keeps the on-disk file in place for opt-in snapshots.
        _base.DisposeAndClear();
        _compacted.DisposeAndClear();
        _persistable.DisposeAndClear();
    }

    /// <summary>
    /// One self-contained snapshot bucket for a single persisted <see cref="SnapshotTier"/>: a <c>To</c>-keyed
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
    private sealed class SnapshotBucket(SnapshotCatalog catalog, SnapshotTier tier)
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
        private ref long GlobalMemory => ref (tier == SnapshotTier.PersistedBase
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
        /// Index a snapshot: insert the dictionary entry, record its block-ordered id, and bump this
        /// bucket's + the global memory/count totals — all under this bucket's lock so the dictionary
        /// and the ordered set stay consistent against a concurrent catalog load or a racing prune.
        /// </summary>
        public void Set(in StateId to, PersistedSnapshot snapshot)
        {
            lock (_lock)
            {
                _byTo[to] = snapshot;
                _ordered.Add(to);
                Interlocked.Add(ref _memoryBytes, snapshot.Size);
                Interlocked.Increment(ref _count);
                Interlocked.Add(ref GlobalMemory, snapshot.Size);
                Interlocked.Increment(ref Metrics._persistedSnapshotCount);
            }
        }

        /// <summary>
        /// Index a snapshot (dictionary + ordered set + totals) and pre-acquire the caller's lease —
        /// both under this bucket's lock so a racing prune cannot dispose the entry between insert and
        /// the caller seeing the return. The catalog entry is written by the caller, not here.
        /// </summary>
        public void Add(in StateId to, PersistedSnapshot snapshot)
        {
            lock (_lock)
            {
                Set(to, snapshot);
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
