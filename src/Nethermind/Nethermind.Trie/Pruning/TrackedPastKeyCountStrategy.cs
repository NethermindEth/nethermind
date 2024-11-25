// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public class TrackedPastKeyCountStrategy(IPruningStrategy baseStrategy, int trackedPastKeyCount) : IPruningStrategy
{
    public bool PruningEnabled => baseStrategy.PruningEnabled;
    public int MaxDepth => baseStrategy.MaxDepth;

    public bool ShouldPrune(in long currentMemory) => baseStrategy.ShouldPrune(in currentMemory);

    public int TrackedPastKeyCount => trackedPastKeyCount;
}
