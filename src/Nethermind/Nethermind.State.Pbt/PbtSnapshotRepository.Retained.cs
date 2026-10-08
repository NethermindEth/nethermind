// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt;

public partial class PbtSnapshotRepository
{
    private static readonly SnapshotTier[] ReadPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] PersistPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.InMemoryCompacted, SnapshotTier.PersistedSmallCompacted, SnapshotTier.InMemoryBase, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] RetainedPriority = [SnapshotTier.PersistedLargeCompacted, SnapshotTier.PersistedCompactSized, SnapshotTier.PersistedSmallCompacted, SnapshotTier.PersistedBase];
    private static readonly SnapshotTier[] MemoryPriority = [SnapshotTier.InMemoryCompacted, SnapshotTier.InMemoryBase];
    private readonly Dictionary<(StateId To, SnapshotTier Tier), SortedDictionary<long, PbtRetainedSnapshot>> _retainedEdges = [];
    private readonly SortedSet<StateId> _retainedStateIds = new(Comparer<StateId>.Create(CompareStates));
    private readonly Dictionary<StateId, int> _retainedStateCounts = [];
    private enum Step { Skip, Traverse, Win, Stop }

    internal bool TryAddRetained(PbtRetainedSnapshot snapshot)
    {
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                (StateId, long) key = (snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber)));
                if (_retained.ContainsKey(key)) return false;
                if (!snapshot.TryLease()) return false;
                _retained.Add(key, snapshot);
                if (!_retainedEdges.TryGetValue((snapshot.To, snapshot.Tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                    _retainedEdges.Add((snapshot.To, snapshot.Tier), edges = []);
                edges.Add(key.Item2, snapshot);
                _retainedStateCounts.TryGetValue(snapshot.To, out int count);
                _retainedStateCounts[snapshot.To] = count + 1;
                _retainedStateIds.Add(snapshot.To);
                return true;
            }
    }

    internal bool ContainsMemorySource(PbtSnapshot snapshot)
    {
        lock (_lock) return _snapshots.TryGetValue(snapshot.To, out PbtSnapshot? value) && ReferenceEquals(snapshot, value)
            || _compactedSnapshots.TryGetValue(snapshot.To, out value) && ReferenceEquals(snapshot, value);
    }

    internal bool ContainsRetainedSource(PbtRetainedSnapshot snapshot)
    {
        lock (_lock) return _retained.TryGetValue((snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber))), out PbtRetainedSnapshot? value) && ReferenceEquals(value, snapshot);
    }

    internal bool TryLeaseRetained(in StateId to, long depth, SnapshotTier tier, out PbtRetainedSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_retained.TryGetValue((to, depth), out snapshot) && snapshot.Tier == tier && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    internal bool TryLeaseMemoryState(in StateId state, SnapshotTier tier, out PbtSnapshot? snapshot)
    {
        tier.EnsureInMemory();
        lock (_lock)
        {
            Dictionary<StateId, PbtSnapshot> source = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
            if (source.TryGetValue(state, out snapshot) && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    internal bool TryLeaseRetainedCatalogKey(in StateId to, long depth, out PbtRetainedSnapshot? snapshot)
    {
        lock (_lock)
        {
            if (_retained.TryGetValue((to, depth), out snapshot) && snapshot.TryLease()) return true;
            snapshot = null;
            return false;
        }
    }

    internal (StateId To, long Depth, SnapshotTier Tier)[] GetRetainedEntries()
    {
        lock (_lock)
        {
            List<(StateId, long, SnapshotTier)> result = [];
            foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained) result.Add((to, depth, snapshot.Tier));
            return result.ToArray();
        }
    }

    private bool HasRetainedBase(in StateId state) => _retainedEdges.ContainsKey((state, SnapshotTier.PersistedBase));

    internal PbtSnapshotChain? TryLeaseReadChain(in StateId target, in StateId readerFloor)
    {
        if (target == readerFloor) return new([]);
        StateId floor = readerFloor;
        return Walk(target, ReadPriority, (edge) => Height(edge.From) < Height(floor)
            ? edge.Tier.IsPersisted() ? Step.Stop : Step.Skip
            : edge.From == floor ? Step.Stop : edge.From.BlockNumber == floor.BlockNumber ? Step.Skip : Step.Traverse);
    }

    internal PbtSnapshotChain? TryLeaseRetainedChain(in StateId target, ulong minimumBlockNumber)
    {
        long best = long.MaxValue;
        return Walk(target, RetainedPriority, edge =>
        {
            if (Height(edge.From) < (long)minimumBlockNumber) return Step.Skip;
            if (edge.From.BlockNumber == minimumBlockNumber) return Step.Stop;
            if (Height(edge.From) < best) { best = Height(edge.From); return Step.Win; }
            return Step.Traverse;
        });
    }

    internal PbtSnapshotLease? FindCandidateToPersist(in StateId seed, in StateId baseState, ulong compactSize,
        Func<StateId, bool> acceptsFinalizedRoot, bool memoryOnly = false)
    {
        StateId floor = baseState;
        using PbtSnapshotChain? chain = Walk(seed, memoryOnly ? MemoryPriority : PersistPriority, edge =>
            edge.From == floor ? unchecked(edge.To.BlockNumber - edge.From.BlockNumber) <= compactSize && acceptsFinalizedRoot(edge.To) ? Step.Stop : Step.Skip
            : Height(edge.From) > Height(floor) ? Step.Traverse : Step.Skip);
        return chain is { Layers.Count: > 0 } ? chain.Layers[0].Lease() : null;
    }

    private PbtSnapshotChain? Walk(in StateId start, SnapshotTier[] priority, Func<PbtSnapshotLease, Step> decide)
    {
        List<(PbtSnapshotLease Edge, int Parent)> visited = [];
        Queue<(StateId State, bool Retained, int Parent)> queue = new();
        HashSet<StateId> seen = [start];
        queue.Enqueue((start, false, -1));
        int winner = -1;
        try
        {
            while (queue.TryDequeue(out (StateId State, bool Retained, int Parent) node))
            {
                foreach (SnapshotTier tier in priority)
                {
                    if (node.Retained && !tier.IsPersisted()) continue;
                    PbtSnapshotLease? edge = LeaseEdge(node.State, tier);
                    if (edge is null) continue;
                    Step step;
                    try { step = decide(edge); }
                    catch { edge.Dispose(); throw; }
                    if (step == Step.Skip || !seen.Add(edge.From)) { edge.Dispose(); continue; }
                    int index = visited.Count;
                    visited.Add((edge, node.Parent));
                    if (step is Step.Win or Step.Stop) winner = index;
                    if (step == Step.Stop) return Gather(visited, winner);
                    queue.Enqueue((edge.From, tier.IsPersisted(), index));
                }
            }
            return winner < 0 ? null : Gather(visited, winner);
        }
        finally { foreach ((PbtSnapshotLease edge, _) in visited) edge.Dispose(); }
    }

    private PbtSnapshotLease? LeaseEdge(in StateId state, SnapshotTier tier)
    {
        lock (_lock)
        {
            if (!tier.IsPersisted())
            {
                Dictionary<StateId, PbtSnapshot> source = tier == SnapshotTier.InMemoryBase ? _snapshots : _compactedSnapshots;
                return source.TryGetValue(state, out PbtSnapshot? snapshot) && snapshot.TryLease() ? new(snapshot, tier) : null;
            }
            if (!_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges)) return null;
            PbtRetainedSnapshot? widest = null;
            foreach (PbtRetainedSnapshot snapshot in edges.Values) widest = snapshot;
            return widest is not null && widest.TryLease() ? new(widest) : null;
        }
    }

    private static PbtSnapshotChain Gather(List<(PbtSnapshotLease Edge, int Parent)> visited, int winner)
    {
        List<PbtSnapshotLease> layers = [];
        try
        {
            for (int i = winner; i >= 0; i = visited[i].Parent) layers.Add(visited[i].Edge.Lease());
            return new(layers);
        }
        catch { foreach (PbtSnapshotLease layer in layers) layer.Dispose(); throw; }
    }

    internal bool RemoveRetainedExact(in StateId to, long depth, SnapshotTier tier)
    {
        PbtRetainedSnapshot? removed;
        lock (_publicationGate.Sync)
        {
            lock (_lock)
                if (!_retained.TryGetValue((to, depth), out removed) || removed.Tier != tier) return false;
            _catalog.Remove(to, depth);
            lock (_lock)
            {
                _retained.Remove((to, depth));
                SortedDictionary<long, PbtRetainedSnapshot> edges = _retainedEdges[(to, tier)];
                edges.Remove(depth);
                if (edges.Count == 0) _retainedEdges.Remove((to, tier));
                int count = _retainedStateCounts[to] - 1;
                if (count == 0)
                {
                    _retainedStateCounts.Remove(to);
                    _retainedStateIds.Remove(to);
                }
                else _retainedStateCounts[to] = count;
            }
        }
        removed.Dispose();
        return true;
    }

    internal void RemoveRetainedStatesBefore(ulong blockNumber)
    {
        List<(StateId To, long Depth, SnapshotTier Tier)> states = [];
        lock (_lock)
            foreach (((StateId to, long depth), PbtRetainedSnapshot snapshot) in _retained)
                if (Height(to) < (long)blockNumber) states.Add((to, depth, snapshot.Tier));
        foreach ((StateId to, long depth, SnapshotTier tier) in states) RemoveRetainedExact(to, depth, tier);
    }

    internal bool RemoveMemorySource(PbtSnapshot snapshot)
    {
        bool removed = false;
        lock (_lock)
        {
            if (_snapshots.TryGetValue(snapshot.To, out PbtSnapshot? current) && ReferenceEquals(snapshot, current))
            {
                _snapshots.Remove(snapshot.To);
                Metrics.AddPbtBaseSnapshotCount(-1);
                if (_recordDetailedMetrics) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                removed = true;
            }
            else if (_compactedSnapshots.TryGetValue(snapshot.To, out current) && ReferenceEquals(snapshot, current))
                removed = _compactedSnapshots.Remove(snapshot.To);
            if (removed) _memoryBytes -= Bytes(snapshot);
        }
        if (removed) snapshot.Dispose();
        return removed;
    }

    internal bool RemoveRetainedSource(PbtRetainedSnapshot snapshot)
    {
        lock (_publicationGate.Sync)
        {
            if (!ContainsRetainedSource(snapshot)) return false;
            return RemoveRetainedExact(snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber)), snapshot.Tier);
        }
    }

    internal void RemoveMemoryState(in StateId state)
    {
        List<PbtSnapshot> removed = [];
        lock (_lock)
        {
            if (_snapshots.Remove(state, out PbtSnapshot? snapshot))
            {
                Metrics.AddPbtBaseSnapshotCount(-1);
                if (_recordDetailedMetrics) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                removed.Add(snapshot);
            }
            if (_compactedSnapshots.Remove(state, out snapshot)) removed.Add(snapshot);
            foreach (PbtSnapshot layer in removed) _memoryBytes -= Bytes(layer);
        }
        foreach (PbtSnapshot snapshot in removed) snapshot.Dispose();
    }

    internal StateId[] GetInMemoryStates()
    {
        lock (_lock)
        {
            StateId[] states = [.. _snapshots.Keys];
            Array.Sort(states, CompareStates);
            return states;
        }
    }

    internal StateId[] GetRetainedStates()
    {
        lock (_lock)
        {
            HashSet<StateId> unique = [];
            foreach (PbtRetainedSnapshot snapshot in _retained.Values) unique.Add(snapshot.To);
            StateId[] states = [.. unique];
            Array.Sort(states, CompareStates);
            return states;
        }
    }

    private static int CompareStates(StateId left, StateId right)
    {
        int height = Height(left).CompareTo(Height(right));
        return height != 0 ? height : left.StateRoot.Bytes.SequenceCompareTo(right.StateRoot.Bytes);
    }

    internal StateId? GetLastSnapshotId()
    {
        lock (_lock)
        {
            StateId? winner = null;
            foreach (StateId state in _snapshots.Keys) Consider(state);
            foreach (StateId state in _compactedSnapshots.Keys) Consider(state);
            foreach (PbtRetainedSnapshot snapshot in _retained.Values) Consider(snapshot.To);
            return winner;
            void Consider(StateId state)
            {
                if (winner is null || CompareStates(state, winner.Value) > 0) winner = state;
            }
        }
    }

    internal bool IsOnDisk(in StateId state, in StateId baseState)
    {
        lock (_lock) return state == baseState || HasRetainedBase(state);
    }

    internal void SetLastCommittedStateId(in StateId state) { lock (_lock) _lastCommittedStateId = state; }

    internal void MarkPersistedTierForShutdown()
    {
        lock (_lock) foreach (PbtRetainedSnapshot snapshot in _retained.Values) snapshot.PersistOnShutdown();
    }

    public void Dispose()
    {
        List<PbtSnapshot> memory;
        List<PbtRetainedSnapshot> retained;
        lock (_publicationGate.Sync)
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                memory = [.. _snapshots.Values, .. _compactedSnapshots.Values];
                retained = [.. _retained.Values];
                Metrics.AddPbtBaseSnapshotCount(-_snapshots.Count);
                if (_recordDetailedMetrics) foreach (PbtSnapshot snapshot in _snapshots.Values) Metrics.AddPbtBaseSnapshotMemory(snapshot.PayloadSize, -1);
                _snapshots.Clear(); _compactedSnapshots.Clear(); _retained.Clear(); _retainedEdges.Clear(); _retainedStateIds.Clear(); _retainedStateCounts.Clear(); _memoryBytes = 0;
            }
        foreach (PbtSnapshot snapshot in memory) snapshot.Dispose();
        foreach (PbtRetainedSnapshot snapshot in retained) snapshot.Dispose();
    }
}
