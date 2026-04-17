// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.StateComposition.Data;
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

    private static ImmutableArray<long> ZeroHistogram() => [.. new long[16]];

    private static CumulativeTrieStats BuildStats(long codeBytes, ImmutableArray<long> hist) =>
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

        Metrics.UpdateFromCumulativeStats(BuildStats(codeBytes: 0, hist: [.. vals]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.StateCompSlotCountHistogram, Has.Count.EqualTo(16));
            for (int i = 0; i < 16; i++)
                Assert.That(Metrics.StateCompSlotCountHistogram[i], Is.EqualTo(vals[i]), $"bucket {i}");
        }
    }
}
