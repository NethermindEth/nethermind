// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.Db.LogIndex.LogIndexStorage;

namespace Nethermind.Db.Test.LogIndex;

/// <summary>
/// Verifies that block numbers above <see cref="int.MaxValue"/> (2^31) are correctly stored,
/// compressed, and retrieved — validating the key-based <c>IsCompressed</c> detection
/// and the <c>uint</c> binary search.
/// </summary>
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class LogIndexHighBlockNumberTests
{
    private string _dbPath = null!;
    private IDbFactory _dbFactory = null!;
    private readonly List<ILogIndexStorage> _storages = [];

    [SetUp]
    public void Setup()
    {
        _dbPath = $"{nameof(LogIndexHighBlockNumberTests)}/{Guid.NewGuid():N}";

        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, true);

        Directory.CreateDirectory(_dbPath);

        var config = new DbConfig();
        var configFactory = new RocksDbConfigFactory(config, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        _dbFactory = new RocksDbFactory(configFactory, config, new HyperClockCacheWrapper(), new TestLogManager(), _dbPath);
    }

    [TearDown]
    public async Task TearDown()
    {
        foreach (ILogIndexStorage storage in _storages)
        {
            await using (storage)
                await storage.StopAsync();
        }

        if (!Directory.Exists(_dbPath))
            return;

        try { Directory.Delete(_dbPath, true); }
        catch { /* ignore */ }
    }

    [OneTimeSetUp]
    public static void RemoveRootFolder()
    {
        if (!Directory.Exists(nameof(LogIndexHighBlockNumberTests)))
            return;

        try { Directory.Delete(nameof(LogIndexHighBlockNumberTests), true); }
        catch { /* ignore */ }
    }

    private ILogIndexStorage CreateStorage(uint compactionDistance = uint.MaxValue)
    {
        LogIndexConfig config = new()
        {
            Enabled = true,
            CompactionDistance = compactionDistance,
            MaxCompressionParallelism = 1,
            MaxReorgDepth = 64,
            CompressionAlgorithm = CompressionAlgorithm.Best.Key
        };

        var storage = new LogIndexStorage(_dbFactory, LimboLogs.Instance, config);
        _storages.Add(storage);
        return storage;
    }

    [TestCase(100u)]
    [TestCase(uint.MaxValue)]
    public async Task Blocks_above_int_max_are_stored_and_retrieved(uint compactionDistance)
    {
        // Block numbers crossing the 2^31 boundary
        const uint startBlock = (uint)int.MaxValue - 50;
        const int blockCount = 100;

        Address address = TestItem.AddressA;
        Hash256 topic = TestItem.KeccakA;

        var storage = CreateStorage(compactionDistance);
        var expectedBlocks = new List<uint>();

        // Build a batch of blocks where every block has a log with our address and topic
        var blocks = new BlockReceipts[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            uint blockNum = startBlock + (uint)i;
            expectedBlocks.Add(blockNum);

            blocks[i] = new BlockReceipts(blockNum,
            [
                new TxReceipt
                {
                    Logs =
                    [
                        Build.A.LogEntry
                            .WithAddress(address)
                            .WithTopics(topic)
                            .TestObject
                    ]
                }
            ]);
        }

        await storage.AddReceiptsAsync(blocks, isBackwardSync: false);

        if (compactionDistance < uint.MaxValue)
            await storage.CompactAsync();

        // Verify address query spanning the full range
        List<uint> addressResult = storage.GetBlockNumbersFor(address, startBlock, startBlock + (uint)blockCount - 1);
        Assert.That(addressResult, Is.EqualTo(expectedBlocks), "Address blocks mismatch");

        // Verify topic query spanning the full range
        List<uint> topicResult = storage.GetBlockNumbersFor(0, topic, startBlock, startBlock + (uint)blockCount - 1);
        Assert.That(topicResult, Is.EqualTo(expectedBlocks), "Topic blocks mismatch");

        // Verify query with from/to fully above 2^31
        uint aboveBoundary = (uint)int.MaxValue + 1;
        uint maxBlock = startBlock + (uint)blockCount - 1;
        List<uint> aboveResult = storage.GetBlockNumbersFor(address, aboveBoundary, maxBlock);
        Assert.That(aboveResult, Is.EqualTo(expectedBlocks.Where(b => b >= aboveBoundary)), "Blocks above 2^31 mismatch");
    }
}
