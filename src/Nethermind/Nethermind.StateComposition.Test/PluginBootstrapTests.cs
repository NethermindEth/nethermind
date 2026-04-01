// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class PluginBootstrapTests
{
    [Test]
    public void Plugin_HasCorrectMetadata()
    {
        StateCompositionPlugin plugin = new();

        Assert.That(plugin.Name, Is.EqualTo("StateComposition"));
        Assert.That(plugin.Description, Is.EqualTo("State composition metrics for bloatnet benchmarking"));
        Assert.That(plugin.Author, Is.EqualTo("Nethermind"));
    }

    [Test]
    public void Plugin_IsEnabledByDefault()
    {
        StateCompositionPlugin plugin = new();

        Assert.That(plugin.Enabled, Is.True);
    }

    [Test]
    public void Plugin_HasModule()
    {
        StateCompositionPlugin plugin = new();

        Assert.That(plugin.Module, Is.Not.Null);
        Assert.That(plugin.Module, Is.InstanceOf<StateCompositionModule>());
    }

    [Test]
    public void StateCompositionStats_HasTrieNodeByteFields()
    {
        StateCompositionStats stats = new()
        {
            AccountTrieNodeBytes = 300,
            StorageTrieNodeBytes = 400,
        };

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountTrieNodeBytes, Is.EqualTo(300));
            Assert.That(stats.StorageTrieNodeBytes, Is.EqualTo(400));
        });
    }

    [Test]
    public void TrieLevelStat_HasTotalSize()
    {
        TrieLevelStat stat = new()
        {
            Depth = 3,
            FullNodeCount = 10,
            ShortNodeCount = 5,
            ValueNodeCount = 20,
            TotalSize = 1500,
        };

        Assert.That(stat.TotalSize, Is.EqualTo(1500));
    }

    [Test]
    public void ScanMetadata_HasIsComplete()
    {
        ScanMetadata meta = new() { IsComplete = true };

        Assert.That(meta.IsComplete, Is.True);
    }

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

        Assert.Multiple(() =>
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
        });
    }

    [Test]
    public void DepthCounter_AddFullNode_UpdatesCountAndSize()
    {
        DepthCounter counter = new();

        counter.AddFullNode(100);
        counter.AddFullNode(200);

        Assert.Multiple(() =>
        {
            Assert.That(counter.FullNodes, Is.EqualTo(2));
            Assert.That(counter.TotalSize, Is.EqualTo(300));
            Assert.That(counter.ShortNodes, Is.EqualTo(0));
            Assert.That(counter.ValueNodes, Is.EqualTo(0));
        });
    }

    [Test]
    public void DepthCounter_AddShortNodeAndValueNode()
    {
        DepthCounter counter = new();

        counter.AddShortNode(50);
        counter.AddValueNode(75);

        Assert.Multiple(() =>
        {
            Assert.That(counter.ShortNodes, Is.EqualTo(1));
            Assert.That(counter.ValueNodes, Is.EqualTo(1));
            Assert.That(counter.TotalSize, Is.EqualTo(125));
        });
    }

    [Test]
    public void DataStructures_RoundtripJson()
    {
        // Round-trip: serialize → deserialize → compare via record struct equality.
        // Uses primitive-only types to avoid needing Nethermind's custom Hash256 converter.
        TrieLevelStat original = new()
        {
            Depth = 3,
            FullNodeCount = 10,
            ShortNodeCount = 5,
            ValueNodeCount = 20,
            TotalSize = 500,
        };

        string json = JsonSerializer.Serialize(original);
        TrieLevelStat deserialized = JsonSerializer.Deserialize<TrieLevelStat>(json);

        Assert.That(deserialized, Is.EqualTo(original));
    }

    [Test]
    public void VisitorCounters_MaxTrackedDepth_Is16()
    {
        Assert.That(VisitorCounters.MaxTrackedDepth, Is.EqualTo(16));
    }

    [Test]
    public void VisitorCounters_TopN_InsertsAndEvictsCorrectly()
    {
        VisitorCounters c = new(topN: 3);

        // Insert 5 contracts — only top 3 by depth should survive
        for (int i = 1; i <= 5; i++)
        {
            c.BeginStorageTrie(new Core.Crypto.ValueHash256(new byte[32]), default);
            c.TrackStorageNode(depth: i * 2, byteSize: 100, isLeaf: true, isBranch: false);
        }

        c.Flush();

        // Top 3 by depth should be depths 10, 8, 6 (the three largest)
        Assert.That(c.TopN.TopByDepthCount, Is.EqualTo(3));

        int[] depths = new int[c.TopN.TopByDepthCount];
        for (int i = 0; i < c.TopN.TopByDepthCount; i++)
            depths[i] = c.TopN.TopByDepth[i].MaxDepth;

        Array.Sort(depths);
        Assert.That(depths, Is.EqualTo(new[] { 7, 9, 11 })); // +1 Geth convention per depth
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

        Assert.Multiple(() =>
        {
            Assert.That(c.StorageMaxDepthHistogram[3], Is.EqualTo(1)); // raw depth 2 + 1
            Assert.That(c.StorageMaxDepthHistogram[6], Is.EqualTo(2)); // raw depth 5 + 1
        });
    }

    [Test]
    public void VisitorCounters_Flush_NoOp_WhenNoActiveTrie()
    {
        VisitorCounters c = new();
        c.Flush(); // Should not throw

        Assert.That(c.TopN.TopByDepthCount, Is.EqualTo(0));
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

    // --- Deterministic comparator tests ---

    [Test]
    public void Comparator_TopByDepth_DeterministicTiebreaking()
    {
        // Same MaxDepth → tiebreak on TotalNodes DESC
        TopContractEntry a = new() { MaxDepth = 10, TotalNodes = 100, ValueNodes = 50 };
        TopContractEntry b = new() { MaxDepth = 10, TotalNodes = 200, ValueNodes = 50 };

        int result = TopNTracker.CompareByDepth(in a, in b);
        Assert.That(result, Is.LessThan(0)); // b has more TotalNodes → a < b
    }

    [Test]
    public void Comparator_TopByNodes_DeterministicTiebreaking()
    {
        // Same TotalNodes → tiebreak on MaxDepth DESC
        TopContractEntry a = new() { TotalNodes = 200, MaxDepth = 5, ValueNodes = 50 };
        TopContractEntry b = new() { TotalNodes = 200, MaxDepth = 10, ValueNodes = 50 };

        int result = TopNTracker.CompareByTotalNodes(in a, in b);
        Assert.That(result, Is.LessThan(0)); // b has more MaxDepth → a < b
    }

    [Test]
    public void Comparator_TopByValueNodes_DeterministicTiebreaking()
    {
        // Same ValueNodes → tiebreak on MaxDepth DESC
        TopContractEntry a = new() { ValueNodes = 100, MaxDepth = 5, TotalNodes = 50 };
        TopContractEntry b = new() { ValueNodes = 100, MaxDepth = 10, TotalNodes = 50 };

        int result = TopNTracker.CompareByValueNodes(in a, in b);
        Assert.That(result, Is.LessThan(0)); // b has more MaxDepth → a < b
    }

    [Test]
    public void TopContractEntry_HasOwnerAndLevels()
    {
        Core.Crypto.ValueHash256 owner = new(new byte[32]);
        TopContractEntry entry = new()
        {
            Owner = owner,
            StorageRoot = default,
            MaxDepth = 5,
            TotalNodes = 100,
            ValueNodes = 50,
            TotalSize = 2000,
            Levels = System.Collections.Immutable.ImmutableArray<TrieLevelStat>.Empty,
            Summary = new TrieLevelStat { Depth = -1, FullNodeCount = 30, ShortNodeCount = 20, ValueNodeCount = 50, TotalSize = 2000 },
        };

        Assert.Multiple(() =>
        {
            Assert.That(entry.Owner, Is.EqualTo(owner));
            Assert.That(entry.Levels.IsDefault, Is.False);
            Assert.That(entry.Summary.TotalSize, Is.EqualTo(2000));
        });
    }

    // --- H-4: Owner hash path tracking ---

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

        Assert.That(c.TopN.TopByDepthCount, Is.EqualTo(1));
        Assert.That(c.TopN.TopByDepth[0].Owner, Is.EqualTo(expectedOwner));
    }

    // --- M-4: TopN eviction tests for TopByNodes and TopByValueNodes ---

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

    [Test]
    public void VisitorCounters_TopN_ByValueNodes_InsertsAndEvictsCorrectly()
    {
        VisitorCounters c = new(topN: 3);

        // Insert 5 contracts with increasing ValueNodes counts
        for (int i = 1; i <= 5; i++)
        {
            byte[] ownerBytes = new byte[32];
            ownerBytes[0] = (byte)i;
            c.BeginStorageTrie(default, new ValueHash256(ownerBytes));
            // i*2 leaf nodes (value nodes)
            for (int j = 0; j < i * 2; j++)
                c.TrackStorageNode(depth: 1, byteSize: 10, isLeaf: true, isBranch: false);
        }

        c.Flush();

        // Top 3 by value nodes should be 10, 8, 6 (from i=5,4,3)
        Assert.That(c.TopN.TopByValueNodesCount, Is.EqualTo(3));

        long[] valueNodes = new long[3];
        for (int i = 0; i < 3; i++)
            valueNodes[i] = c.TopN.TopByValueNodes[i].ValueNodes;

        Array.Sort(valueNodes);
        Assert.That(valueNodes, Is.EqualTo(new long[] { 6, 8, 10 }));
    }
}
