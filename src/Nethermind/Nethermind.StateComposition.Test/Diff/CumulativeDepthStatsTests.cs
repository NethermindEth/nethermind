// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition.Test.Diff;

/// <summary>
/// Verifies <see cref="CumulativeDepthStats"/> seeding, delta application, reset, and clone.
/// </summary>
[TestFixture]
public class CumulativeDepthStatsTests
{
    /// <summary>
    /// Produces an empty-but-seeded instance so <see cref="CumulativeDepthStats.ApplyDelta"/>
    /// takes effect. Required since the IsSeeded gate no-ops deltas on a cold baseline
    /// (the gate is what prevents negative gauges across restarts).
    /// </summary>
    private static CumulativeDepthStats NewSeededEmpty()
    {
        CumulativeDepthStats s = new();
        s.SeedFromSnapshot(new CumulativeDepthStats());
        return s;
    }

    [Test]
    public void SeedThenUpdate_MatchesUpdateFromDistribution()
    {
        // Use a real trie scan to ensure the distribution is internally consistent
        // (hand-crafted distributions with hardcoded scalars may not match derived scalars).
        Nethermind.Db.MemDb db = new();
        Nethermind.State.StateTree tree = new(new Nethermind.Trie.Pruning.RawScopedTrieStore(db), Nethermind.Logging.LimboLogs.Instance);

        for (int i = 0; i < 30; i++)
        {
            byte[] addr = new byte[20];
            addr[0] = (byte)(i / 256);
            addr[1] = (byte)(i % 256);
            tree.Set(new Nethermind.Core.Address(addr), new Nethermind.Core.Account(0, (Nethermind.Int256.UInt256)(i + 1)));
        }
        tree.Commit();
        tree.UpdateRootHash();
        Nethermind.Core.Crypto.Hash256 root = tree.RootHash;

        using StateCompositionVisitor visitor = new(Nethermind.Logging.LimboLogs.Instance);
        tree.Accept(visitor, root);
        TrieDepthDistribution dist = visitor.GetTrieDistribution();

        // Reset gauges to a known state
        MetricsDepthGaugesHelper.ResetAllDepthGauges();

        // Path A: UpdateFromDistribution (Phase A) — the ground truth
        Metrics.UpdateFromDistribution(dist);
        DepthGaugeSnapshot fromDist = DepthGaugeSnapshot.Capture();

        // Reset gauges again
        MetricsDepthGaugesHelper.ResetAllDepthGauges();

        // Path B: SeedFromScan + UpdateFromDepthStats (Phase B)
        CumulativeDepthStats stats = new();
        stats.SeedFromScan(dist);
        Metrics.UpdateFromDepthStats(stats);
        DepthGaugeSnapshot fromStats = DepthGaugeSnapshot.Capture();

        // Per-depth arrays and branch occupancy must be identical
        // Scalars are derived differently: UpdateFromDistribution uses pre-computed scan values,
        // UpdateFromDepthStats recomputes them from arrays. They should match for a real trie.
        fromDist.AssertPerDepthEqualTo(fromStats);
    }

    [Test]
    public void ApplyDelta_SingleBucketIncrement_OnlyTargetBucketChanges()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        DepthDelta delta = new();
        delta.AccountValueNodes[5] = 1;
        delta.AccountShortNodes[5] = 1;
        delta.AccountNodeBytes[5] = 42;

        stats.ApplyDelta(delta);

        Assert.That(stats.AccountValueNodes[5], Is.EqualTo(1));
        Assert.That(stats.AccountShortNodes[5], Is.EqualTo(1));
        Assert.That(stats.AccountNodeBytes[5], Is.EqualTo(42));

        // All other buckets must be zero
        for (int i = 0; i < 16; i++)
        {
            if (i == 5) continue;
            Assert.That(stats.AccountValueNodes[i], Is.EqualTo(0), $"AccountValueNodes[{i}] should be 0");
            Assert.That(stats.AccountShortNodes[i], Is.EqualTo(0), $"AccountShortNodes[{i}] should be 0");
        }

