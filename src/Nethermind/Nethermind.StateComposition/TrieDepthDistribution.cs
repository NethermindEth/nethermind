// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;

namespace Nethermind.StateComposition;

public readonly record struct TrieDepthDistribution()
{
    public ImmutableArray<TrieLevelStat> AccountTrieLevels { get; init; } = ImmutableArray<TrieLevelStat>.Empty;
    public ImmutableArray<TrieLevelStat> StorageTrieLevels { get; init; } = ImmutableArray<TrieLevelStat>.Empty;
    public double AvgAccountPathDepth { get; init; }
    public double AvgStoragePathDepth { get; init; }
    public int MaxAccountDepth { get; init; }
    public int MaxStorageDepth { get; init; }
    public double AvgBranchOccupancy { get; init; }

    public ImmutableArray<long> StorageMaxDepthHistogram { get; init; } = ImmutableArray<long>.Empty;

    /// <summary>Index i = count of account-trie branches with (i+1) children (1..16).</summary>
    public ImmutableArray<long> BranchOccupancyDistribution { get; init; } = ImmutableArray<long>.Empty;
}
