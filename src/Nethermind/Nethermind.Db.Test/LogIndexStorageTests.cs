using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _dbPath = $"testdb/{nameof(LogIndexStorageTests)}";
            _tempFilePath = Path.Combine(_dbPath, "tempfile.bin");
            _finalFilePath = Path.Combine(_dbPath, "finalfile.bin");

            if (Directory.Exists(_dbPath))
            {
                Directory.Delete(_dbPath, true);
            }
            Directory.CreateDirectory(_dbPath);

            _columnsDb = new ColumnsDb<LogIndexColumns>(
                _dbPath,
                new DbSettings("logindex", _dbPath)
                {
                    BlockCacheSize = (ulong)1.KiB(),
                    CacheIndexAndFilterBlocks = false,
                    DeleteOnStart = true,
                    WriteBufferNumber = 4,
                    WriteBufferSize = (ulong)1.KiB()
                },
                new DbConfig(),
                LimboLogs.Instance,
                Enum.GetValues<LogIndexColumns>()
            );

            _logger = LimboLogs.Instance.GetClassLogger();
            _logIndexStorage = new LogIndexStorage(_columnsDb, _logger, _tempFilePath, _finalFilePath);
        }

        [TearDown]
        public void TearDown()
        {
            _logIndexStorage.Dispose();
            _columnsDb.Dispose();
            if (Directory.Exists(_dbPath))
            {
                Directory.Delete(_dbPath, true);
            }
        }

        [Test]
        public void SetReceipts_SavesCorrectAddresses()
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
                        new LogEntry(address1, Array.Empty<byte>(), Array.Empty<Hash256>()),
                        new LogEntry(address1, Array.Empty<byte>(), Array.Empty<Hash256>()), // Multiple logs for the same address
                        new LogEntry(address2, Array.Empty<byte>(), Array.Empty<Hash256>())
                    }.ToArray()
                }
            };

            // Act
            _logIndexStorage.SetReceipts(blockNumber, receipts, isBackwardSync: false);

            // Assert
            var addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            bool foundAddress1 = false;
            bool foundAddress2 = false;
            using (var iterator = addressDb.GetIterator(true))
            {
                while (iterator.Valid())
                {
                    var key = iterator.Key();
                    if (key.AsSpan(0, 20).SequenceEqual(new Address("0x0000000000000000000000000000000000001234").Bytes))
                    {
                        foundAddress1 = true;
                    }
                    if (key.AsSpan(0, 20).SequenceEqual(new Address("0x0000000000000000000000000000000000005678").Bytes))
                    {
                        foundAddress2 = true;
                    }
                    iterator.Next();
                }
            }

            foundAddress1.Should().BeTrue();
            foundAddress2.Should().BeTrue();
        }

        [Test]
            public void SetReceipts_MovesToFinalizedFile()
        {
            // Arrange
            var address = new Address("0x0000000000000000000000000000000000001234");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new LogEntry(address, Array.Empty<byte>(), Array.Empty<Hash256>())
                }.ToArray()
            };

            // Act
            for (int i = 1; i <= 2000; i++)
            {
                _logIndexStorage.SetReceipts(i, new[] { receipt }, isBackwardSync: false);
            }

            _logIndexStorage.Dispose();

            // Assert
            File.Exists(_finalFilePath).Should().BeTrue("The finalized file should be created and contain the data.");

            using var finalFileStream = new FileStream(_finalFilePath, FileMode.Open, FileAccess.Read);
            finalFileStream.Length.Should().BeGreaterThan(0, "The finalized file should not be empty.");
        }

        [Test]
        public void GetBlockNumbersFor_ReturnsCorrectBlocks()
        {
            // Arrange
            var address = new Address("0x0000000000000000000000000000000000001234");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
                {
                    new LogEntry(address, Array.Empty<byte>(), Array.Empty<Hash256>())
                }.ToArray()
            };
            var expectedBlocks = new List<int>();
            for (int i = 1; i <= 2000; i++)
            {
                expectedBlocks.Add(i);
                _logIndexStorage.SetReceipts(i, new[] { receipt }, isBackwardSync: false);
            }

            // Assert
            var resultBlocks = _logIndexStorage.GetBlockNumbersFor(address, 1, 2000).ToList();
            resultBlocks.Should().BeEquivalentTo(expectedBlocks, "The blocks returned should match the blocks that were saved.");
        }

        [Test]
        public void SetReceipts_SavesLogsWithDifferentTopics()
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
                new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { topic1 }),
                new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { topic2 })
            }.ToArray()
        }
    };

            // Act
            _logIndexStorage.SetReceipts(blockNumber, receipts, isBackwardSync: false);

            // Assert
            var topicDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);
            bool foundTopic1 = false;
            bool foundTopic2 = false;
            using (var iterator = topicDb.GetIterator(true))
            {
                while (iterator.Valid())
                {
                    var key = iterator.Key();
                    if (key.AsSpan(0, 32).SequenceEqual(topic1.Bytes.ToArray()))
                    {
                        foundTopic1 = true;
                    }
                    if (key.AsSpan(0, 32).SequenceEqual(topic2.Bytes.ToArray()))
                    {
                        foundTopic2 = true;
                    }
                    iterator.Next();
                }
            }

            foundTopic1.Should().BeTrue();
            foundTopic2.Should().BeTrue();
        }

        [Test]
        public void SetReceipts_DistinguishesOverlappingAddressAndTopicKeys()
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
                new LogEntry(address, Array.Empty<byte>(), new Hash256[] { topic })
            }.ToArray()
        }
    };

            // Act
            _logIndexStorage.SetReceipts(blockNumber, receipts, isBackwardSync: false);

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
        public void SetReceipts_SavesMixedLogsCorrectly()
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
                new LogEntry(address, Array.Empty<byte>(), Array.Empty<Hash256>()),
                new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { topic })
            }.ToArray()
        }
    };

            // Act
            _logIndexStorage.SetReceipts(blockNumber, receipts, isBackwardSync: false);

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
        public void SetReceipts_LargeNumberOfReceiptsAcrossMultipleBlocks()
        {
            // Arrange
            var address1 = new Address("0x0000000000000000000000000000000000001234");
            var topic1 = new Hash256("0x0000000000000000000000000000000000000000000000000000000000000001");
            var receipt = new TxReceipt
            {
                Logs = new List<LogEntry>
        {
            new LogEntry(address1, Array.Empty<byte>(), new[] { topic1 })
        }.ToArray()
            };

            // Act
            for (int i = 1; i <= 10000; i++)
            {
                _logIndexStorage.SetReceipts(i, new[] { receipt }, isBackwardSync: false);
            }

            _logIndexStorage.Dispose();

            // Assert
            File.Exists(_finalFilePath).Should().BeTrue("The finalized file should be created and contain the data.");
            using var finalFileStream = new FileStream(_finalFilePath, FileMode.Open, FileAccess.Read);
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
            new LogEntry(address1, Array.Empty<byte>(), new[] { topic1 })
        }.ToArray()
            };

            // Act
            _logIndexStorage.SetReceipts(1, new[] { receipt }, isBackwardSync: false);

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
            new LogEntry(address1, Array.Empty<byte>(), new[] { topic1 }),
            new LogEntry(address2, Array.Empty<byte>(), new[] { topic2 })
        }.ToArray()
            };

            // Act
            _logIndexStorage.SetReceipts(1, new[] { receipt }, isBackwardSync: false);

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
            List<Address> generatedAddresses = new List<Address>();
            List<Hash256> generatedTopics = new List<Hash256>();
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
                addressBlockMap[address] = new List<int>();
                topicBlockMap[topic] = new List<int>();
            }

            // Act: Create receipts and distribute them across blocks
            for (int blockNumber = 1; blockNumber <= numberOfBlocks; blockNumber++)
            {
                var logs = new List<LogEntry>();
                for (int i = 0; i < 5; i++) // 5 logs per block
                {
                    var address = generatedAddresses[random.Next(generatedAddresses.Count)];
                    var topic = generatedTopics[random.Next(generatedTopics.Count)];
                    logs.Add(new LogEntry(address, Array.Empty<byte>(), new[] { topic }));

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
                _logIndexStorage.SetReceipts(blockNumber, new[] { receipt }, isBackwardSync: false);
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



    }
}
