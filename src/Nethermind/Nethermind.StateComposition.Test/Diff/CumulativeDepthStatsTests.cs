// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.StateComposition.Diff;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Diff;

/// <summary>
/// Verifies <see cref="CumulativeDepthStats"/> seeding, in-place addition, and reset.
/// </summary>
[TestFixture]
public class CumulativeDepthStatsTests
{
    /// <summary>
    /// Produces an empty-but-seeded instance so <see cref="CumulativeDepthStats.AddInPlace"/>
    /// takes effect. Required since the IsSeeded gate no-ops adds on a cold baseline
    /// (the gate is what prevents negative gauges across restarts).
    /// </summary>
    private static CumulativeDepthStats NewSeededEmpty()
    {
        CumulativeDepthStats s = new();
        s.SeedFromSnapshot(new CumulativeDepthStats());
        return s;
    }

    [Test]
    public void AddInPlace_SingleBucketIncrement_OnlyTargetBucketChanges()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        CumulativeDepthStats delta = new();
        delta.AccountValueNodes[5] = 1;
        delta.AccountShortNodes[5] = 1;
        delta.AccountNodeBytes[5] = 42;

        stats.AddInPlace(delta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountValueNodes[5], Is.EqualTo(1));
            Assert.That(stats.AccountShortNodes[5], Is.EqualTo(1));
            Assert.That(stats.AccountNodeBytes[5], Is.EqualTo(42));
        }

        for (int i = 0; i < 16; i++)
        {
            if (i == 5) continue;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.AccountValueNodes[i], Is.Zero, $"AccountValueNodes[{i}] should be 0");
                Assert.That(stats.AccountShortNodes[i], Is.Zero, $"AccountShortNodes[{i}] should be 0");
            }
        }

        for (int i = 0; i < 16; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.StorageValueNodes[i], Is.Zero);
                Assert.That(stats.BranchOccupancy[i], Is.Zero);
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.TotalBranchNodes, Is.Zero);
            Assert.That(stats.TotalBranchChildren, Is.Zero);
        }
    }

    [Test]
    public void AddInPlace_MultipleDeltas_Compound()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        for (int i = 0; i < 3; i++)
        {
            CumulativeDepthStats delta = new()
            {
                TotalBranchNodes = 2,
                TotalBranchChildren = 8 // 2 branches × 4 children each
            };
            delta.AccountFullNodes[2] = 1;
            delta.BranchOccupancy[3] = 2;
            stats.AddInPlace(delta);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountFullNodes[2], Is.EqualTo(3));
            Assert.That(stats.BranchOccupancy[3], Is.EqualTo(6));
            Assert.That(stats.TotalBranchNodes, Is.EqualTo(6));
            Assert.That(stats.TotalBranchChildren, Is.EqualTo(24));
        }
    }

    [Test]
    public void AddInPlace_NegativeDelta_Decrements()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        CumulativeDepthStats addDelta = new();
        addDelta.AccountValueNodes[7] = 5;
        addDelta.AccountShortNodes[7] = 5;
        stats.AddInPlace(addDelta);

        CumulativeDepthStats removeDelta = new();
        removeDelta.AccountValueNodes[7] = -3;
        removeDelta.AccountShortNodes[7] = -3;
        stats.AddInPlace(removeDelta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountValueNodes[7], Is.EqualTo(2));
            Assert.That(stats.AccountShortNodes[7], Is.EqualTo(2));
        }
    }

    [Test]
    public void Reset_ZerosAllArraysAndScalars()
    {
        CumulativeDepthStats stats = NewSeededEmpty();

        CumulativeDepthStats delta = new();
        for (int i = 0; i < 16; i++)
        {
            delta.AccountFullNodes[i] = i + 1;
            delta.StorageValueNodes[i] = i + 2;
            delta.BranchOccupancy[i] = i + 3;
        }
        delta.TotalBranchNodes = 99;
        delta.TotalBranchChildren = 200;
        stats.AddInPlace(delta);

        stats.Reset();

        for (int i = 0; i < 16; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(stats.AccountFullNodes[i], Is.Zero, $"AccountFullNodes[{i}]");
                Assert.That(stats.StorageValueNodes[i], Is.Zero, $"StorageValueNodes[{i}]");
                Assert.That(stats.BranchOccupancy[i], Is.Zero, $"BranchOccupancy[{i}]");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.TotalBranchNodes, Is.Zero);
            Assert.That(stats.TotalBranchChildren, Is.Zero);
        }
    }

}
