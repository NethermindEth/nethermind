/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Encoding;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture]
    public class NodeTest
    {
        [SetUp]
        public void Setup()
        {
            TrieNode.AllowBranchValues = true;
        }

        [TearDown]
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
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child0 = decoded.GetChild(0);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child = decoded.GetChild(3);
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_updatenull_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestItem.KeccakD));
            decoded.RlpEncode();
        }

        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode();
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.Value = new byte[] {1, 2, 3};
            PatriciaTree tree = BuildATreeFromNode(node);
            TrieNode decoded = new TrieNode(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode();
        }

        private static PatriciaTree BuildATreeFromNode(TrieNode node)
        {
            TrieNode.AllowBranchValues = true;
            Rlp rlp = node.RlpEncode();
            node.ResolveKey(true);

            MemDb memDb = new MemDb();
            memDb[node.Keccak.Bytes] = rlp.Bytes;

            PatriciaTree tree = new PatriciaTree(memDb, node.Keccak, false);
            return tree;
        }
    }
}