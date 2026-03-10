// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedSnapshotStateIds = new(new SortedSet<StateId>());

    public int SnapshotCount => _snapshots.Count;
    public int CompactedSnapshotCount => _compactedSnapshots.Count;

    public void AddStateId(in StateId stateId)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.Lock _ = _sortedSnapshotStateIds.EnterWriteLock(out SortedSet<StateId> sortedSnapshots);
        sortedSnapshots.Add(stateId);
    }

    public SnapshotPooledList AssembleSnapshots(in StateId baseBlock, in StateId targetState, int estimatedSize)
    {
        SnapshotPooledList list = AssembleSnapshotsUntil(baseBlock, targetState.BlockNumber, estimatedSize);
        if (list.Count > 0 && list[0].From.BlockNumber == targetState.BlockNumber && list[0].From != targetState)
        {
            list.Dispose();

            // Likely persisted a non-finalized block.
            throw new InvalidOperationException($"Attempted to compile snapshots from {baseBlock} to {targetState} but target is not reachable from baseBlock");
        }

        return list;
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
}
