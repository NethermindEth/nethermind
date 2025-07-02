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
using NUnit.Framework.Interfaces;

namespace Nethermind.Db.Test.LogIndex
{
    // TODO: test for different block ranges intersection
    // TODO: run internal state verification for each test
    // TODO: test for process crash via Thread.Abort
    // TODO: test for reorg out-of-order
    [TestFixtureSource(nameof(TestCases))]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class LogIndexStorageComplexTests(LogIndexStorageComplexTests.TestData testData)
    {
        public static readonly TestFixtureData[] TestCases = new[]
        {
            (batchCount: 10, blocksPerBatch: 100, isExplicit: false),
            (batchCount: 5, blocksPerBatch: 200, isExplicit: false),
            (batchCount: 100, blocksPerBatch: 100, isExplicit: true),
            (batchCount: 100, blocksPerBatch: 200, isExplicit: true)
        }.Select(x => new TestFixtureData(new TestData(new Random(42), x.batchCount, x.blocksPerBatch))
        { RunState = x.isExplicit ? RunState.Explicit : RunState.Runnable }
        ).ToArray();

        private ILogger _logger;
        private string _dbPath = null!;
        private IDbFactory _dbFactory = null!;
        private readonly List<ILogIndexStorage> _createdStorages = [];

        private LogIndexStorage CreateLogIndexStorage(int compactionDistance = 262_144, int ioParallelism = 16, int maxReorgDepth = 32, IDbFactory? dbFactory = null)
        {
            LogIndexStorage storage = new(dbFactory ?? _dbFactory, _logger, ioParallelism, compactionDistance, maxReorgDepth);
            _createdStorages.Add(storage);
            return storage;
        }

        [SetUp]
        public void Setup()
        {
            _logger = LimboLogs.Instance.GetClassLogger();
            _dbPath = $"{nameof(LogIndexStorageComplexTests)}/{Guid.NewGuid():N}";

            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);

            Directory.CreateDirectory(_dbPath);

            _dbFactory = new RocksDbFactory(new DbConfig(), new TestLogManager(), _dbPath);
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (ILogIndexStorage storage in _createdStorages)
            {
                await using (storage)
                    await storage.StopAsync();
            }

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
            if (!Directory.Exists(nameof(LogIndexStorageComplexTests)))
                return;

            try
            {
                Directory.Delete(nameof(LogIndexStorageComplexTests), true);
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
            [Values] bool isBackwardsSync,
            [Values] bool compact
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance, ioParallelism);

            BlockReceipts[][] batches = isBackwardsSync ? Reverse(testData.Batches) : testData.Batches;
            await SetReceiptsAsync(logIndexStorage, batches, isBackwardsSync);

            if (compact)
                await CompactAsync(logIndexStorage);

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task BackwardsSet_Set_Get_Test(
            [Values(100, 200, int.MaxValue)] int compactionDistance,
            [Values(1, 8, 16)] byte ioParallelism
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance, ioParallelism);

            var half = testData.Batches.Length / 2;
            await SetReceiptsAsync(logIndexStorage, Reverse(testData.Batches.Take(half)), isBackwardsSync: true);
            await SetReceiptsAsync(logIndexStorage, testData.Batches.Skip(half), isBackwardsSync: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(logIndexStorage.GetMinBlockNumber(), Is.Zero);
                Assert.That(logIndexStorage.GetMaxBlockNumber(), Is.EqualTo(testData.Batches[^1][^1].BlockNumber));
            }

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task Set_ReorgLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            BlockReceipts[] reorgBlocks = testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            VerifyReceipts(logIndexStorage, testData, excludedBlocks: reorgBlocks, maxBlock: reorgBlocks[0].BlockNumber - 1);
        }

