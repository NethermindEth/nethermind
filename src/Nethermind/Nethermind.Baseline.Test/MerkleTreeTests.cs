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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    [TestFixture]
    public class MerkleTreeTests
    {
        private Bytes32[] _testLeaves = new Bytes32[32];

        [OneTimeSetUp]
        public void Setup()
        {
            for (int i = 0; i < _testLeaves.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i] = (byte) i;
                _testLeaves[i] = Bytes32.Wrap(bytes);
            }
        }

        [Test]
        public void Initially_count_is_0()
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            merkleTree.Count.Should().Be(0);
        }

        private const uint _firstLeafIndexAsNodeIndex = uint.MaxValue / 2 + 1 - 1;

        [TestCase(uint.MinValue, null)]
        [TestCase(1u, null)]
        [TestCase(_firstLeafIndexAsNodeIndex - 1, null)]
        [TestCase(_firstLeafIndexAsNodeIndex, uint.MinValue)]
        [TestCase(_firstLeafIndexAsNodeIndex + 1, 1u)]
        [TestCase(uint.MaxValue - 1, uint.MaxValue / 2)]
        [TestCase(uint.MaxValue, null)]
        public void Can_calculate_leaf_index_from_node_index(uint nodeIndex, uint? leafIndex)
        {
            if (leafIndex == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetLeafIndex(nodeIndex));
            }
            else
            {
                MerkleTree.GetLeafIndex(nodeIndex).Should().Be(leafIndex.Value);
            }
        }
        
        [TestCase(32u, uint.MinValue, null)]
        [TestCase(32u, uint.MaxValue, null)]
        [TestCase(uint.MinValue, uint.MinValue, uint.MinValue)]
        [TestCase(1u, uint.MinValue, 1u)]
        [TestCase(1u, 1u, 2u)]
        [TestCase(2u, uint.MinValue, 3u)]
        [TestCase(2u, 1u, 4u)]
        [TestCase(2u, 2u, 5u)]
        [TestCase(2u, 3u, 6u)]
        [TestCase(2u, 4u, null)]
        [TestCase(1u, 2u, null)]
        [TestCase(31u, uint.MinValue, _firstLeafIndexAsNodeIndex)]
        [TestCase(31u, 1u, _firstLeafIndexAsNodeIndex + 1)]
        [TestCase(31u, uint.MaxValue / 2, uint.MaxValue - 1)]
        [TestCase(31u, uint.MaxValue / 2 + 1, null)]
        public void Can_calculate_node_index_from_level_and_index_at_level(uint level, uint indexAtLevel, uint? nodeIndex)
        {
            if (nodeIndex == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetNodeIndex(level, indexAtLevel));
            }
            else
            {
                MerkleTree.GetNodeIndex(level, indexAtLevel).Should().Be(nodeIndex.Value);
            }
        }
        
        [TestCase(32u, uint.MinValue, null)]
        [TestCase(32u, uint.MaxValue, null)]
        [TestCase(uint.MinValue, uint.MinValue, uint.MinValue)]
        [TestCase(1u, uint.MinValue, null)]
        [TestCase(1u, 1u, uint.MinValue)]
        [TestCase(1u, 2u, 1u)]
        [TestCase(2u, 3u, uint.MinValue)]
        [TestCase(2u, 4u, 1u)]
        [TestCase(2u, 5u, 2u)]
        [TestCase(2u, 6u, 3u)]
        [TestCase(31u, _firstLeafIndexAsNodeIndex, uint.MinValue)]
        [TestCase(31u, _firstLeafIndexAsNodeIndex + 1, 1u)]
        [TestCase(31u, uint.MinValue, null)]
        [TestCase(31u, 1u, null)]
        [TestCase(31u, uint.MaxValue - 1, uint.MaxValue / 2)]
        [TestCase(31u, uint.MaxValue, null)]
        public void Can_calculate_index_at_level_from_node_index(uint level, uint nodeIndex, uint? indexAtLevel)
        {
            if (indexAtLevel == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetIndexAtLevel(level, nodeIndex));
            }
            else
            {
                MerkleTree.GetIndexAtLevel(level, nodeIndex).Should().Be(indexAtLevel.Value);
            }
        }
        
        [TestCase(uint.MinValue, uint.MinValue)]
        [TestCase(1u, 1u)]
        [TestCase(2u, 1u)]
        [TestCase(3u, 2u)]
        [TestCase(7u, 3u)]
        [TestCase(uint.MaxValue - 2, 31u)]
        [TestCase(uint.MaxValue - 1, 31u)]
        [TestCase(uint.MaxValue, null)]
        public void Can_calculate_node_level(uint nodeIndex, uint? expectedLevel)
        {
            if (expectedLevel == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetLevel(nodeIndex));
            }
            else
            {
                MerkleTree.GetLevel(nodeIndex).Should().Be(expectedLevel.Value);
            }
        }
        
        [TestCase(32u, uint.MinValue, null)]
        [TestCase(32u, uint.MaxValue, null)]
        [TestCase(uint.MinValue, uint.MinValue, null)]
        [TestCase(1u, uint.MinValue, 1u)]
        [TestCase(2u, uint.MinValue, 1u)]
        [TestCase(2u, 2u, 3u)]
        [TestCase(3u, 6u, 7u)]
        [TestCase(31u, uint.MinValue, 1u)]
        [TestCase(31u, uint.MaxValue / 2 - 1, uint.MaxValue / 2)]
        [TestCase(31u, uint.MaxValue, null)]
        public void Can_calculate_sibling_index(uint level, uint indexAtLevel, uint? expectedSiblingIndex)
        {
            if (expectedSiblingIndex == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetSiblingIndex(level, indexAtLevel));
            }
            else
            {
                MerkleTree.GetSiblingIndex(level, indexAtLevel).Should().Be(expectedSiblingIndex.Value);
                MerkleTree.GetSiblingIndex(level, expectedSiblingIndex.Value).Should().Be(indexAtLevel);
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
        [TestCase(uint.MaxValue, null)]
        public void Can_calculate_parent_index(uint nodeIndex, uint? parentIndex)
        {
            if (parentIndex == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => MerkleTree.GetParentIndex(nodeIndex));
            }
            else
            {
                MerkleTree.GetParentIndex(nodeIndex).Should().Be(parentIndex.Value);
            }
        }

        [Test]
        public async Task Can_safely_insert_concurrently()
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            uint iterations = 1000;
            uint concurrentTasksCount = 8;
            Action keepAdding = () =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    merkleTree.Insert(_testLeaves[0]);
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

            merkleTree.Count.Should().Be(concurrentTasksCount * iterations);
        }

        [Test]
        public void On_adding_one_leaf_count_goes_up_to_1()
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            merkleTree.Insert(_testLeaves[0]);
            merkleTree.Count.Should().Be(1);
        }

        [Test]
        public void Can_restore_count_from_the_database()
        {
            MemDb memDb = new MemDb();
            MerkleTree merkleTree = new ShaMerkleTree(memDb);
            merkleTree.Insert(_testLeaves[0]);

            MerkleTree merkleTreeRestored = new ShaMerkleTree(memDb);
            merkleTreeRestored.Count.Should().Be(1);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void When_inserting_more_leaves_count_keeps_growing(int numberOfLeaves)
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            for (uint i = 0; i < numberOfLeaves; i++)
            {
                merkleTree.Insert(_testLeaves[i]);
                merkleTree.Count.Should().Be(i + 1);
            }
        }

        [TestCase(uint.MinValue)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_proof_on_a_populated_trie_on_an_index(uint nodesCount)
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            for (int i = 0; i < nodesCount; i++)
            {
                merkleTree.Insert(_testLeaves[0]);    
            }
            
            MerkleTreeNode[] proof = merkleTree.GetProof(0);
            proof.Should().HaveCount(MerkleTree.TreeDepth - 1);

            for (int proofLevel = 0; proofLevel < MerkleTree.TreeDepth - 1; proofLevel++)
            {
                if (nodesCount > 1 >> proofLevel)
                {
                    proof[proofLevel].Should().NotBe(ShaMerkleTree.ZeroHashes[proofLevel], proofLevel.ToString());
                }
                else
                {
                    proof[proofLevel].Hash.Should().Be(Bytes32.Wrap(ShaMerkleTree.ZeroHashes[proofLevel]), proofLevel.ToString());
                }
            }
        }

        [TestCase(uint.MaxValue / 2 + 1)]
        public void Throws_on_get_proof_on_the_leaf_index_out_of_bounds(uint leafIndex)
        {
            MerkleTree merkleTree = new ShaMerkleTree(new MemDb());
            Assert.Throws<IndexOutOfRangeException>(() => merkleTree.GetProof(leafIndex));
        }
    }
}