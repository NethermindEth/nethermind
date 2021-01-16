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

using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.Children)]
    public class NodeTest
    {
        [OneTimeSetUp]
        public void Setup()
        {
            TrieNode.AllowBranchValues = true;
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            TrieNode.AllowBranchValues = false;
        }
        
        [Test]
        public void Two_children_store_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child0 = decoded.GetChild(tree, 0);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child = decoded.GetChild(tree, 3);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_update_null_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestItem.KeccakD));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        private static ITrieNodeResolver BuildATreeFromNode(TrieNode node)
        {
            TrieNode.AllowBranchValues = true;
            byte[] rlp = node.RlpEncode(null);
            node.ResolveKey(null, true);

            MemDb memDb = new MemDb();
            memDb[node.Keccak.Bytes] = rlp;

            // ITrieNodeResolver tree = new PatriciaTree(memDb, node.Keccak, false, true);
            return new TrieStore(memDb, NullLogManager.Instance);
        }
    }
}
