using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Db.Test
{
    [TestFixture]
    public class LogIndexStorageTests
    {
        private LogIndexStorage _logIndexStorage;
        private ColumnsDb<LogIndexColumns> _columnsDb;
        private ILogger _logger;
        private string _dbPath;
        private string _tempFilePath;
        private string _finalFilePath;

        [SetUp]
        public void Setup()
        {
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

            _logger = LimboLogs.Instance.GetClassLogger();

            _logIndexStorage = new(_columnsDb, _logger, _dbPath, 8);
            _tempFilePath = _logIndexStorage.TempFilePath;
            _finalFilePath = _logIndexStorage.FinalFilePath;
        }

        [TearDown]
        public async Task TearDownAsync()
        {
            await ((IAsyncDisposable)_logIndexStorage).DisposeAsync();
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
            await _logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false, CancellationToken.None);

            // Assert
            IDb addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            using IIterator<byte[], byte[]> iterator = addressDb.GetIterator(true);

            Enumerate(iterator)
                .Select(x => new Address(x.key[..Address.Size])).ToHashSet()
                .Should().BeEquivalentTo([address1, address2]);
        }

        [Test]
        public async Task SetReceipts_MovesToFinalizedFile()
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
            for (int i = 1; i <= 2000; i++)
            {
                await _logIndexStorage.SetReceiptsAsync(i, [receipt], isBackwardSync: false, CancellationToken.None);
            }

            await _logIndexStorage.TryDisposeAsync();

            // Assert
            File.Exists(_finalFilePath).Should().BeTrue("The finalized file should be created and contain the data.");

            await using var finalFileStream = new FileStream(_finalFilePath, FileMode.Open, FileAccess.Read);
            finalFileStream.Length.Should().BeGreaterThan(0, "The finalized file should not be empty.");
        }

        [Test]
        public async Task GetBlockNumbersFor_ReturnsCorrectBlocks()
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
            var expectedBlocks = new List<int>();
            for (int i = 1; i <= 2000; i++)
            {
                expectedBlocks.Add(i);
                await _logIndexStorage.SetReceiptsAsync(i, [receipt], isBackwardSync: false, CancellationToken.None);
            }

            // Assert
            var resultBlocks = _logIndexStorage.GetBlockNumbersFor(address, 1, 2000).ToList();
            resultBlocks.Should().BeEquivalentTo(expectedBlocks, "The blocks returned should match the blocks that were saved.");
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
            await _logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false, CancellationToken.None);

            // Assert
            IDb topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);
            using IIterator<byte[], byte[]> iterator = topicDb.GetIterator(true);

            Enumerate(iterator)
                .Select(x => new Hash256(x.key[..Hash256.Size])).ToHashSet()
                .Should().BeEquivalentTo([topic1, topic2]);
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
            await _logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false, CancellationToken.None);

            // Assert
            IDb addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            IDb topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);

            using IIterator<byte[], byte[]> addressIterator = addressDb.GetIterator(true);
            using IIterator<byte[], byte[]> topicIterator = topicDb.GetIterator(true);

            Enumerable.Concat<object>(
                    Enumerate(addressIterator).Select(x => new Address(x.key[..Address.Size])).ToHashSet(),
                    Enumerate(topicIterator).Select(x => new Hash256(x.key[..Hash256.Size])).ToHashSet()
                ).ToArray()
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
            await _logIndexStorage.SetReceiptsAsync(blockNumber, receipts, isBackwardSync: false, CancellationToken.None);

            // Assert
            var addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            var topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);

            bool foundAddress = false;
            bool foundTopic = false;

            using (var iterator = addressDb.GetIterator(true))
            {
                while (iterator.Valid())
                {
                    var key = iterator.Key();
                    if (key.AsSpan(0, 20).SequenceEqual(address.Bytes))
                    {
                        foundAddress = true;
                    }
                    iterator.Next();
                }
            }

            using (var iterator = topicDb.GetIterator(true))
            {
                while (iterator.Valid())
                {
                    var key = iterator.Key();
                    if (key.AsSpan(0, 32).SequenceEqual(topic.Bytes.ToArray()))
                    {
                        foundTopic = true;
                    }
                    iterator.Next();
                }
            }

            foundAddress.Should().BeTrue();
            foundTopic.Should().BeTrue();
        }

        [Test]
        public async Task SetReceipts_LargeNumberOfReceiptsAcrossMultipleBlocks()
        {
            // Arrange
            var address1 = new Address("0x0000000000000000000000000000000000001234");
            var topic1 = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001");
            var address2 = new Address("0x0000000000000000000000000000000000005678");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new(address1, [], [topic1]),
                    new(address2, [], [])
                }.ToArray()
            };

            // Act
            var receiptBatch = Enumerable.Repeat(receipt, 10).ToArray();
            for (int i = 1; i <= 2000; i++)
            {
                await _logIndexStorage.SetReceiptsAsync(i, receiptBatch, isBackwardSync: false, CancellationToken.None);
            }

            await _logIndexStorage.TryDisposeAsync();

            // Assert
            File.Exists(_finalFilePath).Should().BeTrue("The finalized file should be created and contain the data.");
            await using var finalFileStream = new FileStream(_finalFilePath, FileMode.Open, FileAccess.Read);
            finalFileStream.Length.Should().BeGreaterThan(0, "The finalized file should not be empty.");
        }

        [Test]
        public void SetReceipts_MixedAddressAndTopicEntriesInSameBlock()
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
            _logIndexStorage.SetReceiptsAsync(1, [receipt], isBackwardSync: false, CancellationToken.None);

            // Assert
            var addressBlocks = _logIndexStorage.GetBlockNumbersFor(address1, 1, 1).ToList();
            var topicBlocks = _logIndexStorage.GetBlockNumbersFor(topic1, 1, 1).ToList();

            addressBlocks.Should().Contain(1, "The block number should be indexed for the address.");
            topicBlocks.Should().Contain(1, "The block number should be indexed for the topic.");
        }

        [Test]
        public void SetReceipts_MultipleTopicsAndAddressesInSingleBlock()
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
            _logIndexStorage.SetReceiptsAsync(1, [receipt], isBackwardSync: false, CancellationToken.None);

            // Assert
            var addressBlocks1 = _logIndexStorage.GetBlockNumbersFor(address1, 1, 1).ToList();
            var addressBlocks2 = _logIndexStorage.GetBlockNumbersFor(address2, 1, 1).ToList();
            var topicBlocks1 = _logIndexStorage.GetBlockNumbersFor(topic1, 1, 1).ToList();
            var topicBlocks2 = _logIndexStorage.GetBlockNumbersFor(topic2, 1, 1).ToList();

            addressBlocks1.Should().Contain(1, "The block number should be indexed for address1.");
            addressBlocks2.Should().Contain(1, "The block number should be indexed for address2.");
            topicBlocks1.Should().Contain(1, "The block number should be indexed for topic1.");
            topicBlocks2.Should().Contain(1, "The block number should be indexed for topic2.");
        }

        [Test]
        public void SetReceipts_CheckValidityWithDiverseData()
        {
            // Arrange
            int numberOfBlocks = 100;
            List<Address> generatedAddresses = [];
            List<Hash256> generatedTopics = [];
            Dictionary<Address, List<int>> addressBlockMap = new Dictionary<Address, List<int>>();
            Dictionary<Hash256, List<int>> topicBlockMap = new Dictionary<Hash256, List<int>>();
            Random random = new Random();

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
                    var address = generatedAddresses[random.Next(generatedAddresses.Count)];
                    var topic = generatedTopics[random.Next(generatedTopics.Count)];
                    logs.Add(new(address, [], [topic]));

                    // Track which blocks these addresses and topics appear in
                    if (!addressBlockMap[address].Contains(blockNumber))
                    {
                        addressBlockMap[address].Add(blockNumber);
                    }
                    if (!topicBlockMap[topic].Contains(blockNumber))
                    {
                        topicBlockMap[topic].Add(blockNumber);
                    }
                }
                var receipt = new TxReceipt { Logs = logs.ToArray() };
                _logIndexStorage.SetReceiptsAsync(blockNumber, [receipt], isBackwardSync: false, CancellationToken.None);
            }

            // Assert: Check that each address and topic returns the correct block numbers
            foreach (var kvp in addressBlockMap)
            {
                var address = kvp.Key;
                var expectedBlocks = kvp.Value;
                var resultBlocks = _logIndexStorage.GetBlockNumbersFor(address, 1, numberOfBlocks).ToList();
                resultBlocks.Should().BeEquivalentTo(expectedBlocks, $"Address {address} should have the correct block numbers.");
            }

            foreach (var kvp in topicBlockMap)
            {
                var topic = kvp.Key;
                var expectedBlocks = kvp.Value;
                var resultBlocks = _logIndexStorage.GetBlockNumbersFor(topic, 1, numberOfBlocks).ToList();
                resultBlocks.Should().BeEquivalentTo(expectedBlocks, $"Topic {topic} should have the correct block numbers.");
            }
        }

        private static IEnumerable<(TKey key, TValue value)> Enumerate<TKey, TValue>(IIterator<TKey, TValue> iterator)
        {
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return (iterator.Key(), iterator.Value());
                iterator.Next();
            }
        }
    }
}
