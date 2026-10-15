// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.PersistedSnapshots;

/// <summary>The persisted snapshots, keyed by catalog identity, by the state and tier they end at, and the set of states they end at.</summary>
/// <remarks>Holds one lease on every snapshot it indexes. Not thread-safe: the repository guards it with its lock.</remarks>
public sealed class PbtPersistedIndex(IComparer<StateId> stateOrder)
{
    private readonly Dictionary<(StateId To, long Depth), PbtPersistedSnapshot> _byCatalogKey = [];
    private readonly Dictionary<(StateId To, SnapshotTier Tier), SortedDictionary<long, PbtPersistedSnapshot>> _edges = [];
    private readonly SortedSet<StateId> _states = new(stateOrder);
    private readonly Dictionary<StateId, int> _snapshotsPerState = [];

    public int Count => _byCatalogKey.Count;

    public Dictionary<(StateId To, long Depth), PbtPersistedSnapshot>.ValueCollection Snapshots => _byCatalogKey.Values;

    public bool TryGet(in StateId to, long depth, [NotNullWhen(true)] out PbtPersistedSnapshot? snapshot) =>
        _byCatalogKey.TryGetValue((to, depth), out snapshot);

    /// <summary>Whether <paramref name="snapshot"/> itself, not only one with its catalog key, is indexed.</summary>
    public bool Contains(PbtPersistedSnapshot snapshot) =>
        TryGet(snapshot.To, snapshot.Depth, out PbtPersistedSnapshot? current) && ReferenceEquals(current, snapshot);

    /// <summary>The snapshots of <paramref name="tier"/> ending at <paramref name="to"/>, by ascending depth.</summary>
    public bool TryGetEdges(in StateId to, SnapshotTier tier, [NotNullWhen(true)] out SortedDictionary<long, PbtPersistedSnapshot>? edges) =>
        _edges.TryGetValue((to, tier), out edges);

    public bool HasEdges(in StateId to, SnapshotTier tier) => _edges.ContainsKey((to, tier));

    /// <summary>The states some snapshot ends at, from <paramref name="first"/> to <paramref name="last"/> inclusive.</summary>
    public SortedSet<StateId> StatesBetween(in StateId first, in StateId last) => _states.GetViewBetween(first, last);

    /// <summary>Indexes and leases <paramref name="snapshot"/> unless its catalog key is taken or it cannot be leased.</summary>
    public bool TryAdd(PbtPersistedSnapshot snapshot)
    {
        (StateId, long) key = (snapshot.To, snapshot.Depth);
        if (_byCatalogKey.ContainsKey(key) || !snapshot.TryLease()) return false;
        _byCatalogKey.Add(key, snapshot);
        if (!_edges.TryGetValue((snapshot.To, snapshot.Tier), out SortedDictionary<long, PbtPersistedSnapshot>? edges))
            _edges.Add((snapshot.To, snapshot.Tier), edges = []);
        edges.Add(snapshot.Depth, snapshot);
        _snapshotsPerState.TryGetValue(snapshot.To, out int count);
        _snapshotsPerState[snapshot.To] = count + 1;
        _states.Add(snapshot.To);
        return true;
    }

    /// <summary>Unindexes the snapshot with this catalog key; its lease passes to whoever obtained it via <see cref="TryGet"/>.</summary>
    public void Remove(in StateId to, long depth)
    {
        if (!_byCatalogKey.Remove((to, depth), out PbtPersistedSnapshot? removed)) return;
        SortedDictionary<long, PbtPersistedSnapshot> edges = _edges[(to, removed.Tier)];
        edges.Remove(depth);
        if (edges.Count == 0) _edges.Remove((to, removed.Tier));
        int count = _snapshotsPerState[to] - 1;
        if (count == 0)
        {
            _snapshotsPerState.Remove(to);
            _states.Remove(to);
        }
        else _snapshotsPerState[to] = count;
    }

    /// <summary>Swaps indexed <paramref name="expected"/> for <paramref name="replacement"/>, which has the same catalog key and tier, leasing it.</summary>
    /// <returns>False when <paramref name="expected"/> is not indexed or <paramref name="replacement"/> cannot be leased; otherwise the lease on <paramref name="expected"/> passes to the caller.</returns>
    public bool TryReplace(PbtPersistedSnapshot expected, PbtPersistedSnapshot replacement)
    {
        if (!Contains(expected) || !replacement.TryLease()) return false;
        _byCatalogKey[(expected.To, expected.Depth)] = replacement;
        _edges[(expected.To, expected.Tier)][expected.Depth] = replacement;
        return true;
    }

    /// <summary>Unindexes every snapshot.</summary>
    /// <returns>The snapshots, whose leases pass to the caller.</returns>
    public List<PbtPersistedSnapshot> Clear()
    {
        List<PbtPersistedSnapshot> snapshots = [.. _byCatalogKey.Values];
        _byCatalogKey.Clear();
        _edges.Clear();
        _states.Clear();
        _snapshotsPerState.Clear();
        return snapshots;
    }
}
