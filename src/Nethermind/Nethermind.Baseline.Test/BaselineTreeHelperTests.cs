//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [Parallelizable(ParallelScope.All)]
    public class BaselineTreeHelperTests
    {
        [Test]
        public void GetHistoricalLeaf([ValueSource(nameof(GetHistoricalLeafTestCases))]GetHistoricalLeavesTest test)
        {
            var logFinder = Substitute.For<ILogFinder>();
            var mainDb = new MemDb();
            var metadaDataDb = new MemDb();
            var baselineTreeHelper = new BaselineTreeHelper(logFinder, new MemDb(), new MemDb());
            var baselineTree = new ShaBaselineTree(mainDb, metadaDataDb, new byte[] { }, BaselineModule.TruncationLength);

            long lastBlockWithLeaves = 0;
            for (int i = 0; i < test.Blocks.Length; i++)
            {
                var block = test.Blocks[i];
                for (int j = 0; j < block.Leaves.Length; j++)
                {
                    baselineTree.Insert(block.Leaves[j]);
                }

                baselineTree.Metadata.SaveBlockNumberCount(block.BlockNumber, (uint)block.Leaves.Length, lastBlockWithLeaves);
                lastBlockWithLeaves = block.BlockNumber;
                baselineTree.LastBlockWithLeaves = lastBlockWithLeaves;
            }

            for (int i = 0; i < test.ExpectedHashes.Length; i++)
            {
                var leavesAndBlocks = test.LeavesAndBlocksQueries[i];
                var leaf = baselineTreeHelper.GetHistoricalLeaf(baselineTree, leavesAndBlocks.LeavesIndexes[0], leavesAndBlocks.BlockNumber);
                Assert.AreEqual(test.ExpectedHashes[i][0], leaf.Hash);
            }
        }

        public class GetHistoricalLeavesTest
        {
            public TestBlock[] Blocks { get; set; }

            public (uint[] LeavesIndexes, long BlockNumber)[] LeavesAndBlocksQueries { get; set; }

            public long[] BlockNumbers { get; set; }

            public Keccak[][] ExpectedHashes { get; set; }

            public override string ToString() => "Blocks: " + string.Join("; ", Blocks.Select(x => x.BlockNumber.ToString()));
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
            }
        }
    }
}
