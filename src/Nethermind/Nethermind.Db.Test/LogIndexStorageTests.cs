using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class LogIndexStorageTests
    {
        private ILogger _logger;
        private string _dbPath = null!;
        private ColumnsDb<LogIndexColumns> _columnsDb = null!;

        private LogIndexStorage CreateLogIndexStorage(int ioParallelism = 16, int compactionDistance = 262_144)
        {
            return new(_columnsDb, _logger, ioParallelism, compactionDistance);
        }

        [SetUp]
        public void Setup()
        {
            _logger = LimboLogs.Instance.GetClassLogger();
            _dbPath = $"{nameof(LogIndexStorageTests)}";

            if (Directory.Exists(_dbPath))
            {
                Directory.Delete(_dbPath, true);
            }
            Directory.CreateDirectory(_dbPath);

            _columnsDb = new(
                _dbPath,
                new(DbNames.LogIndexStorage, _dbPath) { DeleteOnStart = true },
                new DbConfig(),
                LimboLogs.Instance,
                Enum.GetValues<LogIndexColumns>()
            );
        }

        [TearDown]
        public void TearDown()
        {
            _columnsDb.Dispose();

            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, true);
        }

        [Test]
        public async Task SetReceipts_SavesCorrectAddresses()
        {
            // Arrange
            var address1 = new Address("0x0000000000000000000000000000000000001234");
            var address2 = new Address("0x0000000000000000000000000000000000005678");
            int blockNumber = 100;
            var receipts = new[]
            {
                new TxReceipt
                {
                    Logs = new List<LogEntry>
                    {
                        new(address1, [], []),
                        new(address1, [], []), // Multiple logs for the same address
                        new(address2, [], [])
                    }.ToArray()
                }
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false);

            // Assert
            IDb addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            Enumerate(addressDb)
                .Select(x => new Address(x.key[..Address.Size])).ToHashSet()
                .Should().BeEquivalentTo([address1, address2]);
        }

        [Test]
        public async Task SetReceipts_SavesLogsWithDifferentTopics()
        {
            // Arrange
            var topic1 = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
            var topic2 = new Hash256("0xabcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890");
            int blockNumber = 100;
            var receipts = new[]
            {
                new TxReceipt
                {
                    Logs = new List<LogEntry>
                    {
                        new(Address.Zero, [], [topic1]),
                        new(Address.Zero, [], [topic2])
                    }.ToArray()
                }
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false);

            // Assert
            IDb topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);
            Enumerate(topicDb)
                .Select(x => new Hash256(x.key[..Hash256.Size])).ToHashSet()
                .Should().BeEquivalentTo([topic1, topic2]);
        }

        [TestCase(7, 1)]
        [TestCase(8, 2)]
        [TestCase(15, 3)]
        [TestCase(300, 4)]
        [TestCase(9999, 10)]
        public async Task GetBlockNumbersFor_ReturnsCorrectBlocks(int batchSize, int batchCount)
        {
            // Arrange
            var address = new Address("0x0000000000000000000000000000000000001234");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new(address, [], [])
                }.ToArray()
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            logIndexStorage.GetLastKnownBlockNumber().Should().Be(-1);

            for (var batchNum = 0; batchNum < batchCount; batchNum++)
            {
                BlockReceipts[] receipts = Enumerable.Range(batchSize * batchNum, batchSize)
                    .Select(i => new BlockReceipts(i + 1, [receipt]))
                    .ToArray();

                await logIndexStorage.SetReceiptsAsync(receipts, isBackwardSync: false);
                logIndexStorage.GetLastKnownBlockNumber().Should().Be(receipts[^1].BlockNumber);
            }

            // Assert
            var resultBlocks = logIndexStorage.GetBlockNumbersFor(address, 1, batchSize * batchCount).ToList();
            resultBlocks.Should().BeEquivalentTo(Enumerable.Range(1, batchSize * batchCount), "The blocks returned should match the blocks that were saved.");
        }

        [Test]
        public async Task SetReceipts_DistinguishesOverlappingAddressAndTopicKeys()
        {
            // Arrange
            var address = new Address("0x1234567890abcdef1234567890abcdef12345678");
            var topic = new Hash256("0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef");
            int blockNumber = 100;
            var receipts = new[]
            {
                new TxReceipt
                {
                    Logs = new List<LogEntry>
                    {
                        new(address, [], [topic])
                    }.ToArray()
                }
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false);

            // Assert
            IDb addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            IDb topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);

            Enumerable.Concat<object>(
                    Enumerate(addressDb).Select(x => new Address(x.key[..Address.Size])).ToHashSet(),
                    Enumerate(topicDb).Select(x => new Hash256(x.key[..Hash256.Size])).ToHashSet())
                .ToArray()
                .Should().BeEquivalentTo(new object[] { address, topic });
        }

        [Test]
        public async Task SetReceipts_SavesMixedLogsCorrectly()
        {
            // Arrange
            var address = new Address("0x0000000000000000000000000000000000001234");
            var topic = new Hash256("0xabcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890");
            int blockNumber = 100;
            var receipts = new[]
            {
                new TxReceipt
                {
                    Logs = new List<LogEntry>
                    {
                        new(address, [], []),
                        new(Address.Zero, [], [topic])
                    }.ToArray()
                }
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false);

            // Assert
            IDb addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            Enumerate(addressDb)
                .Select(x => new Address(x.key[..Address.Size])).ToHashSet()
                .Should().BeEquivalentTo([Address.Zero, address]);

            IDb topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);
            Enumerate(topicDb)
                .Select(x => new Hash256(x.key[..Hash256.Size])).ToHashSet()
                .Should().BeEquivalentTo([topic]);
        }

        [Test]
        public async Task SetReceipts_MixedAddressAndTopicEntriesInSameBlock()
        {
            // Arrange
            var address1 = new Address("0x0000000000000000000000000000000000001234");
            var topic1 = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new(address1, [], [topic1])
                }.ToArray()
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(1, [receipt], isBackwardSync: false);

            // Assert
            var addressBlocks = logIndexStorage.GetBlockNumbersFor(address1, 1, 1).ToList();
            var topicBlocks = logIndexStorage.GetBlockNumbersFor(topic1, 1, 1).ToList();

            addressBlocks.Should().Contain(1, "The block number should be indexed for the address.");
            topicBlocks.Should().Contain(1, "The block number should be indexed for the topic.");
        }

        [Test]
        public async Task SetReceipts_MultipleTopicsAndAddressesInSingleBlock()
        {
            // Arrange
            var address1 = new Address("0x0000000000000000000000000000000000001234");
            var address2 = new Address("0x0000000000000000000000000000000000005678");
            var topic1 = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001");
            var topic2 = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000002");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new(address1, [], [topic1]),
                    new(address2, [], [topic2])
                }.ToArray()
            };

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            await logIndexStorage.SetReceiptsAsync(1, [receipt], isBackwardSync: false);

            // Assert
            var addressBlocks1 = logIndexStorage.GetBlockNumbersFor(address1, 1, 1).ToList();
            var addressBlocks2 = logIndexStorage.GetBlockNumbersFor(address2, 1, 1).ToList();
            var topicBlocks1 = logIndexStorage.GetBlockNumbersFor(topic1, 1, 1).ToList();
            var topicBlocks2 = logIndexStorage.GetBlockNumbersFor(topic2, 1, 1).ToList();

            addressBlocks1.Should().Contain(1, "The block number should be indexed for address1.");
            addressBlocks2.Should().Contain(1, "The block number should be indexed for address2.");
            topicBlocks1.Should().Contain(1, "The block number should be indexed for topic1.");
            topicBlocks2.Should().Contain(1, "The block number should be indexed for topic2.");
        }

        [Test]
        public async Task SetReceipts_CheckValidityWithDiverseData()
        {
            // Arrange
            int numberOfBlocks = 100;
            List<Address> generatedAddresses = [];
            List<Hash256> generatedTopics = [];
            var addressBlockMap = new Dictionary<Address, HashSet<int>>();
            var topicBlockMap = new Dictionary<Hash256, HashSet<int>>();
            var random = new Random(42);

            await using var logIndexStorage = CreateLogIndexStorage();

            // Generate unique addresses and topics
            for (int i = 0; i < 20; i++) // 20 unique addresses and topics
            {
                var address = new Address($"0x{i.ToString("x").PadLeft(40, '0')}");
                var topic = new Hash256($"0x{i.ToString("x").PadLeft(64, '0')}");
                generatedAddresses.Add(address);
                generatedTopics.Add(topic);
                addressBlockMap[address] = [];
                topicBlockMap[topic] = [];
            }

            // Act: Create receipts and distribute them across blocks
            for (int blockNumber = 1; blockNumber <= numberOfBlocks; blockNumber++)
            {
                var logs = new List<LogEntry>();
                for (int i = 0; i < 5; i++) // 5 logs per block
                {
                    Address address = generatedAddresses[random.Next(generatedAddresses.Count)];
                    Hash256 topic = generatedTopics[random.Next(generatedTopics.Count)];
                    logs.Add(new(address, [], [topic]));

                    // Track which blocks these addresses and topics appear in
                    addressBlockMap[address].Add(blockNumber);
                    topicBlockMap[topic].Add(blockNumber);
                }
                var receipt = new TxReceipt { Logs = logs.ToArray() };

                await logIndexStorage.SetReceiptsAsync(blockNumber, [receipt], isBackwardSync: false);
            }

            // Assert: Check that each address and topic returns the correct block numbers
            foreach (var kvp in addressBlockMap)
            {
                var address = kvp.Key;
                var expectedBlocks = kvp.Value;
                var resultBlocks = logIndexStorage.GetBlockNumbersFor(address, 1, numberOfBlocks).ToList();
                resultBlocks.Should().BeEquivalentTo(expectedBlocks, $"Address {address} should have the correct block numbers.");
            }

            foreach (var kvp in topicBlockMap)
            {
                var topic = kvp.Key;
                var expectedBlocks = kvp.Value;
                var resultBlocks = logIndexStorage.GetBlockNumbersFor(topic, 1, numberOfBlocks).ToList();
                resultBlocks.Should().BeEquivalentTo(expectedBlocks, $"Topic {topic} should have the correct block numbers.");
            }
        }

        private static readonly Random Random = new(42);

        private BlockReceipts[][] GenerateBatches(int batchCount, int blocksPerBatch)
        {
            var blocksCount = batchCount * blocksPerBatch;
            var addresses = Enumerable.Repeat(0, blocksCount / 100)
                .Select(_ => new Address(Random.NextBytes(Address.Size)))
                .ToArray();
            var topics = Enumerable.Repeat(0, addresses.Length * 10)
                .Select(_ => new Hash256(Random.NextBytes(Hash256.Size)))
                .ToArray();

            var blockNum = 0;
            var result = new BlockReceipts[batchCount][];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new BlockReceipts[blocksPerBatch];
                for (var j = 0; j < result[i].Length; j++)
                {
                    result[i][j] = new(blockNum, GenerateReceipts(addresses, topics));
                    blockNum++;
                }
            }

            return result;
        }

        private TxReceipt[] GenerateReceipts(Address[] addresses, Hash256[] topics)
        {
            (int min, int max) logsPerBlock = (100, 300);
            (int min, int max) logsPerTx = (0, 10);

            var logs = Enumerable
                .Repeat(0, Random.Next(logsPerBlock.min, logsPerBlock.max + 1))
                .Select(_ => Build.A.LogEntry
                    .WithAddress(Random.NextValue(addresses))
                    .WithTopics(Enumerable
                        .Repeat(0, Random.Next(4))
                        .Select(_ => Random.NextValue(topics))
                        .ToArray()
                    ).TestObject
                ).ToArray();

            var receipts = new List<TxReceipt>();
            for (var i = 0; i < logs.Length;)
            {
                var count = Random.Next(logsPerTx.min, Math.Min(logsPerTx.max, logs.Length - i) + 1);
                var range = i..(i + count);

                receipts.Add(new() { Logs = logs[range] });
                i = range.End.Value;
            }

            return receipts.ToArray();
        }

        private (Dictionary<Address, HashSet<int>> address, Dictionary<Hash256, HashSet<int>> topic) MapToBlockNumber(IEnumerable<BlockReceipts> blockReceipts)
        {
            (Dictionary<Address, HashSet<int>> address, Dictionary<Hash256, HashSet<int>> topic) map = (new(), new());

            foreach (var (blockNumber, txReceipts) in blockReceipts)
            foreach (var txReceipt in txReceipts)
            foreach (var log in txReceipt.Logs!)
            {
                var addressMap = map.address.GetOrAdd(log.Address, _ => new());
                addressMap.Add(blockNumber);

                foreach (var topic in log.Topics)
                {
                    var topicMap = map.topic.GetOrAdd(topic, _ => new());
                    topicMap.Add(blockNumber);
                }
            }

            return map;
        }

        //[Repeat(10)]
        [Explicit("Can take a lot of time, CPU, and disk")]
        [TestCase(100, 100, 16, int.MaxValue)]
        [TestCase(100, 100, 16, 100)]
        [TestCase(100, 100, 8, int.MaxValue)]
        [TestCase(100, 100, 8, 100)]
        [TestCase(100, 100, 1, int.MaxValue)]
        [TestCase(100, 100, 1, 100)]
        public async Task LargeTest(int batchCount, int blocksPerBatch, int ioParallelization, int compactionDistance)
        {
            // Arrange
            var batches = GenerateBatches(batchCount, blocksPerBatch);
            var map = MapToBlockNumber(batches.SelectMany(b => b));

            // Act
            await using var logIndexStorage = CreateLogIndexStorage();
            foreach (var batch in batches)
                await logIndexStorage.SetReceiptsAsync(batch, false);

            // Assert
            foreach (var (address, expectedNums) in map.address)
            {
                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(address, 0, int.MaxValue),
                    Is.EquivalentTo(expectedNums.Order())
                );
            }
            foreach (var (topic, expectedNums) in map.topic)
            {
                Assert.That(
                    logIndexStorage.GetBlockNumbersFor(topic, 0, int.MaxValue),
                    Is.EquivalentTo(expectedNums.Order())
                );
            }
        }

        private static IEnumerable<(byte[] key, byte[] value)> Enumerate(IIterator<byte[], byte[]> iterator)
        {
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return (iterator.Key(), iterator.Value());
                iterator.Next();
            }
        }

        private static IEnumerable<(byte[] key, byte[] value)> Enumerate(IDb db)
        {
            // TODO: dispose iterator?
            return Enumerate(db.GetIterator(true));
        }
    }
}
