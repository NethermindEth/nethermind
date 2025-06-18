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
            _dbPath = GetType().Name;

            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);

            Directory.CreateDirectory(_dbPath);

            _dbFactory = new RocksDbFactory(new DbConfig(), new TestLogManager(), _dbPath);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);
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
            [Values(1, 5, 20)] int revertCount,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] revertBlocks = _testData.Batches.SelectMany(b => b).TakeLast(revertCount).ToArray();
            foreach (BlockReceipts revertBlock in revertBlocks)
                await logIndexStorage.ReorgFrom(revertBlock);

            var lastBlock = _testData.Batches[^1][^1].BlockNumber;
            VerifyReceipts(logIndexStorage, _testData, maxBlock: lastBlock - revertCount);
        }

        [Combinatorial]
        public async Task Set_ReorgAndSetLast_Get_Test(
            [Values(1, 5, 20)] int revertCount,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] revertBlocks = _testData.Batches.SelectMany(b => b).TakeLast(revertCount).ToArray();
            foreach (BlockReceipts revertBlock in revertBlocks)
            {
                await logIndexStorage.ReorgFrom(revertBlock);
                await logIndexStorage.SetReceiptsAsync([revertBlock], false);
            }

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_ReorgLast_SetLast_Get_Test(
            [Values(1, 5, 20)] int revertCount,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] revertBlocks = _testData.Batches.SelectMany(b => b).TakeLast(revertCount).ToArray();

            foreach (BlockReceipts revertBlock in revertBlocks)
                await logIndexStorage.ReorgFrom(revertBlock);

            await logIndexStorage.SetReceiptsAsync(revertBlocks, false);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_ReorgUnexisting_Get_Test(
            [Values(1, 5)] int revertCount,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);

            BlockReceipts[] revertBlocks = GenerateBlocks(new Random(43), _testData.Batches[^1][^1].BlockNumber, revertCount);
            foreach (BlockReceipts revertBlock in revertBlocks)
                await logIndexStorage.ReorgFrom(revertBlock);

            VerifyReceipts(logIndexStorage, _testData);
        }

        [Combinatorial]
        public async Task Set_Compact_ReorgLast_Get_Test(
            [Values(1, 5)] int revertCount,
            [Values(100, int.MaxValue)] int compactionDistance
        )
        {
            await using var logIndexStorage = CreateLogIndexStorage(compactionDistance);

            await SetReceiptsAsync(logIndexStorage, _testData.Batches);
            logIndexStorage.Compact();

            BlockReceipts[] revertBlocks = _testData.Batches.SelectMany(b => b).TakeLast(revertCount).ToArray();
            foreach (BlockReceipts revertBlock in revertBlocks)
                await logIndexStorage.ReorgFrom(revertBlock);

            var lastBlock = _testData.Batches[^1][^1].BlockNumber;
            VerifyReceipts(logIndexStorage, _testData, maxBlock: lastBlock - revertCount);
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
                .Select(_ => new Address(random.NextBytes(Address.Size)))
                .ToArray();
            var topics = Enumerable.Repeat(0, addresses.Length * 10)
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

            foreach (var blockReceipts in testData.Batches)
            foreach (var (blockNumber, txReceipts) in blockReceipts)
            foreach (var txReceipt in txReceipts)
            foreach (var log in txReceipt.Logs!)
            {
                var addressMap = testData.AddressMap.GetOrAdd(log.Address, _ => []);
                addressMap.Add(blockNumber);

                foreach (var topic in log.Topics)
                {
                    var topicMap = testData.TopicMap.GetOrAdd(topic, _ => []);
                    topicMap.Add(blockNumber);
                }
            }

            return testData;
        }

        private static TxReceipt[] GenerateReceipts(Random random, Address[] addresses, Hash256[] topics)
        {
            (int min, int max) logsPerBlock = (0, 200);
            (int min, int max) logsPerTx = (0, 10);

            var logs = Enumerable
                .Repeat(0, random.Next(logsPerBlock.min, logsPerBlock.max + 1))
                .Select(_ => Build.A.LogEntry
                    .WithAddress(random.NextValue(addresses))
                    .WithTopics(Enumerable
                        .Repeat(0, random.Next(4))
                        .Select(_ => random.NextValue(topics))
                        .ToArray()
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

        private static void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData, int? maxBlock = null)
        {
            maxBlock ??= int.MaxValue;

            foreach (var (address, expectedNums) in testData.AddressMap)
            {
                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue),
                    Is.EquivalentTo(expectedNums.Order().TakeWhile(b => b <= maxBlock)),
                    $"Address: {address}"
                );
            }

            foreach (var (topic, expectedNums) in testData.TopicMap)
            {
                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue),
                    Is.EquivalentTo(expectedNums.Order().TakeWhile(b => b <= maxBlock)),
                    $"Topic: {topic}"
                );
            }
        }

        private static void GetBlockNumbersLoop(Random random, ILogIndexStorage logIndexStorage, TestData testData,
            CancellationToken cancellationToken)
        {
            var addresses = testData.AddressMap.Keys.ToArray();
            var topics = testData.TopicMap.Keys.ToArray();

            while (!cancellationToken.IsCancellationRequested)
            {
                var address = random.NextValue(addresses);
                logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue);

                var topic = random.NextValue(topics);
                logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue);
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
            public readonly Dictionary<Address, HashSet<int>> AddressMap = new();
            public readonly Dictionary<Hash256, HashSet<int>> TopicMap = new();

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
