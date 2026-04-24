// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.StateComposition.Visitors;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Visitors;

/// <summary>
/// Verifies the per-contract log-bucketed slot-count histogram
/// maintained by <see cref="VisitorCounters"/>.
/// Runs against <see cref="VisitorCounters"/> directly so the bucketing
/// rule is tested in isolation from the full visitor pipeline.
/// </summary>
[TestFixture]
public class SlotCountHistogramTests
{
    // Expected bucket = min(15, floor(log2(slotCount + 1))).
    [TestCase(1L, 1)]
    [TestCase(3L, 2)]
    [TestCase(7L, 3)]
    [TestCase(1023L, 10)]
    [TestCase(1L << 20, 15)]
    public void SlotCountHistogram_BucketsLogScale(long slots, int expectedBucket) =>
        Assert.That(VisitorCounters.ComputeSlotBucket(slots), Is.EqualTo(expectedBucket));

    [Test]
    public void SlotCountHistogram_SumMatchesContractsWithStorage()
    {
        VisitorCounters counters = new();

        // Simulate four contracts with storage, slot counts: 1, 3, 1023, 0.
        long[] slotsPerContract = [1, 3, 1023, 0];
        foreach (long slots in slotsPerContract) AddStorageContract(counters, slots);

        long histogramSum = 0;
        for (int i = 0; i < VisitorCounters.MaxTrackedDepth; i++)
            histogramSum += counters.SlotCountHistogram[i];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(histogramSum, Is.EqualTo(slotsPerContract.Length),
                "histogram sum must equal ContractsWithStorage");

            foreach (long slots in slotsPerContract)
            {
                int expectedBucket = VisitorCounters.ComputeSlotBucket(slots);
                Assert.That(counters.SlotCountHistogram[expectedBucket], Is.GreaterThan(0),
                    $"bucket {expectedBucket} populated for slots={slots}");
            }
        }
    }

    [Test]
    public void SlotCountHistogram_MergeFrom_SumsBuckets()
    {
        VisitorCounters a = new();
        VisitorCounters b = new();

        // Thread A sees two contracts with 3 slots (bucket 2) and one with 1023 (bucket 10).
        foreach (long slots in (long[])[3, 3, 1023]) AddStorageContract(a, slots);

        // Thread B sees one contract with 3 slots (bucket 2) and one with 1 slot (bucket 1).
        foreach (long slots in (long[])[3, 1]) AddStorageContract(b, slots);

        VisitorCounters merged = new();
        merged.MergeFrom(a);
        merged.MergeFrom(b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.SlotCountHistogram[1], Is.EqualTo(1)); // one 1-slot contract
            Assert.That(merged.SlotCountHistogram[2], Is.EqualTo(3)); // three 3-slot contracts
            Assert.That(merged.SlotCountHistogram[10], Is.EqualTo(1)); // one 1023-slot contract
            Assert.That(merged.ContractsWithStorage, Is.EqualTo(5));
        }
    }

    private static void AddStorageContract(VisitorCounters counters, long slots)
    {
        counters.ContractsWithStorage++;
        counters.BeginStorageTrie(default, default);
        for (long i = 0; i < slots; i++)
            counters.TrackStorageNode(depth: 1, byteSize: 1, isLeaf: true, isBranch: false);
        counters.Flush();
    }
}
