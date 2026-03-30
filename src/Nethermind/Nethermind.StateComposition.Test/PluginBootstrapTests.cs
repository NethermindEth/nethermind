// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
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
    public void TrieLevelStat_HasByteSize()
    {
        TrieLevelStat stat = new()
        {
            Depth = 3,
            BranchNodes = 10,
            ExtensionNodes = 5,
            LeafNodes = 20,
            ByteSize = 1500,
        };

        Assert.That(stat.ByteSize, Is.EqualTo(1500));
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
            AccountBranches = 5,
            StorageLeaves = 20,
            AccountNodeBytes = 100,
            TotalBranchChildren = 40,
            TotalBranchNodes = 5,
        };
        a.AccountDepths[2].AddBranch(50);

        VisitorCounters b = new()
        {
            AccountsTotal = 15,
            ContractsTotal = 7,
            AccountBranches = 8,
            StorageLeaves = 30,
            AccountNodeBytes = 200,
            TotalBranchChildren = 60,
            TotalBranchNodes = 8,
        };
        b.AccountDepths[2].AddBranch(75);

        a.MergeFrom(b);

        Assert.Multiple(() =>
        {
            Assert.That(a.AccountsTotal, Is.EqualTo(25));
            Assert.That(a.ContractsTotal, Is.EqualTo(10));
            Assert.That(a.AccountBranches, Is.EqualTo(13));
            Assert.That(a.StorageLeaves, Is.EqualTo(50));
            Assert.That(a.AccountNodeBytes, Is.EqualTo(300));
            Assert.That(a.TotalBranchChildren, Is.EqualTo(100));
            Assert.That(a.TotalBranchNodes, Is.EqualTo(13));
            Assert.That(a.AccountDepths[2].Branches, Is.EqualTo(2));
            Assert.That(a.AccountDepths[2].ByteSize, Is.EqualTo(125));
        });
    }

    [Test]
    public void DepthCounter_AddBranch_UpdatesCountAndSize()
    {
        DepthCounter counter = new();

        counter.AddBranch(100);
        counter.AddBranch(200);

        Assert.Multiple(() =>
        {
            Assert.That(counter.Branches, Is.EqualTo(2));
            Assert.That(counter.ByteSize, Is.EqualTo(300));
            Assert.That(counter.Extensions, Is.EqualTo(0));
            Assert.That(counter.Leaves, Is.EqualTo(0));
        });
    }

    [Test]
    public void DepthCounter_AddExtensionAndLeaf()
    {
        DepthCounter counter = new();

        counter.AddExtension(50);
        counter.AddLeaf(75);

        Assert.Multiple(() =>
        {
            Assert.That(counter.Extensions, Is.EqualTo(1));
            Assert.That(counter.Leaves, Is.EqualTo(1));
            Assert.That(counter.ByteSize, Is.EqualTo(125));
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
            BranchNodes = 10,
            ExtensionNodes = 5,
            LeafNodes = 20,
            ByteSize = 500,
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
            c.BeginStorageTrie(new Core.Crypto.ValueHash256(new byte[32]));
            c.TrackStorageNode(depth: i * 2, byteSize: 100, isLeaf: true);
        }

        c.Flush();

        // Top 3 by depth should be depths 10, 8, 6 (the three largest)
        Assert.That(c.TopByDepthCount, Is.EqualTo(3));

        int[] depths = new int[c.TopByDepthCount];
        for (int i = 0; i < c.TopByDepthCount; i++)
            depths[i] = c.TopByDepth[i].MaxDepth;

        Array.Sort(depths);
        Assert.That(depths, Is.EqualTo(new[] { 6, 8, 10 }));
    }

    [Test]
    public void VisitorCounters_StorageMaxDepthHistogram_TracksCorrectly()
    {
        VisitorCounters c = new();

        // 3 contracts with storage at max depth 2, 5, 5
        c.BeginStorageTrie(default);
        c.TrackStorageNode(depth: 2, byteSize: 10, isLeaf: true);

        c.BeginStorageTrie(default);
        c.TrackStorageNode(depth: 5, byteSize: 10, isLeaf: true);

        c.BeginStorageTrie(default);
        c.TrackStorageNode(depth: 5, byteSize: 10, isLeaf: true);

        c.Flush();

        Assert.Multiple(() =>
        {
            Assert.That(c.StorageMaxDepthHistogram[2], Is.EqualTo(1));
            Assert.That(c.StorageMaxDepthHistogram[5], Is.EqualTo(2));
        });
    }

    [Test]
    public void VisitorCounters_Flush_NoOp_WhenNoActiveTrie()
    {
        VisitorCounters c = new();
        c.Flush(); // Should not throw

        Assert.That(c.TopByDepthCount, Is.EqualTo(0));
    }

    [Test]
    public void VisitorCounters_MergeFrom_MergesTopN()
    {
        VisitorCounters a = new(topN: 5);
        a.BeginStorageTrie(default);
        a.TrackStorageNode(depth: 10, byteSize: 100, isLeaf: true);
        a.Flush();

        VisitorCounters b = new(topN: 5);
        b.BeginStorageTrie(default);
        b.TrackStorageNode(depth: 20, byteSize: 200, isLeaf: true);
        b.Flush();

        a.MergeFrom(b);

        Assert.That(a.TopByDepthCount, Is.EqualTo(2));
    }
}
