// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only 

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Baseline.Tree;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [Parallelizable(ParallelScope.All)]
    public class BaselineTreeMetadataTests
    {
        [TestCase(0, 13)]
        [TestCase(1, 0)]
        [TestCase(2, 3)]
        [TestCase(3, 100)]
        public void Saving_loading_current_block(int keccakIndex, long lastBlockWithLeaves)
        {
            var lastBlockDbHash = TestItem.Keccaks[keccakIndex];
            var baselineMetaData = new BaselineTreeMetadata(new MemDb(), new byte[] { }, LimboNoErrorLogger.Instance);
            baselineMetaData.SaveCurrentBlockInDb(lastBlockDbHash, lastBlockWithLeaves);
            var actual = baselineMetaData.LoadCurrentBlockInDb();
            Assert.AreEqual(lastBlockDbHash, actual.LastBlockDbHash);
            Assert.AreEqual(lastBlockWithLeaves, actual.LastBlockWithLeaves);
        }

        [TestCase(5, (uint)13, 4)]
        [TestCase(6, (uint)0, 5)]
        [TestCase(7, (uint)3, 6)]
        [TestCase(8, (uint)100, 6)]
        public void Saving_loading_block_number_count(long blockNumber, uint count, long previousBlockWithLeaves)
        {
            var baselineMetaData = new BaselineTreeMetadata(new MemDb(), new byte[] { }, LimboNoErrorLogger.Instance);
            baselineMetaData.SaveBlockNumberCount(blockNumber, count, previousBlockWithLeaves);
            var actual = baselineMetaData.LoadBlockNumberCount(blockNumber);
            actual.Count.Should().Be(count);
            actual.PreviousBlockWithLeaves.Should().Be(previousBlockWithLeaves);
        }

        [Test]
        public void GetLeavesCountFromPreviousBlocks([ValueSource(nameof(GetLeavesCountFromPreviousBlockTestCases))] GetLeavesCountTest test)
        {
            var baselineMetaData = new BaselineTreeMetadata(new MemDb(), new byte[] { }, LimboNoErrorLogger.Instance);
            for (int i = 0; i < test.DataToSave.Length; ++i)
            {
                baselineMetaData.SaveBlockNumberCount(test.DataToSave[i].BlockNumber, test.DataToSave[i].Count, test.DataToSave[i].PreviousBlockWithLeaves);
            }

            var actual = baselineMetaData.GoBackTo(test.BlockNumber - 1, test.LastBlockWithLeaves);
            Assert.AreEqual(test.ExpectedResult, actual.Count);
        }

        [Test]
        public void GetLeavesCountByBlockNumber([ValueSource(nameof(GetLeavesCountByBlockNumberTestCases))] GetLeavesCountTest test)
        {
            var baselineMetaData = new BaselineTreeMetadata(new MemDb(), new byte[] { }, LimboNoErrorLogger.Instance);
            for (int i = 0; i < test.DataToSave.Length; ++i)
            {
                baselineMetaData.SaveBlockNumberCount(test.DataToSave[i].BlockNumber, test.DataToSave[i].Count, test.DataToSave[i].PreviousBlockWithLeaves);
            }

            var actual = baselineMetaData.GetBlockCount(test.LastBlockWithLeaves, test.BlockNumber);
            Assert.AreEqual(test.ExpectedResult, actual);
        }

        public class GetLeavesCountTest
        {
            public (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[] DataToSave { get; set; }

            public long LastBlockWithLeaves { get; set; }

            public long BlockNumber { get; set; }

            public uint ExpectedResult { get; set; }

            public override string ToString() => $"Expected result: {ExpectedResult}, Block number: {BlockNumber}";
        }

        public static IEnumerable<GetLeavesCountTest> GetLeavesCountByBlockNumberTestCases
        {
            get
            {
                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (1, 2, 0),
                        (2, 4, 1)
                    },
                    LastBlockWithLeaves = 2,
                    BlockNumber = 2,
                    ExpectedResult = 4
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (6, 1, 0),
                        (7, 3, 6)
                    },
                    LastBlockWithLeaves = 7,
                    BlockNumber = 7,
                    ExpectedResult = 3
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (6, 1, 0),
                    },
                    LastBlockWithLeaves = 6,
                    BlockNumber = 6,
                    ExpectedResult = 1
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (3, 1, 0),
                        (5, 4, 3)
                    },
                    LastBlockWithLeaves = 5,
                    BlockNumber = 3,
                    ExpectedResult = 1
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (3, 1, 0),
                        (5, 4, 3)
                    },
                    LastBlockWithLeaves = 5,
                    BlockNumber = 2,
                    ExpectedResult = 0
                };
            }
        }

        public static IEnumerable<GetLeavesCountTest> GetLeavesCountFromPreviousBlockTestCases
        {
            get
            {
                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (1, 2, 0),
                        (2, 4, 1)
                    },
                    LastBlockWithLeaves = 2,
                    BlockNumber = 2,
                    ExpectedResult = 2
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (6, 1, 0),
                        (7, 3, 6)
                    },
                    LastBlockWithLeaves = 7,
                    BlockNumber = 7,
                    ExpectedResult = 1
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (6, 1, 0),
                    },
                    LastBlockWithLeaves = 6,
                    BlockNumber = 6,
                    ExpectedResult = 0
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (3, 1, 0),
                        (5, 4, 3)
                    },
                    LastBlockWithLeaves = 5,
                    BlockNumber = 3,
                    ExpectedResult = 0
                };

                yield return new GetLeavesCountTest()
                {
                    DataToSave = new (long BlockNumber, uint Count, long PreviousBlockWithLeaves)[]
                    {
                        (3, 1, 0),
                        (5, 4, 3)
                    },
                    LastBlockWithLeaves = 5,
                    BlockNumber = 2,
                    ExpectedResult = 0
                };
            }
        }
    }
}