        // Storage and branch occupancy untouched
        for (int i = 0; i < 16; i++)
        {
            Assert.That(stats.StorageValueNodes[i], Is.EqualTo(0));
            Assert.That(stats.BranchOccupancy[i], Is.EqualTo(0));
        }
        Assert.That(stats.TotalBranchNodes, Is.EqualTo(0));
        Assert.That(stats.TotalBranchChildren, Is.EqualTo(0));
    }

    [Test]
    public void ApplyDelta_MultipleDeltas_Compound()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        for (int i = 0; i < 3; i++)
        {
            DepthDelta delta = new();
            delta.AccountFullNodes[2] = 1;
            delta.BranchOccupancy[3] = 2;
            delta.TotalBranchNodesDelta = 2;
            delta.TotalBranchChildrenDelta = 8; // 2 branches × 4 children each
            stats.ApplyDelta(delta);
        }

        Assert.That(stats.AccountFullNodes[2], Is.EqualTo(3));
        Assert.That(stats.BranchOccupancy[3], Is.EqualTo(6));
        Assert.That(stats.TotalBranchNodes, Is.EqualTo(6));
        Assert.That(stats.TotalBranchChildren, Is.EqualTo(24));
    }

    [Test]
    public void ApplyDelta_NegativeDelta_Decrements()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        // First add 5 leaves at depth 7
        DepthDelta addDelta = new();
        addDelta.AccountValueNodes[7] = 5;
        addDelta.AccountShortNodes[7] = 5;
        stats.ApplyDelta(addDelta);

        // Then remove 3
        DepthDelta removeDelta = new();
        removeDelta.AccountValueNodes[7] = -3;
        removeDelta.AccountShortNodes[7] = -3;
        stats.ApplyDelta(removeDelta);

        Assert.That(stats.AccountValueNodes[7], Is.EqualTo(2));
        Assert.That(stats.AccountShortNodes[7], Is.EqualTo(2));
    }

    [Test]
    public void Reset_ZerosAllArraysAndScalars()
    {
        CumulativeDepthStats stats = new();

        // Populate via delta
        DepthDelta delta = new();
        for (int i = 0; i < 16; i++)
        {
            delta.AccountFullNodes[i] = i + 1;
            delta.StorageValueNodes[i] = i + 2;
            delta.BranchOccupancy[i] = i + 3;
        }
        delta.TotalBranchNodesDelta = 99;
        delta.TotalBranchChildrenDelta = 200;
        stats.ApplyDelta(delta);

        stats.Reset();

        for (int i = 0; i < 16; i++)
        {
            Assert.That(stats.AccountFullNodes[i], Is.EqualTo(0), $"AccountFullNodes[{i}]");
            Assert.That(stats.StorageValueNodes[i], Is.EqualTo(0), $"StorageValueNodes[{i}]");
            Assert.That(stats.BranchOccupancy[i], Is.EqualTo(0), $"BranchOccupancy[{i}]");
        }
        Assert.That(stats.TotalBranchNodes, Is.EqualTo(0));
        Assert.That(stats.TotalBranchChildren, Is.EqualTo(0));
    }

    [Test]
    public void Clone_IsDeepCopy_MutatingOriginalDoesNotAffectClone()
    {
        CumulativeDepthStats original = NewSeededEmpty();
        DepthDelta delta = new();
        delta.AccountFullNodes[3] = 10;
        original.ApplyDelta(delta);

        CumulativeDepthStats clone = original.Clone();

        // Mutate the original
        DepthDelta more = new();
        more.AccountFullNodes[3] = 5;
        original.ApplyDelta(more);

        // Clone must be unaffected
        Assert.That(clone.AccountFullNodes[3], Is.EqualTo(10));
        Assert.That(original.AccountFullNodes[3], Is.EqualTo(15));
    }
}

/// <summary>
/// Snapshot of all 149 depth gauge values captured at a point in time.
/// Used to compare two update paths for parity.
/// </summary>
internal sealed class DepthGaugeSnapshot
{
    public double AvgAccountPathDepth;
    public double AvgStoragePathDepth;
    public long MaxAccountDepth;
    public long MaxStorageDepth;
    public double AvgBranchOccupancy;

    public readonly long[] AccFull = new long[16];
    public readonly long[] AccShort = new long[16];
    public readonly long[] AccValue = new long[16];
    public readonly long[] AccBytes = new long[16];
    public readonly long[] StFull = new long[16];
    public readonly long[] StShort = new long[16];
    public readonly long[] StValue = new long[16];
    public readonly long[] StBytes = new long[16];
    public readonly long[] Occ = new long[16]; // index 0 = 1-children

