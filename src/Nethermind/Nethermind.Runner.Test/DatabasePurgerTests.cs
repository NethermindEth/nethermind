// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

public class DatabasePurgerTests
{
    private string _tempDir = null!;
    private readonly ILogger _logger = LimboLogs.Instance.GetClassLogger();

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nethermind-test-{Path.GetRandomFileName()}");
        CreateTestDbLayout(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Test]
    public void ForceResync_preserves_peer_and_discovery_directories()
    {
        DatabasePurger.Purge(_tempDir, preserveNetwork: true, _logger);

        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.PeersDb)), Is.True, "peers should be preserved");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.DiscoveryNodes)), Is.True, "discoveryNodes should be preserved");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.DiscoveryV5Nodes)), Is.True, "discoveryV5Nodes should be preserved");
    }

    [Test]
    public void ForceResync_deletes_chain_databases()
    {
        DatabasePurger.Purge(_tempDir, preserveNetwork: true, _logger);

        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.State)), Is.False, "state should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Blocks)), Is.False, "blocks should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Headers)), Is.False, "headers should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Receipts)), Is.False, "receipts should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.BlockInfos)), Is.False, "blockInfos should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.BlockNumbers)), Is.False, "blockNumbers should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.BlockAccessLists)), Is.False, "blockAccessLists should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Metadata)), Is.False, "metadata should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Flat)), Is.False, "flat should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Code)), Is.False, "code should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.Bloom)), Is.False, "bloom should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.BadBlocks)), Is.False, "badBlocks should be deleted");
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.BlobTransactions)), Is.False, "blobTransactions should be deleted");
    }

    [Test]
    public void ForceResync_deletes_loose_files()
    {
        File.WriteAllText(Path.Combine(_tempDir, "LOCK"), "");
        File.WriteAllText(Path.Combine(_tempDir, "CURRENT"), "");

        DatabasePurger.Purge(_tempDir, preserveNetwork: true, _logger);

        Assert.That(Directory.EnumerateFiles(_tempDir), Is.Empty, "loose files should be deleted");
    }

    [Test]
    public void PurgeDb_deletes_entire_directory()
    {
        DatabasePurger.Purge(_tempDir, preserveNetwork: false, _logger);

        Assert.That(Directory.Exists(_tempDir), Is.False, "entire directory should be deleted");
    }

    [Test]
    public void PurgeDb_deletes_network_directories()
    {
        DatabasePurger.Purge(_tempDir, preserveNetwork: false, _logger);

        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.PeersDb)), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_tempDir, DbNames.DiscoveryNodes)), Is.False);
    }

    [Test]
    public void Purge_does_nothing_for_nonexistent_directory()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Path.GetRandomFileName()}");

        Assert.DoesNotThrow(() => DatabasePurger.Purge(missing, preserveNetwork: false, _logger));
        Assert.DoesNotThrow(() => DatabasePurger.Purge(missing, preserveNetwork: true, _logger));
    }

    private static void CreateTestDbLayout(string basePath)
    {
        // Chain databases
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.State));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Blocks));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Headers));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Receipts));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.BlockInfos));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.BlockNumbers));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Metadata));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Flat));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Code));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.Bloom));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.BadBlocks));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.BlobTransactions));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.BlockAccessLists));

        // Network databases (should be preserved by --force-resync)
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.PeersDb));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.DiscoveryNodes));
        Directory.CreateDirectory(Path.Combine(basePath, DbNames.DiscoveryV5Nodes));

        // Add a dummy file in each to make them non-empty
        foreach (string dir in Directory.EnumerateDirectories(basePath))
        {
            File.WriteAllText(Path.Combine(dir, "dummy.dat"), "test");
        }
    }
}
