// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.State.Pbt;

/// <summary>
/// Holds the in-memory diff layers keyed by their <see cref="PbtSnapshot.To"/> state, including
/// fork siblings, and assembles backward chains for bundle construction.
/// </summary>
/// <remarks>
/// Two tiers, both keyed by <see cref="PbtSnapshot.To"/>: the base layer a block committed, and the
/// wider layer compaction merged onto the same state. They coexist rather than replace one another —
/// a compacted layer is a shortcut across the base ones, not a substitute — so a walk aiming past it
/// can still step through the narrow layers underneath.
/// </remarks>
public class PbtSnapshotRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<StateId, PbtSnapshot> _snapshots = [];
    private readonly Dictionary<StateId, PbtSnapshot> _compactedSnapshots = [];
    private StateId? _lastCommittedStateId;

    public int Count
    {
        get
        {
            lock (_lock) return _snapshots.Count;
        }
    }

    public int CompactedCount
    {
        get
        {
            lock (_lock) return _compactedSnapshots.Count;
        }
    }

    public StateId? GetLastCommittedStateId()
    {
        lock (_lock) return _lastCommittedStateId;
    }

    /// <summary>Adds a sealed base layer, taking ownership of one lease. Returns false (and releases) on duplicate.</summary>
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

    /// <summary>Publishes a compacted layer alongside the base layer at the same state, taking ownership of one lease.</summary>
    /// <remarks>Returns false (and releases) when that state already has one — two compactions raced.</remarks>
    public bool TryAddCompacted(PbtSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_compactedSnapshots.TryAdd(snapshot.To, snapshot)) return true;
        }

        snapshot.Dispose();
        return false;
    }

    public bool HasState(in StateId stateId)
    {
        lock (_lock) return _snapshots.ContainsKey(stateId);
    }

    /// <summary>
    /// Leases the chain of layers from <paramref name="head"/> (inclusive) down to
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
            long floorHeight = Height(persistedFloor);
            StateId current = head;
            while (current != persistedFloor)
            {
                if (current == StateId.PreGenesis || !TryTakeWidestEdge(current, floorHeight, chain, out current)) return false;
            }

            // the walk runs head-down, so the accumulated chain is newest first
            chain.Reverse();
            return true;
        }
    }

    /// <summary>
    /// Leases the layers covering <paramref name="head"/> back to <paramref name="minBlockNumber"/>,
    /// oldest first, for a compaction window. Returns false when the window cannot be assembled.
    /// </summary>
    /// <remarks>
    /// The floor is a height rather than a state, because a window is defined by the schedule's block
    /// alignment and not by what happens to be persisted. It may sit below genesis, which is why it is
    /// signed: an early wide window simply cannot be assembled rather than wrapping into a huge one.
    /// </remarks>
    public bool TryLeaseCompactionWindow(in StateId head, long minBlockNumber, PbtSnapshotPooledList chain)
    {
        lock (_lock)
        {
            StateId current = head;
            while (Height(current) > minBlockNumber)
            {
                if (!TryTakeWidestEdge(current, minBlockNumber, chain, out current)) return false;
            }

            // landing past the floor means the window does not start on a layer boundary
            if (Height(current) != minBlockNumber) return false;

            chain.Reverse();
            return true;
        }
    }

    /// <summary>Removes and releases every layer, of either tier, at or below <paramref name="blockNumber"/> — persisted canonical layers and stale fork siblings alike.</summary>
    public void RemoveStatesUntil(ulong blockNumber)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            Collect(_snapshots, removed, static (id, floor) => id.BlockNumber <= floor, blockNumber);
            Collect(_compactedSnapshots, removed, static (id, floor) => id.BlockNumber <= floor, blockNumber);
        }

        foreach (PbtSnapshot snapshot in removed)
        {
            snapshot.Dispose();
        }
    }

    /// <summary>Removes and releases the compacted layers at exactly <paramref name="blockNumber"/>, leaving the base tier alone.</summary>
    /// <remarks>
    /// A compacted layer is superseded once a wider one spans across it: it costs memory while no walk
    /// would ever prefer it again. The base layers stay, because a walk aiming between the wide
    /// boundaries still steps through them.
    /// </remarks>
    public void RemoveCompactedAt(ulong blockNumber)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            Collect(_compactedSnapshots, removed, static (id, at) => id.BlockNumber == at, blockNumber);
        }

        foreach (PbtSnapshot snapshot in removed)
        {
            snapshot.Dispose();
        }
    }

    private static void Collect(Dictionary<StateId, PbtSnapshot> tier, List<PbtSnapshot> removed, Func<StateId, ulong, bool> matches, ulong blockNumber)
    {
        int first = removed.Count;
        foreach ((StateId stateId, PbtSnapshot snapshot) in tier)
        {
            if (matches(stateId, blockNumber)) removed.Add(snapshot);
        }

        for (int i = first; i < removed.Count; i++)
        {
            tier.Remove(removed[i].To);
        }
    }

    /// <summary>Takes the widest edge out of <paramref name="current"/> that does not overshoot the floor.</summary>
    /// <remarks>
    /// Widest first is the whole of the promotion: a compacted layer is preferred wherever one spans
    /// far enough, so a wide window naturally consumes the narrower merges below it and a read walks
    /// fewer, wider layers. Falling back to the base edge is what keeps a walk aiming between two wide
    /// boundaries correct. No backtracking is needed — both edges lie on the same branch, and the base
    /// layers under a compacted one are never pruned before it.
    /// </remarks>
    private bool TryTakeWidestEdge(in StateId current, long floorHeight, PbtSnapshotPooledList chain, out StateId next)
    {
        if (_compactedSnapshots.TryGetValue(current, out PbtSnapshot? wide) && Height(wide.From) >= floorHeight && wide.TryLease())
        {
            chain.Add(wide);
            next = wide.From;
            return true;
        }

        if (_snapshots.TryGetValue(current, out PbtSnapshot? narrow) && narrow.TryLease())
        {
            chain.Add(narrow);
            next = narrow.From;
            return true;
        }

        next = default;
        return false;
    }

    /// <summary>The state's height as a signed number, so the slot before genesis orders below block 0.</summary>
    /// <remarks><see cref="StateId.PreGenesis"/> reserves the top of the unsigned range, which reinterprets to -1.</remarks>
    private static long Height(in StateId stateId) => (long)stateId.BlockNumber;
}
