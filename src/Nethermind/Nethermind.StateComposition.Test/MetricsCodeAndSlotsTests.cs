// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Test.Helpers;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Verifies that <see cref="Metrics.UpdateFromCumulativeStats"/> publishes
/// <see cref="CumulativeTrieStats.CodeBytesTotal"/> to <see cref="Metrics.StateCompCodeBytesTotal"/>
/// and fans <see cref="CumulativeTrieStats.SlotCountHistogram"/> out into the
/// labeled <see cref="Metrics.StateCompSlotCountHistogram"/> gauge. Producers
/// (visitor + snapshot decoder) always supply a length-16 histogram, so no
/// default-array path is tested here.
/// </summary>
[TestFixture]
public class MetricsCodeAndSlotsTests
{
    [SetUp]
    public void ClearHistogram() => Metrics.StateCompSlotCountHistogram.Clear();

    [Test]
    public void UpdateFromCumulativeStats_PublishesCodeBytesTotal()
    {
        Metrics.UpdateFromCumulativeStats(TestDataBuilders.BuildStats(codeBytes: 42_000_000));

        Assert.That(Metrics.StateCompCodeBytesTotal, Is.EqualTo(42_000_000));
    }

    [Test]
    public void UpdateFromCumulativeStats_FansOutAllSixteenBuckets()
    {
        // Each bucket gets a distinct value so we detect index swaps.
        long[] vals = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160];

        Metrics.UpdateFromCumulativeStats(TestDataBuilders.BuildStats(hist: [.. vals]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompSlotCountHistogram, Has.Count.EqualTo(16));
            for (int i = 0; i < 16; i++)
                Assert.That(Metrics.StateCompSlotCountHistogram[i], Is.EqualTo(vals[i]), $"bucket {i}");
        }
    }
}
