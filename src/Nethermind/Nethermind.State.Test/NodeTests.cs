// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.Children)]
    public class NodeTest
    {
        [Test]
        public void Two_children_store_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_get1_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            TreePath emptyPath = TreePath.Empty;
            _ = decoded.GetChild(tree, ref emptyPath, 0);
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_getnull_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            TreePath emptyPath = TreePath.Empty;
            _ = decoded.GetChild(tree, ref emptyPath, 3);
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            decoded = decoded.Clone();
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_update_null_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            decoded = decoded.Clone();
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestItem.KeccakD));
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            decoded = decoded.Clone();
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            ITrieNodeResolver tree = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            TreePath emptyPath = TreePath.Empty;
            decoded.RlpEncode(tree, ref emptyPath);
        }

        private static ITrieNodeResolver BuildATreeFromNode(TrieNode node)
        {
            TreePath emptyPath = TreePath.Empty;
            CappedArray<byte> rlp = node.RlpEncode(null, ref emptyPath);
            node.ResolveKey(null, ref emptyPath, true);

            MemDb memDb = new();
            memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, node.Keccak)] = rlp.ToArray();

            // ITrieNodeResolver tree = new PatriciaTree(memDb, node.Keccak, false, true);
            return TestTrieStoreFactory.Build(memDb, NullLogManager.Instance).GetTrieStore(null);
        }
    }
}
