// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat;

namespace Nethermind.State.Flat;

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

    public List<StateId> GetStatesAtBlockNumber(long blockNumber)
    {
        StateId min = new StateId(blockNumber, ValueKeccak.Zero);
        StateId max = new StateId(blockNumber, ValueKeccak.MaxValue);

        return GetKeysBetween(min, max).ToList();
    }

    public IEnumerable<StateId> GetStatesAfterBlock(long startingBlockNumber)
    {
        return GetKeysBetween(
            new StateId(startingBlockNumber + 1, ValueKeccak.Zero),
            new StateId(long.MaxValue, ValueKeccak.Zero)
        );
    }

    public StateId? GetLast()
    {
        if (_sortedKnownStates.Count == 0)
            return null;
        return _sortedKnownStates.Last();
    }
}
