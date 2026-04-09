// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Verifies that <see cref="Metrics.UpdateFromDistribution"/> correctly populates
/// all 149 depth-distribution gauges and resets stale values when called twice
/// with different data.
/// </summary>
[TestFixture]
public class MetricsDepthGaugesTests
{
    [SetUp]
    public void ResetMetrics() => MetricsDepthGaugesHelper.ResetAllDepthGauges();

    [Test]
    public void UpdateFromDistribution_PopulatesAccountTrieDepthGauges()
    {
        TrieDepthDistribution dist = new()
        {
            AccountTrieLevels = ImmutableArray.Create(
                new TrieLevelStat { Depth = 3, FullNodeCount = 5, ShortNodeCount = 2, ValueNodeCount = 0, TotalSize = 100 },
                new TrieLevelStat { Depth = 7, FullNodeCount = 0, ShortNodeCount = 10, ValueNodeCount = 3, TotalSize = 200 }
            ),
            StorageTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            BranchOccupancyDistribution = ImmutableArray.Create<long>(0, 0, 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            AvgAccountPathDepth = 3.5,
            AvgStoragePathDepth = 0.0,
            MaxAccountDepth = 8,
            MaxStorageDepth = 0,
            AvgBranchOccupancy = 4.2,
        };

        Metrics.UpdateFromDistribution(dist);

        using (Assert.EnterMultipleScope())
        {
            // Depth 3 account
            Assert.That(Metrics.StateCompAccountTrieDepth_3_FullNodes,  Is.EqualTo(5));
            Assert.That(Metrics.StateCompAccountTrieDepth_3_ShortNodes, Is.EqualTo(2));
            Assert.That(Metrics.StateCompAccountTrieDepth_3_ValueNodes, Is.EqualTo(0));
            Assert.That(Metrics.StateCompAccountTrieDepth_3_Bytes,      Is.EqualTo(100));

            // Depth 7 account
            Assert.That(Metrics.StateCompAccountTrieDepth_7_FullNodes,  Is.EqualTo(0));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_ShortNodes, Is.EqualTo(10));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_ValueNodes, Is.EqualTo(3));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_Bytes,      Is.EqualTo(200));

            // An unrelated depth must remain 0
            Assert.That(Metrics.StateCompAccountTrieDepth_5_FullNodes, Is.EqualTo(0));
            Assert.That(Metrics.StateCompAccountTrieDepth_5_Bytes,     Is.EqualTo(0));

            // Scalars
            Assert.That(Metrics.StateCompAvgAccountPathDepth, Is.EqualTo(3.5));
            Assert.That(Metrics.StateCompMaxAccountDepth,     Is.EqualTo(8));
            Assert.That(Metrics.StateCompAvgBranchOccupancy,  Is.EqualTo(4.2));