        [Combinatorial]
        public async Task Set_ReorgAndSetLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            BlockReceipts[] reorgBlocks = testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
            {
                await logIndexStorage.ReorgFrom(block);
                await logIndexStorage.SetReceiptsAsync([block], false);
            }

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task Set_ReorgLast_SetLast_Get_Test(
            [Values(1, 5, 20)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            BlockReceipts[] reorgBlocks = testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();

            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            await logIndexStorage.SetReceiptsAsync(reorgBlocks, false);

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task Set_ReorgUnexisting_Get_Test(
            [Values(1, 5)] int reorgDepth,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            var lastBlock = testData.Batches[^1][^1].BlockNumber;
            BlockReceipts[] reorgBlocks = GenerateBlocks(new Random(42), lastBlock - reorgDepth + 1, reorgDepth);
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            // Need custom check because Reorg updates the last block even if it's "unexisting"
            Assert.That(logIndexStorage.GetMaxBlockNumber(), Is.EqualTo(lastBlock - reorgDepth));

            VerifyReceipts(logIndexStorage, testData, excludedBlocks: reorgBlocks, validateMinMax: false);
        }

        [TestCase(1, 1)]
        [TestCase(32, 64)]
        [TestCase(64, 64)]
        [TestCase(65, 64, Explicit = true)]
        public async Task Set_Compact_ReorgLast_Get_Test(int reorgDepth, int maxReorgDepth)
        {
            var logIndexStorage = CreateLogIndexStorage(maxReorgDepth: maxReorgDepth);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);
            await CompactAsync(logIndexStorage);

            BlockReceipts[] reorgBlocks = testData.Batches.SelectMany(b => b).TakeLast(reorgDepth).ToArray();
            foreach (BlockReceipts block in reorgBlocks)
                await logIndexStorage.ReorgFrom(block);

            var lastBlock = testData.Batches[^1][^1].BlockNumber;
            VerifyReceipts(logIndexStorage, testData, maxBlock: lastBlock - reorgDepth);
        }

        [Combinatorial]
        public async Task Set_PeriodicReorg_Get_Test(
            [Values(10, 70)] int reorgFrequency,
            [Values(1, 5)] int maxReorgDepth,
            [Values] bool compactAfter
        )
        {
            var logIndexStorage = CreateLogIndexStorage();

            var random = new Random(42);
            var allReorgBlocks = new List<BlockReceipts>();
            var allAddedBlocks = new List<BlockReceipts>();

            foreach (BlockReceipts[][] batches in testData.Batches.GroupBy(b => b[0].BlockNumber / reorgFrequency).Select(g => g.ToArray()))
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
                    await CompactAsync(logIndexStorage);

                await logIndexStorage.SetReceiptsAsync(addedBlocks, false);
            }

            VerifyReceipts(logIndexStorage, testData, excludedBlocks: allReorgBlocks, addedBlocks: allAddedBlocks);
        }

        [Ignore("Not supported, but probably is not needed.")]
        [Combinatorial]
        public async Task Set_ConsecutiveReorgsLast_Get_Test(
            [Values(new[] { 2, 1 }, new[] { 1, 2 })] int[] reorgDepths,
            [Values] bool compactBetween
        )
        {
            var logIndexStorage = CreateLogIndexStorage();

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            var testBlocks = testData.Batches.SelectMany(b => b).ToArray();

            foreach (var reorgDepth in reorgDepths)
            {
                foreach (BlockReceipts block in testBlocks.TakeLast(reorgDepth).ToArray())
                    await logIndexStorage.ReorgFrom(block);

                if (compactBetween)
                    await CompactAsync(logIndexStorage);
            }

            VerifyReceipts(logIndexStorage, testData, excludedBlocks: testBlocks.TakeLast(reorgDepths.Max()).ToArray());
        }

