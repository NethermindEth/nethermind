// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture(10, 100)]
    [TestFixture(5, 200)]
    [TestFixture(100, 100, Explicit = true)]
    [TestFixture(100, 200, Explicit = true)]
    // TODO: test for different block ranges intersection
    // TODO: run internal state verification for each test
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class LogIndexStorageTests(int batchCount, int blocksPerBatch)
    {
        private readonly TestData _testData = GenerateTestData(new Random(42), batchCount, blocksPerBatch);

        private ILogger _logger;
        private string _dbPath = null!;
        private IDbFactory _dbFactory = null!;

        private LogIndexStorage CreateLogIndexStorage(int compactionDistance = 262_144, byte ioParallelism = 16, IDbFactory? dbFactory = null)
        {
            return new(dbFactory ?? _dbFactory, _logger, ioParallelism, compactionDistance);
        }

        [SetUp]
        public void Setup()
        {
            _logger = LimboLogs.Instance.GetClassLogger();
            _dbPath = $"{nameof(LogIndexStorageTests)}/{Guid.NewGuid():N}";

            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);

            Directory.CreateDirectory(_dbPath);

            _dbFactory = new RocksDbFactory(new DbConfig(), new TestLogManager(), _dbPath);
        }

        [TearDown]
        public void RemoveDbFolder()
        {
            if (!Directory.Exists(_dbPath))
                return;


            try
            {
                Directory.Delete(_dbPath, true);
            }
            catch
            {
                // ignore
            }
        }

        [OneTimeTearDown]
        [OneTimeSetUp]
        public static void RemoveRootFolder()
        {
            if (!Directory.Exists(nameof(LogIndexStorageTests)))
                return;

            try
            {
                Directory.Delete(nameof(LogIndexStorageTests), true);
            }
            catch
            {
                // ignore
            }
        }

        [Combinatorial]
        public async Task Set_Get_Test(
            [Values(100, 200, int.MaxValue)] int compactionDistance,
            [Values(1, 8, 16)] byte ioParallelism,
            [Values] bool isBackwardsSync
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance, ioParallelism);

            BlockReceipts[][] batches = isBackwardsSync ? Reverse(_testData.Batches) : _testData.Batches;
            await SetReceiptsAsync(logIndexStorage, batches, isBackwardsSync);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task BackwardsSet_Set_Get_Test(
            [Values(100, 200, int.MaxValue)] int compactionDistance,
            [Values(1, 8, 16)] byte ioParallelism
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance, ioParallelism);

            var half = _testData.Batches.Length / 2;
            await SetReceiptsAsync(logIndexStorage, Reverse(_testData.Batches.Take(half)), isBackwardsSync: true);
            await SetReceiptsAsync(logIndexStorage, _testData.Batches.Skip(half), isBackwardsSync: false);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_ReorgLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] reorgBlocks = _testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            VerifyReceipts(logIndexStorage, _testData, excludedBlocks: reorgBlocks);
        }

        [Combinatorial]
        public async Task Set_ReorgAndSetLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] reorgBlocks = _testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
            {
                await logIndexStorage.ReorgFrom(block);
                await logIndexStorage.SetReceiptsAsync([block], false);
            }

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_ReorgLast_SetLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] reorgBlocks = _testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();

            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            await logIndexStorage.SetReceiptsAsync(reorgBlocks, false);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_ReorgUnexisting_Get_Test(
            [Values(1, 5)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] reorgBlocks = GenerateBlocks(new Random(42), _testData.Batches[^1][^1].BlockNumber, reorgDepth);
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            VerifyReceipts(logIndexStorage, _testData, excludedBlocks: reorgBlocks);
        }

        [Ignore("Not working yet.")]
        [Combinatorial]
        public async Task Set_Compact_ReorgLast_Get_Test(
            [Values(1, 5)] int reorgDepth
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage();

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);
            logIndexStorage.Compact();

            BlockReceipts[] reorgBlocks = _testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            var lastBlock = _testData.Batches[^1][^1].BlockNumber;
            VerifyReceipts(logIndexStorage, _testData, maxBlock: lastBlock - reorgDepth);
        }

        [Combinatorial]
        public async Task Set_PeriodicReorg_Get_Test(
            [Values(10, 70)] int reorgFrequency,
            [Values(1, 5)] int maxReorgDepth,
            [Values] bool compactAfter
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage();

            var random = new Random(42);
            var allReorgBlocks = new List<BlockReceipts>();
            var allAddedBlocks = new List<BlockReceipts>();

            foreach (BlockReceipts[][] batches in _testData.Batches.GroupBy(b => b[0].BlockNumber / reorgFrequency).Select(g => g.ToArray()))
            {
                await SetReceiptsAsync(logIndexStorage, batches);

                var reorgDepth = random.Next(1, maxReorgDepth);
                BlockReceipts[] reorgBlocks = batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
                BlockReceipts[] addedBlocks = GenerateBlocks(random, reorgBlocks.First().BlockNumber, reorgBlocks.Length);

                allReorgBlocks.AddRange(reorgBlocks);
                allAddedBlocks.AddRange(addedBlocks);

                foreach (BlockReceipts block in reorgBlocks)
                    await logIndexStorage.ReorgFrom(block);

                if (compactAfter)
                    logIndexStorage.Compact();

                await logIndexStorage.SetReceiptsAsync(addedBlocks, false);
            }

            VerifyReceipts(logIndexStorage, _testData, excludedBlocks: allReorgBlocks, addedBlocks: allAddedBlocks);
        }

        [Ignore("Not supported, but probably is not needed.")]
        [Combinatorial]
        public async Task Set_ConsecutiveReorgsLast_Get_Test(
            [Values(new[] { 2, 1 }, new[] { 1, 2 })] int[] reorgDepths,
            [Values] bool compactBetween
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage();

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            var testBlocks = _testData.Batches.SelectMany(b => b).ToArray();

            foreach (var reorgDepth in reorgDepths)
            {
                foreach (BlockReceipts block in testBlocks.TakeLast(reorgDepth).ToArray())
                    await logIndexStorage.ReorgFrom(block);

                if (compactBetween)
                    logIndexStorage.Compact();
            }

            VerifyReceipts(logIndexStorage, _testData, excludedBlocks: testBlocks.TakeLast(reorgDepths.Max()).ToArray());
        }

        [Combinatorial]
        public async Task SetMultiInstance_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            var half = _testData.Batches.Length / 2;

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, _testData.Batches.Take(half));

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, _testData.Batches.Skip(half));

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task RepeatedSet_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);
            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task RepeatedSetMultiInstance_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_NewInstance_Get_Test(
            [Values(1, 8)] int ioParallelism,
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public async Task Set_ConcurrentGet_Test(
            [Values(1, 8)] int ioParallelism,
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            using var getCancellation = new CancellationTokenSource();
            var token = getCancellation.Token;

            var getThreads = new[]
            {
                new Thread(() => GetBlockNumbersLoop(new Random(42), logIndexStorage, _testData, token)),
                new Thread(() => GetBlockNumbersLoop(new Random(4242), logIndexStorage, _testData, token)),
                new Thread(() => GetBlockNumbersLoop(new Random(424242), logIndexStorage, _testData, token)),
            };
            getThreads.ForEach(t => t.Start());

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            await getCancellation.CancelAsync();
            getThreads.ForEach(t => t.Join());

            VerifyReceipts(logIndexStorage, _testData);
        }

        private static TestData GenerateTestData(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
        {
            var testData = new TestData(batchCount, blocksPerBatch);

            var blocksCount = batchCount * blocksPerBatch;
            var addresses = Enumerable.Repeat(0, Math.Max(10, blocksCount / 5))
            //var addresses = Enumerable.Repeat(0, 1)
                .Select(_ => new Address(random.NextBytes(Address.Size)))
                .ToArray();
            var topics = Enumerable.Repeat(0, addresses.Length * 10)
            //var topics = Enumerable.Repeat(0, 0)
                .Select(_ => new Hash256(random.NextBytes(Hash256.Size)))
                .ToArray();

            // Generate batches
            var blockNum = startNum;
            foreach (BlockReceipts[] batch in testData.Batches)
            {
                for (var i = 0; i < batch.Length; i++)
                {
                    batch[i] = new(blockNum++, GenerateReceipts(random, addresses, topics));
                }
            }

            var maps = GenerateMaps(testData.Batches.SelectMany(b => b));
            testData.AddressMap = maps.address;
            testData.TopicMap = maps.topic;

            return testData;
        }

        private static (Dictionary<Address, HashSet<int>> address, Dictionary<Hash256, HashSet<int>> topic) GenerateMaps(IEnumerable<BlockReceipts> blocks)
        {
            var excludedAddresses = new Dictionary<Address, HashSet<int>>();
            var excludedTopics = new Dictionary<Hash256, HashSet<int>>();

            foreach (var block in blocks)
            foreach (var txReceipt in block.Receipts)
            foreach (var log in txReceipt.Logs!)
            {
                var addressMap = excludedAddresses.GetOrAdd(log.Address, _ => []);
                addressMap.Add(block.BlockNumber);

                foreach (var topic in log.Topics)
                {
                    var topicMap = excludedTopics.GetOrAdd(topic, _ => []);
                    topicMap.Add(block.BlockNumber);
                }
            }

            return (excludedAddresses, excludedTopics);
        }

        private static TxReceipt[] GenerateReceipts(Random random, Address[] addresses, Hash256[] topics)
        {
            (int min, int max) logsPerBlock = (0, 200);
            (int min, int max) logsPerTx = (0, 10);

            LogEntry[] logs = Enumerable
                .Repeat(0, random.Next(logsPerBlock.min, logsPerBlock.max + 1))
                .Select(_ => Build.A.LogEntry
                    .WithAddress(random.NextValue(addresses))
                    .WithTopics(topics.Length == 0
                            ? []
                            : Enumerable.Repeat(0, random.Next(4)).Select(_ => random.NextValue(topics)).ToArray()
                    ).TestObject
                ).ToArray();

            var receipts = new List<TxReceipt>();
            for (var i = 0; i < logs.Length;)
            {
                var count = random.Next(logsPerTx.min, Math.Min(logsPerTx.max, logs.Length - i) + 1);
                var range = i..(i + count);

                receipts.Add(new() { Logs = logs[range] });
                i = range.End.Value;
            }

            return receipts.ToArray();
        }

        private static BlockReceipts[] GenerateBlocks(Random random, int from, int count) => GenerateTestData(
            random,
            batchCount: 1, blocksPerBatch: count,
            startNum: from
        ).Batches[0];

        private async Task SetReceiptsAsync(ILogIndexStorage logIndexStorage, IEnumerable<BlockReceipts[]> batches, bool isBackwardsSync = false)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var totalStats = new SetReceiptsStats();
            var count = 0;
            foreach (BlockReceipts[] batch in batches)
            {
                count++;
                SetReceiptsStats stats = await logIndexStorage.SetReceiptsAsync(batch, isBackwardsSync);
                totalStats.Combine(stats);
            }

            //totalStats.Combine(logIndexStorage.Compact());

            // Log statistics
            await TestContext.Out.WriteLineAsync($"{nameof(LogIndexStorage.SetReceiptsAsync)}[{count}] in {Stopwatch.GetElapsedTime(timestamp)}:" +
                $"\n\tTxs: +{totalStats.TxAdded:N0}" +
                $"\n\tLogs: +{totalStats.LogsAdded:N0}" +
                $"\n\tTopics: +{totalStats.TopicsAdded:N0}" +
                "\n" +
                $"\n\tWaiting batch: {totalStats.WaitingBatch}" +
                $"\n\tKeys per batch: {totalStats.KeysCount:N0}" +
                $"\n\tBuilding dictionary: {totalStats.BuildingDictionary}" +
                $"\n\tProcessing: {totalStats.Processing}" +
                $"\n\tMerge call: {totalStats.CallingMerge}" +
                $"\n\tIn-memory merging: {totalStats.InMemoryMerging}" +
                "\n" +
                $"\n\tFlushing DBs: {totalStats.FlushingDbs}" +
                $"\n\tCompacting DBs: {totalStats.CompactingDbs}" +
                $"\n\tPost-merge processing: {totalStats.PostMergeProcessing.Execution}" +
                $"\n\t\tDB getting: {totalStats.PostMergeProcessing.GettingValue}" +
                $"\n\t\tCompressing: {totalStats.PostMergeProcessing.CompressingValue}" +
                $"\n\t\tPutting: {totalStats.PostMergeProcessing.PuttingValues}" +
                $"\n\t\tCompressed keys: {totalStats.PostMergeProcessing.CompressedAddressKeys:N0} address, {totalStats.PostMergeProcessing.CompressedTopicKeys:N0} topic" +
                $"\n\tDB size: {LogIndexMigration.GetFolderSize(Path.Combine(_dbPath, DbNames.LogIndex))}");
        }

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
            Dictionary<Address, HashSet<int>>? excludedAddresses = null,
            Dictionary<Hash256, HashSet<int>>? excludedTopics = null,
            HashSet<int>? excludedBlockNums = null,
            Dictionary<Address, HashSet<int>>? addedAddresses = null,
            Dictionary<Hash256, HashSet<int>>? addedTopics = null
        )
        {
            foreach (var (address, nums) in testData.AddressMap)
            {
                IEnumerable<int> expectedNums = nums;

                if (excludedAddresses != null && excludedAddresses.TryGetValue(address, out HashSet<int> addressExcludedBlocks))
                    expectedNums = expectedNums.Except(addressExcludedBlocks);

                if (excludedBlockNums != null)
                    expectedNums = expectedNums.Except(excludedBlockNums);

                if (addedAddresses != null && addedAddresses.TryGetValue(address, out HashSet<int> addressAddedBlocks))
                    expectedNums = expectedNums.Concat(addressAddedBlocks);

                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue),
                    Is.EqualTo(expectedNums.Order()),
                    $"Address: {address}"
                );
            }

            foreach (var (topic, nums) in testData.TopicMap)
            {
                IEnumerable<int> expectedNums = nums;

                if (excludedTopics != null && excludedTopics.TryGetValue(topic, out HashSet<int> topicExcludedBlocks))
                    expectedNums = expectedNums.Except(topicExcludedBlocks);

                if (excludedBlockNums != null)
                    expectedNums = expectedNums.Except(excludedBlockNums);

                if (addedTopics != null && addedTopics.TryGetValue(topic, out HashSet<int> topicAddedBlocks))
                    expectedNums = expectedNums.Concat(topicAddedBlocks);

                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue),
                    Is.EqualTo(expectedNums.Order()),
                    $"Topic: {topic}"
                );
            }
        }

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData, int maxBlock) => VerifyReceipts(
            logIndexStorage, testData,
            excludedBlockNums: Enumerable.Range(maxBlock + 1, testData.Batches[^1][^1].BlockNumber - maxBlock).ToHashSet()
        );

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
            IEnumerable<BlockReceipts>? excludedBlocks, IEnumerable<BlockReceipts>? addedBlocks = null)
        {
            var excludeMaps = excludedBlocks == null ? default : GenerateMaps(excludedBlocks);
            var addMaps = addedBlocks == null ? default : GenerateMaps(addedBlocks);

            VerifyReceipts(
                logIndexStorage, testData,
                excludedAddresses: excludeMaps.address, excludedTopics: excludeMaps.topic,
                addedAddresses: addMaps.address, addedTopics: addMaps.topic
            );
        }

        private static void GetBlockNumbersLoop(Random random, ILogIndexStorage logIndexStorage, TestData testData,
            CancellationToken cancellationToken)
        {
            var addresses = testData.AddressMap.Keys.ToArray();
            var topics = testData.TopicMap.Keys.ToArray();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (addresses.Length != 0)
                {
                    var address = random.NextValue(addresses);
                    logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue);
                }

                if (topics.Length != 0)
                {
                    var topic = random.NextValue(topics);
                    logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue);
                }
            }
        }

        private static BlockReceipts[][] Reverse(IEnumerable<BlockReceipts[]> batches)
        {
            var length = batches.Count();
            var result = new BlockReceipts[length][];

            var index = 0;
            foreach (BlockReceipts[] batch in batches.Reverse())
                result[index++] = batch.Reverse().ToArray();

            return result;
        }

        private class TestData
        {
            public readonly BlockReceipts[][] Batches;
            public Dictionary<Address, HashSet<int>> AddressMap = new();
            public Dictionary<Hash256, HashSet<int>> TopicMap = new();

            public TestData(int batchCount, int blocksPerBatch)
            {
                Batches = new BlockReceipts[batchCount][];

                for (var i = 0; i < Batches.Length; i++)
                    Batches[i] = new BlockReceipts[blocksPerBatch];
            }

            public override string ToString() => $"{Batches.Length} * {Batches[0].Length} blocks";
        }
    }
}
