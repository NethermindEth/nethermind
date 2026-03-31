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

    public ImmutableArray<long> StorageMaxDepthHistogram { get; init; }

    /// <summary>Index i = count of account-trie branches with (i+1) children (1..16).</summary>
    public ImmutableArray<long> BranchOccupancyDistribution { get; init; }

    /// <summary>Balance buckets: 0 | &lt;0.01 ETH | 0.01-1 | 1-10 | 10-100 | 100-1K | 1K-10K | 10K+</summary>
    public ImmutableArray<long> BalanceDistribution { get; init; }

    /// <summary>Nonce buckets: 0 | 1 | 2-10 | 11-100 | 101-1K | 1K+</summary>
    public ImmutableArray<long> NonceDistribution { get; init; }

    /// <summary>Slots per contract: 1 | 2-10 | 11-100 | 101-1K | 1K-10K | 10K-100K | 100K+</summary>
    public ImmutableArray<long> StorageSlotDistribution { get; init; }
}
