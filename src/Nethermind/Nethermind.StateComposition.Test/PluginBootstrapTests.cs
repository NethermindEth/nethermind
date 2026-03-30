// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
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
        Assert.That(plugin.Author, Is.EqualTo("Nethermind/StateBenchmarks"));
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
    public void StateCompositionStats_FromTrieStats_SetsBlockAndStateRoot()
    {
        // TrieStats internal fields cannot be set from outside Nethermind.Trie;
        // verify FromTrieStats correctly wires BlockNumber, StateRoot, and
        // maps all zero-valued counters from a default TrieStats.
        TrieStats trieStats = new();
        Hash256 stateRoot = Keccak.EmptyTreeHash;

        StateCompositionStats stats = StateCompositionStats.FromTrieStats(trieStats, 42, stateRoot);

        Assert.Multiple(() =>
        {
            Assert.That(stats.BlockNumber, Is.EqualTo(42));
            Assert.That(stats.StateRoot, Is.EqualTo(stateRoot));
            Assert.That(stats.AccountsTotal, Is.EqualTo(trieStats.AccountCount));
            Assert.That(stats.ContractsTotal, Is.EqualTo(trieStats.CodeCount));
            Assert.That(stats.StorageSlotsTotal, Is.EqualTo(trieStats.StorageLeafCount));
            Assert.That(stats.AccountTrieBranchNodes, Is.EqualTo(trieStats.StateBranchCount));
            Assert.That(stats.AccountTrieExtensionNodes, Is.EqualTo(trieStats.StateExtensionCount));
            Assert.That(stats.AccountTrieLeafNodes, Is.EqualTo(trieStats.AccountCount));
            Assert.That(stats.StorageTrieBranchNodes, Is.EqualTo(trieStats.StorageBranchCount));
            Assert.That(stats.StorageTrieExtensionNodes, Is.EqualTo(trieStats.StorageExtensionCount));
            Assert.That(stats.StorageTrieLeafNodes, Is.EqualTo(trieStats.StorageLeafCount));
            Assert.That(stats.AccountTrieNodeBytes, Is.EqualTo(trieStats.StateSize));
            Assert.That(stats.StorageTrieNodeBytes, Is.EqualTo(trieStats.StorageSize));
            Assert.That(stats.TotalCodeSize, Is.EqualTo(trieStats.CodeSize));
        });
    }

    [Test]
    public void StateCompositionStats_FromTrieStats_NullStateRoot()
    {
        TrieStats trieStats = new();

        StateCompositionStats stats = StateCompositionStats.FromTrieStats(trieStats, 99, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.BlockNumber, Is.EqualTo(99));
            Assert.That(stats.StateRoot, Is.Null);
        });
    }

    [Test]
    public void StateCompositionStats_HasByteSizeFields()
    {
        StateCompositionStats stats = new()
        {
            AccountBytes = 100,
            StorageBytes = 200,
            AccountTrieNodeBytes = 300,
            StorageTrieNodeBytes = 400,
        };

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountBytes, Is.EqualTo(100));
            Assert.That(stats.StorageBytes, Is.EqualTo(200));
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

}