            // Branch occupancy bucket 4 (index 3 = 4 children)
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_4_Children, Is.EqualTo(42));
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_5_Children, Is.EqualTo(0));
        }
    }

    [Test]
    public void UpdateFromDistribution_PopulatesStorageTrieDepthGauges()
    {
        TrieDepthDistribution dist = new()
        {
            AccountTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            StorageTrieLevels = ImmutableArray.Create(
                new TrieLevelStat { Depth = 2, FullNodeCount = 7, ShortNodeCount = 3, ValueNodeCount = 1, TotalSize = 300 }
            ),
            BranchOccupancyDistribution = ImmutableArray.Create(new long[16]),
            AvgAccountPathDepth = 0.0,
            AvgStoragePathDepth = 2.1,
            MaxAccountDepth = 0,
            MaxStorageDepth = 3,
            AvgBranchOccupancy = 0.0,
        };

        Metrics.UpdateFromDistribution(dist);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompStorageTrieDepth_2_FullNodes,  Is.EqualTo(7));
            Assert.That(Metrics.StateCompStorageTrieDepth_2_ShortNodes, Is.EqualTo(3));
            Assert.That(Metrics.StateCompStorageTrieDepth_2_ValueNodes, Is.EqualTo(1));
            Assert.That(Metrics.StateCompStorageTrieDepth_2_Bytes,      Is.EqualTo(300));

            // Other storage depths remain 0
            Assert.That(Metrics.StateCompStorageTrieDepth_0_FullNodes, Is.EqualTo(0));
            Assert.That(Metrics.StateCompStorageTrieDepth_5_FullNodes, Is.EqualTo(0));

            Assert.That(Metrics.StateCompAvgStoragePathDepth, Is.EqualTo(2.1));
            Assert.That(Metrics.StateCompMaxStorageDepth,     Is.EqualTo(3));
        }
    }

    /// <summary>
    /// Calling UpdateFromDistribution twice must not leak values from the first call.
    /// Any depth that was populated in call 1 but absent in call 2 must revert to 0.
    /// </summary>
    [Test]
    public void UpdateFromDistribution_SecondCall_ClearsStalePreviousValues()
    {
        // First call: depth 3 has data
        TrieDepthDistribution first = new()
        {
            AccountTrieLevels = ImmutableArray.Create(
                new TrieLevelStat { Depth = 3, FullNodeCount = 99, ShortNodeCount = 11, ValueNodeCount = 5, TotalSize = 999 }
            ),
            StorageTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            BranchOccupancyDistribution = ImmutableArray.Create(new long[16]),
            AvgAccountPathDepth = 3.0,
            AvgStoragePathDepth = 0.0,
            MaxAccountDepth = 4,
            MaxStorageDepth = 0,
            AvgBranchOccupancy = 0.0,
        };

        // Second call: only depth 7 has data; depth 3 is absent
        TrieDepthDistribution second = new()
        {
            AccountTrieLevels = ImmutableArray.Create(
                new TrieLevelStat { Depth = 7, FullNodeCount = 1, ShortNodeCount = 2, ValueNodeCount = 3, TotalSize = 50 }
            ),
            StorageTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            BranchOccupancyDistribution = ImmutableArray.Create(new long[16]),
            AvgAccountPathDepth = 7.0,
            AvgStoragePathDepth = 0.0,
            MaxAccountDepth = 8,
            MaxStorageDepth = 0,
            AvgBranchOccupancy = 0.0,
        };

        Metrics.UpdateFromDistribution(first);

        // Confirm first call set depth 3
        Assert.That(Metrics.StateCompAccountTrieDepth_3_FullNodes, Is.EqualTo(99));

        Metrics.UpdateFromDistribution(second);

        using (Assert.EnterMultipleScope())
        {
            // Depth 3 must be wiped — it's absent in the second distribution
            Assert.That(Metrics.StateCompAccountTrieDepth_3_FullNodes,  Is.EqualTo(0), "stale depth-3 full nodes must be zeroed");
            Assert.That(Metrics.StateCompAccountTrieDepth_3_ShortNodes, Is.EqualTo(0), "stale depth-3 short nodes must be zeroed");
            Assert.That(Metrics.StateCompAccountTrieDepth_3_Bytes,      Is.EqualTo(0), "stale depth-3 bytes must be zeroed");

            // Depth 7 must now carry the second call's values
            Assert.That(Metrics.StateCompAccountTrieDepth_7_FullNodes,  Is.EqualTo(1));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_ShortNodes, Is.EqualTo(2));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_ValueNodes, Is.EqualTo(3));
            Assert.That(Metrics.StateCompAccountTrieDepth_7_Bytes,      Is.EqualTo(50));

            Assert.That(Metrics.StateCompAvgAccountPathDepth, Is.EqualTo(7.0));
            Assert.That(Metrics.StateCompMaxAccountDepth,     Is.EqualTo(8));
        }
    }

    [Test]
    public void UpdateFromDistribution_BranchOccupancyAllBuckets()
    {
        // Populate all 16 occupancy buckets with distinct non-zero values
        long[] occupancy = new long[16];
        for (int i = 0; i < 16; i++) occupancy[i] = (i + 1) * 10L;

        TrieDepthDistribution dist = new()
        {
            AccountTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            StorageTrieLevels = ImmutableArray<TrieLevelStat>.Empty,
            BranchOccupancyDistribution = ImmutableArray.Create(occupancy),
            AvgAccountPathDepth = 0,
            AvgStoragePathDepth = 0,
            MaxAccountDepth = 0,
            MaxStorageDepth = 0,
            AvgBranchOccupancy = 0,
        };

        Metrics.UpdateFromDistribution(dist);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_1_Children,  Is.EqualTo(10));
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_4_Children,  Is.EqualTo(40));
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_8_Children,  Is.EqualTo(80));
            Assert.That(Metrics.StateCompAccountTrieBranchOccupancy_16_Children, Is.EqualTo(160));
        }
    }
}
