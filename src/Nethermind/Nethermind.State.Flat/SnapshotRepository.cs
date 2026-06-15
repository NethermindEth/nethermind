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
using Timer = System.Timers.Timer;

namespace Nethermind.State.Flat;

/// <summary>
/// The single snapshot repository owning both tiers: the in-memory snapshots (base + compacted
/// dictionaries) and the persisted tier (three <see cref="SnapshotBucket"/>s over the
/// arena/blob/catalog stores). Two-tier graph walks, persistence, and compaction-assembly all
/// live here so they operate on the buckets directly.
/// </summary>
public class SnapshotRepository : ISnapshotRepository
{
    // Below this many catalog entries / bloom picks we skip the progress logger and
    // the heartbeat timer — the cost of one Parallel.ForEach over a tiny input is in
    // the µs range, well below the bookkeeping overhead the logger adds per tick.
    private const int ParallelLoadThreshold = 1024;
    // Heartbeat for the progress logger inside the parallel sections. The logger
    // itself dedups via state-change comparison, so sub-second ticks are cheap.
    private const int ProgressLogIntervalMs = 1000;

    private readonly ILogManager _logManager;
    private readonly ILogger _logger;

    // ---- Persisted tier: three buckets keyed by StateId.To, plus the arena/blob/catalog stores.
    // Each bucket is a self-contained, individually-locked store: its To-keyed ConcurrentDictionary
    // (lock-free point lookups), its block-ordered StateId set + running memory/count totals
    // (guarded by the bucket's own lock), and its share of the catalog and global metrics. A `To`
    // can live in more than one bucket (a base and a compacted snapshot can share it).
    private readonly IArenaManager _arena;
    private readonly BlobArenaManager _blobs;
    private readonly SnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly bool _validatePersistedSnapshot;
    private readonly double _bloomBitsPerKey;
    private readonly StringLabel _tierLabel = new("persisted");
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
        _catalog = new(catalogDb);
        _base = new SnapshotBucket(_catalog, SnapshotKind.Base);
        _compacted = new SnapshotBucket(_catalog, SnapshotKind.Compacted);
        _persistable = new SnapshotBucket(_catalog, SnapshotKind.Persistable);
        _compactSize = config.CompactSize;
        _validatePersistedSnapshot = config.ValidatePersistedSnapshot;
        _bloomBitsPerKey = config.PersistedSnapshotBloomBitsPerKey;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<SnapshotRepository>();
        LoadFromCatalog();
    }

    private bool BloomEnabled => _bloomBitsPerKey > 0;

    public int SnapshotCount => (int)Interlocked.Read(ref _snapshotCount);
    // Test-only observability; not part of ISnapshotRepository.
    internal int CompactedSnapshotCount => (int)Interlocked.Read(ref _compactedSnapshotCount);

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

    public AssembledSnapshotResult AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
    {
        if (baseBlock == targetState) return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        // BFS over the snapshot graph: each StateId node has up to 4 edges
        // (compacted/base × in-memory/persisted). Once on a persisted edge,
        // further in-memory edges are not explored.
        using ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited = new(estimatedSize);
        try
        {
            Queue<(StateId current, bool isPersisted, int parentIndex)> queue = new();
            HashSet<StateId> seen = [];
            queue.Enqueue((baseBlock, false, -1));
            seen.Add(baseBlock);
            int winnerIndex = -1;

            while (queue.Count > 0 && winnerIndex < 0)
            {
                (StateId current, bool currentPersisted, int parentIdx) = queue.Dequeue();

                // The cursor's in-mem-base-before-persisted-base priority matters here: a
                // persisted-base win would lock the rest of the BFS into the persisted tier
                // (via the enqueue below), barring any wider in-mem compacted skip-pointer
                // that might exist downstream.
                ParentCursor edges = EnumerateParents(current, currentPersisted, includePersisted: true);
                while (edges.TryLeaseNext(out IDisposable? snapshot, out StateId from, out bool edgePersisted))
                {
                    if (from.BlockNumber < targetState.BlockNumber)
                    {
                        // In-memory snapshots are persistence-granular; overshoot means unusable edge.
                        // Persisted (especially compacted) snapshots can span past the target — accept
                        // as the terminal element without enqueuing further.
                        if (!edgePersisted)
                        {
                            snapshot.Dispose();
                            continue;
                        }

                        if (_logger.IsTrace) _logger.Trace($"BFS terminal persisted edge: {from} -> {current} spans below target {targetState} (persisted={edgePersisted})");
                        int terminalIdx = visited.Count;
                        visited.Add((snapshot, parentIdx));
                        winnerIndex = terminalIdx;
                        break;
                    }

                    // Cycle: already visited this node
                    if (!seen.Add(from))
                    {
                        snapshot.Dispose();
                        continue;
                    }

                    if (_logger.IsTrace) _logger.Trace($"BFS edge: {from} -> {current} (persisted={edgePersisted})");

                    int idx = visited.Count;
                    visited.Add((snapshot, parentIdx));

                    if (from == targetState || from.BlockNumber == targetState.BlockNumber)
                    {
                        winnerIndex = idx;
                        break;
                    }

                    queue.Enqueue((from, edgePersisted, idx));
                }
            }

            if (winnerIndex < 0)
                return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

            // Reconstruct winning path and double-lease those snapshots so they
            // survive the finally block which disposes all visited entries.
            HashSet<int> pathIndices = [];
            int walk = winnerIndex;
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

    public SnapshotPooledList AssembleSnapshotsUntil(in StateId baseBlock, long minBlockNumber, int estimatedSize)
        => AssembleSnapshotsBfs(baseBlock, minBlockNumber, exactTarget: null, estimatedSize);

    /// <summary>
    /// BFS over the snapshot graph from <paramref name="baseBlock"/> back toward
    /// <paramref name="minBlockNumber"/>, returning the snapshots along the winning path in ascending
    /// order (<c>result[0].From</c> is the terminus, <c>result[^1].To == baseBlock</c>). Returns an
    /// empty list when no path reaches the terminus.
    /// </summary>
    /// <remarks>
    /// Each StateId node has up to 2 edges, explored widest-jump first - the in-memory compacted
    /// snapshot, then the in-memory base snapshot. Edges dropping below <paramref name="minBlockNumber"/>
    /// are pruned, so a wide compacted jump that overshoots is discarded in favour of the narrower base
    /// edge. The path wins at the first node reaching <paramref name="minBlockNumber"/>; when
    /// <paramref name="exactTarget"/> is supplied that node must also equal it (used to assemble a path
    /// to a specific state), otherwise any state at that block number qualifies (used to gather a window
    /// for compaction). `visited` owns a lease on every leased snapshot; the winning path is re-leased
    /// before the finally releases all of them.
    /// </remarks>
    private SnapshotPooledList AssembleSnapshotsBfs(in StateId baseBlock, long minBlockNumber, StateId? exactTarget, int estimatedSize)
    {
        using ArrayPoolListRef<(Snapshot Snapshot, int ParentIndex)> visited = new(estimatedSize);
        using PooledQueue<(StateId Current, int ParentIndex)> queue = new();
        using PooledSet<StateId> seen = new();
        try
        {
            queue.Enqueue((baseBlock, -1));
            seen.Add(baseBlock);
            int winnerIndex = -1;

            while (queue.Count > 0 && winnerIndex < 0)
            {
                (StateId current, int parentIndex) = queue.Dequeue();

                ParentCursor edges = EnumerateParents(current, fromPersistedEdge: false, includePersisted: false);
                while (edges.TryLeaseNext(out IDisposable? leased, out StateId from, out _))
                {
                    // In-memory-only expansion — the lease is always a Snapshot.
                    Snapshot snapshot = (Snapshot)leased;

                    if (from.BlockNumber < minBlockNumber || !seen.Add(from))
                    {
                        snapshot.Dispose();
                        continue;
                    }

                    int index = visited.Count;
                    visited.Add((snapshot, parentIndex));

                    if (from.BlockNumber == minBlockNumber && (exactTarget is not StateId target || from == target))
                    {
                        winnerIndex = index;
                        break;
                    }

                    queue.Enqueue((from, index));
                }
            }

            if (winnerIndex < 0) return SnapshotPooledList.Empty();

            // Walk winner -> root: yields ascending order directly (result[0].From == terminus,
            // result[^1].To == baseBlock).
            SnapshotPooledList result = new(estimatedSize);
            for (int walk = winnerIndex; walk >= 0; walk = visited[walk].ParentIndex)
            {
                // `visited` still holds a lease, so re-acquire cannot fail; assert flags future
                // Snapshot lifecycle changes that could break this invariant.
                bool acquired = visited[walk].Snapshot.TryAcquire();
                Debug.Assert(acquired, "TryAcquire failed despite held lease");
                result.Add(visited[walk].Snapshot);
            }
            return result;
        }
        finally
        {
            for (int i = 0; i < visited.Count; i++)
            {
                visited[i].Snapshot.Dispose();
            }
        }
    }

    /// <summary>
    /// Parent-edge kinds of the two-tier snapshot DAG. The first four values are ordered by
    /// <see cref="ParentCursor"/>'s expansion priority (in-RAM-tier-first / widest-first).
    /// </summary>
    private enum SnapshotEdge
    {
        /// <summary>In-memory compacted — widest in-RAM hop, no disk read.</summary>
        InMemoryCompacted,
        /// <summary>In-memory base — narrow in-RAM hop, no disk read.</summary>
        InMemoryBase,
        /// <summary>Persisted compacted — &gt;CompactSize merges and the CompactSize persistable.</summary>
        PersistedCompacted,
        /// <summary>Persisted base — sub-CompactSize, narrowest persisted hop.</summary>
        PersistedBase,
        /// <summary>The CompactSize-wide persistable. Never expanded by <see cref="ParentCursor"/>;
        /// only leased through explicit <see cref="TryLeaseParent"/> calls (see
        /// <see cref="FindSnapshotToPersist"/>).</summary>
        PersistedPersistable,
    }

    /// <summary>
    /// Edge seam over the two-tier snapshot DAG: given a node, leases the snapshot backing one of
    /// its parent (<c>From</c>) edges. Callers own every lease and must dispose it on all paths.
    /// </summary>
    private bool TryLeaseParent(in StateId to, SnapshotEdge edge, [NotNullWhen(true)] out IDisposable? snapshot, out StateId from)
    {
        switch (edge)
        {
            case SnapshotEdge.InMemoryCompacted:
                if (TryLeaseCompactedState(to, out Snapshot? inMemoryCompacted))
                {
                    (snapshot, from) = (inMemoryCompacted, inMemoryCompacted.From);
                    return true;
                }
                break;
            case SnapshotEdge.InMemoryBase:
                if (TryLeaseState(to, out Snapshot? inMemoryBase))
                {
                    (snapshot, from) = (inMemoryBase, inMemoryBase.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedCompacted:
                if (TryLeaseCompactedSnapshotTo(to, out PersistedSnapshot? persistedCompacted))
                {
                    (snapshot, from) = (persistedCompacted, persistedCompacted.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedBase:
                if (TryLeaseSnapshotTo(to, out PersistedSnapshot? persistedBase))
                {
                    (snapshot, from) = (persistedBase, persistedBase.From);
                    return true;
                }
                break;
            case SnapshotEdge.PersistedPersistable:
                if (TryLeasePersistableCompactedSnapshotTo(to, out PersistedSnapshot? persistable))
                {
                    (snapshot, from) = (persistable, persistable.From);
                    return true;
                }
                break;
        }

        (snapshot, from) = (null, default);
        return false;
    }

    /// <summary>
    /// Starts a priority-ordered expansion of <paramref name="to"/>'s parent edges:
    /// <see cref="SnapshotEdge.InMemoryCompacted"/>, <see cref="SnapshotEdge.InMemoryBase"/>,
    /// <see cref="SnapshotEdge.PersistedCompacted"/>, <see cref="SnapshotEdge.PersistedBase"/>.
    /// </summary>
    /// <param name="fromPersistedEdge">Whether <paramref name="to"/> was itself reached over a
    /// persisted edge. Persisted snapshots only chain back to other persisted snapshots, so the
    /// in-memory edges are guaranteed misses and are skipped.</param>
    /// <param name="includePersisted">When <see langword="false"/>, only the in-memory edges are expanded.</param>
    private ParentCursor EnumerateParents(in StateId to, bool fromPersistedEdge, bool includePersisted) =>
        new(this, to, fromPersistedEdge, includePersisted);

    private struct ParentCursor
    {
        private readonly SnapshotRepository _repo;
        private readonly StateId _to;
        private readonly SnapshotEdge _end; // Exclusive.
        private SnapshotEdge _next;

        internal ParentCursor(SnapshotRepository repo, in StateId to, bool fromPersistedEdge, bool includePersisted)
        {
            _repo = repo;
            _to = to;
            _next = fromPersistedEdge ? SnapshotEdge.PersistedCompacted : SnapshotEdge.InMemoryCompacted;
            _end = includePersisted ? SnapshotEdge.PersistedPersistable : SnapshotEdge.PersistedCompacted;
        }

        /// <summary>Leases the next available parent edge in priority order. The caller owns the lease.</summary>
        public bool TryLeaseNext([NotNullWhen(true)] out IDisposable? snapshot, out StateId from, out bool viaPersistedEdge)
        {
            while (_next < _end)
            {
                SnapshotEdge edge = _next++;
                if (_repo.TryLeaseParent(_to, edge, out snapshot, out from))
                {
                    viaPersistedEdge = edge >= SnapshotEdge.PersistedCompacted;
                    return true;
                }
            }

            (snapshot, from, viaPersistedEdge) = (null, default, false);
            return false;
        }
    }

    /// <summary>
    /// Phase 1 BFS — walks backward over the snapshot graph from <paramref name="seed"/> via
    /// <see cref="Snapshot.From"/> pointers, returning the first snapshot whose <c>From</c> equals
    /// <paramref name="currentPersistedState"/>. At each visited <c>StateId</c> the candidate
    /// sources are tried in the fixed <see cref="PersistEdgePriority"/> order:
    /// <list type="number">
    ///   <item><see cref="SnapshotEdge.PersistedPersistable"/> — the CompactSize-wide
    ///   persistable (one persist covers the whole window)</item>
    ///   <item><see cref="SnapshotEdge.PersistedBase"/> — a persisted base (fallback when the
    ///   persistable for this window has not been compacted yet)</item>
    ///   <item><see cref="SnapshotEdge.InMemoryCompacted"/> filtered to depth == <paramref name="compactSize"/> —
    ///   in-memory boundary compacted</item>
    ///   <item><see cref="SnapshotEdge.InMemoryBase"/> — in-memory base, depth == 1</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// &gt;CompactSize compacted persisted entries (<see cref="SnapshotEdge.PersistedCompacted"/>,
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
            foreach (SnapshotEdge edge in PersistEdgePriority)
            {
                if (!TryLeaseParent(current, edge, out IDisposable? snapshot, out StateId from)) continue;

                if (from == currentPersistedState && IsPersistCandidate(edge, current, from, compactSize))
                {
                    return snapshot is PersistedSnapshot persistedSnapshot
                        ? (persistedSnapshot, null)
                        : (null, (Snapshot)snapshot);
                }

                EnqueueAncestor(from, currentPersistedState, visited, queue);
                snapshot.Dispose();
            }
        }

        return (null, null);
    }

    private static readonly SnapshotEdge[] PersistEdgePriority =
    [
        SnapshotEdge.PersistedPersistable,
        SnapshotEdge.PersistedBase,
        SnapshotEdge.InMemoryCompacted,
        SnapshotEdge.InMemoryBase,
        SnapshotEdge.PersistedCompacted,
    ];

    private static bool IsPersistCandidate(SnapshotEdge edge, in StateId to, in StateId from, int compactSize) => edge switch
    {
        SnapshotEdge.PersistedCompacted => false,
        SnapshotEdge.InMemoryCompacted => to.BlockNumber - from.BlockNumber == compactSize,
        _ => true,
    };

    private static void EnqueueAncestor(in StateId from, in StateId currentPersistedState, HashSet<StateId> visited, Queue<StateId> queue)
    {
        if (from.BlockNumber > currentPersistedState.BlockNumber && visited.Add(from))
            queue.Enqueue(from);
    }

    /// <summary>
    /// Assemble persisted snapshots for compaction, walking backward from <paramref name="toStateId"/>.
    /// At each hop the widest persisted snapshot whose <c>From</c> does not span past
    /// <paramref name="minBlockNumber"/> is chosen — compacted, then the CompactSize-wide
    /// persistable, then base. Returns oldest-first, or empty if fewer than two are found.
    /// </summary>
    /// <remarks>
    /// Per-edge selection reuses <see cref="TryLeaseParent"/> (persisted edges only), so each
    /// candidate inspected is leased — overshooting ones are leased then disposed rather than
    /// peeked. That trades a little work for sharing the single edge-lease path with the other walks.
    /// </remarks>
    public PersistedSnapshotList AssembleSnapshotsForCompaction(in StateId toStateId, long minBlockNumber)
    {
        PersistedSnapshotList result = new(0);
        StateId current = toStateId;

        while (true)
        {
            PersistedSnapshot? snapshot = SelectPersistedForCompaction(current, minBlockNumber);
            if (snapshot is null) break;

            result.Add(snapshot); // already leased by TryLeaseParent

            if (snapshot.From == current) break;            // guard against a self-edge
            if (snapshot.From.BlockNumber == minBlockNumber) break;
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

    // Widest-first persisted edge whose From does not span past minBlockNumber: compacted, then
    // the CompactSize-wide persistable (the only source >CompactSize boundary compaction has),
    // then base.
    private static readonly SnapshotEdge[] CompactionEdgePriority =
    [
        SnapshotEdge.PersistedCompacted,
        SnapshotEdge.PersistedPersistable,
        SnapshotEdge.PersistedBase,
    ];

    private PersistedSnapshot? SelectPersistedForCompaction(in StateId current, long minBlockNumber)
    {
        foreach (SnapshotEdge edge in CompactionEdgePriority)
        {
            if (!TryLeaseParent(current, edge, out IDisposable? leased, out StateId from)) continue;
            PersistedSnapshot persisted = (PersistedSnapshot)leased;
            if (from.BlockNumber >= minBlockNumber) return persisted;
            persisted.Dispose(); // overshoots the window — release and try a narrower edge
        }
        return null;
    }

    public bool TryLeaseCompactedState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
    {
        SpinWait sw = new();
        while (_compactedSnapshots.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;

            sw.SpinOnce();
        }
        return false;
    }

    public bool TryLeaseState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
    {
        SpinWait sw = new();
        while (_snapshots.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;

            sw.SpinOnce();
        }
        return false;
    }

    public bool TryAddCompactedSnapshot(Snapshot snapshot)
    {
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

    public bool TryAddSnapshot(Snapshot snapshot)
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

    public bool RemoveAndReleaseCompactedKnownState(in StateId stateId)
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

    public void RemoveAndReleaseKnownState(in StateId stateId)
    {
        if (_snapshots.TryRemove(stateId, out Snapshot? existingState))
        {
            Interlocked.Decrement(ref _snapshotCount);
            Metrics.SnapshotCount--;

            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
                if (_lastRegisteredState == stateId)
                    _lastRegisteredState = sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;
            }

            long totalBytes = existingState.EstimateMemory();
            Metrics.SnapshotMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            existingState.Dispose(); // After memory
        }
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
            RemoveAndReleaseCompactedKnownState(stateToRemove);
            RemoveAndReleaseKnownState(stateToRemove);
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

        using PooledStack<(StateId State, bool IsPersisted)> stack = new();
        using PooledSet<StateId> seen = new();

        while (batchStart <= maxBlock)
        {
            long batchEnd = Math.Min(batchStart + PruneBatchSize - 1, maxBlock);

            // In-memory orphans above the persisted block.
            using (ArrayPoolListRef<StateId> inMemory = GetStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in inMemory)
                {
                    if (!CanReachState(stateId, canonicalStateId, stack, seen))
                    {
                        RemoveAndReleaseCompactedKnownState(stateId);
                        RemoveAndReleaseKnownState(stateId);
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
                    if (!CanReachState(stateId, canonicalStateId, stack, seen)
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
    private bool CanReachState(in StateId from, in StateId target, PooledStack<(StateId State, bool IsPersisted)> stack, PooledSet<StateId> seen)
    {
        if (from == target) return true;
        if (from.BlockNumber <= target.BlockNumber) return false;

        stack.Clear();
        seen.Clear();
        stack.Push((from, false));
        seen.Add(from);

        while (stack.Count > 0)
        {
            (StateId current, bool currentPersisted) = stack.Pop();

            ParentCursor edges = EnumerateParents(current, currentPersisted, includePersisted: true);
            while (edges.TryLeaseNext(out IDisposable? snapshot, out StateId parent, out bool edgePersisted))
            {
                snapshot.Dispose();

                if (parent == target) return true;
                if (parent.BlockNumber > target.BlockNumber && seen.Add(parent))
                {
                    stack.Push((parent, edgePersisted));
                }
            }
        }
        return false;
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
        // lease under the bucket's lock so a racing RemovePersistedStatesUntil can't dispose the
        // entry between insert and the caller seeing the return.
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
        // RemovePersistedStatesUntil on a background compactor thread can't dispose it between
        // insert and the caller seeing the return.
        (isPersistable ? _persistable : _compacted).Add(from, to, location, snapshot);

        // Release the caller's "creation" lease — see ConvertSnapshotToPersistedSnapshot.
        reservation.Dispose();
        return snapshot;
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
