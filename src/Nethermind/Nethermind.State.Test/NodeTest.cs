// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
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
        private static (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) CreateBranchAndDecode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            node.SetChild(1, new TrieNode(NodeType.Leaf, TestItem.KeccakB));
            (ITrieNodeResolver tree, byte[] originalRlp) = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);
            return (tree, decoded, originalRlp);
        }

        private static byte[] Encode(ITrieNodeResolver tree, TrieNode node)
        {
            TreePath emptyPath = TreePath.Empty;
            return node.RlpEncode(tree, ref emptyPath).ToArray();
        }

        // A fresh node built with the same children is the encoding oracle for the
        // decode-then-mutate paths.
        private static byte[] EncodeFreshBranch(params (int index, Hash256 child)[] children)
        {
            TrieNode node = new(NodeType.Branch);
            foreach ((int index, Hash256 child) in children)
            {
                node.SetChild(index, new TrieNode(NodeType.Leaf, child));
            }

            TreePath emptyPath = TreePath.Empty;
            return node.RlpEncode(null, ref emptyPath).ToArray();
        }

        private static void AssertEncodesLikeFreshBranch(
            byte[] encoded, byte[] originalRlp, params (int index, Hash256 child)[] expectedChildren)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(encoded, Is.EqualTo(EncodeFreshBranch(expectedChildren)),
                    "a mutated clone must encode like a fresh node with the same children");
                Assert.That(encoded, Is.Not.EqualTo(originalRlp), "the mutation must change the encoding");
            }
        }

        [Test]
        public void Two_children_store_encode()
        {
            (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) = CreateBranchAndDecode();

            Assert.That(Encode(tree, decoded), Is.EqualTo(originalRlp),
                "a decoded node must re-encode to the stored bytes");
        }

        [TestCase(0, TestName = "Two_children_store_resolve_get_existing_child_encode")]
        [TestCase(3, TestName = "Two_children_store_resolve_get_null_child_encode")]
        public void Two_children_store_resolve_get_encode(int childIndex)
        {
            (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) = CreateBranchAndDecode();
            TreePath emptyPath = TreePath.Empty;
            _ = decoded.GetChild(tree, ref emptyPath, childIndex);

            Assert.That(Encode(tree, decoded), Is.EqualTo(originalRlp),
                "a child read must not change the encoding");
        }

        [Test]
        public void Two_children_store_resolve_update_encode()
        {
            (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) = CreateBranchAndDecode();
            decoded = decoded.Clone();
            decoded.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakC));

            AssertEncodesLikeFreshBranch(Encode(tree, decoded), originalRlp,
                (0, TestItem.KeccakC), (1, TestItem.KeccakB));
        }

        [Test]
        public void Two_children_store_resolve_update_null_encode()
        {
            (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) = CreateBranchAndDecode();
            decoded = decoded.Clone();
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));
            decoded.SetChild(5, new TrieNode(NodeType.Leaf, TestItem.KeccakD));

            AssertEncodesLikeFreshBranch(Encode(tree, decoded), originalRlp,
                (0, TestItem.KeccakA), (1, TestItem.KeccakB), (4, TestItem.KeccakC), (5, TestItem.KeccakD));
        }

        [Test]
        public void Two_children_store_resolve_delete_and_add_encode()
        {
            (ITrieNodeResolver tree, TrieNode decoded, byte[] originalRlp) = CreateBranchAndDecode();
            decoded = decoded.Clone();
            decoded.SetChild(0, null);
            decoded.SetChild(4, new TrieNode(NodeType.Leaf, TestItem.KeccakC));

            AssertEncodesLikeFreshBranch(Encode(tree, decoded), originalRlp,
                (1, TestItem.KeccakB), (4, TestItem.KeccakC));
        }

        [Test]
        public void Child_and_value_store_encode()
        {
            TrieNode node = new(NodeType.Branch);
            node.SetChild(0, new TrieNode(NodeType.Leaf, TestItem.KeccakA));
            (ITrieNodeResolver tree, byte[] originalRlp) = BuildATreeFromNode(node);
            TrieNode decoded = new(NodeType.Unknown, node.Keccak);
            decoded.ResolveNode(tree, TreePath.Empty);

            Assert.That(Encode(tree, decoded), Is.EqualTo(originalRlp),
                "a decoded node must re-encode to the stored bytes");
        }

        private static (ITrieNodeResolver tree, byte[] originalRlp) BuildATreeFromNode(TrieNode node)
        {
            TreePath emptyPath = TreePath.Empty;
            CappedArray<byte> rlp = node.RlpEncode(null, ref emptyPath);
            node.ResolveKey(null, ref emptyPath);

            byte[] rlpBytes = rlp.ToArray();
            MemDb memDb = new();
            memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, node.Keccak)] = rlpBytes;

            // The oracle is an independent copy. A write into the stored buffer must fail
            // the compare, not silently update the expectation.
            return (TestTrieStoreFactory.Build(memDb, NullLogManager.Instance).GetTrieStore(null), [.. rlpBytes]);
        }
    }
}
