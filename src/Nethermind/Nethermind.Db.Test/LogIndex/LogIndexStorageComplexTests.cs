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
    // TODO: run internal state verification for each test
    // TODO: test for process crash via Thread.Abort
    // TODO: test for reorg out-of-order
    // TODO: test for concurrent forward and backward sync after first block is added
    [TestFixtureSource(nameof(TestCases))]
    [Parallelizable(ParallelScope.None)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class LogIndexStorageComplexTests(LogIndexStorageComplexTests.TestData testData)
    {
        // (batchCount: 10, blocksPerBatch: 100, isExplicit: false, extendedGetRanges = false),
        // (batchCount: 5, blocksPerBatch: 200, isExplicit: false),
        // (batchCount: 100, blocksPerBatch: 100, isExplicit: true),
        // (batchCount: 100, blocksPerBatch: 200, isExplicit: true)

        public static readonly TestFixtureData[] TestCases =
        [
            new(new TestData(10, 100)),
            new(new TestData(5, 200)),
            new(new TestData(10, 100) { ExtendedGetRanges = true }) { RunState = RunState.Explicit },
            new(new TestData(100, 100)) { RunState = RunState.Explicit },
            new(new TestData(100, 200)) { RunState = RunState.Explicit }
        ];

        private ILogger _logger;
        private string _dbPath = null!;
        private IDbFactory _dbFactory = null!;
        private readonly List<ILogIndexStorage> _createdStorages = [];

        private LogIndexStorage CreateLogIndexStorage(int compactionDistance = 262_144, int ioParallelism = 16, int maxReorgDepth = 32, IDbFactory? dbFactory = null)
        {
            LogIndexStorage storage = new(dbFactory ?? _dbFactory, LimboLogs.Instance, ioParallelism, compactionDistance, maxReorgDepth);
            _createdStorages.Add(storage);
            return storage;
        }

        [SetUp]
        public void Setup()
        {
            _logger = LimboLogs.Instance.GetClassLogger();
            _dbPath = $"{nameof(LogIndexStorageComplexTests)}/{Guid.NewGuid():N}";

            // if (Directory.Exists(_dbPath))
            //     Directory.Delete(_dbPath, true);

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
                //Directory.Delete(_dbPath, true);
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
                //Directory.Delete(nameof(LogIndexStorageComplexTests), true);
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
                await logIndexStorage.SetReceiptsAsync(batch, isBackwardsSync, totalStats);
            }

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 x{count} {nameof(LogIndexStorage.SetReceiptsAsync)}({length}) in {Stopwatch.GetElapsedTime(timestamp)}:
                 {totalStats}
                 {'\t'}DB size: {GetFolderSize(Path.Combine(_dbPath, DbNames.LogIndex))}

                 """
            );
        }

        private void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
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

                expectedNums = expectedNums.ToArray();

                foreach (var (from, to) in testData.Ranges)
                {
                    Assert.That(
                        logIndexStorage.GetBlockNumbersFor(address, from, to).ToArray(),
                        Is.EqualTo(expectedNums.SkipWhile(i => i < from).TakeWhile(i => i <= to).ToArray()),
                        $"Address: {address}, from {from} to {to} (endianness: {(BitConverter.IsLittleEndian ? "little" : "big")}, db: {_dbPath})"
                    );
                }
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

                expectedNums = expectedNums.ToArray();

                foreach (var (from, to) in testData.Ranges)
                {
                    Assert.That(
                        logIndexStorage.GetBlockNumbersFor(topic, from, to),
                        Is.EqualTo(expectedNums.SkipWhile(i => i < from).TakeWhile(i => i <= to)),
                        $"Topic: {topic}, {from} - {to}"
                    );
                }
            }
        }

        private void VerifyReceipts(ILogIndexStorage logIndexStorage, TestData testData,
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
            await logIndexStorage.CompactAsync(flush);

            // Log statistics
            await TestContext.Out.WriteLineAsync(
                $"""
                 {nameof(LogIndexStorage.CompactAsync)}({flush}) in {Stopwatch.GetElapsedTime(timestamp)}:
                 {'\t'}DB size: {GetFolderSize(Path.Combine(_dbPath, DbNames.LogIndex))}

                 """
            );
        }

        private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

        private static string GetFolderSize(string path)
        {
            var info = new DirectoryInfo(path);

            double size = info.Exists ? info.GetFiles().Sum(f => f.Length) : 0;

            int index = 0;
            while (size >= 1024 && index < SizeSuffixes.Length - 1)
            {
                size /= 1024;
                index++;
            }

            return $"{size:0.##} {SizeSuffixes[index]}";
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

            public IReadOnlyDictionary<Address, HashSet<int>> AddressMap { get; private set; } = new Dictionary<Address, HashSet<int>>();
            public IReadOnlyDictionary<Hash256, HashSet<int>> TopicMap { get; private set; } = new Dictionary<Hash256, HashSet<int>>();

            public bool ExtendedGetRanges { get; init; }

            public TestData(Random random, int batchCount, int blocksPerBatch, int startNum = 0)
            {
                _batchCount = batchCount;
                _blocksPerBatch = blocksPerBatch;
                _startNum = startNum;

                _batches = new(() => GenerateBatches(random, batchCount, blocksPerBatch, startNum));
                _ranges = new(() => ExtendedGetRanges ? GenerateExtendedRanges() : GenerateSimpleRanges());
            }

            public TestData(int batchCount, int blocksPerBatch, int startNum = 0) : this(new(42), batchCount, blocksPerBatch, startNum) { }

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
                            var addressMap = excludedAddresses.GetOrAdd(log.Address, static _ => []);
                            addressMap.Add(block.BlockNumber);

                            foreach (var topic in log.Topics)
                            {
                                var topicMap = excludedTopics.GetOrAdd(topic, static _ => []);
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

            private static HashSet<(int from, int to)> GenerateSimpleRanges(int min, int max)
            {
                var quarter = (max - min) / 4;
                return [(0, int.MaxValue), (min, max), (min + quarter, max - quarter)];
            }

            private static HashSet<(int from, int to)> GenerateExtendedRanges(int min, int max)
            {
                var ranges = new HashSet<(int, int)>();

                var edges = new[] { min - 1, min, min + 1, max - 1, max + 1 };
                ranges.AddRange(edges.Zip(edges));

                const int step = 100;
                for (var i = min; i <= max; i += step)
                {
                    var middles = new[] { i - step, i - 1, i, i + 1, i + step };
                    ranges.AddRange(middles.Zip(middles));
                }

                return ranges;
            }

            private HashSet<(int from, int to)> GenerateSimpleRanges() => GenerateSimpleRanges(
                _startNum, _startNum + _batchCount * _blocksPerBatch - 1
            );

            private HashSet<(int from, int to)> GenerateExtendedRanges() => GenerateExtendedRanges(
                _startNum, _startNum + _batchCount * _blocksPerBatch - 1
            );

            public override string ToString() => $"{_batchCount} * {_blocksPerBatch} blocks (ex-ranges: {ExtendedGetRanges})";
        }
    }
}
