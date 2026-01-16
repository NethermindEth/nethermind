// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using Prometheus;

namespace Nethermind.State.Flat;

public class SnapshotRepository(ILogManager logManager) : ISnapshotRepository
{
    private const int MaxLeaseAttempt = 10_000;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapshotRepository>();

    private readonly ConcurrentDictionary<StateId, Snapshot> _compactedKnownStates = new();
    private readonly ConcurrentDictionary<StateId, Snapshot> _knownStates = new();
    private readonly ReadWriteLockBox<SortedSet<StateId>> _sortedKnownStates = new(new SortedSet<StateId>());

    private static Gauge _knownStatesMemory = DevMetric.Factory.CreateGauge("flatdiff_knownstates_memory", "memory", "category");
    private static Gauge _compactedMemory = DevMetric.Factory.CreateGauge("flatdiff_compacted_memory", "memory", "category");

    public int SnapshotCount => _knownStates.Count;
    public int CompactedSnapshotCount => _compactedKnownStates.Count;

    public void AddStateId(StateId stateId)
    {
        using (var _ = _sortedKnownStates.EnterWriteLock(out SortedSet<StateId> sortedSnapshots)) sortedSnapshots.Add(stateId);
    }

    public SnapshotPooledList AssembleSnapshotsUntil(StateId stateId, long startingBlockNumber, int estimatedSize)
    {
        SnapshotPooledList snapshots = new(estimatedSize);
        StateId current = stateId;
        while(TryLeaseCompactedState(current, out Snapshot? snapshot) || TryLeaseState(current, out snapshot))
        {
            if (_logger.IsTrace) _logger.Trace($"Got {snapshot.From} -> {snapshot.To}");

            if (snapshot.From.blockNumber < startingBlockNumber)
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

            snapshots.Add(snapshot);
            if (snapshot.From == current) {
                break; // Some test commit two block with the same id, so we dont know the parent anymore.
            }

            current = snapshot.From;
            if (snapshot.From.blockNumber == startingBlockNumber)
            {
                break;
            }
        }

        snapshots.Reverse();
        return snapshots;
    }

    public bool TryLeaseCompactedState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
    {
        int attempt = 0;
        SpinWait sw = new SpinWait();
        while (_compactedKnownStates.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;
            attempt++;
            sw.SpinOnce();
            if (attempt > MaxLeaseAttempt) throw new Exception($"Unable to acquire lease on compacted state {stateId}");
        }
        return false;
    }

    public bool TryLeaseState(StateId stateId, [NotNullWhen(true)] out Snapshot? entry)
    {
        int attempt = 0;
        SpinWait sw = new SpinWait();
        while (_knownStates.TryGetValue(stateId, out entry))
        {
            if (entry.TryAcquire()) return true;
            attempt++;
            sw.SpinOnce();
            if (attempt > MaxLeaseAttempt) throw new Exception($"Unable to acquire lease on state {stateId}");
        }
        return false;
    }

    public bool TryAddCompactedSnapshot(Snapshot snapshot)
    {
        if (_compactedKnownStates.TryAdd(snapshot.To, snapshot))
        {
            foreach (var keyValuePair in snapshot.EstimateMemory())
            {
                _compactedMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
            }

            _compactedMemory.WithLabels("count").Inc(1);

            return true;
        }

        return false;
    }

    public bool TryAddSnapshot(Snapshot snapshot)
    {
        if (_knownStates.TryAdd(snapshot.To, snapshot))
        {
            var memory = snapshot.EstimateMemory(); // Note: This is slow, do it outside.
            foreach (var keyValuePair in memory)
            {
                _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Inc(keyValuePair.Value);
            }
            _knownStatesMemory.WithLabels("count").Inc(1);

            return true;
        }

        return false;
    }

    internal ArrayPoolList<StateId> GetStatesAfterBlock(long blockNumber)
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new StateId(blockNumber + 1, ValueKeccak.Zero);
        StateId max = new StateId(long.MaxValue, ValueKeccak.Zero);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    public ArrayPoolList<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        StateId min = new StateId(blockNumber, ValueKeccak.Zero);
        StateId max = new StateId(blockNumber, ValueKeccak.MaxValue);

        return sortedSnapshots.GetViewBetween(min, max).ToPooledList(0);
    }

    public StateId? GetLastSnapshotId()
    {
        using var _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapshots);

        if (sortedSnapshots.Count == 0)
            return null;
        return sortedSnapshots.Max;
    }

    public bool RemoveAndReleaseCompactedKnownState(StateId stateId)
    {
        if (_compactedKnownStates.TryRemove(stateId, out var existingState))
        {
            var memory = existingState.EstimateMemory();
            foreach (var keyValuePair in memory)
            {
                _compactedMemory.WithLabels(keyValuePair.Key.ToString()).Dec(keyValuePair.Value);
            }
            _compactedMemory.WithLabels("count").Dec(1);

            existingState.Dispose();

            return true;
        }

        return false;
    }

    public void RemoveAndReleaseKnownState(StateId stateId)
    {
        if (_knownStates.TryRemove(stateId, out var existingState))
        {
            using (var _ = _sortedKnownStates.EnterWriteLock(out SortedSet<StateId> sortedSnapshots))
            {
                sortedSnapshots.Remove(stateId);
            }

            var memory = existingState.EstimateMemory();
            foreach (var keyValuePair in memory)
            {
                _knownStatesMemory.WithLabels(keyValuePair.Key.ToString()).Dec(keyValuePair.Value);
            }
            _knownStatesMemory.WithLabels("count").Dec();

            existingState.Dispose(); // After memory
        }
    }

    public bool HasState(StateId stateId) => _knownStates.ContainsKey(stateId);

    public ArrayPoolList<StateId> GetSnapshotBeforeStateId(StateId stateId)
    {
        using ReadWriteLockBox<SortedSet<StateId>>.LockExitor _ = _sortedKnownStates.EnterReadLock(out SortedSet<StateId> sortedSnapashots);

        return sortedSnapashots
            .GetViewBetween(new StateId(0, Hash256.Zero), new StateId(stateId.blockNumber, Keccak.MaxValue))
            .ToPooledList(0);
    }

    public void RemoveStatesUntil(StateId currentPersistedStateId)
    {
        using ArrayPoolList<StateId> statesBeforeStateId = GetSnapshotBeforeStateId(currentPersistedStateId);
        foreach (var stateToRemove in statesBeforeStateId)
        {
            RemoveAndReleaseCompactedKnownState(stateToRemove);
            RemoveAndReleaseKnownState(stateToRemove);
        }
    }
}
