// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedSnapshots = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _snapshots = new();
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new(new SortedSet<StateId>());

    public int SnapshotCount => _snapshots.Count;
    public int CompactedSnapshotCount => _compactedSnapshots.Count;

    public void AddStateId(in StateId stateId)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots);
        sortedSnapshots.Add(stateId);
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
            HashSet<StateId> seen = new();
            queue.Enqueue((baseBlock, false, -1));
            seen.Add(baseBlock);
            int winnerIndex = -1;

            while (queue.Count > 0 && winnerIndex < 0)
            {
                (StateId current, bool currentPersisted, int parentIdx) = queue.Dequeue();

                // Expand up to 4 edges from `current` (compacted/base × in-memory/persisted).
                // When already on a persisted path, skip in-memory edges (offset by 2).
                int edgeStart = currentPersisted ? 2 : 0;
                for (int e = edgeStart; e < 4; e++)
                {
                    IDisposable? snapshot;
                    StateId from;

                    switch (e)
                    {
                        case 0: // in-memory compacted
                            if (!TryLeaseCompactedState(current, out Snapshot? sc)) continue;
                            snapshot = sc; from = sc.From;
                            break;
                        case 1: // in-memory base
                            if (!TryLeaseState(current, out Snapshot? sb)) continue;
                            snapshot = sb; from = sb.From;
                            break;
                        case 2: // persisted compacted
                            if (!persistedSnapshotRepository.TryLeaseCompactedSnapshotTo(current, out PersistedSnapshot? pc)) continue;
                            snapshot = pc; from = pc.From;
                            break;
                        case 3: // persisted base
                            if (!persistedSnapshotRepository.TryLeaseSnapshotTo(current, out PersistedSnapshot? pb)) continue;
                            snapshot = pb; from = pb.From;
                            break;
                        default: continue;
                    }

                    // Overshoot: snapshot jumps past target
                    if (from.BlockNumber < targetState.BlockNumber)
                    {
                        snapshot.Dispose();
                        continue;
                    }

                    // Cycle: already visited this node
                    if (!seen.Add(from))
                    {
                        snapshot.Dispose();
                        continue;
                    }

                    bool edgePersisted = snapshot is PersistedSnapshot;
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
            HashSet<int> pathIndices = new();
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
    {
        SnapshotPooledList snapshots = new(estimatedSize);

        StateId current = baseBlock;
        while (TryLeaseCompactedState(current, out Snapshot? snapshot) || TryLeaseState(current, out snapshot))
        {
            if (_logger.IsTrace) _logger.Trace($"Got {snapshot.From} -> {snapshot.To}");

            if (snapshot.From.BlockNumber < minBlockNumber)
            {
                // `snapshot` is now a compacted snapshot, we dont want to use it.
                snapshot.Dispose();

                // Try got get a non compacted one
                if (!TryLeaseState(current, out snapshot))
                {
                    // Failure, exit loop.
                    break;
                }
            }

            if (snapshot.From.BlockNumber < minBlockNumber)
            {
                // Should not happen... unless someone try to add out of order snapshots
                snapshot.Dispose();
                break;
            }

            snapshots.Add(snapshot);
            if (snapshot.From == current)
            {
                break; // Some test commit two block with the same id, so we dont know the parent anymore.
            }

            if (snapshot.From.BlockNumber == minBlockNumber)
            {
                break;
            }

            current = snapshot.From;
        }

        snapshots.Reverse();
        return snapshots;
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

    public StateId? GetLastSnapshotId()
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);
        return sortedSnapshots.Count == 0 ? null : sortedSnapshots.Max;
    }

    public StateId? GetEarliestSnapshotId()
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        if (sortedSnapshots.Count == 0)
            return null;
        return sortedSnapshots.Min;
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

    public void RemoveAndReleaseKnownState(in StateId stateId)
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

    public bool HasState(in StateId stateId)
    {
        if (_snapshots.ContainsKey(stateId)) return true;
        if (persistedSnapshotRepository.HasBaseSnapshot(stateId)) return true;
        return false;
    }

    public ArrayPoolList<StateId> GetSnapshotBeforeStateId(long blockNumber)
    {
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
}
