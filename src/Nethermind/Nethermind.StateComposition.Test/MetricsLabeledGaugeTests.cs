// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.StateComposition.Diff;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Verifies <see cref="Metrics.UpdateDepthDistribution"/> fans a seeded
/// <see cref="CumulativeDepthStats"/> into the four labeled gauges, applies
/// the Geth +1 ValueNode shift at the presentation boundary, and produces
/// the expected per-gauge cardinality.
/// </summary>
[TestFixture]
public class MetricsLabeledGaugeTests
{
    [SetUp]
    public void ClearGauges()
    {
        Metrics.StateCompTrieDepthNodes.Clear();
        Metrics.StateCompTrieDepthBytes.Clear();
        Metrics.StateCompAccountBranchOccupancy.Clear();
    }

    [Test]
    public void UpdateDepthDistribution_PopulatesLabeledGauges()
    {
        CumulativeDepthStats stats = BuildSeededStats();

        Metrics.UpdateDepthDistribution(stats);

        using (Assert.EnterMultipleScope())
        {
            // Per-depth cardinality: 2 tries × 16 depths × 3 kinds.
            Assert.That(Metrics.StateCompTrieDepthNodes, Has.Count.EqualTo(96));
            // Per-depth byte cardinality: 2 tries × 16 depths.
            Assert.That(Metrics.StateCompTrieDepthBytes, Has.Count.EqualTo(32));
            // Branch occupancy cardinality: 16 child-count buckets.
            Assert.That(Metrics.StateCompAccountBranchOccupancy, Has.Count.EqualTo(16));

            Assert.That(Metrics.StateCompTrieDepthNodes[("account", 3, "full")], Is.EqualTo(stats.AccountFullNodes[3]));
            Assert.That(Metrics.StateCompTrieDepthNodes[("account", 3, "short")], Is.EqualTo(stats.AccountShortNodes[3]));
            Assert.That(Metrics.StateCompTrieDepthBytes[("account", 3)], Is.EqualTo(stats.AccountNodeBytes[3]));

            Assert.That(Metrics.StateCompTrieDepthNodes[("storage", 5, "full")], Is.EqualTo(stats.StorageFullNodes[5]));
            Assert.That(Metrics.StateCompTrieDepthNodes[("storage", 5, "short")], Is.EqualTo(stats.StorageShortNodes[5]));
            Assert.That(Metrics.StateCompTrieDepthBytes[("storage", 5)], Is.EqualTo(stats.StorageNodeBytes[5]));

            // Branch occupancy: bucket i (1..16) comes from BranchOccupancy[i-1].
            Assert.That(Metrics.StateCompAccountBranchOccupancy[1], Is.EqualTo(stats.BranchOccupancy[0]));
            Assert.That(Metrics.StateCompAccountBranchOccupancy[16], Is.EqualTo(stats.BranchOccupancy[15]));
        }
    }

    [Test]
    public void UpdateDepthDistribution_AppliesGethValueNodeShiftAtDepth1()
    {
        CumulativeDepthStats stats = BuildSeededStats();

        Metrics.UpdateDepthDistribution(stats);

        using (Assert.EnterMultipleScope())
        {
            // Depth 0 has no physical predecessor → always 0.
            Assert.That(Metrics.StateCompTrieDepthNodes[("account", 0, "value")], Is.Zero);
            Assert.That(Metrics.StateCompTrieDepthNodes[("storage", 0, "value")], Is.Zero);

            // Depth d>0 reads physical depth d-1 — the Geth +1 presentation shift.
            Assert.That(Metrics.StateCompTrieDepthNodes[("account", 1, "value")], Is.EqualTo(stats.AccountValueNodes[0]));
            Assert.That(Metrics.StateCompTrieDepthNodes[("account", 7, "value")], Is.EqualTo(stats.AccountValueNodes[6]));
            Assert.That(Metrics.StateCompTrieDepthNodes[("storage", 1, "value")], Is.EqualTo(stats.StorageValueNodes[0]));
            Assert.That(Metrics.StateCompTrieDepthNodes[("storage", 7, "value")], Is.EqualTo(stats.StorageValueNodes[6]));
        }
    }

    private static CumulativeDepthStats BuildSeededStats()
    {
        CumulativeDepthStats source = new();
        for (int d = 0; d < 16; d++)
        {
            source.AccountFullNodes[d] = 100 + d;
            source.AccountShortNodes[d] = 200 + d;
            source.AccountValueNodes[d] = 300 + d;
            source.AccountNodeBytes[d] = 400 + d;

            source.StorageFullNodes[d] = 500 + d;
            source.StorageShortNodes[d] = 600 + d;
            source.StorageValueNodes[d] = 700 + d;
            source.StorageNodeBytes[d] = 800 + d;

            source.BranchOccupancy[d] = 900 + d;
        }
        source.TotalBranchNodes = 1_000;
        source.TotalBranchChildren = 8_000;

        CumulativeDepthStats seeded = new();
        seeded.SeedFromSnapshot(source);
        return seeded;
    }
}
