// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
            TrieNode trieNode = new(NodeType.Branch);
            Assert.Throws<TrieException>(() => trieNode.Value = new byte[] { 1, 2, 3 });
        }

        [Test]
        public void Throws_trie_exception_on_missing_node()
        {
            TrieNode trieNode = new(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Forward_read_flags_on_resolve()
        {
            ITrieNodeResolver resolver = Substitute.For<ITrieNodeResolver>();
            resolver.LoadRlp(TestItem.KeccakA, ReadFlags.HintReadAhead).Returns((byte[])null);
            TrieNode trieNode = new(NodeType.Unknown, TestItem.KeccakA);
            try
            {
                Assert.Throws<TrieException>(() => trieNode.ResolveNode(resolver, ReadFlags.HintReadAhead));
            }
            catch (TrieException)
            {
            }
            resolver.Received().LoadRlp(TestItem.KeccakA, ReadFlags.HintReadAhead);
        }

        [Test]
        public void Throws_trie_exception_on_unexpected_format()
        {
            TrieNode trieNode = new(NodeType.Unknown, new byte[42]);
            Assert.Throws<TrieNodeException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_keccak_and_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void When_resolving_an_unknown_node_without_rlp_trie_exception_should_be_thrown()
        {
            TrieNode trieNode = new(NodeType.Unknown, Keccak.Zero);
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Encoding_leaf_without_key_throws_trie_exception()
        {
            TrieNode trieNode = new(NodeType.Leaf);
            trieNode.Value = new byte[] { 1, 2, 3 };
            Assert.Throws<TrieException>(() => trieNode.RlpEncode(NullTrieNodeResolver.Instance));
        }

        [Test]
        public void Throws_trie_exception_when_resolving_key_on_missing_rlp()
        {
            TrieNode trieNode = new(NodeType.Unknown);
            Assert.Throws<TrieException>(() => trieNode.ResolveKey(NullTrieNodeResolver.Instance, false));
        }

        [Test(Description = "This is controversial and only used in visitors. Can consider an exception instead.")]
        public void Get_child_hash_is_null_when_rlp_is_null()
        {
            TrieNode trieNode = new(NodeType.Branch);
            Assert.Null(trieNode.GetChildHash(0));
        }

        [Test]
        public void Can_check_if_branch_is_valid_with_one_child_less()
        {
            Context ctx = new();
            for (int i = 0; i < 16; i++)
            {
                TrieNode trieNode = new(NodeType.Branch);
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
            Context ctx = new();
            for (int nonNullChildrenCount = 0; nonNullChildrenCount < 16; nonNullChildrenCount++)
            {
                TrieNode trieNode = new(NodeType.Branch);
                for (int j = 0; j < nonNullChildrenCount; j++)
                {
                    trieNode.SetChild(j, ctx.TiniestLeaf);
                }

                CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
                TrieNode restoredNode = new(NodeType.Branch, rlp);

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
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(11, ctx.TiniestLeaf);

            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 11);
            decodedTiniest.ResolveNode(NullTrieNodeResolver.Instance);

            Assert.That(decodedTiniest.Value, Is.EqualTo(ctx.TiniestLeaf.Value), "value");
            Assert.That(HexPrefix.ToBytes(decodedTiniest.Key!, true), Is.EqualTo(HexPrefix.ToBytes(ctx.TiniestLeaf.Key!, true)), "key");
        }

        [Test]
        public void Can_encode_decode_heavy_branch()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(11, ctx.HeavyLeaf);

            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 11);

            Assert.That(decodedTiniest.Keccak, Is.EqualTo(decoded.GetChildHash(11)), "value");
        }

        [Test]
        public void Can_encode_decode_tiny_extension()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.Key = new byte[] { 5 };
            trieNode.SetChild(0, ctx.TiniestLeaf);

            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode? decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 0);
            decodedTiniest?.ResolveNode(NullTrieNodeResolver.Instance);

            Assert.That(decodedTiniest.Value, Is.EqualTo(ctx.TiniestLeaf.Value), "value");
            Assert.That(HexPrefix.ToBytes(decodedTiniest.Key!, true), Is.EqualTo(HexPrefix.ToBytes(ctx.TiniestLeaf.Key!, true)),
                "key");
        }

        [Test]
        public void Can_encode_decode_heavy_extension()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.Key = new byte[] { 5 };
            trieNode.SetChild(0, ctx.HeavyLeaf);

            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode decoded = new(NodeType.Unknown, rlp);
            decoded.ResolveNode(NullTrieNodeResolver.Instance);
            TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, 0);

            Assert.That(decodedTiniest.Keccak, Is.EqualTo(decoded.GetChildHash(0)), "keccak");
        }

        [Test]
        public void Can_set_and_get_children_using_indexer()
        {
            TrieNode tiniest = new(NodeType.Leaf);
            tiniest.Key = new byte[] { 5 };
            tiniest.Value = new byte[] { 10 };

            TrieNode trieNode = new(NodeType.Branch);
            trieNode[11] = tiniest;
            TrieNode getResult = trieNode.GetChild(NullTrieNodeResolver.Instance, 11);
            Assert.That(getResult, Is.SameAs(tiniest));
        }

        [Test]
        public void Get_child_hash_works_on_hashed_child_of_a_branch()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Branch);
            trieNode[11] = ctx.HeavyLeaf;
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
            TrieNode decoded = new(NodeType.Branch, rlp);

            Keccak getResult = decoded.GetChildHash(11);
            Assert.NotNull(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_inlined_child_of_a_branch()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Branch);

            trieNode[11] = ctx.TiniestLeaf;
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
            TrieNode decoded = new(NodeType.Branch, rlp);

            Keccak getResult = decoded.GetChildHash(11);
            Assert.Null(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_hashed_child_of_an_extension()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode[0] = ctx.HeavyLeaf;
            trieNode.Key = new byte[] { 5 };
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
            TrieNode decoded = new(NodeType.Extension, rlp);

            Keccak getResult = decoded.GetChildHash(0);
            Assert.NotNull(getResult);
        }

        [Test]
        public void Get_child_hash_works_on_inlined_child_of_an_extension()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode[0] = ctx.TiniestLeaf;
            trieNode.Key = new byte[] { 5 };
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance);
            TrieNode decoded = new(NodeType.Extension, rlp);

            Keccak getResult = decoded.GetChildHash(0);
            Assert.Null(getResult);
        }

        [Test]
        public void Extension_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new();
            TrieNode ignore = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("ccc"), Array.Empty<byte>());
            TrieNode node = TrieNodeFactory.CreateExtension(Bytes.FromHexString("aa"), ignore);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitExtension(node, context);
        }

        [Test]
        public void Unknown_node_with_missing_data_can_accept_visitor()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new();
            TrieNode node = new(NodeType.Unknown);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitMissingNode(node.Keccak, context);
        }

        [Test]
        public void Leaf_with_simple_account_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new();
            Account account = new(100);
            AccountDecoder decoder = new();
            TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_empty_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            TrieVisitContext context = new();
            Account account = new(1, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new();
            TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_without_storage_and_with_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new();
            Account account = new(1, 100, Keccak.EmptyTreeHash, Keccak.Zero);
            AccountDecoder decoder = new();
            TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Leaf_with_contract_with_storage_and_without_code_can_accept_visitors()
        {
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new();
            Account account = new(1, 100, Keccak.Zero, Keccak.OfAnEmptyString);
            AccountDecoder decoder = new();
            TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitLeaf(node, context, node.Value);
        }

        [Test]
        public void Extension_with_leaf_can_be_visited()
        {
            Context ctx = new();
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new();
            TrieNode node = TrieNodeFactory.CreateExtension(Bytes.FromHexString("aa"), ctx.AccountLeaf);

            node.Accept(visitor, NullTrieNodeResolver.Instance, context);

            visitor.Received().VisitExtension(node, context);
            visitor.Received().VisitLeaf(ctx.AccountLeaf, context, ctx.AccountLeaf.Value);
        }

        [Test]
        public void Branch_with_children_can_be_visited()
        {
            Context ctx = new();
            ITreeVisitor visitor = Substitute.For<ITreeVisitor>();
            visitor.ShouldVisit(Arg.Any<Keccak>()).Returns(true);

            TrieVisitContext context = new();
            TrieNode node = new(NodeType.Branch);
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
            TrieVisitContext context = new();
            TrieNode node = new(NodeType.Branch);
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
            TrieNode node = new(NodeType.Branch);
            node.RlpEncode(NullTrieNodeResolver.Instance);
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_null_returns_false()
        {
            TrieNode node = new(NodeType.Extension);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_null_node_returns_false()
        {
            TrieNode node = new(NodeType.Extension);
            node.SetChild(0, null);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_not_dirty_returns_false()
        {
            TrieNode node = new(NodeType.Extension);
            TrieNode cleanChild = new(NodeType.Leaf, Keccak.Zero);
            node.SetChild(0, cleanChild);
            Assert.False(node.IsChildDirty(0));
        }

        [Test]
        public void Is_child_dirty_on_extension_when_child_is_dirty_returns_true()
        {
            TrieNode node = new(NodeType.Extension);
            TrieNode dirtyChild = new(NodeType.Leaf);
            node.SetChild(0, dirtyChild);
            Assert.True(node.IsChildDirty(0));
        }

        [Test]
        public void Empty_branch_will_not_be_valid_with_one_child_less()
        {
            TrieNode node = new(NodeType.Branch);
            Assert.False(node.IsValidWithOneNodeLess);
        }

        [Test]
        public void Cannot_ask_about_validity_on_non_branch_nodes()
        {
            TrieNode leaf = new(NodeType.Leaf);
            TrieNode extension = new(NodeType.Leaf);
            Assert.Throws<TrieException>(() => _ = leaf.IsValidWithOneNodeLess, "leaf");
            Assert.Throws<TrieException>(() => _ = extension.IsValidWithOneNodeLess, "extension");
        }

        [Test]
        public void Can_encode_branch_with_unresolved_children()
        {
            TrieNode node = new(NodeType.Branch);
            TrieNode randomTrieNode = new(NodeType.Leaf);
            randomTrieNode.Key = new byte[] { 1, 2, 3 };
            randomTrieNode.Value = new byte[] { 1, 2, 3 };
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, randomTrieNode);
            }

            CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode restoredNode = new(NodeType.Branch, rlp);

            restoredNode.RlpEncode(NullTrieNodeResolver.Instance);
        }

        [Test]
        public void Size_of_a_heavy_leaf_is_correct()
        {
            Context ctx = new();
            Assert.That(ctx.HeavyLeaf.GetMemorySize(false), Is.EqualTo(224));
        }

        [Test]
        public void Size_of_a_tiny_leaf_is_correct()
        {
            Context ctx = new();
            Assert.That(ctx.TiniestLeaf.GetMemorySize(false), Is.EqualTo(144));
        }

        [Test]
        public void Size_of_a_branch_is_correct()
        {
            Context ctx = new();
            TrieNode node = new(NodeType.Branch);
            node.Key = new byte[] { 1 };
            for (int i = 0; i < 16; i++)
            {
                node.SetChild(i, ctx.AccountLeaf);
            }

            Assert.That(node.GetMemorySize(true), Is.EqualTo(3664));
            Assert.That(node.GetMemorySize(false), Is.EqualTo(208));
        }

        [Test]
        public void Size_of_an_extension_is_correct()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.Key = new byte[] { 1 };
            trieNode.SetChild(0, ctx.TiniestLeaf);

            Assert.That(trieNode.GetMemorySize(false), Is.EqualTo(120));
        }

        [Test]
        public void Size_of_unknown_node_is_correct()
        {
            Context ctx = new();
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.Key = new byte[] { 1 };
            trieNode.SetChild(0, ctx.TiniestLeaf);

            Assert.That(trieNode.GetMemorySize(true), Is.EqualTo(264));
            Assert.That(trieNode.GetMemorySize(false), Is.EqualTo(120));
        }

        [Test]
        public void Size_of_an_unknown_empty_node_is_correct()
        {
            TrieNode trieNode = new(NodeType.Unknown);
            trieNode.GetMemorySize(false).Should().Be(56);
        }

        [Test]
        public void Size_of_an_unknown_node_with_keccak_is_correct()
        {
            TrieNode trieNode = new(NodeType.Unknown, Keccak.Zero);
            trieNode.GetMemorySize(false).Should().Be(104);
        }

        [Test]
        public void Size_of_extension_with_child()
        {
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, null);
            trieNode.GetMemorySize(false).Should().Be(96);
        }

        [Test]
        public void Size_of_branch_with_data()
        {
            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(0, null);
            trieNode.GetMemorySize(false).Should().Be(208);
        }

        [Test]
        public void Size_of_leaf_with_value()
        {
            TrieNode trieNode = new(NodeType.Leaf);
            trieNode.Value = new byte[7];
            trieNode.GetMemorySize(false).Should().Be(128);
        }

        [Test]
        public void Size_of_an_unknown_node_with_full_rlp_is_correct()
        {
            TrieNode trieNode = new(NodeType.Unknown, new byte[7]);
            trieNode.GetMemorySize(false).Should().Be(120);
        }

        [Test]
        public void Size_of_keccak_is_correct()
        {
            Keccak.MemorySize.Should().Be(48);
        }

        [Test]
        public void Size_of_rlp_stream_is_correct()
        {
            RlpStream rlpStream = new(100);
            rlpStream.MemorySize.Should().Be(160);
        }

        [Test]
        public void Size_of_rlp_stream_7_is_correct()
        {
            RlpStream rlpStream = new(7);
            rlpStream.MemorySize.Should().Be(64);
        }

        [Test]
        public void Size_of_rlp_unaligned_is_correct()
        {
            Rlp rlp = new(new byte[1]);
            rlp.MemorySize.Should().Be(56);
        }

        [Test]
        public void Size_of_rlp_aligned_is_correct()
        {
            Rlp rlp = new(new byte[8]);
            rlp.MemorySize.Should().Be(56);
        }

        [Test]
        public void Cannot_seal_already_sealed()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.Seal());
        }

        [Test]
        public void Cannot_change_value_on_sealed()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.Value = new byte[5]);
        }

        [Test]
        public void Cannot_change_key_on_sealed()
        {
            TrieNode trieNode = new(NodeType.Leaf, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(
                () => trieNode.Key = Bytes.FromHexString("aaa"));
        }

        [Test]
        public void Cannot_set_child_on_sealed()
        {
            TrieNode child = new(NodeType.Leaf, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension, Keccak.Zero);
            Assert.Throws<InvalidOperationException>(() => trieNode.SetChild(0, child));
        }

        [Test]
        public void Pruning_regression()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.Key = Bytes.FromHexString("abcd");
            trieNode.RlpEncode(NullTrieStore.Instance);
        }

        [Test]
        public void Extension_child_as_keccak()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.GetChild(NullTrieStore.Instance, 0).Should().BeOfType<TrieNode>();
        }

        [Test]
        public void Extension_child_as_keccak_memory_size()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.GetMemorySize(false).Should().Be(144);
        }

        [Test]
        public void Extension_child_as_keccak_clone()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            TrieNode cloned = trieNode.Clone();

            cloned.GetMemorySize(false).Should().Be(144);
        }

        [Test]
        public void Unresolve_of_persisted()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);
            trieNode.Key = Bytes.FromHexString("abcd");
            trieNode.ResolveKey(NullTrieStore.Instance, false);

            trieNode.PrunePersistedRecursively(1);
            trieNode.PrunePersistedRecursively(1);
        }

        [Test]
        public void Small_child_unresolve()
        {
            TrieNode child = new(NodeType.Leaf);
            child.Value = Bytes.FromHexString("a");
            child.Key = Bytes.FromHexString("b");
            child.ResolveKey(NullTrieStore.Instance, false);
            child.IsPersisted = true;

            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);
            trieNode.Key = Bytes.FromHexString("abcd");
            trieNode.ResolveKey(NullTrieStore.Instance, false);

            trieNode.PrunePersistedRecursively(2);
            trieNode.GetChild(NullTrieStore.Instance, 0).Should().Be(child);
        }

        [Test]
        public void Extension_child_as_keccak_not_dirty()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.IsChildDirty(0).Should().Be(false);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Extension_child_as_keccak_call_recursively(bool skipPersisted)
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            int count = 0;
            trieNode.CallRecursively(n => count++, NullTrieStore.Instance, skipPersisted, LimboTraceLogger.Instance);
            count.Should().Be(1);
        }

        [Test]
        public void Branch_child_as_keccak_encode()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(0, child);
            trieNode.SetChild(4, child);

            trieNode.PrunePersistedRecursively(1);
            trieNode.RlpEncode(NullTrieStore.Instance);
        }

        [Test]
        public void Branch_child_as_keccak_resolved()
        {
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Branch);
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
            TrieNode child = new(NodeType.Unknown, Keccak.Zero);
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);

            trieNode.PrunePersistedRecursively(1);
            var trieStore = Substitute.For<ITrieNodeResolver>();
            trieStore.FindCachedOrUnknown(Arg.Any<Keccak>()).Returns(child);
            trieNode.GetChild(trieStore, 0).Should().Be(child);
        }

        [Test]
        public void Batch_not_db_regression()
        {
            TrieNode child = new(NodeType.Leaf);
            child.Key = Bytes.FromHexString("abc");
            child.Value = new byte[200];
            child.Seal();

            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, child);
            trieNode.Seal();

            ITrieNodeResolver trieStore = Substitute.For<ITrieNodeResolver>();
            trieStore.LoadRlp(Arg.Any<Keccak>()).Throws(new TrieException());
            child.ResolveKey(trieStore, false);
            child.IsPersisted = true;

            trieStore.FindCachedOrUnknown(Arg.Any<Keccak>()).Returns(new TrieNode(NodeType.Unknown, child.Keccak!));
            trieNode.GetChild(trieStore, 0);
            Assert.Throws<TrieException>(() => trieNode.GetChild(trieStore, 0).ResolveNode(trieStore));
        }

        [Ignore("This does not fail on the build server")]
        [Test]
        public async Task Trie_node_is_not_thread_safe()
        {
            TrieNode trieNode = new(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                trieNode.SetChild(i, new TrieNode(NodeType.Unknown, TestItem.Keccaks[i]));
            }

            trieNode.Seal();
            trieNode.ResolveKey(Substitute.For<ITrieNodeResolver>(), false);

            void CheckChildren()
            {
                for (int i = 0; i < 16 * 10; i++)
                {
                    try
                    {
                        trieNode.GetChildHash(i % 16).Should().BeEquivalentTo(TestItem.Keccaks[i % 16], i.ToString());
                    }
                    catch (Exception)
                    {
                        throw new AssertionException("Failed");
                    }
                }
            }

            List<Task> tasks = new();
            for (int i = 0; i < 2; i++)
            {
                Task task = new(CheckChildren);
                task.Start();
                tasks.Add(task);
            }

            Assert.ThrowsAsync<AssertionException>(() => Task.WhenAll(tasks));
            await Task.CompletedTask;
        }

        [Test]
        public void Rlp_is_cloned_when_cloning()
        {
            TrieStore trieStore = new(new MemDb(), NullLogManager.Instance);

            TrieNode leaf1 = new(NodeType.Leaf);
            leaf1.Key = Bytes.FromHexString("abc");
            leaf1.Value = new byte[111];
            leaf1.ResolveKey(trieStore, false);
            leaf1.Seal();
            trieStore.CommitNode(0, new NodeCommitInfo(leaf1));

            TrieNode leaf2 = new(NodeType.Leaf);
            leaf2.Key = Bytes.FromHexString("abd");
            leaf2.Value = new byte[222];
            leaf2.ResolveKey(trieStore, false);
            leaf2.Seal();
            trieStore.CommitNode(0, new NodeCommitInfo(leaf2));

            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(1, leaf1);
            trieNode.SetChild(2, leaf2);
            trieNode.ResolveKey(trieStore, true);
            CappedArray<byte>? rlp = trieNode.FullRlp;

            TrieNode restoredBranch = new(NodeType.Branch, rlp);

            TrieNode clone = restoredBranch.Clone();
            var restoredLeaf1 = clone.GetChild(trieStore, 1);
            restoredLeaf1.Should().NotBeNull();
            restoredLeaf1.ResolveNode(trieStore);
            restoredLeaf1.Value.Should().BeEquivalentTo(leaf1.Value);
        }

        [Test]
        public void Can_parallel_read_unresolved_children()
        {
            TrieNode node = new(NodeType.Branch);
            for (int i = 0; i < 16; i++)
            {
                TrieNode randomTrieNode = new(NodeType.Leaf);
                randomTrieNode.Key = new byte[] { (byte)i, 2, 3 };
                randomTrieNode.Value = new byte[] { 1, 2, 3 };
                node.SetChild(i, randomTrieNode);
            }

            CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance);

            TrieNode restoredNode = new(NodeType.Branch, rlp);

            Parallel.For(0, 32, (index, _) => restoredNode.GetChild(NullTrieNodeResolver.Instance, index % 3));
        }

        private class Context
        {
            public TrieNode TiniestLeaf { get; }
            public TrieNode HeavyLeaf { get; }
            public TrieNode AccountLeaf { get; }

            public Context()
            {
                TiniestLeaf = new TrieNode(NodeType.Leaf);
                TiniestLeaf.Key = new byte[] { 5 };
                TiniestLeaf.Value = new byte[] { 10 };

                HeavyLeaf = new TrieNode(NodeType.Leaf);
                HeavyLeaf.Key = new byte[20];
                HeavyLeaf.Value = Bytes.Concat(Keccak.EmptyTreeHash.Bytes, Keccak.EmptyTreeHash.Bytes);

                Account account = new(100);
                AccountDecoder decoder = new();
                AccountLeaf = TrieNodeFactory.CreateLeaf(
                    Bytes.FromHexString("bbb"),
                    decoder.Encode(account).Bytes);
            }
        }
    }
}
