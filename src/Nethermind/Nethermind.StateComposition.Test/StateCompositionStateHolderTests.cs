// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.IO;
using System.Linq;
using Nethermind.Api;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionStateHolderTests
{
    private string _tempDir = null!;

    private StateCompositionStateHolder CreateHolder(string? dataDir = null)
    {
        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.DataDir.Returns(dataDir ?? _tempDir);

        IStateCompositionConfig config = Substitute.For<IStateCompositionConfig>();
        config.CachePath.Returns("statecomp");

        return new StateCompositionStateHolder(
            initConfig, config, new EthereumJsonSerializer(), LimboLogs.Instance);
    }

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"statecomp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void HasAnyScan_ReturnsFalse_WhenEmpty()
    {
        StateCompositionStateHolder holder = CreateHolder();
        Assert.That(holder.HasAnyScan, Is.False);
    }

    [Test]
    public void GetScan_ReturnsNull_WhenEmpty()
    {
        StateCompositionStateHolder holder = CreateHolder();
        Assert.That(holder.GetScan(null), Is.Null);
        Assert.That(holder.GetScan(100), Is.Null);
    }

    [Test]
    public void StoreScan_MakesScanRetrievable()
    {
        StateCompositionStateHolder holder = CreateHolder();
        StateCompositionStats stats = new() { AccountsTotal = 1000 };
        TrieDepthDistribution dist = new() { AvgAccountPathDepth = 7.5 };

        holder.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(30), stats, dist);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder.HasAnyScan, Is.True);
            Assert.That(holder.HasScan(100), Is.True);
            Assert.That(holder.HasScan(200), Is.False);

            ScanCacheEntry? entry = holder.GetScan(100);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.Value.Stats.AccountsTotal, Is.EqualTo(1000));
            Assert.That(entry.Value.Distribution.AvgAccountPathDepth, Is.EqualTo(7.5));
            Assert.That(entry.Value.Metadata.BlockNumber, Is.EqualTo(100));
            Assert.That(entry.Value.Metadata.IsComplete, Is.True);
        }
    }

    [Test]
    public void GetScan_Null_ReturnsLatestScan()
    {
        StateCompositionStateHolder holder = CreateHolder();

        holder.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(10),
            new StateCompositionStats { AccountsTotal = 1 }, new TrieDepthDistribution());
        holder.StoreScan(300, Keccak.Zero, TimeSpan.FromSeconds(20),
            new StateCompositionStats { AccountsTotal = 3 }, new TrieDepthDistribution());
        holder.StoreScan(200, Keccak.Zero, TimeSpan.FromSeconds(15),
            new StateCompositionStats { AccountsTotal = 2 }, new TrieDepthDistribution());

        ScanCacheEntry? latest = holder.GetScan(null);
        Assert.That(latest!.Value.Stats.AccountsTotal, Is.EqualTo(3),
            "null should return the scan with the highest block number");
    }

    [Test]
    public void ListScans_ReturnsAllScans_OrderedByBlockNumber()
    {
        StateCompositionStateHolder holder = CreateHolder();

        holder.StoreScan(300, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats(), new TrieDepthDistribution());
        holder.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats(), new TrieDepthDistribution());
        holder.StoreScan(200, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats(), new TrieDepthDistribution());

        var scans = holder.ListScans();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scans, Has.Count.EqualTo(3));
            Assert.That(scans[0].BlockNumber, Is.EqualTo(100));
            Assert.That(scans[1].BlockNumber, Is.EqualTo(200));
            Assert.That(scans[2].BlockNumber, Is.EqualTo(300));
        }
    }

    [Test]
    public void StoreScan_PersistsToDisk()
    {
        StateCompositionStateHolder holder = CreateHolder();

        holder.StoreScan(42, Keccak.Zero, TimeSpan.FromSeconds(5),
            new StateCompositionStats { AccountsTotal = 999 }, new TrieDepthDistribution());

        string expectedFile = Path.Combine(_tempDir, "statecomp", "scan-42.json");
        Assert.That(File.Exists(expectedFile), Is.True, "Scan file should be persisted to disk");
    }

    [Test]
    public void Constructor_LoadsPersistedScans()
    {
        // Store a scan with one holder instance
        StateCompositionStateHolder holder1 = CreateHolder();
        holder1.StoreScan(42, Keccak.Zero, TimeSpan.FromSeconds(5),
            new StateCompositionStats { AccountsTotal = 999 },
            new TrieDepthDistribution { AvgAccountPathDepth = 8.0 });

        // Create a new holder reading from the same directory
        StateCompositionStateHolder holder2 = CreateHolder();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder2.HasAnyScan, Is.True);
            Assert.That(holder2.HasScan(42), Is.True);

            ScanCacheEntry? entry = holder2.GetScan(42);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry!.Value.Stats.AccountsTotal, Is.EqualTo(999));
            Assert.That(entry.Value.Distribution.AvgAccountPathDepth, Is.EqualTo(8.0));
        }
    }

    [Test]
    public void Constructor_LoadsMultiplePersistedScans_PicksLatest()
    {
        StateCompositionStateHolder holder1 = CreateHolder();
        holder1.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats { AccountsTotal = 1 }, new TrieDepthDistribution());
        holder1.StoreScan(500, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats { AccountsTotal = 5 }, new TrieDepthDistribution());
        holder1.StoreScan(300, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats { AccountsTotal = 3 }, new TrieDepthDistribution());

        StateCompositionStateHolder holder2 = CreateHolder();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder2.ListScans(), Has.Count.EqualTo(3));
            ScanCacheEntry? latest = holder2.GetScan(null);
            Assert.That(latest!.Value.Stats.AccountsTotal, Is.EqualTo(5),
                "Latest should be block 500");
        }
    }

    [Test]
    public void StoreScan_OverwritesExistingBlockEntry()
    {
        StateCompositionStateHolder holder = CreateHolder();

        holder.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(1),
            new StateCompositionStats { AccountsTotal = 1 }, new TrieDepthDistribution());
        holder.StoreScan(100, Keccak.Zero, TimeSpan.FromSeconds(2),
            new StateCompositionStats { AccountsTotal = 2 }, new TrieDepthDistribution());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(holder.ListScans(), Has.Count.EqualTo(1));
            Assert.That(holder.GetScan(100)!.Value.Stats.AccountsTotal, Is.EqualTo(2));
        }
    }
}