    public static DepthGaugeSnapshot Capture()
    {
        DepthGaugeSnapshot s = new()
        {
            AvgAccountPathDepth = Metrics.StateCompAvgAccountPathDepth,
            AvgStoragePathDepth = Metrics.StateCompAvgStoragePathDepth,
            MaxAccountDepth = Metrics.StateCompMaxAccountDepth,
            MaxStorageDepth = Metrics.StateCompMaxStorageDepth,
            AvgBranchOccupancy = Metrics.StateCompAvgBranchOccupancy,
        };

        // Depths 0-15
        long[] full = [Metrics.StateCompAccountTrieDepth_0_FullNodes, Metrics.StateCompAccountTrieDepth_1_FullNodes, Metrics.StateCompAccountTrieDepth_2_FullNodes, Metrics.StateCompAccountTrieDepth_3_FullNodes, Metrics.StateCompAccountTrieDepth_4_FullNodes, Metrics.StateCompAccountTrieDepth_5_FullNodes, Metrics.StateCompAccountTrieDepth_6_FullNodes, Metrics.StateCompAccountTrieDepth_7_FullNodes, Metrics.StateCompAccountTrieDepth_8_FullNodes, Metrics.StateCompAccountTrieDepth_9_FullNodes, Metrics.StateCompAccountTrieDepth_10_FullNodes, Metrics.StateCompAccountTrieDepth_11_FullNodes, Metrics.StateCompAccountTrieDepth_12_FullNodes, Metrics.StateCompAccountTrieDepth_13_FullNodes, Metrics.StateCompAccountTrieDepth_14_FullNodes, Metrics.StateCompAccountTrieDepth_15_FullNodes];
        long[] shrt = [Metrics.StateCompAccountTrieDepth_0_ShortNodes, Metrics.StateCompAccountTrieDepth_1_ShortNodes, Metrics.StateCompAccountTrieDepth_2_ShortNodes, Metrics.StateCompAccountTrieDepth_3_ShortNodes, Metrics.StateCompAccountTrieDepth_4_ShortNodes, Metrics.StateCompAccountTrieDepth_5_ShortNodes, Metrics.StateCompAccountTrieDepth_6_ShortNodes, Metrics.StateCompAccountTrieDepth_7_ShortNodes, Metrics.StateCompAccountTrieDepth_8_ShortNodes, Metrics.StateCompAccountTrieDepth_9_ShortNodes, Metrics.StateCompAccountTrieDepth_10_ShortNodes, Metrics.StateCompAccountTrieDepth_11_ShortNodes, Metrics.StateCompAccountTrieDepth_12_ShortNodes, Metrics.StateCompAccountTrieDepth_13_ShortNodes, Metrics.StateCompAccountTrieDepth_14_ShortNodes, Metrics.StateCompAccountTrieDepth_15_ShortNodes];
        long[] val = [Metrics.StateCompAccountTrieDepth_0_ValueNodes, Metrics.StateCompAccountTrieDepth_1_ValueNodes, Metrics.StateCompAccountTrieDepth_2_ValueNodes, Metrics.StateCompAccountTrieDepth_3_ValueNodes, Metrics.StateCompAccountTrieDepth_4_ValueNodes, Metrics.StateCompAccountTrieDepth_5_ValueNodes, Metrics.StateCompAccountTrieDepth_6_ValueNodes, Metrics.StateCompAccountTrieDepth_7_ValueNodes, Metrics.StateCompAccountTrieDepth_8_ValueNodes, Metrics.StateCompAccountTrieDepth_9_ValueNodes, Metrics.StateCompAccountTrieDepth_10_ValueNodes, Metrics.StateCompAccountTrieDepth_11_ValueNodes, Metrics.StateCompAccountTrieDepth_12_ValueNodes, Metrics.StateCompAccountTrieDepth_13_ValueNodes, Metrics.StateCompAccountTrieDepth_14_ValueNodes, Metrics.StateCompAccountTrieDepth_15_ValueNodes];
        long[] bytes = [Metrics.StateCompAccountTrieDepth_0_Bytes, Metrics.StateCompAccountTrieDepth_1_Bytes, Metrics.StateCompAccountTrieDepth_2_Bytes, Metrics.StateCompAccountTrieDepth_3_Bytes, Metrics.StateCompAccountTrieDepth_4_Bytes, Metrics.StateCompAccountTrieDepth_5_Bytes, Metrics.StateCompAccountTrieDepth_6_Bytes, Metrics.StateCompAccountTrieDepth_7_Bytes, Metrics.StateCompAccountTrieDepth_8_Bytes, Metrics.StateCompAccountTrieDepth_9_Bytes, Metrics.StateCompAccountTrieDepth_10_Bytes, Metrics.StateCompAccountTrieDepth_11_Bytes, Metrics.StateCompAccountTrieDepth_12_Bytes, Metrics.StateCompAccountTrieDepth_13_Bytes, Metrics.StateCompAccountTrieDepth_14_Bytes, Metrics.StateCompAccountTrieDepth_15_Bytes];
        full.CopyTo(s.AccFull, 0); shrt.CopyTo(s.AccShort, 0); val.CopyTo(s.AccValue, 0); bytes.CopyTo(s.AccBytes, 0);

        long[] stFull = [Metrics.StateCompStorageTrieDepth_0_FullNodes, Metrics.StateCompStorageTrieDepth_1_FullNodes, Metrics.StateCompStorageTrieDepth_2_FullNodes, Metrics.StateCompStorageTrieDepth_3_FullNodes, Metrics.StateCompStorageTrieDepth_4_FullNodes, Metrics.StateCompStorageTrieDepth_5_FullNodes, Metrics.StateCompStorageTrieDepth_6_FullNodes, Metrics.StateCompStorageTrieDepth_7_FullNodes, Metrics.StateCompStorageTrieDepth_8_FullNodes, Metrics.StateCompStorageTrieDepth_9_FullNodes, Metrics.StateCompStorageTrieDepth_10_FullNodes, Metrics.StateCompStorageTrieDepth_11_FullNodes, Metrics.StateCompStorageTrieDepth_12_FullNodes, Metrics.StateCompStorageTrieDepth_13_FullNodes, Metrics.StateCompStorageTrieDepth_14_FullNodes, Metrics.StateCompStorageTrieDepth_15_FullNodes];
        long[] stShort = [Metrics.StateCompStorageTrieDepth_0_ShortNodes, Metrics.StateCompStorageTrieDepth_1_ShortNodes, Metrics.StateCompStorageTrieDepth_2_ShortNodes, Metrics.StateCompStorageTrieDepth_3_ShortNodes, Metrics.StateCompStorageTrieDepth_4_ShortNodes, Metrics.StateCompStorageTrieDepth_5_ShortNodes, Metrics.StateCompStorageTrieDepth_6_ShortNodes, Metrics.StateCompStorageTrieDepth_7_ShortNodes, Metrics.StateCompStorageTrieDepth_8_ShortNodes, Metrics.StateCompStorageTrieDepth_9_ShortNodes, Metrics.StateCompStorageTrieDepth_10_ShortNodes, Metrics.StateCompStorageTrieDepth_11_ShortNodes, Metrics.StateCompStorageTrieDepth_12_ShortNodes, Metrics.StateCompStorageTrieDepth_13_ShortNodes, Metrics.StateCompStorageTrieDepth_14_ShortNodes, Metrics.StateCompStorageTrieDepth_15_ShortNodes];
        long[] stVal = [Metrics.StateCompStorageTrieDepth_0_ValueNodes, Metrics.StateCompStorageTrieDepth_1_ValueNodes, Metrics.StateCompStorageTrieDepth_2_ValueNodes, Metrics.StateCompStorageTrieDepth_3_ValueNodes, Metrics.StateCompStorageTrieDepth_4_ValueNodes, Metrics.StateCompStorageTrieDepth_5_ValueNodes, Metrics.StateCompStorageTrieDepth_6_ValueNodes, Metrics.StateCompStorageTrieDepth_7_ValueNodes, Metrics.StateCompStorageTrieDepth_8_ValueNodes, Metrics.StateCompStorageTrieDepth_9_ValueNodes, Metrics.StateCompStorageTrieDepth_10_ValueNodes, Metrics.StateCompStorageTrieDepth_11_ValueNodes, Metrics.StateCompStorageTrieDepth_12_ValueNodes, Metrics.StateCompStorageTrieDepth_13_ValueNodes, Metrics.StateCompStorageTrieDepth_14_ValueNodes, Metrics.StateCompStorageTrieDepth_15_ValueNodes];
        long[] stBytes = [Metrics.StateCompStorageTrieDepth_0_Bytes, Metrics.StateCompStorageTrieDepth_1_Bytes, Metrics.StateCompStorageTrieDepth_2_Bytes, Metrics.StateCompStorageTrieDepth_3_Bytes, Metrics.StateCompStorageTrieDepth_4_Bytes, Metrics.StateCompStorageTrieDepth_5_Bytes, Metrics.StateCompStorageTrieDepth_6_Bytes, Metrics.StateCompStorageTrieDepth_7_Bytes, Metrics.StateCompStorageTrieDepth_8_Bytes, Metrics.StateCompStorageTrieDepth_9_Bytes, Metrics.StateCompStorageTrieDepth_10_Bytes, Metrics.StateCompStorageTrieDepth_11_Bytes, Metrics.StateCompStorageTrieDepth_12_Bytes, Metrics.StateCompStorageTrieDepth_13_Bytes, Metrics.StateCompStorageTrieDepth_14_Bytes, Metrics.StateCompStorageTrieDepth_15_Bytes];
        stFull.CopyTo(s.StFull, 0); stShort.CopyTo(s.StShort, 0); stVal.CopyTo(s.StValue, 0); stBytes.CopyTo(s.StBytes, 0);

        long[] occ = [Metrics.StateCompAccountTrieBranchOccupancy_1_Children, Metrics.StateCompAccountTrieBranchOccupancy_2_Children, Metrics.StateCompAccountTrieBranchOccupancy_3_Children, Metrics.StateCompAccountTrieBranchOccupancy_4_Children, Metrics.StateCompAccountTrieBranchOccupancy_5_Children, Metrics.StateCompAccountTrieBranchOccupancy_6_Children, Metrics.StateCompAccountTrieBranchOccupancy_7_Children, Metrics.StateCompAccountTrieBranchOccupancy_8_Children, Metrics.StateCompAccountTrieBranchOccupancy_9_Children, Metrics.StateCompAccountTrieBranchOccupancy_10_Children, Metrics.StateCompAccountTrieBranchOccupancy_11_Children, Metrics.StateCompAccountTrieBranchOccupancy_12_Children, Metrics.StateCompAccountTrieBranchOccupancy_13_Children, Metrics.StateCompAccountTrieBranchOccupancy_14_Children, Metrics.StateCompAccountTrieBranchOccupancy_15_Children, Metrics.StateCompAccountTrieBranchOccupancy_16_Children];
        occ.CopyTo(s.Occ, 0);

        return s;
    }

