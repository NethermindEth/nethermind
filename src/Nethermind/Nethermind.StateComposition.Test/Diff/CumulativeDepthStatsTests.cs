// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

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