        [Combinatorial]
        public async Task SetMultiInstance_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            var half = testData.Batches.Length / 2;

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, testData.Batches.Take(half));

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, testData.Batches.Skip(half));

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task RepeatedSet_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, testData.Batches);
            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task RepeatedSetMultiInstance_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task Set_NewInstance_Get_Test(
            [Values(1, 8)] int ioParallelism,
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                await SetReceiptsAsync(logIndexStorage, testData.Batches);

            await using (var logIndexStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public async Task Set_ConcurrentGet_Test(
            [Values(1, 8)] int ioParallelism,
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            using var getCancellation = new CancellationTokenSource();
            var token = getCancellation.Token;

            var getThreads = new[]
            {
                new Thread(() => GetBlockNumbersLoop(new Random(42), logIndexStorage, testData, token)),
                new Thread(() => GetBlockNumbersLoop(new Random(4242), logIndexStorage, testData, token)),
                new Thread(() => GetBlockNumbersLoop(new Random(424242), logIndexStorage, testData, token)),
            };
            getThreads.ForEach(t => t.Start());

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            await getCancellation.CancelAsync();
            getThreads.ForEach(t => t.Join());

            VerifyReceipts(logIndexStorage, testData);
        }

        private static BlockReceipts[] GenerateBlocks(Random random, int from, int count) =>
            new TestData(random, 1, count, startNum: from).Batches[0];

        private async Task SetReceiptsAsync(ILogIndexStorage logIndexStorage, IEnumerable<BlockReceipts[]> batches, bool isBackwardsSync = false)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var totalStats = new LogIndexUpdateStats();
            var (count, length) = (0, 0);
            foreach (BlockReceipts[] batch in batches)
            {
                count++;
                length = batch.Length;
                LogIndexUpdateStats stats = await logIndexStorage.SetReceiptsAsync(batch, isBackwardsSync);
                totalStats.Combine(stats);
            }

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 x{count} {nameof(LogIndexStorage.SetReceiptsAsync)}({length}) in {Stopwatch.GetElapsedTime(timestamp)}:
                 {'\t'}{totalStats}
                 {'\t'}DB size: {LogIndexMigration.GetFolderSize(Path.Combine(_dbPath, DbNames.LogIndex))}

                 """
            );
        }

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
            Dictionary<Address, HashSet<int>>? excludedAddresses = null,
            Dictionary<Hash256, HashSet<int>>? excludedTopics = null,
            HashSet<int>? excludedBlockNums = null,
            Dictionary<Address, HashSet<int>>? addedAddresses = null,
            Dictionary<Hash256, HashSet<int>>? addedTopics = null,
            int? minBlock = null, int? maxBlock = null,
            bool validateMinMax = true
        )
        {
            minBlock ??= testData.Batches[0][0].BlockNumber;
            maxBlock ??= testData.Batches[^1][^1].BlockNumber;

            if (validateMinMax)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(logIndexStorage.GetMinBlockNumber(), Is.EqualTo(minBlock));
                    Assert.That(logIndexStorage.GetMaxBlockNumber(), Is.EqualTo(maxBlock));
                }
            }

            foreach (var (address, nums) in testData.AddressMap)
            {
                IEnumerable<int> expectedNums = nums;

                if (excludedAddresses != null && excludedAddresses.TryGetValue(address, out HashSet<int> addressExcludedBlocks))
                    expectedNums = expectedNums.Except(addressExcludedBlocks);

                if (excludedBlockNums != null)
                    expectedNums = expectedNums.Except(excludedBlockNums);

                if (addedAddresses != null && addedAddresses.TryGetValue(address, out HashSet<int> addressAddedBlocks))
                    expectedNums = expectedNums.Concat(addressAddedBlocks);

                expectedNums = expectedNums.Order();

                if (minBlock > testData.Batches[0][0].BlockNumber)
                    expectedNums = expectedNums.SkipWhile(b => b < minBlock);

                if (maxBlock < testData.Batches[^1][^1].BlockNumber)
                    expectedNums = expectedNums.TakeWhile(b => b <= maxBlock);

                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue),
                    Is.EqualTo(expectedNums),
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

                expectedNums = expectedNums.Order();

                if (minBlock > testData.Batches[0][0].BlockNumber)
                    expectedNums = expectedNums.SkipWhile(b => b < minBlock);

                if (maxBlock < testData.Batches[^1][^1].BlockNumber)
                    expectedNums = expectedNums.TakeWhile(b => b <= maxBlock);

                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue),
                    Is.EqualTo(expectedNums),
                    $"Topic: {topic}"
                );
            }
        }

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
            IEnumerable<BlockReceipts>? excludedBlocks, IEnumerable<BlockReceipts>? addedBlocks = null,
            int? minBlock = null, int? maxBlock = null,
            bool validateMinMax = true)
        {
            var excludeMaps = excludedBlocks == null ? default : TestData.GenerateMaps(excludedBlocks);
            var addMaps = addedBlocks == null ? default : TestData.GenerateMaps(addedBlocks);

            VerifyReceipts(
                logIndexStorage, testData,
                excludedAddresses: excludeMaps.address, excludedTopics: excludeMaps.topic,
                addedAddresses: addMaps.address, addedTopics: addMaps.topic,
                minBlock: minBlock, maxBlock: maxBlock,
                validateMinMax: validateMinMax
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

        private async Task CompactAsync(ILogIndexStorage logIndexStorage)
        {
            const bool flush = true;

            var timestamp = Stopwatch.GetTimestamp();
            CompactingStats stats = await logIndexStorage.CompactAsync(flush);

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 {nameof(LogIndexStorage.CompactAsync)}({flush}) in {Stopwatch.GetElapsedTime(timestamp)}:
                 {'\t'}DB size: {LogIndexMigration.GetFolderSize(Path.Combine(_dbPath, DbNames.LogIndex))}

                 """
            );
        }

        public class TestData
        {
            private readonly int _batchCount;
            private readonly int _blocksPerBatch;

            // To avoid generating all the data just to display test cases
            private readonly Lazy<BlockReceipts[][]> _batches;
            public BlockReceipts[][] Batches => _batches.Value;

            public Dictionary<Address, HashSet<int>> AddressMap = new();
            public Dictionary<Hash256, HashSet<int>> TopicMap = new();

            public TestData(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
            {
                _batchCount = batchCount;
                _blocksPerBatch = blocksPerBatch;
                _batches = new(() => GenerateBatches(random, batchCount, blocksPerBatch, startNum));
            }

            private BlockReceipts[][] GenerateBatches(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
            {
                var batches = new BlockReceipts[batchCount][];
                var blocksCount = batchCount * blocksPerBatch;
                var addresses = Enumerable.Repeat(0, Math.Max(10, blocksCount / 5))
                //var addresses = Enumerable.Repeat(0, 1)
                    .Select(_ => new Address(random.NextBytes(Address.Size)))
                    .ToArray();
                var topics = Enumerable.Repeat(0, addresses.Length * 7)
                //var topics = Enumerable.Repeat(0, 0)
                    .Select(_ => new Hash256(random.NextBytes(Hash256.Size)))
                    .ToArray();

                // Generate batches
                var blockNum = startNum;
                for (var i = 0; i < batches.Length; i++)
                {
                    var batch = batches[i] = new BlockReceipts[blocksPerBatch];

                    for (var j = 0; j < batch.Length; j++)
                        batch[j] = new(blockNum++, GenerateReceipts(random, addresses, topics));
                }

                var maps = GenerateMaps(batches.SelectMany(b => b));
                AddressMap = maps.address;
                TopicMap = maps.topic;

                return batches;
            }

            public static (Dictionary<Address, HashSet<int>> address, Dictionary<Hash256, HashSet<int>> topic) GenerateMaps(
                IEnumerable<BlockReceipts> blocks)
            {
                var excludedAddresses = new Dictionary<Address, HashSet<int>>();
                var excludedTopics = new Dictionary<Hash256, HashSet<int>>();

                foreach (var block in blocks)
                {
                    foreach (var txReceipt in block.Receipts)
                    {
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

            public override string ToString() => $"{_batchCount} * {_blocksPerBatch} blocks";
        }
    }
}
