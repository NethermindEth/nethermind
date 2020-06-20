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
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture(0)]
    [TestFixture(5)]
    public class BaselineTreeTests
    {
        private readonly int _truncationLength;
        private Bytes32[] _testLeaves = new Bytes32[32];
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
                bytes[i] = (byte) (i + 1);
                _testLeaves[i] = Bytes32.Wrap(bytes);
            }
        }

        private BaselineTree BuildATree(IKeyValueStore keyValueStore = null)
        {
            return new ShaBaselineTree(keyValueStore ?? new MemDb(), new byte[] {}, _truncationLength);
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
        [TestCase(_lastNodeIndex + 1, null)]
        public void Can_calculate_leaf_index_from_node_index(ulong nodeIndex, uint? leafIndex)
        {
            if (leafIndex == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetLeafIndex(nodeIndex));
            }
            else
            {
                BaselineTree.GetLeafIndex(nodeIndex).Should().Be(leafIndex.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetNodeIndex(row, indexAtRow));
            }
            else
            {
                BaselineTree.GetNodeIndex(row, indexAtRow).Should().Be(nodeIndex.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetIndexAtRow(row, nodeIndex));
            }
            else
            {
                BaselineTree.GetIndexAtRow(row, nodeIndex).Should().Be(indexAtRow.Value);
            }
        }
        
        [TestCase(uint.MinValue, uint.MinValue)]
        [TestCase(1u, 1u)]
        [TestCase(2u, 1u)]
        [TestCase(3u, 2u)]
        [TestCase(7u, 3u)]
        [TestCase(_lastNodeIndex, 31u)]
        [TestCase(_lastNodeIndex + 1ul, null)]
        public void Can_calculate_node_row(ulong nodeIndex, uint? expectedRow)
        {
            if (expectedRow == null)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetRow(nodeIndex));
            }
            else
            {
                BaselineTree.GetRow(nodeIndex).Should().Be(expectedRow.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => BaselineTree.GetSiblingIndexAtRow(row, indexAtRow));
            }
            else
            {
                BaselineTree.GetSiblingIndexAtRow(row, indexAtRow).Should().Be(expectedSiblingIndex.Value);
                BaselineTree.GetSiblingIndexAtRow(row, expectedSiblingIndex.Value).Should().Be(indexAtRow);
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

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(123u)]
        public void Can_restore_count_from_the_database(uint leafCount)
        {
            MemDb memDb = new MemDb();
            BaselineTree baselineTree = BuildATree(memDb);

            for (int i = 0; i < leafCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);    
            }

            BaselineTree baselineTreeRestored = BuildATree(memDb);
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

        [TestCase(uint.MinValue)]
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
        
        [TestCase(uint.MinValue)]
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
                baselineTree.GetLeaf((uint)i).Hash.Should().NotBe(Keccak.Zero);
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
    }
}