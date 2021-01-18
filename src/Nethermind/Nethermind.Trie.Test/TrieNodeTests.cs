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

using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TrieNodeTests
    {
        // private TrieNode _tiniestLeaf;
        // private TrieNode _heavyLeaf;
        // private TrieNode _accountLeaf;
        //
        // [SetUp]
        // public void Setup()
        // {
        //     _tiniestLeaf = new TrieNode(NodeType.Leaf);
        //     _tiniestLeaf.Key = new HexPrefix(true, 5);
        //     _tiniestLeaf.Value = new byte[] {10};
        //
        //     _heavyLeaf = new TrieNode(NodeType.Leaf);
        //     _heavyLeaf.Key = new HexPrefix(true, new byte[20]);
        //     _heavyLeaf.Value = Keccak.EmptyTreeHash.Bytes.Concat(Keccak.EmptyTreeHash.Bytes).ToArray();
        //
        //     Account account = new Account(100);
        //     AccountDecoder decoder = new AccountDecoder();
        //     _accountLeaf = TrieNodeFactory.CreateLeaf(
        //         HexPrefix.Leaf("bbb"),
        //         decoder.Encode(account).Bytes);
        // }
        
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
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Throws_trie_exception_on_unexpected_format()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, new byte[42]);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_keccak_and_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Encoding_leaf_without_key_throws_trie_exception()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf);
            trieNode.Value = new byte[] {1, 2, 3};
            Assert.Throws<TrieException>(() => trieNode.RlpEncode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Throws_trie_exception_when_resolving_key_on_missing_rlp()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveKey(NullTrieNodeResolver.Instance, false));
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

                byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
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

            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 11);
            decodedTiniest.ResolveNode(NullTrieNodeResolver.Instance);

            Assert.AreEqual(ctx.TiniestLeaf.Value, decodedTiniest.Value, "value");
            Assert.AreEqual(ctx.TiniestLeaf.Key!.ToBytes(), decodedTiniest.Key!.ToBytes(), "key");
        }

        [Test]
        public void Can_encode_decode_heavy_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(11, ctx.HeavyLeaf);

            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 11);

            Assert.AreEqual(decoded.GetChildHash(11), decodedTiniest.Keccak, "value");
        }

        [Test]
        public void Can_encode_decode_tiny_extension()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 5);
            trieNode.SetChild(0, ctx.TiniestLeaf);

            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode? decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 0);
            decodedTiniest?.ResolveNode(NullTrieNodeResolver.Instance);
        
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

            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new TrieNode(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 0);

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
            TrieNode getResult = trieNode.GetChild(NullTrieNodeResolver.Instance, 11);
            Assert.AreSame(tiniest, getResult);
        }

        [Test]
        public void Get_child_hash_works_on_hashed_child_of_a_branch()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode[11] = ctx.HeavyLeaf;
            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
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
            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
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
            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
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
            byte[] rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
            TrieNode decoded = new TrieNode(NodeType.Extension, rlp);

            Keccak getResult = decoded.GetChildHash(0);
            Assert.Null(getResult);
        }

        [Test]
        public void Extension_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            TrieNode ignore = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf("ccc"), Array.Empty<byte>());
            TrieNode node = TrieNodeFactory.CreateExtension(HexPrefix.Extension("aa"), ignore);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitExtension(node, context);
        }

        [Test]
        public void Unknown_node_with_missing_data_can_accept_visitor()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            TrieNode node = new TrieNode(NodeType.Unknown);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitMissingNode(node.Keccak, context);
        }

        [Test]
        public void Leaf_with_simple_account_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            Account account = new Account(100);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_empty_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            Account account = new Account(1, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_with_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            Account account = new Account(1, 100, Keccak.EmptyTreeHash, Keccak.Zero);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_with_storage_and_without_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new TrieVisitContext();
            Account account = new Account(1, 100, Keccak.Zero, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new AccountDecoder();
            TrieNode node = TrieNodeFactory.CreateLeaf(HexPrefix.Leaf("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Extension_with_leaf_can_be_visited()
        {
            Context ctx = new Context();
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new();
            TrieNode node = TrieNodeFactory.CreateExtension(HexPrefix.Extension("aa"), ctx.AccountLeaf);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

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
            TrieNode node = new TrieNode(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, ctx.AccountLeaf);
            }

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitBranch(node, context);
            visitor.Received(16).VisitLeaf(ctx.AccountLeaf, context, ctx.AccountLeaf.Value);
        }

        [Test]
        public void Branch_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new TrieVisitContext();
            TrieNode node = new TrieNode(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, null);
            }

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitBranch(node, context);
        }

        [Test]
        public void Can_encode_branch_with_nulls()
        {
            TrieNode node = new TrieNode(NodeType.Branch);
            node.RlpEncode(NullTrieNodeResolver.Instance);
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
            TrieNode cleanChild = new TrieNode(NodeType.Leaf, Keccak.Zero);
            node.SetChild(0, cleanChild);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_dirty_returns_true()
        {
            TrieNode node = new TrieNode(NodeType.Extension);
            TrieNode dirtyChild = new TrieNode(NodeType.Leaf);
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

            byte[] rlp = node.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode restoredNode = new TrieNode(NodeType.Branch, rlp);

            restoredNode.RlpEncode(NullTrieNodeResolver.Instance);
        }

        [Test]
        public void Size_of_a_heavy_leaf_is_correct()
        {
            Context ctx = new Context();
            Assert.AreEqual(184, ctx.HeavyLeaf.GetMemorySize(false));
        }
        
        [Test]
        public void Size_of_a_tiny_leaf_is_correct()
        {
            Context ctx = new Context();
            Assert.AreEqual(120, ctx.TiniestLeaf.GetMemorySize(false));
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

            Assert.AreEqual(3152, node.GetMemorySize(true));
            Assert.AreEqual(208, node.GetMemorySize(false));
        }

        [Test]
        public void Size_of_an_extension_is_correct()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 1);
            trieNode.SetChild(0, ctx.TiniestLeaf);
            
            Assert.AreEqual(96, trieNode.GetMemorySize(false));
        }

        [Test]
        public void Size_of_unknown_node_is_correct()
        {
            Context ctx = new Context();
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.Key = new HexPrefix(false, 1);
            trieNode.SetChild(0, ctx.TiniestLeaf);

            Assert.AreEqual(216, trieNode.GetMemorySize(true));
            Assert.AreEqual(96, trieNode.GetMemorySize(false));
        }

        [Test]
        public void Size_of_an_unknown_empty_node_is_correct()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            trieNode.GetMemorySize(false).Should().Be(56);
        }

        [Test]
        public void Size_of_an_unknown_node_with_keccak_is_correct()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Zero);
            trieNode.GetMemorySize(false).Should().Be(136);
        }

        [Test]
        public void Size_of_extension_with_child()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, null);
            trieNode.GetMemorySize(false).Should().Be(96);
        }

        [Test]
        public void Size_of_branch_with_data()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(0, null);
            trieNode.GetMemorySize(false).Should().Be(208);
        }

        [Test]
        public void Size_of_leaf_with_value()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf);
            trieNode.Value = new byte[7];
            trieNode.GetMemorySize(false).Should().Be(128);
        }

        [Test]
        public void Size_of_an_unknown_node_with_full_rlp_is_correct()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown, new byte[7]);
            trieNode.GetMemorySize(false).Should().Be(120);
        }

        [Test]
        public void Size_of_keccak_is_correct()
        {
            Keccak.MemorySize.Should().Be(80);
        }

        [Test]
        public void Size_of_rlp_stream_is_correct()
        {
            RlpStream rlpStream = new RlpStream(100);
            rlpStream.MemorySize.Should().Be(160);
        }

        [Test]
        public void Size_of_rlp_stream_7_is_correct()
        {
            RlpStream rlpStream = new RlpStream(7);
            rlpStream.MemorySize.Should().Be(64);
        }

        [Test]
        public void Size_of_rlp_unaligned_is_correct()
        {
            Rlp rlp = new Rlp(new byte[1]);
            rlp.MemorySize.Should().Be(56);
        }

        [Test]
        public void Size_of_rlp_aligned_is_correct()
        {
            Rlp rlp = new Rlp(new byte[8]);
            rlp.MemorySize.Should().Be(56);
        }

        [Test]
        public void Size_of_hex_prefix_is_correct()
        {
            HexPrefix hexPrefix = new HexPrefix(true, new byte[5]);
            hexPrefix.MemorySize.Should().Be(64);
        }

        [Test]
        public void Cannot_seal_already_sealed()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.Seal());
        }

        [Test]
        public void Cannot_change_value_on_sealed()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.Value = new byte[5]);
        }

        [Test]
        public void Cannot_change_key_on_sealed()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(
                () => trieNode.Key = HexPrefix.FromBytes(Bytes.FromHexString("aaa")));
        }

        [Test]
        public void Cannot_set_child_on_sealed()
        {
            TrieNode child = new TrieNode(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.SetChild(0, child));
        }

        [Test]
        public void Pruning_regression()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.Key = HexPrefix.Extension("abcd");
            trieNode.RlpEncode(NullTrieStore.Instance);
        }

        [Test]
        public void Extension_child_as_keccak()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.GetChild(NullTrieStore.Instance, 0).Should().BeOfType<TrieNode>();
        }
        
        [Test]
        public void Extension_child_as_keccak_memory_size()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.GetMemorySize(false).Should().Be(176);
        }
        
        [Test]
        public void Extension_child_as_keccak_clone()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            TrieNode cloned = trieNode.Clone();
            
            cloned.GetMemorySize(false).Should().Be(176);
        }
        
        [Test]
        public void Unresolve_of_persisted()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);
            trieNode.Key = HexPrefix.Extension("abcd");
            trieNode.ResolveKey(NullTrieStore.Instance, false);

            trieNode.PrunePersistedRecursively(1);
            trieNode.PrunePersistedRecursively(1);
        }
        
        [Test]
        public void Small_child_unresolve()
        {
            TrieNode child = new TrieNode(NodeType.Leaf);
            child.Value = Bytes.FromHexString("a");
            child.Key = HexPrefix.Leaf("b");
            child.ResolveKey(NullTrieStore.Instance, false);
            child.IsPersisted = true;

            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);
            trieNode.Key = HexPrefix.Extension("abcd");
            trieNode.ResolveKey(NullTrieStore.Instance, false);

            trieNode.PrunePersistedRecursively(2);
            trieNode.GetChild(NullTrieStore.Instance, 0).Should().Be(child);
        }

        [Test]
        public void Extension_child_as_keccak_not_dirty()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.IsChildDirty(0).Should().Be(false);
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Extension_child_as_keccak_call_recursively(bool skipPersisted)
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            int count = 0;
            trieNode.CallRecursively(n => count++, NullTrieStore.Instance, skipPersisted, LimboTraceLogger.Instance);
            count.Should().Be(1);
        }
        
        [Test]
        public void Branch_child_as_keccak_encode()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(0, child);
            trieNode.SetChild(4, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.RlpEncode(NullTrieStore.Instance);
        }
        
        [Test]
        public void Branch_child_as_keccak_resolved()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(0, child);
            trieNode.SetChild(4, child);

            trieNode.PrunePersistedRecursively(1);
            var trieStore = Substitute.For<ITrieNodeResolver>();
            trieStore.FindCachedOrUnknown(Arg.Any<Keccak>()).Returns(child);
            trieNode.GetChild(trieStore, 0).Should().Be(child);
            trieNode.GetChild(trieStore, 1).Should().BeNull();
            trieNode.GetChild(trieStore, 4).Should().Be(child);
        }

        [Test]
        public void Child_as_keccak_cached()
        {
            TrieNode child = new TrieNode(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            var trieStore = Substitute.For<ITrieNodeResolver>();
            trieStore.FindCachedOrUnknown(Arg.Any<Keccak>()).Returns(child);
            trieNode.GetChild(trieStore, 0).Should().Be(child);
        }

        [Test]
        public void Rlp_is_cloned_when_cloning()
        {
            TrieStore trieStore = new TrieStore(new MemDb(), NullLogManager.Instance);

            TrieNode leaf1 = new TrieNode(NodeType.Leaf);
            leaf1.Key = new HexPrefix(true, Bytes.FromHexString("abc"));
            leaf1.Value = new byte[111];
            leaf1.ResolveKey(trieStore, false);
            leaf1.Seal();
            trieStore.CommitNode(0, new NodeCommitInfo(leaf1));

            TrieNode leaf2 = new TrieNode(NodeType.Leaf);
            leaf2.Key = new HexPrefix(true, Bytes.FromHexString("abd"));
            leaf2.Value = new byte[222];
            leaf2.ResolveKey(trieStore, false);
            leaf2.Seal();
            trieStore.CommitNode(0, new NodeCommitInfo(leaf2));

            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(1, leaf1);
            trieNode.SetChild(2, leaf2);
            trieNode.ResolveKey(trieStore, true);
            byte[] rlp = trieNode.FullRlp;

            TrieNode restoredBranch = new TrieNode(NodeType.Branch, rlp);

            TrieNode clone = restoredBranch.Clone();
            var restoredLeaf1 = clone.GetChild(trieStore, 1);
            restoredLeaf1.Should().NotBeNull();
            restoredLeaf1.ResolveNode(trieStore);
            restoredLeaf1.Value.Should().BeEquivalentTo(leaf1.Value);
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
                AccountLeaf = TrieNodeFactory.CreateLeaf(
                    HexPrefix.Leaf("bbb"),
                    decoder.Encode(account).Bytes);
            }
        }
    }
}