    public void AssertEqualTo(DepthGaugeSnapshot other)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(other.AvgAccountPathDepth, Is.EqualTo(AvgAccountPathDepth), "AvgAccountPathDepth");
            Assert.That(other.AvgStoragePathDepth, Is.EqualTo(AvgStoragePathDepth), "AvgStoragePathDepth");
            Assert.That(other.MaxAccountDepth, Is.EqualTo(MaxAccountDepth), "MaxAccountDepth");
            Assert.That(other.MaxStorageDepth, Is.EqualTo(MaxStorageDepth), "MaxStorageDepth");
            Assert.That(other.AvgBranchOccupancy, Is.EqualTo(AvgBranchOccupancy), "AvgBranchOccupancy");
            AssertPerDepthEqualTo(other);
        }
    }

    /// <summary>
    /// Assert per-depth array gauges are identical, without checking scalars.
    /// Used when scalar derivation paths legitimately differ (e.g., scan-precomputed vs array-derived).
    /// </summary>
    public void AssertPerDepthEqualTo(DepthGaugeSnapshot other)
    {
        using (Assert.EnterMultipleScope())
        {
            for (int d = 0; d < 16; d++)
            {
                Assert.That(other.AccFull[d], Is.EqualTo(AccFull[d]), $"AccFull[{d}]");
                Assert.That(other.AccShort[d], Is.EqualTo(AccShort[d]), $"AccShort[{d}]");
                Assert.That(other.AccValue[d], Is.EqualTo(AccValue[d]), $"AccValue[{d}]");
                Assert.That(other.AccBytes[d], Is.EqualTo(AccBytes[d]), $"AccBytes[{d}]");
                Assert.That(other.StFull[d], Is.EqualTo(StFull[d]), $"StFull[{d}]");
                Assert.That(other.StShort[d], Is.EqualTo(StShort[d]), $"StShort[{d}]");
                Assert.That(other.StValue[d], Is.EqualTo(StValue[d]), $"StValue[{d}]");
                Assert.That(other.StBytes[d], Is.EqualTo(StBytes[d]), $"StBytes[{d}]");
                Assert.That(other.Occ[d], Is.EqualTo(Occ[d]), $"BranchOccupancy[{d}]");
            }
        }
    }
}
