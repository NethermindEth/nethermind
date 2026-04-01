// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

/// <summary>
/// Multi-threaded MergeFrom test — verify ThreadLocal counter pattern
/// produces correct aggregated totals when merging from multiple threads.
/// </summary>
[TestFixture]
public class MultiThreadedMergeTests
{
    [Test]
    public async Task MergeFrom_MultiThread_AggregatesCorrectly()
    {
        const int threadCount = 4;
        const int accountsPerThread = 100;

        VisitorCounters[] counters = new VisitorCounters[threadCount];

        await Task.WhenAll(Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            VisitorCounters c = new();
            for (int i = 0; i < accountsPerThread; i++)
            {
                c.AccountsTotal++;
                c.ContractsTotal++;
                c.AccountFullNodes += 2;
                c.AccountNodeBytes += 50;
                c.StorageSlotsTotal += 3;
                c.AccountDepths[1].AddFullNode(25);
                c.StorageDepths[2].AddShortNode(15);
            }
            counters[t] = c;
        })));

        VisitorCounters merged = new();
        foreach (VisitorCounters c in counters)
            merged.MergeFrom(c);

        long expected = threadCount * accountsPerThread;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(merged.AccountsTotal, Is.EqualTo(expected));
            Assert.That(merged.ContractsTotal, Is.EqualTo(expected));
            Assert.That(merged.AccountFullNodes, Is.EqualTo(expected * 2));
            Assert.That(merged.AccountNodeBytes, Is.EqualTo(expected * 50));
            Assert.That(merged.StorageSlotsTotal, Is.EqualTo(expected * 3));
            Assert.That(merged.AccountDepths[1].FullNodes, Is.EqualTo(expected));
            Assert.That(merged.AccountDepths[1].TotalSize, Is.EqualTo(expected * 25));
            Assert.That(merged.StorageDepths[2].ShortNodes, Is.EqualTo(expected));
            Assert.That(merged.StorageDepths[2].TotalSize, Is.EqualTo(expected * 15));
        }
    }

    [Test]
    public async Task MergeFrom_MultiThread_TopN_MergesCorrectly()
    {
        const int threadCount = 4;

        VisitorCounters[] counters = new VisitorCounters[threadCount];

        await Task.WhenAll(Enumerable.Range(0, threadCount).Select(t => Task.Run(() =>
        {
            VisitorCounters c = new(topN: 5);
            byte[] ownerBytes = new byte[32];
            ownerBytes[0] = (byte)t;

            c.BeginStorageTrie(default, new ValueHash256(ownerBytes));
            c.TrackStorageNode(depth: (t + 1) * 3, byteSize: 100, isLeaf: true, isBranch: false);
            c.Flush();
            counters[t] = c;
        })));

        VisitorCounters merged = new(topN: 5);
        foreach (VisitorCounters c in counters)
            merged.MergeFrom(c);

        Assert.That(merged.TopN.TopByDepthCount, Is.EqualTo(threadCount),
            "All 4 thread contributions should be in TopN");
    }
}
