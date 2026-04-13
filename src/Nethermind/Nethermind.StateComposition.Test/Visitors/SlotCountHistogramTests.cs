// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using NUnit.Framework;

using Nethermind.StateComposition.Visitors;

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
    [Test]
    public void SlotCountHistogram_BucketsLogScale()
    {
        // Expected bucket = min(15, floor(log2(slotCount + 1))).
        //   1 slot  → floor(log2(2)) = 1
        //   3 slots → floor(log2(4)) = 2
        //   7 slots → floor(log2(8)) = 3
        //   1023    → floor(log2(1024)) = 10
        //   1 << 20 → clamped to 15
        (long slots, int expectedBucket)[] cases =
        [
            (1, 1),
            (3, 2),
            (7, 3),
            (1023, 10),
            (1 << 20, 15),
        ];

        foreach ((long slots, int expectedBucket) in cases)
        {
            int actual = VisitorCounters.ComputeSlotBucket(slots);
            Assert.That(actual, Is.EqualTo(expectedBucket),
                $"slotCount={slots}");
        }
    }

    [Test]
    public void SlotCountHistogram_SumMatchesContractsWithStorage()
    {
        VisitorCounters counters = new();

        // Simulate four contracts with storage, slot counts: 1, 3, 1023, 0.
        // Each contract is a BeginStorageTrie -> N × TrackStorageNode(leaf) -> Flush cycle.
        (long slots, int expectedBucket)[] contracts =
        [
            (1, 1),
            (3, 2),
            (1023, 10),
            (0, 0),
        ];

        long withStorage = 0;
        foreach ((long slots, int _) in contracts)
        {
            counters.ContractsWithStorage++;
            withStorage++;
            counters.BeginStorageTrie(default, default);
            for (long i = 0; i < slots; i++)
                counters.TrackStorageNode(depth: 1, byteSize: 1, isLeaf: true, isBranch: false);
            counters.Flush();
        }

        long histogramSum = 0;
        for (int i = 0; i < VisitorCounters.MaxTrackedDepth; i++)
            histogramSum += counters.SlotCountHistogram[i];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(histogramSum, Is.EqualTo(withStorage),
                "histogram sum must equal ContractsWithStorage");

            foreach ((long slots, int expectedBucket) in contracts)
                Assert.That(counters.SlotCountHistogram[expectedBucket], Is.GreaterThan(0),
                    $"bucket {expectedBucket} populated for slots={slots}");
        }
    }

    [Test]
    public void SlotCountHistogram_MergeFrom_SumsBuckets()
    {
        VisitorCounters a = new();
        VisitorCounters b = new();

        // Thread A sees two contracts with 3 slots (bucket 2) and one with 1023 (bucket 10).
        foreach (long slots in new long[] { 3, 3, 1023 })
        {
            a.ContractsWithStorage++;
            a.BeginStorageTrie(default, default);
            for (long i = 0; i < slots; i++)
                a.TrackStorageNode(1, 1, isLeaf: true, isBranch: false);
            a.Flush();
        }

        // Thread B sees one contract with 3 slots (bucket 2) and one with 1 slot (bucket 1).
        foreach (long slots in new long[] { 3, 1 })
        {
            b.ContractsWithStorage++;
            b.BeginStorageTrie(default, default);
            for (long i = 0; i < slots; i++)
                b.TrackStorageNode(1, 1, isLeaf: true, isBranch: false);
            b.Flush();
        }

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
}
