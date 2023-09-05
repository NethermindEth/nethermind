// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child0 = decoded.GetChild(tree, 0);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            TrieNode child = decoded.GetChild(tree, 3);
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_update_null_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestItem.KeccakD));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded = decoded.Clone();
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.RlpEncode(tree);
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree);
            decoded.RlpEncode(tree);
        }

        private static ITrieNodeResolver BuildATreeFromNode(TrieNode node)
        {
            MemDb memDb = new();
            TrieStore trieStore = new(memDb, NullLogManager.Instance);

            TrieNode.AllowBranchValues = true;
            byte[] rlp = node.RlpEncode(trieStore);
            node.ResolveKey(trieStore, true);

            memDb[node.Keccak.Bytes] = rlp;

            // ITrieNodeResolver tree = new PatriciaTree(memDb, node.Keccak, false, true);
            return trieStore;
        }
    }
}
