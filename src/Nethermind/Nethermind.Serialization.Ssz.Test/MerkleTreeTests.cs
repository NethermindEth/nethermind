// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
//using Nethermind.Core2.Types;
using Nethermind.Merkleization;
using NUnit.Framework;

namespace Nethermind.Serialization.Ssz.Test
{
    [TestFixture]
    public class MerkleTreeTests
    {
        private Bytes32[] _testLeaves = new Bytes32[32];
        private const ulong _nodeIndexOfTheFirstLeaf = (1ul << MerkleTree.TreeHeight) - 1ul;
        private const ulong _lastNodeIndex = (1ul << (MerkleTree.TreeHeight + 1)) - 2ul;
        private const uint _lastLeafIndex = (uint)((1ul << MerkleTree.TreeHeight) - 1u);
        private const uint _lastRow = MerkleTree.TreeHeight;

        [OneTimeSetUp]
        public void Setup()
        {
            for (int i = 0; i < _testLeaves.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i] = (byte)(i + 1);
                _testLeaves[i] = Bytes32.Wrap(bytes);
            }
        }

        private MerkleTree BuildATree(IKeyValueStore<ulong, byte[]>? keyValueStore = null)
        {
            return new ShaMerkleTree(keyValueStore ?? new MemMerkleTreeStore());
        }

        [Test]
        public void Initially_count_is_0()
        {
            MerkleTree baselineTree = BuildATree();
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetLeafIndex(nodeIndex));
            }
            else
            {
                MerkleTree.GetLeafIndex(nodeIndex).Should().Be(leafIndex.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetNodeIndex(row, indexAtRow));
            }
            else
            {
                MerkleTree.GetNodeIndex(row, indexAtRow).Should().Be(nodeIndex.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetIndexAtRow(row, nodeIndex));
            }
            else
            {
                MerkleTree.GetIndexAtRow(row, nodeIndex).Should().Be(indexAtRow.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetRow(nodeIndex));
            }
            else
            {
                MerkleTree.GetRow(nodeIndex).Should().Be(expectedRow.Value);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetSiblingIndex(row, indexAtRow));
            }
            else
            {
                MerkleTree.GetSiblingIndex(row, indexAtRow).Should().Be(expectedSiblingIndex.Value);
                MerkleTree.GetSiblingIndex(row, expectedSiblingIndex.Value).Should().Be(indexAtRow);
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
                Assert.Throws<ArgumentOutOfRangeException>(() => MerkleTree.GetParentIndex(nodeIndex));
            }
            else
            {
                MerkleTree.GetParentIndex(nodeIndex).Should().Be(parentIndex.Value);
            }
        }

        [Test]
        public async Task Can_safely_insert_concurrently()
        {
            MerkleTree baselineTree = BuildATree();
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
            MerkleTree baselineTree = BuildATree();
            baselineTree.Insert(_testLeaves[0]);
            baselineTree.Count.Should().Be(1);
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(123u)]
        public void Can_restore_count_from_the_database(uint leafCount)
        {
            MemMerkleTreeStore? memDb = new MemMerkleTreeStore();
            MerkleTree baselineTree = BuildATree(memDb);

            for (int i = 0; i < leafCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            MerkleTree baselineTreeRestored = BuildATree(memDb);
            baselineTreeRestored.Count.Should().Be(leafCount);
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void When_inserting_more_leaves_count_keeps_growing(int numberOfLeaves)
        {
            MerkleTree baselineTree = BuildATree();
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
            MerkleTree baselineTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            IList<Bytes32> proof = baselineTree.GetProof(0);
            proof.Should().HaveCount(MerkleTree.TreeHeight + 1);

            for (int proofRow = 0; proofRow < MerkleTree.TreeHeight; proofRow++)
            {
                if (nodesCount > 1 >> proofRow)
                {
                    proof[proofRow].Should().NotBe(Bytes32.Zero, proofRow.ToString());
                }
                else
                {
                    proof[proofRow].Should().Be(ShaMerkleTree.ZeroHashes[proofRow], proofRow.ToString());
                }
            }
        }

        [TestCase(uint.MinValue)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(23u)]
        public void Can_get_leaf(uint nodesCount)
        {
            MerkleTree baselineTree = BuildATree();
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
            MerkleTree baselineTree = BuildATree();
            for (int i = 0; i < nodesCount; i++)
            {
                baselineTree.Insert(_testLeaves[0]);
            }

            var leafIndexes = Enumerable.Range(0, (int)nodesCount).Select(l => (uint)l).ToArray();
            var result = baselineTree.GetLeaves(leafIndexes);
            for (int i = 0; i < result.Length; i++)
            {
                result[i].Hash.Should().NotBe(Keccak.Zero);
            }
        }
    }
}
