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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using Index = Nethermind.Baseline.Tree.BaselineTree.Index;

namespace Nethermind.Baseline.Test
{
    [TestFixture(0)]
    [TestFixture(5)]
    [Parallelizable(ParallelScope.All)]
    public class BaselineTreeTests
    {
        private readonly int _truncationLength;
        private Keccak[] _testLeaves = new Keccak[32];
        private const ulong _nodeIndexOfTheFirstLeaf = (1ul << BaselineTree.TreeHeight) - 1ul;
        private const ulong _lastNodeIndex = (1ul << (BaselineTree.TreeHeight + 1)) - 2ul;
        private const uint _lastLeafIndex = (uint) ((1ul << BaselineTree.TreeHeight) - 1u);
        private const uint _lastRow = BaselineTree.TreeHeight;

        public BaselineTreeTests(int truncationLength)
        {
            _truncationLength = truncationLength;
        }

        [OneTimeSetUp]
        public void Setup()
        {
            for (int i = 0; i < _testLeaves.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i % (32 - _truncationLength) + _truncationLength] = (byte) (i + 1);
                _testLeaves[i] = new Keccak(bytes);
            }
        }

        private BaselineTree BuildATree(IDb db = null)
        {
            return new ShaBaselineTree(db ?? new MemDb(), new MemDb(), new byte[] { }, _truncationLength, LimboNoErrorLogger.Instance);
        }

        [Test]
        public void Initially_count_is_0()
        {
            BaselineTree baselineTree = BuildATree();
            baselineTree.Count.Should().Be(0);
        }

        [TestCase(ulong.MinValue, null)]
        [TestCase(1ul, null)]
        [TestCase(_nodeIndexOfTheFirstLeaf - 1, null)]
        [TestCase(_nodeIndexOfTheFirstLeaf, uint.MinValue)]
        [TestCase(_nodeIndexOfTheFirstLeaf + 1, 1u)]
        [TestCase(_lastNodeIndex, _lastLeafIndex)]
        public void Can_calculate_leaf_index_from_node_index(ulong nodeIndex, uint? leafIndex)
        {
            if (leafIndex == null)
            {
                new Index(nodeIndex).Row.Should().NotBe(BaselineTree.LeafRow);
            }
            else
            {
                new Index(nodeIndex).Row.Should().Be(BaselineTree.LeafRow);
                new Index(nodeIndex).IndexAtRow.Should().Be(leafIndex.Value);
            }
        }

