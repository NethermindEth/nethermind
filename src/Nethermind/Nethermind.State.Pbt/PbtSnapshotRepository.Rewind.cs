// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt;

public partial class PbtSnapshotRepository
{
    internal bool TryRemoveUnreachableFrom(in StateId head, in StateId persisted, out int removedCount)
    {
        removedCount = 0;
        lock (_publicationGate.Sync)
        {
            List<PbtSnapshot> memoryCandidates = [];
            List<(StateId To, long Depth, SnapshotTier Tier)> retainedCandidates = [];
            HashSet<StateId> reachable;
            lock (_lock)
            {
                if (head != persisted && !_snapshots.ContainsKey(head) && !HasRetainedBase(head)) return false;
                foreach (PbtSnapshot snapshot in _snapshots.Values) memoryCandidates.Add(snapshot);
                foreach (PbtSnapshot snapshot in _compactedSnapshots.Values) memoryCandidates.Add(snapshot);
                foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained)
                    retainedCandidates.Add((to, depth, snapshot.Tier));
                reachable = CollectRewindAncestry(head);
                // The durable base is irreversible; pruning its connecting layers would strand this head.
                if (persisted != StateId.PreGenesis && !reachable.Contains(persisted)) return false;
            }

            HashSet<StateId> removedStates = [];
            foreach (PbtSnapshot snapshot in memoryCandidates)
                if (!reachable.Contains(snapshot.To) && RemoveMemorySource(snapshot)) removedStates.Add(snapshot.To);
            foreach ((StateId to, long depth, SnapshotTier tier) in retainedCandidates)
                if (!reachable.Contains(to) && RemoveRetainedExact(to, depth, tier)) removedStates.Add(to);
            lock (_lock) _lastCommittedStateId = head;
            removedCount = removedStates.Count;
            return true;
        }
    }

    // Caller holds the repository lock so candidate capture and ancestry see the same graph.
    private HashSet<StateId> CollectRewindAncestry(in StateId head)
    {
        HashSet<StateId> reachable = [head];
        Stack<StateId> pending = new();
        pending.Push(head);
        while (pending.TryPop(out StateId state))
        {
            if (_snapshots.TryGetValue(state, out PbtSnapshot? memory) && reachable.Add(memory.From)) pending.Push(memory.From);
            if (_compactedSnapshots.TryGetValue(state, out memory) && reachable.Add(memory.From)) pending.Push(memory.From);
            foreach (SnapshotTier tier in RetainedPriority)
                if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                    foreach (PbtRetainedSnapshot snapshot in edges.Values)
                        if (reachable.Add(snapshot.From)) pending.Push(snapshot.From);
        }
        return reachable;
    }
}
