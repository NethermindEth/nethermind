// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Collections.Pooled;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat;

public class SnapshotRepository(IPersistedSnapshotRepository persistedSnapshotRepository, ILogManager logManager) : ISnapshotRepository
{
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotRepository>();
    private readonly IPersistedSnapshotRepository _persisted = persistedSnapshotRepository;

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

    public int SnapshotCount => (int)Interlocked.Read(ref _snapshotCount);
    public int CompactedSnapshotCount => (int)Interlocked.Read(ref _compactedSnapshotCount);

    private SnapshotGraphWalker Walker => new(this, _persisted);

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
                SnapshotGraphWalker.ParentCursor edges = Walker.EnumerateParents(current, currentPersisted, includePersisted: true);
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

                SnapshotGraphWalker.ParentCursor edges = Walker.EnumerateParents(current, fromPersistedEdge: false, includePersisted: false);
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
        if (_persisted.HasBaseSnapshot(stateId)) return true;
        return false;
    }

    public ArrayPoolList<StateId> GetSnapshotBeforeStateId(long blockNumber)
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
        using ArrayPoolList<StateId> statesBeforeStateId = GetSnapshotBeforeStateId(blockNumber);
        foreach (StateId stateToRemove in statesBeforeStateId)
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

        long maxBlock = Math.Max(
            GetLastSnapshotId()?.BlockNumber ?? long.MinValue,
            _persisted.LastRegisteredState?.BlockNumber ?? long.MinValue);
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
            using (ArrayPoolList<StateId> persisted = _persisted.GetPersistedStatesInRange(batchStart, batchEnd))
            {
                foreach (StateId stateId in persisted)
                {
                    if (!CanReachState(stateId, canonicalStateId, stack, seen)
                        && _persisted.RemovePersistedStateExact(stateId))
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
            _persisted.GetPersistedStatesInRange(canonicalStateId.BlockNumber, canonicalStateId.BlockNumber);
        foreach (StateId stateId in atBlock)
            if (stateId != canonicalStateId) return true;
        return false;
    }

    /// <remarks>
    /// Walks parent (<c>From</c>) edges from <paramref name="from"/> toward <paramref name="target"/>
    /// across both tiers via the same <see cref="SnapshotGraphWalker.ParentCursor"/> expansion as
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

            SnapshotGraphWalker.ParentCursor edges = Walker.EnumerateParents(current, currentPersisted, includePersisted: true);
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
}
