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
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            merkleTree.Count.Should().Be(0);
        }

        private const int _firstLeafIndexAsNodeIndex = int.MaxValue / 2 + 1 - 1;
        private const int _lastLeafIndexAsNodeIndex = int.MaxValue;
        
        [TestCase(0, null)]
        [TestCase(1, null)]
        [TestCase(_firstLeafIndexAsNodeIndex - 1, null)]
        [TestCase(_firstLeafIndexAsNodeIndex, 0)]
        [TestCase(_firstLeafIndexAsNodeIndex + 1, 1)]
        [TestCase(int.MaxValue - 1, int.MaxValue / 2)]
        [TestCase(int.MaxValue, null)]
        public void Can_calculate_leaf_index_from_node_index(int nodeIndex, int? leafIndex)
        {
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            if (leafIndex == null)
            {
                Assert.Throws<IndexOutOfRangeException>(() => merkleTree.GetLeafIndex(nodeIndex));
            }
            else
            {
                merkleTree.GetLeafIndex(nodeIndex).Should().Be(leafIndex.Value);
            }
        }

        [Test]
        public async Task Can_safely_insert_concurrently()
        {
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            int iterations = 10000;
            int concurrentTasksCount = 8;
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
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            merkleTree.Insert(_testLeaves[0]);
            merkleTree.Count.Should().Be(1);
        }

        [Test]
        public void Can_restore_count_from_the_database()
        {
            MemDb memDb = new MemDb();
            MerkleTree merkleTree = new MerkleTree(memDb);
            merkleTree.Insert(_testLeaves[0]);

            MerkleTree merkleTreeRestored = new MerkleTree(memDb);
            merkleTreeRestored.Count.Should().Be(1);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void When_inserting_more_leaves_count_keeps_growing(int numberOfLeaves)
        {
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            for (int i = 0; i < numberOfLeaves; i++)
            {
                merkleTree.Insert(_testLeaves[i]);
                merkleTree.Count.Should().Be(i + 1);
            }
        }

        [TestCase(0)]
        [TestCase(short.MaxValue + 1)]
        public void Can_get_proof_from_an_emptyTree_on_an_index(int leafIndex)
        {
            throw new NotImplementedException();
        }

        [TestCase(0)]
        [TestCase(short.MaxValue + 1)]
        public void Can_get_proof_on_a_populated_trie_on_an_index(int leafIndex)
        {
            throw new NotImplementedException();
        }

        [TestCase(int.MinValue)]
        [TestCase(-1)]
        [TestCase(short.MaxValue + 2)]
        [TestCase(int.MaxValue)]
        public void Throws_on_get_proof_on_the_leaf_index_out_of_bounds(int leafIndex)
        {
            MerkleTree merkleTree = new MerkleTree(new MemDb());
            Assert.Throws<IndexOutOfRangeException>(() => merkleTree.GetProof(leafIndex));
        }
    }
}