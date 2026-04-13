// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using NUnit.Framework;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Verifies that <see cref="Metrics.UpdateFromCumulativeStats"/> publishes
/// <see cref="CumulativeSizeStats.CodeBytesTotal"/> to <see cref="Metrics.StateCompCodeBytesTotal"/>
/// and fans <see cref="CumulativeSizeStats.SlotCountHistogram"/> out across the
/// 16 per-bucket gauges. Producers (visitor + snapshot decoder) always supply a
/// length-16 histogram, so no default-array path is tested here.
/// </summary>
[TestFixture]
public class MetricsCodeAndSlotsTests
{
    private static ImmutableArray<long> ZeroHistogram() => ImmutableArray.Create(new long[16]);

    private static CumulativeSizeStats BuildStats(long codeBytes, ImmutableArray<long> hist) =>
        new(
            AccountsTotal: 0,
            ContractsTotal: 0,
            StorageSlotsTotal: 0,
            AccountTrieBranches: 0,
            AccountTrieExtensions: 0,
            AccountTrieLeaves: 0,
            AccountTrieBytes: 0,
            StorageTrieBranches: 0,
            StorageTrieExtensions: 0,
            StorageTrieLeaves: 0,
            StorageTrieBytes: 0,
            ContractsWithStorage: 0,
            EmptyAccounts: 0)
        {
            CodeBytesTotal = codeBytes,
            SlotCountHistogram = hist,
        };

    [Test]
    public void UpdateFromCumulativeStats_PublishesCodeBytesTotal()
    {
        Metrics.UpdateFromCumulativeStats(BuildStats(codeBytes: 42_000_000, hist: ZeroHistogram()));

        Assert.That(Metrics.StateCompCodeBytesTotal, Is.EqualTo(42_000_000));
    }

    [Test]
    public void UpdateFromCumulativeStats_FansOutAllSixteenBuckets()
    {
        // Each bucket gets a distinct value so we detect index swaps.
        long[] vals = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160];

        Metrics.UpdateFromCumulativeStats(BuildStats(codeBytes: 0, hist: ImmutableArray.Create(vals)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompSlotCountBucket0, Is.EqualTo(10));
            Assert.That(Metrics.StateCompSlotCountBucket1, Is.EqualTo(20));
            Assert.That(Metrics.StateCompSlotCountBucket2, Is.EqualTo(30));
            Assert.That(Metrics.StateCompSlotCountBucket3, Is.EqualTo(40));
            Assert.That(Metrics.StateCompSlotCountBucket4, Is.EqualTo(50));
            Assert.That(Metrics.StateCompSlotCountBucket5, Is.EqualTo(60));
            Assert.That(Metrics.StateCompSlotCountBucket6, Is.EqualTo(70));
            Assert.That(Metrics.StateCompSlotCountBucket7, Is.EqualTo(80));
            Assert.That(Metrics.StateCompSlotCountBucket8, Is.EqualTo(90));
            Assert.That(Metrics.StateCompSlotCountBucket9, Is.EqualTo(100));
            Assert.That(Metrics.StateCompSlotCountBucket10, Is.EqualTo(110));
            Assert.That(Metrics.StateCompSlotCountBucket11, Is.EqualTo(120));
            Assert.That(Metrics.StateCompSlotCountBucket12, Is.EqualTo(130));
            Assert.That(Metrics.StateCompSlotCountBucket13, Is.EqualTo(140));
            Assert.That(Metrics.StateCompSlotCountBucket14, Is.EqualTo(150));
            Assert.That(Metrics.StateCompSlotCountBucket15, Is.EqualTo(160));
        }
    }
}
