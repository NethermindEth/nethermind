// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace Nethermind.Db.Test.LogIndex
{
    // TODO: test for different block ranges intersection
    // TODO: run internal state verification for all/some tests
    // TODO: test for process crash via Thread.Abort
    // TODO: test for reorg out-of-order
    // TODO: test for concurrent reorg and backward sync
    // TODO: rename to IntegrationTests?
    [TestFixtureSource(nameof(TestCases))]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class LogIndexStorageComplexTests(LogIndexStorageComplexTests.TestData testData)
    {
        private const int RaceConditionTestRepeat = 3;

        public static readonly TestFixtureData[] TestCases =
        [
            new(new TestData(10, 100) { Compression = LogIndexStorage.CompressionAlgorithm.Best.Key }),
            new(new TestData(5, 200) { Compression = nameof(TurboPFor.p4nd1enc128v32) }),
            new(new TestData(10, 100) { ExtendedGetRanges = true }) { RunState = RunState.Explicit },
            new(new TestData(100, 100) { Compression = nameof(TurboPFor.p4nd1enc128v32) }) { RunState = RunState.Explicit },
            new(new TestData(100, 200) { Compression = LogIndexStorage.CompressionAlgorithm.Best.Key }) { RunState = RunState.Explicit }
        ];

        private string _dbPath = null!;
        private IDbFactory _dbFactory = null!;
        private readonly List<ILogIndexStorage> _createdStorages = [];

        private ILogIndexStorage CreateLogIndexStorage(
            int compactionDistance = 262_144, int compressionParallelism = 16, int maxReorgDepth = 64, IDbFactory? dbFactory = null,
            int? failOnBlock = null, int? failOnCallN = null
        )
        {
            LogIndexConfig config = new() { CompactionDistance = compactionDistance, CompressionParallelism = compressionParallelism, MaxReorgDepth = maxReorgDepth };

            ILogIndexStorage storage = failOnBlock is not null || failOnCallN is not null
                ? new SaveFailingLogIndexStorage(dbFactory ?? _dbFactory, LimboLogs.Instance, config)
                {
                    FailOnBlock = failOnBlock ?? 0,
                    FailOnCallN = failOnCallN ?? 0
                }
                : new LogIndexStorage(dbFactory ?? _dbFactory, LimboLogs.Instance, config);

            _createdStorages.Add(storage);
            return storage;
        }

        [SetUp]
        public void Setup()
        {
            _dbPath = $"{nameof(LogIndexStorageComplexTests)}/{Guid.NewGuid():N}";

            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);

            Directory.CreateDirectory(_dbPath);

            var config = new DbConfig();
            var configFactory = new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
            _dbFactory = new RocksDbFactory(configFactory, config, new TestLogManager(), _dbPath);
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

        [OneTimeSetUp]
        // Causes DB error under some race condition, TODO: find & fix or remove
        //[OneTimeTearDown]
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
            [Values(100, 200, int.MaxValue)] int compactionDistance
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            var batches = testData.Batches;
            var half = batches.Length / 2;

            for (var i = 0; i < half + 1; i++)
            {
                if (half + i < batches.Length)
                    await SetReceiptsAsync(logIndexStorage, [batches[half + i]], isBackwardsSync: false);
                if (i != 0 && half - i >= 0)
                    await SetReceiptsAsync(logIndexStorage, Reverse([batches[half - i]]), isBackwardsSync: true);
            }

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        [Repeat(RaceConditionTestRepeat)]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public async Task Concurrent_BackwardsSet_Set_Get_Test(
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using (var setStorage = CreateLogIndexStorage(compactionDistance))
            {
                var half = testData.Batches.Length / 2;
                var batches = testData.Batches
                    .Select((b, i) => i >= half ? b : b.Reverse().ToArray())
                    .ToArray();

                var forwardTask = Task.Run(async () =>
                {
                    for (var i = half; i < batches.Length; i++)
                    {
                        BlockReceipts[] batch = batches[i];
                        await SetReceiptsAsync(setStorage, [batch], isBackwardsSync: false);

                        Assert.That(setStorage.GetMinBlockNumber(), Is.LessThanOrEqualTo(batch[0].BlockNumber));
                        Assert.That(setStorage.GetMaxBlockNumber(), Is.EqualTo(batch[^1].BlockNumber));
                    }
                });

                var backwardTask = Task.Run(async () =>
                {
                    for (var i = half - 1; i >= 0; i--)
                    {
                        BlockReceipts[] batch = batches[i];
                        await SetReceiptsAsync(setStorage, [batch], isBackwardsSync: true);

                        Assert.That(setStorage.GetMinBlockNumber(), Is.EqualTo(batch[^1].BlockNumber));
                        Assert.That(setStorage.GetMaxBlockNumber(), Is.GreaterThanOrEqualTo(batch[0].BlockNumber));
                    }
                });

                await forwardTask;
                await backwardTask;
            }

            // Create new storage to force-load everything from DB
            await using (var testStorage = CreateLogIndexStorage(compactionDistance))
                VerifyReceipts(testStorage, testData);
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
            BlockReceipts[] reorgBlocks = GenerateBlocks(new Random(4242), lastBlock - reorgDepth + 1, reorgDepth);
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

        [Ignore("Not supported, but is probably not needed.")]
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

            VerifyReceipts(logIndexStorage, testData, maxBlock: testBlocks[^1].BlockNumber - reorgDepths.Max());
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
        [Repeat(RaceConditionTestRepeat)]
        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        public async Task Set_ConcurrentGet_Test(
            [Values(100, int.MaxValue)] int compactionDistance,
            [Values] bool isBackwardsSync
        )
        {
            var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            using var getCancellation = new CancellationTokenSource();
            var token = getCancellation.Token;

            ConcurrentBag<Exception> exceptions = [];
            var getThreads = new[]
            {
                new Thread(() => VerifyReceiptsPartialLoop(new Random(42), logIndexStorage, testData, exceptions, token)),
                new Thread(() => VerifyReceiptsPartialLoop(new Random(4242), logIndexStorage, testData, exceptions, token)),
                new Thread(() => VerifyReceiptsPartialLoop(new Random(424242), logIndexStorage, testData, exceptions, token)),
            };
            getThreads.ForEach(t => t.Start());

            await SetReceiptsAsync(logIndexStorage, testData.Batches);

            await getCancellation.CancelAsync();
            getThreads.ForEach(t => t.Join());

            if (exceptions.FirstOrDefault() is { } exception)
                ExceptionDispatchInfo.Capture(exception).Throw();

            VerifyReceipts(logIndexStorage, testData);
        }

        [Combinatorial]
        public async Task SetFailure_Get_Test(
            [Values(10, 100, 200)] int failOnCallN,
            [Values] bool isBackwardsSync
        )
        {
            BlockReceipts[][] batches = isBackwardsSync ? Reverse(testData.Batches) : testData.Batches;
            var midBlock = testData.Batches[^1][^1].BlockNumber / 2;

            await using var failLogIndexStorage = CreateLogIndexStorage(failOnBlock: midBlock, failOnCallN: failOnCallN);

            Exception exception = Assert.ThrowsAsync<Exception>(() => SetReceiptsAsync(failLogIndexStorage, batches, isBackwardsSync));
            Assert.That(exception, Has.Message.EqualTo(SaveFailingLogIndexStorage.FailMessage));

            VerifyReceipts(
                failLogIndexStorage, testData,
                minBlock: failLogIndexStorage.GetMinBlockNumber() ?? 0, maxBlock: failLogIndexStorage.GetMaxBlockNumber() ?? 0
            );
        }

        [Combinatorial]
        public async Task SetFailure_Set_Test(
            [Values(10)] int failOnCallN,
            [Values] bool isBackwardsSync
        )
        {
            BlockReceipts[][] batches = isBackwardsSync ? Reverse(testData.Batches) : testData.Batches;

            await using var failLogIndexStorage = CreateLogIndexStorage(failOnBlock: 0, failOnCallN: failOnCallN);

            Assert.ThrowsAsync<Exception>(() => SetReceiptsAsync(failLogIndexStorage, batches, isBackwardsSync));
            Assert.ThrowsAsync<InvalidOperationException>(() => SetReceiptsAsync(failLogIndexStorage, batches, isBackwardsSync));
        }

        [Combinatorial]
        public async Task SetFailure_Set_Get_Test(
            [Values(10, 100, 200)] int failOnCallN,
            [Values] bool isBackwardsSync
        )
        {
            BlockReceipts[][] batches = isBackwardsSync ? Reverse(testData.Batches) : testData.Batches;
            var midBlock = testData.Batches[^1][^1].BlockNumber / 2;

            await using (var failLogIndexStorage = CreateLogIndexStorage(failOnBlock: midBlock, failOnCallN: failOnCallN))
            {
                Exception exception = Assert.ThrowsAsync<Exception>(() => SetReceiptsAsync(failLogIndexStorage, batches, isBackwardsSync));
                Assert.That(exception, Has.Message.EqualTo(SaveFailingLogIndexStorage.FailMessage));
            };

            await using var logIndexStorage = CreateLogIndexStorage();
            await SetReceiptsAsync(logIndexStorage, batches, isBackwardsSync);

            VerifyReceipts(logIndexStorage, testData);
        }

        private static BlockReceipts[] GenerateBlocks(Random random, int from, int count) =>
            new TestData(random, 1, count, startNum: from).Batches[0];

        private static async Task SetReceiptsAsync(ILogIndexStorage logIndexStorage, IEnumerable<BlockReceipts[]> batches, bool isBackwardsSync = false)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var totalStats = new LogIndexUpdateStats(logIndexStorage);
            var (count, length) = (0, 0);
            foreach (BlockReceipts[] batch in batches)
            {
                count++;
                length = batch.Length;
                await logIndexStorage.SetReceiptsAsync(batch, isBackwardsSync, totalStats);
            }

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 x{count} {nameof(LogIndexStorage.SetReceiptsAsync)}([{length}], {isBackwardsSync}) in {Stopwatch.GetElapsedTime(timestamp)}:
                 {totalStats:d}
                 {'\t'}DB size: {logIndexStorage.GetDbSize()}

                 """
            );
        }

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
            Dictionary<Address, HashSet<int>>? excludedAddresses = null,
            Dictionary<int, Dictionary<Hash256, HashSet<int>>>? excludedTopics = null,
            HashSet<int>? excludedBlockNums = null,
            Dictionary<Address, HashSet<int>>? addedAddresses = null,
            Dictionary<int, Dictionary<Hash256, HashSet<int>>>? addedTopics = null,
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

                expectedNums = expectedNums.ToArray();

                foreach (var (from, to) in testData.Ranges)
                {
                    Assert.That(
                        logIndexStorage.GetBlockNumbersFor(address, from, to),
                        Is.EqualTo(expectedNums.SkipWhile(i => i < from).TakeWhile(i => i <= to)),
                        $"Address: {address}, from {from} to {to}"
                    );
                }
            }

            foreach (var (idx, byTopic) in testData.TopicMap)
            {
                foreach (var (topic, nums) in byTopic)
                {
                    IEnumerable<int> expectedNums = nums;

                    if (excludedTopics != null && excludedTopics[idx].TryGetValue(topic, out HashSet<int> topicExcludedBlocks))
                        expectedNums = expectedNums.Except(topicExcludedBlocks);

                    if (excludedBlockNums != null)
                        expectedNums = expectedNums.Except(excludedBlockNums);

                    if (addedTopics != null && addedTopics[idx].TryGetValue(topic, out HashSet<int> topicAddedBlocks))
                        expectedNums = expectedNums.Concat(topicAddedBlocks);

                    expectedNums = expectedNums.Order();

                    if (minBlock > testData.Batches[0][0].BlockNumber)
                        expectedNums = expectedNums.SkipWhile(b => b < minBlock);

                    if (maxBlock < testData.Batches[^1][^1].BlockNumber)
                        expectedNums = expectedNums.TakeWhile(b => b <= maxBlock);

                    expectedNums = expectedNums.ToArray();

                    foreach (var (from, to) in testData.Ranges)
                    {
                        Assert.That(
                            logIndexStorage.GetBlockNumbersFor(idx, topic, from, to),
                            Is.EqualTo(expectedNums.SkipWhile(i => i < from).TakeWhile(i => i <= to)),
                            $"Topic: [{idx}] {topic}, {from} - {to}"
                        );
                    }
                }
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

        private static void VerifyReceiptsPartialLoop(Random random, ILogIndexStorage logIndexStorage, TestData testData,
            ConcurrentBag<Exception> exceptions, CancellationToken cancellationToken)
        {
            try
            {
                var (addresses, topics) = (testData.Addresses, testData.Topics);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (addresses.Count != 0)
                    {
                        var address = random.NextValue(addresses);
                        var expectedNums = testData.AddressMap[address];

                        if (logIndexStorage.GetMinBlockNumber() is not { } min || logIndexStorage.GetMaxBlockNumber() is not { } max)
                            continue;

                        Assert.That(
                            logIndexStorage.GetBlockNumbersFor(address, min, max),
                            Is.EqualTo(expectedNums.SkipWhile(i => i < min).TakeWhile(i => i <= max)),
                            $"Address: {address}, available: {min} - {max}"
                        );
                    }

                    if (topics.Count != 0)
                    {
                        var (idx, topic) = random.NextValue(topics);
                        var expectedNums = testData.TopicMap[idx][topic];

                        if (logIndexStorage.GetMinBlockNumber() is not { } min || logIndexStorage.GetMaxBlockNumber() is not { } max)
                            continue;

                        Assert.That(
                            logIndexStorage.GetBlockNumbersFor(idx, topic, min, max),
                            Is.EqualTo(expectedNums.SkipWhile(i => i < min).TakeWhile(i => i <= max)),
                            $"Topic: [{idx}] {topic}, available: {min} - {max}"
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
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
            var timestamp = Stopwatch.GetTimestamp();
            await logIndexStorage.CompactAsync();

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 {nameof(LogIndexStorage.CompactAsync)}() in {Stopwatch.GetElapsedTime(timestamp)}:
                 {'\t'}DB size: {logIndexStorage.GetDbSize()}

                 """
            );
        }

        public class TestData
        {
            private readonly int _batchCount;
            private readonly int _blocksPerBatch;
            private readonly int _startNum;

            // To avoid generating all the data just to display test cases
            private readonly Lazy<BlockReceipts[][]> _batches;
            public BlockReceipts[][] Batches => _batches.Value;

            private readonly Lazy<IEnumerable<(int from, int to)>> _ranges;
            public IEnumerable<(int from, int to)> Ranges => _ranges.Value;

            public Dictionary<Address, HashSet<int>> AddressMap { get; private set; } = new();
            public Dictionary<int, Dictionary<Hash256, HashSet<int>>> TopicMap { get; private set; } = new();

            public List<Address> Addresses { get; private set; }
            public List<(int, Hash256)> Topics { get; private set; }

            public bool ExtendedGetRanges { get; init; }
            public string? Compression { get; init; }

            public TestData(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
            {
                _batchCount = batchCount;
                _blocksPerBatch = blocksPerBatch;
                _startNum = startNum;

                // Populated during GenerateBatches()
                Addresses = null!;
                Topics = null!;

                _batches = new(() => GenerateBatches(random, batchCount, blocksPerBatch, startNum));
                _ranges = new(() => ExtendedGetRanges ? GenerateExtendedRanges() : GenerateSimpleRanges());
            }

            public TestData(int batchCount, int blocksPerBatch, int startNum = 0) : this(new(42), batchCount, blocksPerBatch, startNum) { }

            private BlockReceipts[][] GenerateBatches(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
            {
                var batches = new BlockReceipts[batchCount][];
                var blocksCount = batchCount * blocksPerBatch;

                Address[] customAddresses =
                [
                    Address.Zero, Address.MaxValue,
                    new(new byte[] { 1 }.PadLeft(Address.Size)), new(new byte[] { 1, 1 }.PadLeft(Address.Size)),
                    new(new byte[] { 1 }.PadRight(Address.Size)), new(new byte[] { 1, 1 }.PadRight(Address.Size)),
                    new(new byte[] { 0 }.PadLeft(Address.Size, 0xFF)), new(new byte[] { 0 }.PadRight(Address.Size, 0xFF)),
                ];

                Hash256[] customTopics =
                [
                    Hash256.Zero, new(Array.Empty<byte>().PadRight(Hash256.Size, 0xFF)),
                    new(new byte[] { 0 }.PadLeft(Hash256.Size)), new(new byte[] { 1 }.PadLeft(Hash256.Size)),
                    new(new byte[] { 0 }.PadRight(Hash256.Size)), new(new byte[] { 1 }.PadRight(Hash256.Size)),
                    new(new byte[] { 0 }.PadLeft(Hash256.Size, 0xFF)), new(new byte[] { 0 }.PadRight(Hash256.Size, 0xFF)),
                ];

                var addresses = Enumerable.Repeat(0, Math.Max(10, blocksCount / 5) - customAddresses.Length)
                //var addresses = Enumerable.Repeat(0, 0)
                    .Select(_ => new Address(random.NextBytes(Address.Size)))
                    .Concat(customAddresses)
                    .ToArray();
                var topics = Enumerable.Repeat(0, addresses.Length * 7 - customTopics.Length)
                //var topics = Enumerable.Repeat(0, 0)
                    .Select(_ => new Hash256(random.NextBytes(Hash256.Size)))
                    .Concat(customTopics)
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

                (AddressMap, TopicMap) = (maps.address, maps.topic);
                (Addresses, Topics) = (maps.address.Keys.ToList(), maps.topic.SelectMany(byIdx => byIdx.Value.Select(byTpc => (byIdx.Key, byTpc.Key)))
                    .ToList());

                return batches;
            }

            public static (Dictionary<Address, HashSet<int>> address, Dictionary<int, Dictionary<Hash256, HashSet<int>>> topic) GenerateMaps(
                IEnumerable<BlockReceipts> blocks)
            {
                var address = new Dictionary<Address, HashSet<int>>();
                var topic = new Dictionary<int, Dictionary<Hash256, HashSet<int>>>();

                foreach (var block in blocks)
                {
                    foreach (var txReceipt in block.Receipts)
                    {
                        foreach (var log in txReceipt.Logs!)
                        {
                            var addressMap = address.GetOrAdd(log.Address, static _ => []);
                            addressMap.Add(block.BlockNumber);

                            for (var i = 0; i < log.Topics.Length; i++)
                            {
                                var topicI = topic.GetOrAdd(i, static _ => []);
                                var topicMap = topicI.GetOrAdd(log.Topics[i], static _ => []);
                                topicMap.Add(block.BlockNumber);
                            }
                        }
                    }
                }

                return (address, topic);
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

            private static HashSet<(int from, int to)> GenerateSimpleRanges(int min, int max)
            {
                var quarter = (max - min) / 4;
                return [(0, int.MaxValue), (min, max), (min + quarter, max - quarter)];
            }

            private static HashSet<(int from, int to)> GenerateExtendedRanges(int min, int max)
            {
                var ranges = new HashSet<(int, int)>();

                var edges = new[] { min - 1, min, min + 1, max - 1, max + 1 };
                ranges.AddRange(edges.SelectMany(_ => edges, static (x, y) => (x, y)));

                const int step = 100;
                for (var i = min; i <= max; i += step)
                {
                    var middles = new[] { i - step, i - 1, i, i + 1, i + step };
                    ranges.AddRange(middles.SelectMany(_ => middles, static (x, y) => (x, y)));
                }

                return ranges;
            }

            private HashSet<(int from, int to)> GenerateSimpleRanges() => GenerateSimpleRanges(
                _startNum, _startNum + _batchCount * _blocksPerBatch - 1
            );

            private HashSet<(int from, int to)> GenerateExtendedRanges() => GenerateExtendedRanges(
                _startNum, _startNum + _batchCount * _blocksPerBatch - 1
            );

            public override string ToString() =>
                $"{_batchCount} * {_blocksPerBatch} blocks (ex-ranges: {ExtendedGetRanges}, compression: {Compression})";
        }

        private class SaveFailingLogIndexStorage(IDbFactory dbFactory, ILogManager logManager, ILogIndexConfig config)
            : LogIndexStorage(dbFactory, logManager, config)
        {
            public const string FailMessage = "Test exception.";

            public int FailOnBlock { get; init; }
            public int FailOnCallN { get; init; }

            private int _count = 0;

            protected override void SaveBlockNumbersByKey(IWriteBatch dbBatch, ReadOnlySpan<byte> key, IReadOnlyList<int> blockNums, bool isBackwardSync, LogIndexUpdateStats? stats)
            {
                var isFailBlock =
                    FailOnBlock >= Math.Min(blockNums[0], blockNums[^1]) &&
                    FailOnBlock <= Math.Max(blockNums[0], blockNums[^1]);

                if (isFailBlock && Interlocked.Increment(ref _count) > FailOnCallN)
                    throw new(FailMessage);

                base.SaveBlockNumbersByKey(dbBatch, key, blockNums, isBackwardSync, stats);
            }
        }
    }
}
