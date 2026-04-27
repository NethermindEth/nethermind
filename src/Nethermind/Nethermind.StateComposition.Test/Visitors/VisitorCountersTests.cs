// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Visitors;

[TestFixture]
public class VisitorCountersTests
{
    // Mirrors TopNTracker.EntryComparer (which is internal — exposing it through a
    // public test method signature triggers CS0051). Defined here so TestCaseSource
    // can dispatch the three CompareBy* method groups through a single typed handle.
    public delegate int EntryComparer(in TopContractEntry a, in TopContractEntry b);

    [Test]
    public void VisitorCounters_MergeFrom_AggregatesCorrectly()
    {
        VisitorCounters a = new()
        {
            AccountsTotal = 10,
            ContractsTotal = 3,
            AccountFullNodes = 5,
            StorageValueNodes = 20,
            AccountNodeBytes = 100,
            TotalBranchChildren = 40,
            TotalBranchNodes = 5,
        };
        a.AccountDepths[2].AddFullNode(50);

        VisitorCounters b = new()
        {
            AccountsTotal = 15,
            ContractsTotal = 7,
            AccountFullNodes = 8,
            StorageValueNodes = 30,
            AccountNodeBytes = 200,
            TotalBranchChildren = 60,
            TotalBranchNodes = 8,
        };
        b.AccountDepths[2].AddFullNode(75);

        a.MergeFrom(b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.AccountsTotal, Is.EqualTo(25));
            Assert.That(a.ContractsTotal, Is.EqualTo(10));
            Assert.That(a.AccountFullNodes, Is.EqualTo(13));
            Assert.That(a.StorageValueNodes, Is.EqualTo(50));
            Assert.That(a.AccountNodeBytes, Is.EqualTo(300));
            Assert.That(a.TotalBranchChildren, Is.EqualTo(100));
            Assert.That(a.TotalBranchNodes, Is.EqualTo(13));
            Assert.That(a.AccountDepths[2].FullNodes, Is.EqualTo(2));
            Assert.That(a.AccountDepths[2].TotalSize, Is.EqualTo(125));
        }
    }

    [Test]
    public void VisitorCounters_TopN_InsertsAndEvictsCorrectly()
    {
        VisitorCounters c = new(topN: 3);

        // Insert 5 contracts — only top 3 by depth should survive
        for (int i = 1; i <= 5; i++)
        {
            c.BeginStorageTrie(new ValueHash256(new byte[32]), default);
            c.TrackStorageNode(depth: i * 2, byteSize: 100, isLeaf: true, isBranch: false);
        }

        c.Flush();

        // Top 3 by depth should be depths 10, 8, 6 (the three largest)
        Assert.That(c.TopN.TopByDepthCount, Is.EqualTo(3));

        int[] depths = new int[c.TopN.TopByDepthCount];
        for (int i = 0; i < c.TopN.TopByDepthCount; i++)
            depths[i] = c.TopN.TopByDepth[i].MaxDepth;

        Array.Sort(depths);
        Assert.That(depths, Is.EqualTo([7, 9, 11])); // +1 Geth convention per depth
    }

    [Test]
    public void VisitorCounters_StorageMaxDepthHistogram_TracksCorrectly()
    {
        VisitorCounters c = new();

        // 3 contracts with storage at max depth 2, 5, 5
        c.BeginStorageTrie(default, default);
        c.TrackStorageNode(depth: 2, byteSize: 10, isLeaf: true, isBranch: false);

        c.BeginStorageTrie(default, default);
        c.TrackStorageNode(depth: 5, byteSize: 10, isLeaf: true, isBranch: false);

        c.BeginStorageTrie(default, default);
        c.TrackStorageNode(depth: 5, byteSize: 10, isLeaf: true, isBranch: false);

        c.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.StorageMaxDepthHistogram[3], Is.EqualTo(1)); // raw depth 2 + 1
            Assert.That(c.StorageMaxDepthHistogram[6], Is.EqualTo(2)); // raw depth 5 + 1
        }
    }

    [Test]
    public void VisitorCounters_MergeFrom_MergesTopN()
    {
        VisitorCounters a = new(topN: 5);
        a.BeginStorageTrie(default, default);
        a.TrackStorageNode(depth: 10, byteSize: 100, isLeaf: true, isBranch: false);
        a.Flush();

        VisitorCounters b = new(topN: 5);
        b.BeginStorageTrie(default, default);
        b.TrackStorageNode(depth: 20, byteSize: 200, isLeaf: true, isBranch: false);
        b.Flush();

        a.MergeFrom(b);

        Assert.That(a.TopN.TopByDepthCount, Is.EqualTo(2));
    }

    // For each primary key, two entries with identical primary value but a
    // different secondary must produce strict ordering so the heap never
    // compares equal. `b` is constructed to win the tiebreak so the comparer
    // returns < 0 for every case.
    private static IEnumerable<TestCaseData> TiebreakCases()
    {
        yield return new TestCaseData(
            new TopContractEntry { MaxDepth = 10, TotalNodes = 100, ValueNodes = 50 },
            new TopContractEntry { MaxDepth = 10, TotalNodes = 200, ValueNodes = 50 },
            (EntryComparer)TopNTracker.CompareByDepth)
            .SetName(nameof(Comparator_DeterministicTiebreaking) + "_ByDepth");

        yield return new TestCaseData(
            new TopContractEntry { TotalNodes = 200, MaxDepth = 5, ValueNodes = 50 },
            new TopContractEntry { TotalNodes = 200, MaxDepth = 10, ValueNodes = 50 },
            (EntryComparer)TopNTracker.CompareByTotalNodes)
            .SetName(nameof(Comparator_DeterministicTiebreaking) + "_ByTotalNodes");

        yield return new TestCaseData(
            new TopContractEntry { ValueNodes = 100, MaxDepth = 5, TotalNodes = 50 },
            new TopContractEntry { ValueNodes = 100, MaxDepth = 10, TotalNodes = 50 },
            (EntryComparer)TopNTracker.CompareByValueNodes)
            .SetName(nameof(Comparator_DeterministicTiebreaking) + "_ByValueNodes");
    }

    [TestCaseSource(nameof(TiebreakCases))]
    public void Comparator_DeterministicTiebreaking(TopContractEntry a, TopContractEntry b, EntryComparer cmp) =>
        Assert.That(cmp(in a, in b), Is.LessThan(0));

    [Test]
    public void VisitorCounters_BeginStorageTrie_PreservesOwner()
    {
        VisitorCounters c = new(topN: 5);

        byte[] ownerBytes = new byte[32];
        ownerBytes[0] = 0xAB;
        ownerBytes[31] = 0xCD;
        ValueHash256 expectedOwner = new(ownerBytes);

        c.BeginStorageTrie(default, expectedOwner);
        c.TrackStorageNode(depth: 5, byteSize: 100, isLeaf: true, isBranch: false);
        c.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(c.TopN.TopByDepthCount, Is.EqualTo(1));
            Assert.That(c.TopN.TopByDepth[0].Owner, Is.EqualTo(expectedOwner));
        }
    }

    [Test]
    public void VisitorCounters_TopN_ByNodes_InsertsAndEvictsCorrectly()
    {
        VisitorCounters c = new(topN: 3);

        // Insert 5 contracts with increasing TotalNodes counts
        for (int i = 1; i <= 5; i++)
        {
            byte[] ownerBytes = new byte[32];
            ownerBytes[0] = (byte)i;
            c.BeginStorageTrie(default, new ValueHash256(ownerBytes));
            // i*3 branch nodes to vary TotalNodes
            for (int j = 0; j < i * 3; j++)
                c.TrackStorageNode(depth: 1, byteSize: 10, isLeaf: false, isBranch: true);
        }

        c.Flush();

        // Top 3 by total nodes should be 15, 12, 9 (from i=5,4,3)
        Assert.That(c.TopN.TopByNodesCount, Is.EqualTo(3));

        long[] nodes = new long[3];
        for (int i = 0; i < 3; i++)
            nodes[i] = c.TopN.TopByNodes[i].TotalNodes;

        Array.Sort(nodes);
        Assert.That(nodes, Is.EqualTo(new long[] { 9, 12, 15 }));
    }
}
