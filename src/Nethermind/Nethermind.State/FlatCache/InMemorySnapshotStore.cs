// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.State.FlatCache;

public class InMemorySnapshotStore
{
    private Dictionary<StateId, Snapshot> _knownStates = new();
    private SortedSet<StateId> _sortedKnownStates = new();

    internal int KnownStatesCount => _knownStates.Count;

    public bool TryGetValue(StateId current, out Snapshot value)
    {
        return _knownStates.TryGetValue(current, out value);
    }

    public void AddBlock(StateId endBlock, Snapshot snapshot)
    {
        _knownStates[endBlock] = snapshot;
        _sortedKnownStates.Add(endBlock);
    }

    public (StateId, Snapshot) GetFirst()
    {
        var firstKey = _sortedKnownStates.First();
        var snapshot = _knownStates[firstKey];
        return (firstKey, snapshot);
    }

    public void Remove(StateId firstKey)
    {
        _knownStates.Remove(firstKey);
        _sortedKnownStates.Remove(firstKey);
    }

    public List<StateId> GetKeysBetween(StateId start, StateId end)
    {
        return _sortedKnownStates.GetViewBetween(start, end).ToList();
    }
}
