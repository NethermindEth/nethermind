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

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Store.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TrieNodeTests
    {
        [Test]
        public void Throws_trie_exception_when_setting_value_on_branch()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            Assert.Throws<TrieException>(() => trieNode.Value = new byte[] {1, 2, 3});
        }

        [Test]
        public void Throws_trie_exception_on_missing_node()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(new PatriciaTree()));
        }

        [Test]
        public void Throws_trie_exception_on_unexpected_format()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, new byte[42]);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(new PatriciaTree()));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_keccak_and_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(new PatriciaTree()));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            trieNode.Keccak = Keccak.Zero;
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(new PatriciaTree()));
        }

        [Test]
        public void Encoding_leaf_without_key_throws_trie_exception()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf);
            trieNode.Value = new byte[] {1, 2, 3};
            Assert.Throws<TrieException>(() => trieNode.RlpEncode());
        }

        [Test]
        public void Throws_trie_exception_when_resolving_key_on_missing_rlp()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveKey(false));
        }

        [Test(Description = "This is controversial and only used in visitors. Can consider an exception instead.")]
        public void Get_child_hash_is_null_when_rlp_is_null()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            Assert.Null(trieNode.GetChildHash(0));
        }

        [Test]
        public void Can_check_if_branch_is_valid_with_one_child_less()
        {
            Context ctx = new Context();
            for (int i = 0; i < 16; i++)
            {
                TrieNode trieNode = new TrieNode(NodeType.Branch);
                for (int j = 0; j < i; j++)
                {
                    trieNode.SetChild(j, ctx.TiniestLeaf);
                }

                if (i > 2)
                {
                    Assert.True(trieNode.IsValidWithOneNodeLess);
                }
                else
                {
                    Assert.False(trieNode.IsValidWithOneNodeLess);
                }
            }
        }

        [Test]
        public void Can_check_if_child_is_null_on_a_branch()
        {
            Context ctx = new Context();
            for (int nonNullChildrenCount = 0; nonNullChildrenCount < 16; nonNullChildrenCount++)
            {
                TrieNode trieNode = new TrieNode(NodeType.Branch);
                for (int j = 0; j < nonNullChildrenCount; j++)
                {
                    trieNode.SetChild(j, ctx.TiniestLeaf);
                }

                byte[] rlp = trieNode.RlpEncode();
                TrieNode restoredNode = new TrieNode(NodeType.Branch, rlp);

                for (int childIndex = 0; childIndex < 16; childIndex++)
                {
                    if (childIndex < nonNullChildrenCount)
                    {
                        Assert.False(trieNode.IsChildNull(childIndex), $"original {childIndex}");
                        Assert.False(restoredNode.IsChildNull(childIndex), $"restored {childIndex}");
                    }
                    else
                    {
                        Assert.True(trieNode.IsChildNull(childIndex), $"original {childIndex}");
                        Assert.True(restoredNode.IsChildNull(childIndex), $"restored {childIndex}");
                    }
                }
            }
        }

        [Test]
        public void Can_encode_decode_tiny_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(11, ctx.TiniestLeaf);

            byte[] rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(11);
            decodedTiniest.ResolveNode(null);

            Assert.AreEqual(ctx.TiniestLeaf.Value, decodedTiniest.Value, "value");
            Assert.AreEqual(ctx.TiniestLeaf.Key!.ToBytes(), decodedTiniest.Key!.ToBytes(), "key");
        }

        [Test]
        public void Can_encode_decode_heavy_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(11, ctx.HeavyLeaf);

            byte[] rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(11);

            Assert.AreEqual(decoded.GetChildHash(11), decodedTiniest.Keccak, "value");
        }

        [Test]
        public void Can_encode_decode_tiny_extension()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 5);
            trieNode.SetChild(0, ctx.TiniestLeaf);

            byte[] rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(0);
            decodedTiniest.ResolveNode(null);

            Assert.AreEqual(ctx.TiniestLeaf.Value, decodedTiniest.Value, "value");
            Assert.AreEqual(ctx.TiniestLeaf.Key!.ToBytes(), decodedTiniest.Key!.ToBytes(), "key");
        }

        [Test]
        public void Can_encode_decode_heavy_extension()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 5);
            trieNode.SetChild(0, ctx.HeavyLeaf);

            byte[] rlp = trieNode.RlpEncode();

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(null);
            TrieNode decodedTiniest = decoded.GetChild(0);

            Assert.AreEqual(decoded.GetChildHash(0), decodedTiniest.Keccak, "keccak");
        }

        [Test]
        public void Can_set_and_get_children_using_indexer()
        {
            TrieNode tiniest = new TrieNode(NodeType.Leaf);
            tiniest.Key = new HexPrefix(true, 5);
            tiniest.Value = new byte[] {10};

            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = tiniest;
            TrieNode getResult = trieNode[11];
            Assert.AreSame(tiniest, getResult);
        }

        [Test]
        public void Get_child_hash_works_on_hashed_child_of_a_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = ctx.HeavyLeaf;
            byte[] rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Branch, rlp);

            Keccak getResult = decoded.GetChildHash(11);
            Assert.NotNull(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_inlined_child_of_a_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = ctx.TiniestLeaf;
            byte[] rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Branch, rlp);

            Keccak getResult = decoded.GetChildHash(11);
            Assert.Null(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_hashed_child_of_an_extension()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode[0] = ctx.HeavyLeaf;
            trieNode.Key = new HexPrefix(false, 5);
            byte[] rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Extension, rlp);

            Keccak getResult = decoded.GetChildHash(0);
            Assert.NotNull(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_inlined_child_of_an_extension()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode[0] = ctx.TiniestLeaf;
            trieNode.Key = new HexPrefix(false, 5);
            byte[] rlp = trieNode.RlpEncode();
            TrieNode decoded = new TrieNode(NodeType.Extension, rlp);

            Keccak getResult = decoded.GetChildHash(0);
            Assert.Null(getResult);
        }

        [Test]
        public void Extension_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            TrieNode ignore = new TrieNode(NodeType.Unknown);
            TrieNode node = new TrieNode(NodeType.Extension);
            node.SetChild(0, ignore);

            node.Accept(visitor, tree, context);

            visitor.Received().VisitExtension(node, context);
        }

        [Test]
        public void Unknown_node_with_missing_data_can_accept_visitor()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            TrieNode node = new TrieNode(NodeType.Unknown);

            node.Accept(visitor, tree, context);

            visitor.Received().VisitMissingNode(node.Keccak, context);
        }

        [Test]
        public void Leaf_with_simple_account_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            Account account = new Account(100);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = new TrieNode(NodeType.Leaf);
            node.Value = decoder.Encode(account).Bytes;

            node.Accept(visitor, tree, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_empty_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            Account account = new Account(1, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = new TrieNode(NodeType.Leaf);
            node.Value = decoder.Encode(account).Bytes;

            node.Accept(visitor, tree, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_with_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            Account account = new Account(1, 100, Keccak.EmptyTreeHash, Keccak.Zero);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = new TrieNode(NodeType.Leaf);
            node.Value = decoder.Encode(account).Bytes;

            node.Accept(visitor, tree, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }
        
        [Test]
        public void Leaf_with_contract_with_storage_and_without_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            Account account = new Account(1, 100, Keccak.Zero, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = new TrieNode(NodeType.Leaf);
            node.Value = decoder.Encode(account).Bytes;

            node.Accept(visitor, tree, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Extension_with_leaf_can_be_visited()
        {
            Context ctx = new Context();
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            TrieNode node = new TrieNode(NodeType.Extension);
            node.SetChild(0, ctx.AccountLeaf);

            node.Accept(visitor, tree, context);

            visitor.Received().VisitExtension(node, context);
            visitor.Received().VisitLeaf(ctx.AccountLeaf, context, ctx.AccountLeaf.Value);
        }

        [Test]
        public void Branch_with_children_can_be_visited()
        {
            Context ctx = new Context();
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            TrieNode node = new TrieNode(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, ctx.AccountLeaf);
            }

            node.Accept(visitor, tree, context);

            visitor.Received().VisitBranch(node, context);
            visitor.Received(16).VisitLeaf(ctx.AccountLeaf, context, ctx.AccountLeaf.Value);
        }

        [Test]
        public void Branch_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            PatriciaTree tree = new PatriciaTree();
            TrieNode node = new TrieNode(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, null);
            }

            node.Accept(visitor, tree, context);

            visitor.Received().VisitBranch(node, context);
        }

        [Test]
        public void Can_encode_branch_with_nulls()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.RlpEncode();
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_null_returns_false()
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_null_node_returns_false()
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            node.SetChild(0, null);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_not_dirty_returns_false()
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            node.SetChild(0, new TrieNode(NodeType.Leaf));
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_dirty_returns_true()
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            TrieNode dirtyChild = new TrieNode(NodeType.Leaf);
            dirtyChild.IsDirty = true;
            node.SetChild(0, dirtyChild);
            Assert.True(node.IsChildDirty(0));
        }

        [Test]
        public void Empty_branch_will_not_be_valid_with_one_child_less()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            Assert.False(node.IsValidWithOneNodeLess);
        }

        [Test]
        public void Cannot_ask_about_validity_on_non_branch_nodes()
        {
            TrieNode leaf = new TrieNode(NodeType.Leaf);
            TrieNode extension = new TrieNode(NodeType.Leaf);
            Assert.Throws<TrieException>(() => _ = leaf.IsValidWithOneNodeLess, "leaf");
            Assert.Throws<TrieException>(() => _ = extension.IsValidWithOneNodeLess, "extension");
        }

        [Test]
        public void Can_encode_branch_with_unresolved_children()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            TrieNode randomTrieNode = new TrieNode(NodeType.Leaf);
            randomTrieNode.Key = new HexPrefix(true, new byte[] {1, 2, 3});
            randomTrieNode.Value = new byte[] {1, 2, 3};
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, randomTrieNode);
            }

            byte[] rlp = node.RlpEncode();

            TrieNode restoredNode = new TrieNode(NodeType.Branch, rlp);

            restoredNode.RlpEncode();
        }
        
        [Test]
        public void Size_of_a_heavy_leaf_is_correct()
        {
            Context ctx = new Context();
            Assert.AreEqual(168, ctx.HeavyLeaf.MemorySize);
        }
        
        [Test]
        public void Size_of_a_tiny_leaf_is_correct()
        {
            Context ctx = new Context();
            Assert.AreEqual(152, ctx.TiniestLeaf.MemorySize);
        }

        [Test]
        public void Size_of_a_branch_is_correct()
        {
            Context ctx = new Context();
            TrieNode node = new TrieNode(NodeType.Branch);
            node.Key = new HexPrefix(false, 1);
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, ctx.AccountLeaf);
            }
            
            Assert.AreEqual(216, node.MemorySize);
        }
        
        [Test]
        public void Size_of_an_extension_is_correct()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 1);
            trieNode.SetChild(0, ctx.TiniestLeaf);
            
            Assert.AreEqual(152, trieNode.MemorySize);
        }
        
        [Test]
        public void Size_of_unknown_node_is_correct()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 1);
            trieNode.SetChild(0, ctx.TiniestLeaf);
            
            Assert.AreEqual(152, trieNode.MemorySize);
        }

        private class Context
        {
            public TrieNode TiniestLeaf { get; }
            public TrieNode HeavyLeaf { get; }
            public TrieNode AccountLeaf { get; }

            public Context()
            {
                TiniestLeaf = new TrieNode(NodeType.Leaf);
                TiniestLeaf.Key = new HexPrefix(true, 5);
                TiniestLeaf.Value = new byte[] {10};

                HeavyLeaf = new TrieNode(NodeType.Leaf);
                HeavyLeaf.Key = new HexPrefix(true, new byte[20]);
                HeavyLeaf.Value = Keccak.EmptyTreeHash.Bytes.Concat(Keccak.EmptyTreeHash.Bytes).ToArray();

                Account account = new Account(100);
                AccountDecoder decoder = new AccountDecoder();
                AccountLeaf = new TrieNode(NodeType.Leaf);
                AccountLeaf.Value = decoder.Encode(account).Bytes;
            }
        }
    }
}