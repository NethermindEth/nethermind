// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class TrackedPastKeyCountStrategy : IPruningStrategy
{
    private IPruningStrategy _baseStrategy;
    private readonly int _trackedPastKeyCount;
    public bool PruningEnabled => _baseStrategy.PruningEnabled;

    public TrackedPastKeyCountStrategy(IPruningStrategy baseStrategy, int trackedPastKeyCount)
    {
        _baseStrategy = baseStrategy;
        _trackedPastKeyCount = trackedPastKeyCount;
    }

    public bool ShouldPrune(in long currentMemory)
    {
        return _baseStrategy.ShouldPrune(in currentMemory);
    }

    public int TrackedPastKeyCount => _trackedPastKeyCount;
}
