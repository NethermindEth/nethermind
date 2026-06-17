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

namespace Nethermind.State.Flat;

public class SnapshotRepository(ILogManager logManager) : ISnapshotRepository
{
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotRepository>();

    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new([]);

    // History kept below the persisted state for snap serving (early persist mode). Per-block forward
    // snapshots keyed by To, and per-chunk reverse diffs keyed by To (their older end) so the chain can
    // be walked from any chunk boundary up to the persisted state.
    private readonly ConcurrentDictionary<StateId, Snapshot> _historicalSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _reverseDiffs = new();

    public int SnapshotCount => _snapshots.Count;
    public int CompactedSnapshotCount => _compactedSnapshots.Count;

    public void AddStateId(in StateId stateId)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots);
        sortedSnapshots.Add(stateId);
    }

    public SnapshotPooledList AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
        => baseBlock == targetState
            ? SnapshotPooledList.Empty()
            : AssembleSnapshotsBfs(baseBlock, targetState.BlockNumber, targetState, estimatedSize);

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

                for (int edge = 0; edge < 2; edge++)
                {
                    Snapshot? snapshot;
                    if (edge == 0)
                    {
                        if (!TryLeaseCompactedState(current, out snapshot)) continue;
                    }
                    else
                    {
                        if (!TryLeaseState(current, out snapshot)) continue;
                    }

                    StateId from = snapshot.From;

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
        => TryLeaseFrom(_compactedSnapshots, stateId, out entry);

    public bool TryLeaseState(in StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
        => TryLeaseFrom(_snapshots, stateId, out entry);

    private static bool TryLeaseFrom(ConcurrentDictionary<StateId, Snapshot> snapshots, in StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
    {
        SpinWait sw = new();
        while (snapshots.TryGetValue(stateId, out entry))
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
            Metrics.CompactedSnapshotCount--;

            long compactedBytes = existingState.Content.EstimateCompactedMemory();
            Metrics.CompactedSnapshotMemory -= compactedBytes;
            Metrics.TotalSnapshotMemory -= compactedBytes;

            existingState.Dispose();

            return true;
        }

        return false;
    }

    public void RemoveAndReleaseKnownState(StateId stateId)
    {
        if (_snapshots.TryRemove(stateId, out Snapshot? existingState))
        {
            Metrics.SnapshotCount--;

            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
            }

            long totalBytes = existingState.EstimateMemory();
            Metrics.SnapshotMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            existingState.Dispose(); // After memory
        }
    }

    public bool HasState(in StateId stateId) => _snapshots.ContainsKey(stateId);

    public ArrayPoolList<StateId> GetSnapshotBeforeStateId(StateId stateId)
    {
        if (stateId.BlockNumber < 0)
            return ArrayPoolList<StateId>.Empty();

        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        return sortedSnapshots
            .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(stateId.BlockNumber, Keccak.MaxValue))
            .ToPooledList(0);
    }

    public void RemoveStatesUntil(in StateId currentPersistedStateId)
    {
        using ArrayPoolList<StateId> statesBeforeStateId = GetSnapshotBeforeStateId(currentPersistedStateId);
        foreach (StateId stateToRemove in statesBeforeStateId)
        {
            RemoveAndReleaseCompactedKnownState(stateToRemove);
            RemoveAndReleaseKnownState(stateToRemove);
        }
    }

    public bool TryAddReverseDiff(Snapshot reverseDiff)
    {
        if (_reverseDiffs.TryAdd(reverseDiff.To, reverseDiff))
        {
            Metrics.ReverseDiffCount++;

            // Unlike compacted snapshots, reverse diff values are not shared with other snapshots.
            long totalBytes = reverseDiff.EstimateMemory();
            Metrics.ReverseDiffMemory += totalBytes;
            Metrics.TotalSnapshotMemory += totalBytes;

            return true;
        }

        return false;
    }

    public bool HasHistoricalState(in StateId stateId) => _historicalSnapshots.ContainsKey(stateId);

    public void ArchiveStatesUntil(in StateId persistedStateId)
    {
        // Move the canonical per-block chain below the persisted state into the historical set. The
        // persisted state's own snapshot is released instead: that state is fully readable from
        // persistence and its chunk is covered by the reverse diff. Compacted snapshots are released
        // outright; historical reads only ever need per-block granularity above a chunk boundary.
        StateId current = persistedStateId;
        bool isPersistedState = true;
        while (_snapshots.TryGetValue(current, out Snapshot? snapshot))
        {
            StateId from = snapshot.From;

            RemoveAndReleaseCompactedKnownState(current);
            if (isPersistedState)
            {
                RemoveAndReleaseKnownState(current);
            }
            else
            {
                MoveToHistorical(current);
            }

            isPersistedState = false;
            current = from;
        }

        // Sweep non-canonical leftovers (orphaned forks at or below the persisted block).
        using ArrayPoolList<StateId> statesBeforeStateId = GetSnapshotBeforeStateId(persistedStateId);
        foreach (StateId stateToRemove in statesBeforeStateId)
        {
            RemoveAndReleaseCompactedKnownState(stateToRemove);
            RemoveAndReleaseKnownState(stateToRemove);
        }
    }

    /// <summary>
    /// Assembles the snapshot stack for a historical state <paramref name="baseBlock"/> below the
    /// persisted state: per-block forward snapshots down to the nearest chunk boundary, then reverse
    /// diffs up to <paramref name="persistedState"/>.
    /// </summary>
    /// <remarks>
    /// Returned list is in ascending read priority like <see cref="AssembleSnapshots"/>: reverse diffs
    /// first (topmost last), then forwards ending at <paramref name="baseBlock"/>, so the newest-first
    /// read loop checks forwards, then reverse diffs from the boundary upward, then persistence.
    /// Returns an empty list when the chain is broken (pruned concurrently or outside the serving
    /// window); the caller retries or fails.
    /// </remarks>
    public SnapshotPooledList AssembleHistoricalSnapshots(in StateId baseBlock, in StateId persistedState, int estimatedSize)
    {
        using ArrayPoolListRef<Snapshot> forwards = new(estimatedSize);
        using ArrayPoolListRef<Snapshot> reverses = new(4);
        bool success = false;
        try
        {
            // Per-block forwards downward from baseBlock until a state with a reverse diff (chunk boundary).
            StateId current = baseBlock;
            Snapshot? reverse;
            while (!TryLeaseFrom(_reverseDiffs, current, out reverse))
            {
                if (!TryLeaseFrom(_historicalSnapshots, current, out Snapshot? snapshot)) return SnapshotPooledList.Empty();

                forwards.Add(snapshot);
                current = snapshot.From;
            }

            // Reverse diffs upward; each diff's From is the chunk boundary above it.
            while (true)
            {
                reverses.Add(reverse);
                if (reverse.From == persistedState) break;
                if (!TryLeaseFrom(_reverseDiffs, reverse.From, out reverse)) return SnapshotPooledList.Empty();
            }

            SnapshotPooledList result = new(forwards.Count + reverses.Count);
            for (int i = reverses.Count - 1; i >= 0; i--) result.Add(reverses[i]);
            for (int i = forwards.Count - 1; i >= 0; i--) result.Add(forwards[i]);
            success = true;
            return result;
        }
        finally
        {
            if (!success)
            {
                foreach (Snapshot snapshot in forwards) snapshot.Dispose();
                foreach (Snapshot snapshot in reverses) snapshot.Dispose();
            }
        }
    }

    public void PruneHistory(long oldestServedBlockNumber, in StateId persistedState)
    {
        // Walk the reverse chain down from the persisted state; the first diff reaching at or below
        // oldestServedBlockNumber is the keep-boundary (chunk granularity keeps historical reads able to
        // reach every state in the serving window). Everything below it or off-chain is released.
        StateId keepBoundary = persistedState;
        using PooledSet<StateId> keptDiffs = new();
        while (TryFindReverseDiffFrom(keepBoundary, out StateId diffTo))
        {
            keptDiffs.Add(diffTo);
            keepBoundary = diffTo;
            if (diffTo.BlockNumber <= oldestServedBlockNumber) break;
        }

        foreach (KeyValuePair<StateId, Snapshot> kv in _reverseDiffs)
        {
            if (!keptDiffs.Contains(kv.Key)) RemoveAndReleaseReverseDiff(kv.Key);
        }

        foreach (KeyValuePair<StateId, Snapshot> kv in _historicalSnapshots)
        {
            if (kv.Key.BlockNumber <= keepBoundary.BlockNumber) RemoveAndReleaseHistoricalState(kv.Key);
        }
    }

    public void ClearHistory()
    {
        foreach (KeyValuePair<StateId, Snapshot> kv in _reverseDiffs) RemoveAndReleaseReverseDiff(kv.Key);
        foreach (KeyValuePair<StateId, Snapshot> kv in _historicalSnapshots) RemoveAndReleaseHistoricalState(kv.Key);
    }

    private bool TryFindReverseDiffFrom(in StateId from, out StateId diffTo)
    {
        foreach (KeyValuePair<StateId, Snapshot> kv in _reverseDiffs)
        {
            if (kv.Value.From == from)
            {
                diffTo = kv.Key;
                return true;
            }
        }

        diffTo = default;
        return false;
    }

    private void MoveToHistorical(in StateId stateId)
    {
        if (_snapshots.TryRemove(stateId, out Snapshot? snapshot))
        {
            Metrics.SnapshotCount--;

            using (_sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
            }

            // TotalSnapshotMemory is unchanged; the snapshot is still alive, just re-homed.
            long totalBytes = snapshot.EstimateMemory();
            Metrics.SnapshotMemory -= totalBytes;
            Metrics.HistoricalSnapshotCount++;
            Metrics.HistoricalSnapshotMemory += totalBytes;

            _historicalSnapshots[stateId] = snapshot;
        }
    }

    private void RemoveAndReleaseHistoricalState(in StateId stateId)
    {
        if (_historicalSnapshots.TryRemove(stateId, out Snapshot? snapshot))
        {
            Metrics.HistoricalSnapshotCount--;

            long totalBytes = snapshot.EstimateMemory();
            Metrics.HistoricalSnapshotMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            snapshot.Dispose();
        }
    }

    private void RemoveAndReleaseReverseDiff(in StateId stateId)
    {
        if (_reverseDiffs.TryRemove(stateId, out Snapshot? reverseDiff))
        {
            Metrics.ReverseDiffCount--;

            long totalBytes = reverseDiff.EstimateMemory();
            Metrics.ReverseDiffMemory -= totalBytes;
            Metrics.TotalSnapshotMemory -= totalBytes;

            reverseDiff.Dispose();
        }
    }

    private const int PruneBatchSize = 1000;

    public void RemoveSiblingAndDescendents(in StateId canonicalStateId)
    {
        // Fast-fail when the persisted block has no sibling state: nothing above it can be orphaned.
        if (!HasForkAt(canonicalStateId.BlockNumber)) return;

        StateId? lastStateId = GetLastSnapshotId();
        if (lastStateId is null || lastStateId.Value.BlockNumber <= canonicalStateId.BlockNumber) return;

        long maxBlock = lastStateId.Value.BlockNumber;
        long batchStart = canonicalStateId.BlockNumber + 1;
        int totalPruned = 0;

        using PooledStack<StateId> stack = new();
        using PooledSet<StateId> seen = new();

        while (batchStart <= maxBlock)
        {
            long batchEnd = Math.Min(batchStart + PruneBatchSize - 1, maxBlock);
            using ArrayPoolListRef<StateId> batch = GetStatesInRange(batchStart, batchEnd);
            foreach (StateId stateId in batch)
            {
                if (!CanReachState(stateId, canonicalStateId, stack, seen))
                {
                    RemoveAndReleaseCompactedKnownState(stateId);
                    RemoveAndReleaseKnownState(stateId);
                    totalPruned++;
                }
            }
            batchStart = batchEnd + 1;
        }

        if (totalPruned > 0 && _logger.IsInfo)
        {
            _logger.Info($"Pruned {totalPruned} orphaned non-canonical snapshot(s) above persisted state {canonicalStateId}.");
        }
    }

    private bool CanReachState(in StateId from, in StateId target, PooledStack<StateId> stack, PooledSet<StateId> seen)
    {
        if (from == target) return true;
        if (from.BlockNumber <= target.BlockNumber) return false;

        stack.Clear();
        seen.Clear();
        stack.Push(from);
        seen.Add(from);

        while (stack.Count > 0)
        {
            StateId current = stack.Pop();

            for (int edge = 0; edge < 2; edge++)
            {
                Snapshot? snapshot;
                if (edge == 0)
                {
                    if (!TryLeaseCompactedState(current, out snapshot)) continue;
                }
                else
                {
                    if (!TryLeaseState(current, out snapshot)) continue;
                }

                StateId parent = snapshot.From;
                snapshot.Dispose();

                if (parent == target) return true;
                if (parent.BlockNumber > target.BlockNumber && seen.Add(parent))
                {
                    stack.Push(parent);
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
