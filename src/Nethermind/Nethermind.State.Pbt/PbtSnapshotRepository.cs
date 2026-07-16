// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Pbt;

/// <summary>
/// Holds the in-memory diff layers keyed by their <see cref="PbtSnapshot.To"/> state, including
/// fork siblings, and assembles backward chains for bundle construction.
/// </summary>
public class PbtSnapshotRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<StateId, PbtSnapshot> _snapshots = [];
    private StateId? _lastCommittedStateId;

    public int Count
    {
        get
        {
            lock (_lock) return _snapshots.Count;
        }
    }

    public StateId? GetLastCommittedStateId()
    {
        lock (_lock) return _lastCommittedStateId;
    }

    /// <summary>Adds a sealed snapshot, taking ownership of one lease. Returns false (and releases) on duplicate.</summary>
    public bool TryAdd(PbtSnapshot snapshot)
    {
        lock (_lock)
        {
            _lastCommittedStateId = snapshot.To;
            if (_snapshots.TryAdd(snapshot.To, snapshot)) return true;
        }

        snapshot.Dispose();
        return false;
    }

    public bool HasState(in StateId stateId)
    {
        lock (_lock) return _snapshots.ContainsKey(stateId);
    }

    /// <summary>
    /// Leases the chain of snapshots from <paramref name="head"/> (inclusive) down to
    /// <paramref name="persistedFloor"/> (exclusive), oldest first. Returns false when the chain is
    /// broken, e.g. pruned concurrently; the caller should re-read the persisted floor and retry.
    /// </summary>
    /// <remarks>
    /// <paramref name="chain"/> is owned by the caller either way: on failure it is left holding the
    /// leases taken before the walk broke, which disposing it releases.
    /// </remarks>
    public bool TryLeaseChain(in StateId head, in StateId persistedFloor, PbtSnapshotPooledList chain)
    {
        lock (_lock)
        {
            StateId current = head;
            while (current != persistedFloor)
            {
                if (current == StateId.PreGenesis
                    || !_snapshots.TryGetValue(current, out PbtSnapshot? snapshot)
                    || !snapshot.TryLease())
                {
                    return false;
                }

                chain.Add(snapshot);
                current = snapshot.From;
            }

            // the walk runs head-down, so the accumulated chain is newest first
            chain.Reverse();
            return true;
        }
    }

    /// <summary>Removes and releases every snapshot at or below <paramref name="blockNumber"/> — persisted canonical layers and stale fork siblings alike.</summary>
    public void RemoveStatesUntil(ulong blockNumber)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            foreach ((StateId stateId, PbtSnapshot snapshot) in _snapshots)
            {
                if (stateId.BlockNumber <= blockNumber) removed.Add(snapshot);
            }

            foreach (PbtSnapshot snapshot in removed)
            {
                _snapshots.Remove(snapshot.To);
            }
        }

        foreach (PbtSnapshot snapshot in removed)
        {
            snapshot.Dispose();
        }
    }
}
