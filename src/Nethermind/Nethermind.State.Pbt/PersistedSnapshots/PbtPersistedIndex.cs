// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.State.Flat;

namespace Nethermind.State.Pbt.PersistedSnapshots;

/// <summary>The persisted snapshots, keyed by catalog identity — the state and tier they end at — and the set of states they end at.</summary>
/// <remarks>Holds one lease on every snapshot it indexes. Not thread-safe: the repository guards it with its lock.</remarks>
public sealed class PbtPersistedIndex(IComparer<StateId> stateOrder)
{
    private readonly Dictionary<(StateId To, SnapshotTier Tier), PbtPersistedSnapshot> _byCatalogKey = [];
    private readonly SortedSet<StateId> _states = new(stateOrder);
    private readonly Dictionary<StateId, int> _snapshotsPerState = [];

    public int Count => _byCatalogKey.Count;

    public Dictionary<(StateId To, SnapshotTier Tier), PbtPersistedSnapshot>.ValueCollection Snapshots => _byCatalogKey.Values;

    public bool TryGet(in StateId to, SnapshotTier tier, [NotNullWhen(true)] out PbtPersistedSnapshot? snapshot) =>
        _byCatalogKey.TryGetValue((to, tier), out snapshot);

    /// <summary>Whether <paramref name="snapshot"/> itself, not only one with its catalog key, is indexed.</summary>
    public bool Contains(PbtPersistedSnapshot snapshot) =>
        TryGet(snapshot.To, snapshot.Tier, out PbtPersistedSnapshot? current) && ReferenceEquals(current, snapshot);

    public bool Contains(in StateId to, SnapshotTier tier) => _byCatalogKey.ContainsKey((to, tier));

    /// <summary>The states some snapshot ends at, from <paramref name="first"/> to <paramref name="last"/> inclusive.</summary>
    public SortedSet<StateId> StatesBetween(in StateId first, in StateId last) => _states.GetViewBetween(first, last);

    /// <summary>Indexes and leases <paramref name="snapshot"/>, displacing the snapshot at its catalog key, unless it is already indexed or cannot be leased.</summary>
    /// <param name="displaced">The snapshot it displaced, whose lease passes to the caller.</param>
    public bool TryAdd(PbtPersistedSnapshot snapshot, out PbtPersistedSnapshot? displaced)
    {
        displaced = null;
        if (Contains(snapshot) || !snapshot.TryLease()) return false;
        (StateId, SnapshotTier) key = (snapshot.To, snapshot.Tier);
        if (!_byCatalogKey.TryGetValue(key, out displaced))
        {
            _snapshotsPerState.TryGetValue(snapshot.To, out int count);
            _snapshotsPerState[snapshot.To] = count + 1;
            _states.Add(snapshot.To);
        }
        _byCatalogKey[key] = snapshot;
        return true;
    }

    /// <summary>Unindexes the snapshot with this catalog key; its lease passes to whoever obtained it via <see cref="TryGet"/>.</summary>
    public void Remove(in StateId to, SnapshotTier tier)
    {
        if (!_byCatalogKey.Remove((to, tier))) return;
        int count = _snapshotsPerState[to] - 1;
        if (count == 0)
        {
            _snapshotsPerState.Remove(to);
            _states.Remove(to);
        }
        else _snapshotsPerState[to] = count;
    }

    /// <summary>Swaps indexed <paramref name="expected"/> for <paramref name="replacement"/>, which has the same catalog key, leasing it.</summary>
    /// <returns>False when <paramref name="expected"/> is not indexed or <paramref name="replacement"/> cannot be leased; otherwise the lease on <paramref name="expected"/> passes to the caller.</returns>
    public bool TryReplace(PbtPersistedSnapshot expected, PbtPersistedSnapshot replacement)
    {
        if (!Contains(expected) || !replacement.TryLease()) return false;
        _byCatalogKey[(expected.To, expected.Tier)] = replacement;
        return true;
    }

    /// <summary>Unindexes every snapshot.</summary>
    /// <returns>The snapshots, whose leases pass to the caller.</returns>
    public List<PbtPersistedSnapshot> Clear()
    {
        List<PbtPersistedSnapshot> snapshots = [.. _byCatalogKey.Values];
        _byCatalogKey.Clear();
        _states.Clear();
        _snapshotsPerState.Clear();
        return snapshots;
    }
}
