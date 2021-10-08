//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [Parallelizable(ParallelScope.All)]
    public class BaselineTreeHelperTests
    {
        [Test]
        public void GetHistoricalLeaf_return_expected_results([ValueSource(nameof(GetHistoricalLeafTestCases))]GetHistoricalLeavesTest test)
        {
            var logFinder = Substitute.For<ILogFinder>();
            var mainDb = new MemDb();
            var metadaDataDb = new MemDb();
            var baselineTreeHelper = new BaselineTreeHelper(logFinder, new MemDb(), new MemDb(), LimboNoErrorLogger.Instance);
            var baselineTree = new ShaBaselineTree(mainDb, metadaDataDb, new byte[] { }, BaselineModule.TruncationLength, LimboNoErrorLogger.Instance);

            Stack<long> lastBlockWithLeavesCheck = new Stack<long>();
            for (int i = 0; i < test.Blocks.Length; i++)
            {
                var block = test.Blocks[i];
                for (int j = 0; j < block.Leaves.Length; j++)
                {
                    baselineTree.Insert(block.Leaves[j]);
                }

                baselineTree.MemorizeCurrentCount(TestItem.Keccaks[block.BlockNumber], block.BlockNumber, baselineTree.Count);
                lastBlockWithLeavesCheck.Push(block.BlockNumber);
                baselineTree.LastBlockWithLeaves.Should().Be(lastBlockWithLeavesCheck.Peek());
            }

            for (int i = 0; i < test.ExpectedHashes.Length; i++)
            {
                var leavesAndBlocks = test.LeavesAndBlocksQueries[i];
                var leaf = baselineTreeHelper.GetHistoricalLeaf(baselineTree, leavesAndBlocks.LeavesIndexes[0], leavesAndBlocks.BlockNumber);
                Assert.AreEqual(test.ExpectedHashes[i][0], leaf.Hash);
            }
        }

        [Test]
        public void GetHistoricalLeaves_return_expected_results([ValueSource(nameof(GetHistoricalLeafTestCases))]GetHistoricalLeavesTest test)
        {
            var logFinder = Substitute.For<ILogFinder>();
            var mainDb = new MemDb();
            var metadaDataDb = new MemDb();
            var baselineTreeHelper = new BaselineTreeHelper(logFinder, new MemDb(), new MemDb(), LimboNoErrorLogger.Instance);
            var baselineTree = new ShaBaselineTree(mainDb, metadaDataDb, new byte[] { }, BaselineModule.TruncationLength, LimboNoErrorLogger.Instance);

            for (int i = 0; i < test.Blocks.Length; i++)
            {
                var block = test.Blocks[i];
                for (int j = 0; j < block.Leaves.Length; j++)
                {
                    baselineTree.Insert(block.Leaves[j]);
                }
                
                baselineTree.MemorizeCurrentCount(TestItem.Keccaks[block.BlockNumber], block.BlockNumber, (uint)block.Leaves.Length);
            }

            for (int i = 0; i < test.ExpectedHashes.Length; i++)
            {
                var leavesAndBlocks = test.LeavesAndBlocksQueries[i];
                var leaves = baselineTreeHelper.GetHistoricalLeaves(baselineTree, leavesAndBlocks.LeavesIndexes, leavesAndBlocks.BlockNumber);
                for (int j = 0; j < leavesAndBlocks.LeavesIndexes.Length; j++)
                {
                    Assert.AreEqual(test.ExpectedHashes[i][j], leaves[j].Hash);
                }
            }
        }

        [Test]
        public void GetHistoricalTree_return_expected_results([ValueSource(nameof(HistoricalTreeTestCases))]GetHistoricalLeavesTest test)
        {
            var address = TestItem.AddressA;
            var logFinder = Substitute.For<ILogFinder>();
            var mainDb = new MemDb();
            var metadataDataDb = new MemDb();
            var baselineTreeHelper = new BaselineTreeHelper(logFinder, mainDb, metadataDataDb, LimboNoErrorLogger.Instance);
            var baselineTree = new ShaBaselineTree(mainDb, metadataDataDb, address.Bytes, BaselineModule.TruncationLength, LimboNoErrorLogger.Instance);
            
            for (int i = 0; i < test.Blocks.Length; i++)
            {
                var block = test.Blocks[i];
                for (int j = 0; j < block.Leaves.Length; j++)
                {
                    baselineTree.Insert(block.Leaves[j]);
                }
                
                baselineTree.MemorizeCurrentCount(TestItem.Keccaks[block.BlockNumber], block.BlockNumber, (uint)block.Leaves.Length);
            }

            var historicalTree = baselineTreeHelper.CreateHistoricalTree(address, 1);
            Assert.AreNotEqual(historicalTree.Count, baselineTree.Count);
            Assert.AreNotEqual(historicalTree.Root, baselineTree.Root);
        }

        public class GetHistoricalLeavesTest
        {
            public TestBlock[] Blocks { get; set; }

            public (uint[] LeavesIndexes, long BlockNumber)[] LeavesAndBlocksQueries { get; set; }

            public long[] BlockNumbers { get; set; }

            public Keccak[][] ExpectedHashes { get; set; }

            public override string ToString() => "Blocks: " + string.Join("; ", Blocks.Select(x => x.BlockNumber.ToString())) + $" Number of queries:{LeavesAndBlocksQueries?.Length}";
        }

        public class TestBlock
        {
            public long BlockNumber { get; set; }

            public Keccak[] Leaves { get; set; }
        }

        public static IEnumerable<GetHistoricalLeavesTest> GetHistoricalLeafTestCases
        {
            get
            {
                yield return new GetHistoricalLeavesTest()
                {
                    Blocks = Array.Empty<TestBlock>(),
                    LeavesAndBlocksQueries = new (uint[] LeafIndex, long BlockNumber)[]
                   {
                       (new uint[] { 0 }, 0), (new uint[] { 1 }, 0)
                   },
                    ExpectedHashes = new Keccak[][]
                   {
                       new Keccak[] { Keccak.Zero }, new Keccak[] { Keccak.Zero }
                   }
                };

                yield return new GetHistoricalLeavesTest()
                {
                    Blocks = new TestBlock[]
                    {
                        new TestBlock()
                        {
                            BlockNumber = 1,
                            Leaves = new Keccak[] { TestItem.KeccakA, TestItem.KeccakB }
                        }
                    },
                    LeavesAndBlocksQueries = new (uint[] LeafIndex, long BlockNumber)[]
                    {
                       (new uint[] { 0 }, 0), (new uint[] { 1 }, 0), (new uint[] { 0 }, 1), (new uint[] { 1 }, 1), (new uint[] { 2 }, 1)
                    },
                    ExpectedHashes = new Keccak[][]
                    {
                       new Keccak[] { Keccak.Zero }, new Keccak[] { Keccak.Zero }, new Keccak[] { TestItem.KeccakA }, new Keccak[] { TestItem.KeccakB }, new Keccak[] { Keccak.Zero }
                    }
                };

                yield return new GetHistoricalLeavesTest()
                {
                    Blocks = new TestBlock[]
                   {
                        new TestBlock()
                        {
                            BlockNumber = 1,
                            Leaves = new Keccak[] { TestItem.KeccakA }
                        },
                        new TestBlock()
                        {
                            BlockNumber = 3,
                            Leaves = new Keccak[] { TestItem.KeccakB, TestItem.KeccakC }
                        }
                   },
                    LeavesAndBlocksQueries = new (uint[] LeafIndex, long BlockNumber)[]
                   {
                       (new uint[] { 0 }, 0), (new uint[] { 1 }, 0), (new uint[] { 0 }, 1), (new uint[] { 1 }, 1), (new uint[] { 2 }, 1), (new uint[] { 0 }, 2)
                   },
                    ExpectedHashes = new Keccak[][]
                   {
                       new Keccak[] { Keccak.Zero }, new Keccak[] { Keccak.Zero }, new Keccak[] { TestItem.KeccakA }, new Keccak[] { Keccak.Zero }, new Keccak[] { Keccak.Zero },  new Keccak[] { TestItem.KeccakA },
                   }
                };

                yield return new GetHistoricalLeavesTest()
                {
                    Blocks = new TestBlock[]
                   {
                        new TestBlock()
                        {
                            BlockNumber = 1,
                            Leaves = new Keccak[] { TestItem.KeccakA }
                        },
                        new TestBlock()
                        {
                            BlockNumber = 3,
                            Leaves = new Keccak[] { TestItem.KeccakB, TestItem.KeccakC }
                        }
                   },
                    LeavesAndBlocksQueries = new (uint[] LeafIndex, long BlockNumber)[]
                   {
                       (new uint[] { 1 }, 1)
                   },
                    ExpectedHashes = new Keccak[][]
                   {
                       new Keccak[] { Keccak.Zero }
                   }
                };
            }
        }

        public static IEnumerable<GetHistoricalLeavesTest> HistoricalTreeTestCases
        {
            get
            {

                yield return new GetHistoricalLeavesTest()
                {
                    Blocks = new TestBlock[]
                    {
                        new TestBlock()
                        {
                            BlockNumber = 1,
                            Leaves = new Keccak[] { TestItem.KeccakA, TestItem.KeccakB }
                        },
                        new TestBlock()
                        {
                            BlockNumber = 2,
                            Leaves = new Keccak[] { TestItem.KeccakC, TestItem.KeccakD }
                        }
                    },
                };
            }
        }
    }
}
