// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// dictionaries) and the persisted tier (three <see cref="PersistedSnapshotBucket"/>s over the
/// arena/blob/catalog stores). Two-tier graph walks, persistence, and compaction-assembly all
/// live here so they operate on the buckets directly.
/// </summary>
public class SnapshotRepository : ISnapshotRepository, IDisposable
{
    // Canonical two-tier expansion order for the assemble/reachability walks: in-RAM-first, widest-first
    // within a tier, then persisted. The walk driver hardcodes the invariant that once an edge crosses into
    // the persisted tier the in-memory tiers are unreachable, so it filters these down to the persisted
    // suffix for any node reached over a persisted edge. PersistedPersistable is never expanded here.
    private static readonly SnapshotTier[] FullEdgePriority =
    [
        SnapshotTier.InMemoryCompacted,
        SnapshotTier.InMemoryBase,
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

    private readonly ILogger _logger;

    // ---- Persisted tier: three buckets keyed by StateId.To, plus the arena/blob/catalog stores.
    // Each bucket is a self-contained, individually-locked store: its To-keyed ConcurrentDictionary
    // (lock-free point lookups), its block-ordered StateId set + running memory/count totals
    // (guarded by the bucket's own lock), and its share of the catalog and global metrics. A `To`
    // can live in more than one bucket (a base and a compacted snapshot can share it).
    private readonly SnapshotCatalog _catalog;
    private readonly int _compactSize;
    private readonly PersistedSnapshotBucket _base;
    private readonly PersistedSnapshotBucket _compacted;
    private readonly PersistedSnapshotBucket _persistable;
    private int _disposed;

    // ---- In-memory tier. Holds only the recent unpersisted snapshots — a few hundred at most
    // (bounded by MaxInMemoryBaseSnapshotCount). Aggregates (the SnapshotCount / CompactedSnapshotCount
    // properties below, plus the static Metrics.Snapshot* gauges) are kept as running totals at the
    // TryAdd* / RemoveAndRelease* sites rather than via ConcurrentDictionary.Count.
    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private long _snapshotCount;
    private long _compactedSnapshotCount;
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new([]);
    // The last-registered tip under its own lock — read on the hot BFS-seed path, independent of the
    // ordered-set operations.
    private readonly Lock _lastRegisteredLock = new();
    private StateId? _lastRegisteredState;

    public SnapshotRepository(
        IArenaManager arenaManager,
        BlobArenaManager blobArenaManager,
        SnapshotCatalog catalog,
        IFlatDbConfig config,
        ILogManager logManager)
    {
        _catalog = catalog;
        _base = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedBase);
        _compacted = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedCompacted);
        _persistable = new PersistedSnapshotBucket(_catalog, SnapshotTier.PersistedPersistable);
        _compactSize = config.CompactSize;
        _logger = logManager.GetClassLogger<SnapshotRepository>();
    }

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
            lock (_lastRegisteredLock) return _lastRegisteredState;
        }
    }

    public void AddStateId(in StateId stateId)
    {
        using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            sortedSnapshots.Add(stateId);
        lock (_lastRegisteredLock) _lastRegisteredState = stateId;
    }

    public AssembledSnapshotResult AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
    {
        if (baseBlock == targetState) return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        AssemblePolicy policy = new(targetState);
        return WalkAndAssemble(baseBlock, estimatedSize, ref policy);
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
        InMemoryCompactionPolicy policy = new(minBlockNumber);
        AssembledSnapshotResult result = WalkAndAssemble(baseBlock, estimatedSize, ref policy);
        result.Persisted.Dispose(); // in-memory-only policy never yields persisted entries
        return result.InMemory;
    }

    private readonly struct WalkNode(in StateId current, bool viaPersisted, int parentIndex)
    {
        public readonly StateId Current = current;
        public readonly bool ViaPersisted = viaPersisted;
        public readonly int ParentIndex = parentIndex;
    }

    private enum AssembleStep { Skip, Traverse, Win, WinAndStop }

    /// <summary>
    /// Per-edge policy for <see cref="WalkAndAssemble{TPolicy}"/>: the edge-priority table to expand and a
    /// per-edge <see cref="Decide"/> verdict. The driver owns all storage, lease handling, cycle detection,
    /// winner tracking, and chain reconstruction — the policy only inspects each candidate parent edge and
    /// returns whether to skip it, traverse it, mark it the (current) winner, or mark-and-stop.
    /// </summary>
    private interface IAssemblePolicy
    {
        SnapshotTier[] EdgePriority { get; }
        AssembleStep Decide(in StateId from, SnapshotTier tier);
    }

    // Full dual-tier walk for AssembleSnapshots. The driver hardcodes the in-mem-cannot-follow-persisted
    // invariant (drops in-memory tiers once on a persisted edge), so this only filters by block: an
    // overshooting persisted snapshot is accepted as the terminal element, an overshooting in-memory edge
    // is unusable, and reaching the target's block wins.
    private readonly struct AssemblePolicy(StateId target) : IAssemblePolicy
    {
        public SnapshotTier[] EdgePriority => FullEdgePriority;

        public AssembleStep Decide(in StateId from, SnapshotTier tier)
        {
            if (from.BlockNumber < target.BlockNumber)
                return tier.IsPersisted() ? AssembleStep.WinAndStop : AssembleStep.Skip;
            return from == target || from.BlockNumber == target.BlockNumber
                ? AssembleStep.WinAndStop
                : AssembleStep.Traverse;
        }
    }

    // In-memory-only walk for AssembleInMemorySnapshotsForCompaction: widest-jump first, pruning edges
    // below minBlockNumber; wins at the first node reaching minBlockNumber.
    private readonly struct InMemoryCompactionPolicy(long minBlockNumber) : IAssemblePolicy
    {
        private static readonly SnapshotTier[] InMemoryExpansion =
            [SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase];

        public SnapshotTier[] EdgePriority => InMemoryExpansion;

        public AssembleStep Decide(in StateId from, SnapshotTier tier) =>
            from.BlockNumber < minBlockNumber ? AssembleStep.Skip
            : from.BlockNumber == minBlockNumber ? AssembleStep.WinAndStop
            : AssembleStep.Traverse;
    }

    // Best-effort persisted-only compaction walk: prunes edges overshooting minBlockNumber and marks the
    // deepest (lowest-block) node reached as the winner. Widest-first + BFS means the first path to each
    // depth is the widest; the window need not be fully populated.
    private struct PersistedCompactionPolicy(long minBlockNumber) : IAssemblePolicy
    {
        private long _winnerBlock = long.MaxValue;

        private static readonly SnapshotTier[] CompactionEdges =
            [SnapshotTier.PersistedCompacted, SnapshotTier.PersistedPersistable, SnapshotTier.PersistedBase];

        public readonly SnapshotTier[] EdgePriority => CompactionEdges;

        public AssembleStep Decide(in StateId from, SnapshotTier tier)
        {
            if (from.BlockNumber < minBlockNumber) return AssembleStep.Skip;
            if (from.BlockNumber == minBlockNumber) return AssembleStep.WinAndStop; // window start — deepest possible
            if (from.BlockNumber < _winnerBlock)
            {
                _winnerBlock = from.BlockNumber;
                return AssembleStep.Win;
            }
            return AssembleStep.Traverse;
        }
    }

    /// <summary>
    /// Backward BFS over parent (<c>From</c>) edges that gathers the winning chain directly into an
    /// <see cref="AssembledSnapshotResult"/> (in-memory + persisted lists, oldest-first). Owns the frontier
    /// queue, the visited buffer, cycle detection, winner tracking, and reconstruction. Hardcodes the
    /// invariant that once an edge crosses into the persisted tier the in-memory tiers are unreachable, so
    /// in-memory edges are skipped for any node reached over a persisted edge. The <paramref name="policy"/>
    /// only supplies the edge-priority table and a per-edge verdict.
    /// </summary>
    private AssembledSnapshotResult WalkAndAssemble<TPolicy>(in StateId start, int estimatedSize, ref TPolicy policy)
        where TPolicy : struct, IAssemblePolicy
    {
        using PooledQueue<WalkNode> queue = new();
        using PooledSet<StateId> seen = new();
        // visited owns a lease on every retained edge; GatherChain re-leases the winning path before the
        // finally releases all of them (the same ownership handoff the per-method reconstruction used).
        ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited = new(estimatedSize);
        try
        {
            int winnerIndex = -1;
            seen.Add(start);
            // The root starts in the in-memory tier; ViaPersisted flips on as the walk crosses a persisted
            // edge. A persisted-only policy simply has no in-memory tiers to expand.
            queue.Enqueue(new WalkNode(start, viaPersisted: false, -1));

            while (queue.Count > 0)
            {
                WalkNode node = queue.Dequeue();

                foreach (SnapshotTier tier in policy.EdgePriority)
                {
                    // Hardcoded invariant: a node reached over a persisted edge chains only to persisted tiers.
                    if (node.ViaPersisted && !tier.IsPersisted()) continue;

                    IDisposable snapshot;
                    StateId from;
                    if (tier.IsPersisted())
                    {
                        if (!TryLeasePersistedState(node.Current, tier, out PersistedSnapshot? persisted)) continue;
                        (snapshot, from) = (persisted, persisted.From);
                    }
                    else
                    {
                        if (!TryLeaseInMemoryState(node.Current, tier, out Snapshot? inMemory)) continue;
                        (snapshot, from) = (inMemory, inMemory.From);
                    }

                    if (!seen.Add(from)) { snapshot.Dispose(); continue; } // cycle detection

                    AssembleStep step = policy.Decide(from, tier);
                    if (step == AssembleStep.Skip) { snapshot.Dispose(); continue; }

                    int idx = visited.Count;
                    visited.Add((snapshot, node.ParentIndex));
                    if (step != AssembleStep.Traverse) winnerIndex = idx; // Win or WinAndStop
                    if (step == AssembleStep.WinAndStop) return GatherChain(visited, winnerIndex, estimatedSize);

                    queue.Enqueue(new WalkNode(from, tier.IsPersisted(), idx));
                }
            }

            return GatherChain(visited, winnerIndex, estimatedSize);
        }
        finally
        {
            for (int i = 0; i < visited.Count; i++) visited[i].snapshot.Dispose();
            visited.Dispose();
        }
    }

    /// <summary>
    /// Reconstruct the winner→root path into oldest-first in-memory + persisted lists, re-leasing each
    /// snapshot so it survives the caller's release of the visited buffer. The winner is the terminus
    /// (oldest), and the in-mem-before-persisted invariant keeps each tier contiguous, so both lists come
    /// out ascending without a reversal. Returns two empty lists when no winner was found.
    /// </summary>
    private static AssembledSnapshotResult GatherChain(
        ArrayPoolList<(IDisposable snapshot, int parentIndex)> visited, int winnerIndex, int estimatedSize)
    {
        if (winnerIndex < 0)
            return new AssembledSnapshotResult(SnapshotPooledList.Empty(), PersistedSnapshotList.Empty());

        SnapshotPooledList inMemory = new(estimatedSize);
        PersistedSnapshotList persisted = new(estimatedSize);
        for (int walk = winnerIndex; walk >= 0; walk = visited[walk].parentIndex)
        {
            switch (visited[walk].snapshot)
            {
                case PersistedSnapshot ps:
                    // visited still holds a lease, so re-acquire cannot fail.
                    bool pAcquired = ps.TryAcquire();
                    Debug.Assert(pAcquired, "TryAcquire failed despite held lease");
                    persisted.Add(ps);
                    break;
                case Snapshot s:
                    bool sAcquired = s.TryAcquire();
                    Debug.Assert(sAcquired, "TryAcquire failed despite held lease");
                    inMemory.Add(s);
                    break;
            }
        }
        return new AssembledSnapshotResult(inMemory, persisted);
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
                IDisposable snapshot;
                StateId from;
                if (tier.IsPersisted())
                {
                    if (!TryLeasePersistedState(current, tier, out PersistedSnapshot? persisted)) continue;
                    (snapshot, from) = (persisted, persisted.From);
                }
                else
                {
                    if (!TryLeaseInMemoryState(current, tier, out Snapshot? inMemory)) continue;
                    (snapshot, from) = (inMemory, inMemory.From);
                }

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

    /// <summary>
    /// Best-effort backward BFS over the persisted tier from <paramref name="toStateId"/>, returning the
    /// contiguous chain reaching the deepest block <c>&gt;= </c><paramref name="minBlockNumber"/>
    /// (oldest-first). The window need not be fully populated; returns empty when fewer than two
    /// snapshots are found.
    /// </summary>
    public PersistedSnapshotList AssemblePersistedSnapshotsForCompaction(in StateId toStateId, long minBlockNumber)
    {
        int estimatedSize = (int)Math.Clamp(toStateId.BlockNumber - minBlockNumber, 4, 4096);
        PersistedCompactionPolicy policy = new(minBlockNumber);
        AssembledSnapshotResult result = WalkAndAssemble(toStateId, estimatedSize, ref policy);
        result.InMemory.Dispose(); // persisted-only policy never yields in-memory entries

        PersistedSnapshotList persisted = result.Persisted;
        if (persisted.Count < 2)
        {
            persisted.Dispose();
            return PersistedSnapshotList.Empty();
        }
        return persisted;
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
        StateId? max;
        using (_sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots))
            max = sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;

        // Persisted-tier tips are not tracked in `_sortedSnapshotStateIds`, and after a reorg the persisted tier
        // can hold an (orphan) state at a block ABOVE the in-memory tip — so always fold the persisted
        // maxima in; callers (the flush bound and the orphan-walk bound) need the true cross-tier max.
        // (Regression: RemoveSiblingAndDescendents_PersistedOrphanAboveInMemoryTip_IsPruned.)
        max = MaxState(max, _base.Max);
        max = MaxState(max, _compacted.Max);
        max = MaxState(max, _persistable.Max);
        return max;
    }

    private static StateId? MaxState(StateId? a, StateId? b) =>
        a is null ? b : b is null ? a : a.Value.CompareTo(b.Value) >= 0 ? a : b;

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

            StateId? newMax;
            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
                newMax = sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;
            }
            // Only reset if it is still the removed tip; a racing AddStateId that advanced the tip
            // leaves _lastRegisteredState != stateId, so newMax (possibly stale) is not applied.
            lock (_lastRegisteredLock)
                if (_lastRegisteredState == stateId) _lastRegisteredState = newMax;

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

        // Bound the orphan walk by the highest block in either tier. GetLastSnapshotId folds in the
        // persisted-tier tips, so a persisted orphan above the in-memory tip — DoConvert moves a
        // converted range into the persisted tier and drops it from in-memory — is still covered.
        long maxBlock = GetLastSnapshotId()?.BlockNumber ?? long.MinValue;
        if (maxBlock <= canonicalBlock) return;

        long batchStart = canonicalBlock + 1;
        int totalPruned = 0;

        while (batchStart <= maxBlock)
        {
            long batchEnd = Math.Min(batchStart + PruneBatchSize - 1, maxBlock);

            // In-memory orphans above the persisted block.
            using (ArrayPoolListRef<StateId> inMemory = GetStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in inMemory)
                {
                    if (!CanReachState(stateId, canonicalStateId))
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
                    if (!CanReachState(stateId, canonicalStateId)
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
    /// across both tiers via the same backward walk as <see cref="AssembleSnapshots"/>. Each lease is
    /// read for its <c>From</c> then disposed immediately. Crossing into the persisted tier is required
    /// so a canonical in-memory state whose ancestry descends through a converted snapshot is not
    /// mistaken for an orphan.
    /// </remarks>
    private bool CanReachState(in StateId from, in StateId target)
    {
        if (from == target) return true;
        if (from.BlockNumber <= target.BlockNumber) return false;

        // Order-independent reachability, so a stack DFS suffices. Each lease is read for its From then
        // disposed immediately — reachability never retains a chain. Same hardcoded in-mem-cannot-follow-
        // persisted invariant as WalkAndAssemble.
        using PooledStack<WalkNode> stack = new();
        using PooledSet<StateId> seen = new();
        seen.Add(from);
        stack.Push(new WalkNode(from, viaPersisted: false, -1));

        while (stack.Count > 0)
        {
            WalkNode node = stack.Pop();
            foreach (SnapshotTier tier in FullEdgePriority)
            {
                if (node.ViaPersisted && !tier.IsPersisted()) continue;

                IDisposable snapshot;
                StateId parentFrom;
                if (tier.IsPersisted())
                {
                    if (!TryLeasePersistedState(node.Current, tier, out PersistedSnapshot? persisted)) continue;
                    (snapshot, parentFrom) = (persisted, persisted.From);
                }
                else
                {
                    if (!TryLeaseInMemoryState(node.Current, tier, out Snapshot? inMemory)) continue;
                    (snapshot, parentFrom) = (inMemory, inMemory.From);
                }

                snapshot.Dispose();

                if (parentFrom == target) return true;
                if (parentFrom.BlockNumber > target.BlockNumber && seen.Add(parentFrom))
                    stack.Push(new WalkNode(parentFrom, tier.IsPersisted(), -1));
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
    /// Index a caller-built <paramref name="snapshot"/> into the bucket selected by <paramref name="tier"/>,
    /// acquiring the bucket's own lease under the bucket's lock so a racing prune can't dispose it
    /// mid-insert. The caller retains its construction lease (and disposes it) and is responsible for the
    /// catalog entry — a freshly persisted/compacted snapshot writes one; a snapshot reloaded from the
    /// catalog does not.
    /// </summary>
    public void AddPersistedSnapshot(PersistedSnapshot snapshot, SnapshotTier tier) =>
        BucketFor(tier).Add(snapshot.To, snapshot);

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

    private static bool TryLeaseFrom(PersistedSnapshotBucket bucket, in StateId toState, [NotNullWhen(true)] out PersistedSnapshot? snapshot)
    {
        if (bucket.TryGet(toState, out snapshot) && snapshot.TryAcquire())
            return true;
        snapshot = null;
        return false;
    }

    /// <summary>The single bucket owning a persisted-tier catalog entry. Each entry carries exactly
    /// one <c>Persisted*</c> tier, so this is a 1:1 map (unlike leasing, where the compacted edge
    /// spans two buckets).</summary>
    private PersistedSnapshotBucket BucketFor(SnapshotTier tier) => tier switch
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
    private ArrayPoolList<StateId> GetPersistedStatesInRange(long startBlockInclusive, long endBlockInclusive)
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
}
