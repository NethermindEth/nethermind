// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

namespace Nethermind.StateComposition;

public readonly record struct TrieDepthDistribution
{
    public ImmutableArray<TrieLevelStat> AccountTrieLevels { get; init; }
    public ImmutableArray<TrieLevelStat> StorageTrieLevels { get; init; }
    public double AvgAccountPathDepth { get; init; }
    public double AvgStoragePathDepth { get; init; }
    public int MaxAccountDepth { get; init; }
    public int MaxStorageDepth { get; init; }
    public double AvgBranchOccupancy { get; init; }

    // Geth parity: histogram of storage trie max depths
    public ImmutableArray<long> StorageMaxDepthHistogram { get; init; }
}