        [TestCase(uint.MinValue, uint.MinValue, ulong.MinValue)]
        [TestCase(1u, uint.MinValue, 1ul)]
        [TestCase(1u, 1u, 2ul)]
        [TestCase(2u, uint.MinValue, 3ul)]
        [TestCase(2u, 1u, 4ul)]
        [TestCase(2u, 2u, 5ul)]
        [TestCase(2u, 3u, 6ul)]
        [TestCase(2u, 4u, null)]
        [TestCase(1u, 2u, null)]
        [TestCase(_lastRow, uint.MinValue, _nodeIndexOfTheFirstLeaf)]
        [TestCase(_lastRow, 1u, _nodeIndexOfTheFirstLeaf + 1)]
        [TestCase(_lastRow, _lastLeafIndex, _lastNodeIndex)]
        [TestCase(_lastRow + 1, uint.MinValue, null)]
        [TestCase(_lastRow + 1, _lastLeafIndex, null)]
        public void Can_calculate_node_index_from_row_and_index_at_row(uint row, uint indexAtRow, ulong? nodeIndex)
        {
            if (nodeIndex == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new Index(row, indexAtRow));
            }
            else
            {
                new Index(row, indexAtRow).NodeIndex.Should().Be(nodeIndex.Value);
            }
        }

        [TestCase(uint.MinValue, ulong.MinValue, uint.MinValue)]
        [TestCase(1u, ulong.MinValue, null)]
        [TestCase(1u, 1ul, uint.MinValue)]
        [TestCase(1u, 2ul, 1u)]
        [TestCase(2u, 3ul, uint.MinValue)]
        [TestCase(2u, 4ul, 1u)]
        [TestCase(2u, 5ul, 2u)]
        [TestCase(2u, 6ul, 3u)]
        [TestCase(_lastRow, _nodeIndexOfTheFirstLeaf, uint.MinValue)]
        [TestCase(_lastRow, uint.MinValue, null, Description = "index too low for the last level")]
        [TestCase(_lastRow, _lastNodeIndex, _lastLeafIndex)]
        [TestCase(_lastRow, _lastNodeIndex + 1ul, null)]
        [TestCase(_lastRow + 1, uint.MinValue, null, Description = "index too low and the level does not exist")]
        [TestCase(_lastRow + 1, _lastNodeIndex + 1ul, null, Description = "first valid index at the level that does not exist")]
        public void Can_calculate_index_at_row_from_node_index(uint row, ulong nodeIndex, uint? indexAtRow)
        {
            if (indexAtRow == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new Index(row, nodeIndex));
            }
            else
            {
                new Index(row, nodeIndex).IndexAtRow.Should().Be(indexAtRow.Value);
            }
        }

        [TestCase(uint.MinValue, uint.MinValue)]
        [TestCase(1u, 1u)]
        [TestCase(2u, 1u)]
        [TestCase(3u, 2u)]
        [TestCase(7u, 3u)]
        [TestCase(_lastNodeIndex, 32u)]
        [TestCase(_lastNodeIndex + 1ul, null)]
        public void Can_calculate_node_row(ulong nodeIndex, uint? expectedRow)
        {
            if (expectedRow == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new Index(nodeIndex));
            }
            else
            {
                new Index(nodeIndex).Row.Should().Be(expectedRow.Value);
            }
        }

        [TestCase(uint.MinValue, uint.MinValue, null)]
        [TestCase(1u, uint.MinValue, 1u)]
        [TestCase(2u, uint.MinValue, 1u)]
        [TestCase(2u, 2u, 3u)]
        [TestCase(3u, 6u, 7u)]
        [TestCase(_lastRow, uint.MinValue, 1u)]
        [TestCase(_lastRow, _lastLeafIndex, _lastLeafIndex - 1)]
        [TestCase(_lastRow + 1, uint.MinValue, null)]
        [TestCase(_lastRow + 1, _lastLeafIndex, null)]
        public void Can_calculate_sibling_index(uint row, uint indexAtRow, uint? expectedSiblingIndex)
        {
            if (expectedSiblingIndex == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new Index(row, indexAtRow).Sibling());
            }
            else
            {
                new Index(row, indexAtRow).Sibling().IndexAtRow.Should().Be(expectedSiblingIndex.Value);
                new Index(row, expectedSiblingIndex.Value).Sibling().IndexAtRow.Should().Be(indexAtRow);
            }
        }

        [TestCase(uint.MinValue, null)]
        [TestCase(1u, uint.MinValue)]
        [TestCase(2u, uint.MinValue)]
        [TestCase(3u, 1u)]
        [TestCase(4u, 1u)]
        [TestCase(5u, 2u)]
        [TestCase(6u, 2u)]
        [TestCase(7u, 3u)]
        [TestCase(_lastNodeIndex + 1, null)]
        public void Can_calculate_parent_index(ulong nodeIndex, uint? parentIndex)
        {
            if (parentIndex == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetParentIndex(nodeIndex));
            }
            else
            {
                BaselineTree.GetParentIndex(nodeIndex).Should().Be(parentIndex.Value);
            }
        }

        [Test]
        public async Task Can_safely_insert_concurrently()
        {
            BaselineTree baselineTree = BuildATree();
            uint iterations = 1000;
            uint concurrentTasksCount = 8;
            Action keepAdding = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    baselineTree.Insert(_testLeaves[0]);
                }
            };

            Task[] tasks = new Task[concurrentTasksCount];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = new Task(keepAdding);
            }

            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i].Start();
            }

            await Task.WhenAll(tasks);

            baselineTree.Count.Should().Be(concurrentTasksCount * iterations);
        }

        [Test]
        public void On_adding_one_leaf_count_goes_up_to_1()
        {
            BaselineTree baselineTree = BuildATree();
            baselineTree.Insert(_testLeaves[0]);
            baselineTree.Count.Should().Be(1);
        }

        [Test]
        public void On_inserting_one_leaf_and_deleting_last_element()
        {
            BaselineTree baselineTree = BuildATree();
            baselineTree.Insert(_testLeaves[0]);
            baselineTree.Count.Should().Be(1);
            baselineTree.DeleteLast();
            baselineTree.Count.Should().Be(0);
        }

        [Test]
        public void On_deleting_last_element()
        {
            BaselineTree baselineTree = BuildATree();
            baselineTree.Insert(_testLeaves[0]);
            baselineTree.Insert(_testLeaves[1]);
            baselineTree.Insert(_testLeaves[2]);
            baselineTree.Count.Should().Be(3);
            baselineTree.DeleteLast();
            baselineTree.Count.Should().Be(2);
        }

        public class Test
        {
            public int PreviousBlockNumber { get; set; }
            public long Count { get; set; }

            public Test(int PreviousBlockNumber, long Count)
            {
                this.PreviousBlockNumber = PreviousBlockNumber;
                this.Count = Count;
            }
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(123u)]
        public void Can_restore_count_from_the_database(uint leafCount)
        {
            MemDb memDb = new MemDb();
            var metadataMemDb = new MemDb();
            BaselineTree baselineTree = new ShaBaselineTree(memDb, metadataMemDb, new byte[] { }, _truncationLength, LimboNoErrorLogger.Instance);

            for (int i = 0; i < leafCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            BaselineTree baselineTreeRestored = new ShaBaselineTree(memDb, metadataMemDb, new byte[] { }, _truncationLength, LimboNoErrorLogger.Instance);
            baselineTreeRestored.Count.Should().Be(leafCount);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void When_inserting_more_leaves_count_keeps_growing(int numberOfLeaves)
        {
            BaselineTree baselineTree = BuildATree();
            for (uint i = 0; i < numberOfLeaves; i++)
            {
                baselineTree.Insert(_testLeaves[i]);
                baselineTree.Count.Should().Be(i + 1);
            }
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_proof_on_a_populated_trie_on_an_index(uint nodesCount)
        {
            BaselineTree baselineTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            BaselineTreeNode[] proof = baselineTree.GetProof(0);
            proof.Should().HaveCount(BaselineTree.TreeHeight);

            for (int proofRow = 0; proofRow < BaselineTree.TreeHeight; proofRow++)
            {
                if (nodesCount > 1 >> proofRow)
                {
                    proof[proofRow].Should().NotBe(Keccak.Zero, proofRow.ToString());
                }
                else
                {
                    proof[proofRow].Hash.Should().Be(Keccak.Zero, proofRow.ToString());
                }
            }
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_leaf(uint nodesCount)
        {
            BaselineTree baselineTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.GetLeaf((uint) i).Hash.Should().NotBe(Keccak.Zero);
            }
        }

        [TestCase(uint.MinValue)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_leaves(uint nodesCount)
        {
            BaselineTree baselineTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            var leafIndexes = Enumerable.Range(0, (int) nodesCount).Select(l => (uint) l).ToArray();
            var result = baselineTree.GetLeaves(leafIndexes);
            for (int i = 0; i < result.Length; i++)
            {
                result[i].Hash.Should().NotBe(Keccak.Zero);
            }
        }

        [TestCase(uint.MinValue)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_root(uint nodesCount)
        {
            BaselineTree baselineTree = BuildATree();
            Keccak root = baselineTree.Root;
            Console.WriteLine(root);
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
                Keccak newRoot = baselineTree.Root;
                Console.WriteLine(newRoot);
                newRoot.Should().NotBe(root);
                root = newRoot;
            }
        }

        [TestCase(uint.MinValue)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_verify_zero_and_one_elements(uint nodesCount)
        {
            BaselineTree baselineTree = BuildATree();
            Keccak root = baselineTree.Root;
            Console.WriteLine(root);
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[i]);
                Keccak newRoot = baselineTree.Root;
                Console.WriteLine(newRoot);
                newRoot.Should().NotBe(root);
                root = newRoot;
                var proof0 = baselineTree.GetProof(0);
                var proof1 = baselineTree.GetProof(1);
                baselineTree.Verify(root, _testLeaves[0], proof0).Should().BeTrue("left in " + i);
                if (i > 0)
                {
                    baselineTree.Verify(root, _testLeaves[1], proof1).Should().BeTrue("right in " + i);
                }
            }
        }

        [Test]
        public void Keccak_a_b_verify()
        {
            BaselineTree baselineTree = BuildATree();
            Keccak root0 = baselineTree.Root;
            Console.WriteLine("root0 " + root0);
            Console.WriteLine("KeccakA " + TestItem.KeccakA);
            baselineTree.Insert(TestItem.KeccakA);
            var proof0_0 = baselineTree.GetProof(0);
            Keccak root1 = baselineTree.Root;
            Console.WriteLine("root1 " + root1);
            Console.WriteLine("KeccakB " + TestItem.KeccakB);
            baselineTree.Insert(TestItem.KeccakB);
            Keccak root2 = baselineTree.Root;
            Console.WriteLine("root2 " + root2);
            var proof1_0 = baselineTree.GetProof(0);
            var proof1_1 = baselineTree.GetProof(1);
            baselineTree.Verify(root1, TestItem.KeccakA, proof0_0).Should().BeTrue();
            baselineTree.Verify(root2, TestItem.KeccakA, proof1_0).Should().BeTrue();
            baselineTree.Verify(root2, TestItem.KeccakB, proof1_1).Should().BeTrue();
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        [TestCase(4u)]
        [TestCase(5u)]
        [TestCase(6u)]
        [TestCase(8u)]
        [TestCase(13u)]
        [TestCase(23u)]
        [TestCase(25u)]
        [TestCase(32u)]
        public void Insert_without_recalculating_hashes(uint nodesCount)
        {
            BaselineTree withHashesTree = BuildATree();
            BaselineTree withoutHashesTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                withHashesTree.Insert(_testLeaves[i]);
                withoutHashesTree.Insert(_testLeaves[i], false);

                Assert.AreNotEqual(withHashesTree.Root, withoutHashesTree.Root);
                Assert.AreEqual(withHashesTree.Count, withoutHashesTree.Count);
            }


            withoutHashesTree.CalculateHashes();
            Assert.AreEqual(withHashesTree.Root, withoutHashesTree.Root);
            Assert.AreEqual(withHashesTree.Count, withoutHashesTree.Count);
        }

        [TestCase(1u, 0u)]
        [TestCase(1u, 1u)]
        [TestCase(2u, 1u)]
        [TestCase(3u, 1u)]
        [TestCase(3u, 2u)]
        [TestCase(8u, 3u)]
        [TestCase(8u, 4u)]
        [TestCase(8u, 2u)]
        [TestCase(22u, 21u)]
        [TestCase(22u, 2u)]
        [TestCase(21u, 1u)]
        [TestCase(32u, 6u)]
        [TestCase(23u, 4u)]
        [TestCase(32u, 3u)]
        [TestCase(32u, 4u)]
        [TestCase(32u, 1u)]
        [TestCase(32u, 31u)]
        public void Insert_without_recalculating_hashes_with_starting_index(uint nodesCount, uint startCalculatingHashes)
        {
            BaselineTree withHashesTree = BuildATree();
            BaselineTree withoutHashesTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                withHashesTree.Insert(_testLeaves[i]);
                if (i < startCalculatingHashes)
                {
                    withoutHashesTree.Insert(_testLeaves[i]);
                    Assert.AreEqual(withHashesTree.Root, withoutHashesTree.Root);
                }
                else
                {
                    withoutHashesTree.Insert(_testLeaves[i], false);
                    Assert.AreNotEqual(withHashesTree.Root, withoutHashesTree.Root);
                }

                Assert.AreEqual(withHashesTree.Count, withoutHashesTree.Count);
            }


            withoutHashesTree.CalculateHashes(startCalculatingHashes);
            Assert.AreEqual(withHashesTree.Root, withoutHashesTree.Root);
            Assert.AreEqual(withHashesTree.Count, withoutHashesTree.Count);
        }

        private static Random _random = new Random();

        [TestCase(2, 10, 50, true, false, null)]
        [TestCase(2, 10, 50, false, false, null)]
        [TestCase(10, 25, 90, false, true, 1524199427)]
        [TestCase(10, 25, 90, false, true, 943302129)]
        [TestCase(10, 25, 90, false, true, null)]
        [TestCase(10, 100, 90, false, true, 496297040)]
        [TestCase(10, 100, 90, false, true, null)]
        [TestCase(10, 10000, 50, false, true, null)]
        [TestCase(1, 100, 20, false, true, 484284241)]
        [TestCase(1, 100, 20, false, true, null)]
        // TODO: fuzzer with concurrent inserts
        public void Baseline_tree_fuzzer(
            int leavesPerBlock,
            int blocksCount,
            int emptyBlocksRatio,
            bool recalculateOnInsert,
            bool withReorgs,
            int? randomSeed)
        {
            MemDb mainDb = new MemDb();
            MemDb metadataDb = new MemDb();
            Address address = Address.Zero;
            BaselineTreeHelper helper = new BaselineTreeHelper(
                Substitute.For<ILogFinder>(), mainDb, metadataDb, LimboNoErrorLogger.Instance);
            BaselineTree baselineTree = new ShaBaselineTree(
                mainDb, metadataDb, address.Bytes, 0, LimboNoErrorLogger.Instance);

            randomSeed ??= _random.Next();
            Console.WriteLine($"random seed was {randomSeed} - hardcode it to recreate the failign test");
            // Random random = new Random(1524199427); <- example
            Random random = new Random(randomSeed.Value);
            int currentBlockNumber = 0;
            uint totalCountCheck = 0;
            Stack<long> lastBlockWithLeavesCheck = new Stack<long>();
            Dictionary<long, uint> historicalCountChecks = new Dictionary<long, uint>();
            historicalCountChecks[0] = 0;
            for (int i = 0; i < blocksCount; i++)
            {

                if (i == 18)
                {
                    
                }
                currentBlockNumber++;
                uint numberOfLeaves = (uint) random.Next(leavesPerBlock) + 1; // not zero
                bool hasLeaves = random.Next(100) < emptyBlocksRatio;

                if (hasLeaves)
                {
                    totalCountCheck += numberOfLeaves;
                    
                    TestContext.WriteLine($"Adding {numberOfLeaves} at block {currentBlockNumber}");
                    for (int j = 0; j < numberOfLeaves; j++)
                    {
                        byte[] leafBytes = new byte[32];
                        random.NextBytes(leafBytes);
                        baselineTree.Insert(new Keccak(leafBytes), recalculateOnInsert);
                    }

                    lastBlockWithLeavesCheck.TryPeek(out long previous);
                    TestContext.WriteLine($"Previous is {previous}");
                    baselineTree.LastBlockWithLeaves.Should().Be(previous);
                    baselineTree.MemorizeCurrentCount(TestItem.Keccaks[currentBlockNumber], currentBlockNumber, baselineTree.Count);
                    lastBlockWithLeavesCheck.Push(currentBlockNumber);

                    baselineTree.Count.Should().Be(totalCountCheck);
                    baselineTree.LastBlockWithLeaves.Should().Be(lastBlockWithLeavesCheck.Peek());
                }
                else
                {
                    TestContext.WriteLine($"Block {currentBlockNumber} has no leaves");
                }
                
                historicalCountChecks[currentBlockNumber] = totalCountCheck;

                WriteHistory(historicalCountChecks, baselineTree);

                for (int j = 1; j <= currentBlockNumber; j++)
                {
                    TestContext.WriteLine($"Creating historical at {j}");
                    var historicalTrie = helper.CreateHistoricalTree(address, j);
                    TestContext.WriteLine($"Checking if trie count ({historicalTrie.Count}) is {historicalCountChecks[j]} as expected");
                    historicalTrie.Count.Should().Be(historicalCountChecks[j], $"Block is {currentBlockNumber}, checking count at block {j}.");
                }

                if (withReorgs)
                {
                    bool shouldReorg = random.Next(100) < 50;
                    if (shouldReorg && currentBlockNumber >= 1)
                    {
                        int reorgDepth = random.Next(currentBlockNumber) + 1;
                        TestContext.WriteLine($"Reorganizing {reorgDepth} from {currentBlockNumber}");
                        uint expectedDeleteCount = historicalCountChecks[currentBlockNumber] - historicalCountChecks[currentBlockNumber - reorgDepth]; 
                        baselineTree.GoBackTo(currentBlockNumber - reorgDepth).Should().Be(expectedDeleteCount);
                        for (int j = 0; j < reorgDepth; j++)
                        {
                            historicalCountChecks.Remove(currentBlockNumber - j);
                        }
                        
                        currentBlockNumber -= reorgDepth;
                        totalCountCheck = historicalCountChecks[currentBlockNumber];
                        baselineTree.MemorizeCurrentCount(TestItem.Keccaks[currentBlockNumber], currentBlockNumber, totalCountCheck);
                        
                        TestContext.WriteLine($"Total count after reorg is {totalCountCheck} at block {currentBlockNumber}");

                        
                        while (lastBlockWithLeavesCheck.Any() && lastBlockWithLeavesCheck.Peek() > currentBlockNumber)
                        {
                            lastBlockWithLeavesCheck.Pop();
                        }

                        lastBlockWithLeavesCheck.TryPeek(out long last);
                        if (last != currentBlockNumber)
                        {
                            TestContext.WriteLine($"Pushing {currentBlockNumber} on test stack after reorg.");
                            // after reorg we always push a memorized count
                            lastBlockWithLeavesCheck.Push(currentBlockNumber);
                        }
                    }
                    
                    WriteHistory(historicalCountChecks, baselineTree);
                }
            }
        }

        private static void WriteHistory(Dictionary<long, uint> historicalCountChecks, BaselineTree baselineTree)
        {
            foreach (KeyValuePair<long, uint> check in historicalCountChecks)
            {
                TestContext.WriteLine($"  History is {check.Key}=>{check.Value} {baselineTree.Metadata.LoadBlockNumberCount(check.Key)})");
            }

            TestContext.WriteLine($"  Last with leaves {baselineTree.LastBlockWithLeaves}");
            TestContext.WriteLine($"  Last with leaves in DB {baselineTree.Metadata.LoadCurrentBlockInDb().LastBlockWithLeaves}");
            TestContext.WriteLine($"  Count {baselineTree.Count}");
        }
    }
}