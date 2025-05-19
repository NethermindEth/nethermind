// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
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
        Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty));
    }

    [Test]
    public void Forward_read_flags_on_resolve()
    {
        ITrieNodeResolver resolver = Substitute.For<ITrieNodeResolver>();
        resolver.LoadRlp(TreePath.Empty, TestItem.KeccakA, ReadFlags.HintReadAhead).Returns((byte[])null);
        TrieNode trieNode = new(NodeType.Unknown, TestItem.KeccakA);
        try
        {
            Assert.Throws<TrieException>(() => trieNode.ResolveNode(resolver, TreePath.Empty, ReadFlags.HintReadAhead));
        }
        catch (TrieException)
        {
        }
        resolver.Received().LoadRlp(TreePath.Empty, TestItem.KeccakA, ReadFlags.HintReadAhead);
    }

    [Test]
    public void Throws_trie_exception_on_unexpected_format()
    {
        TrieNode trieNode = new(NodeType.Unknown, new byte[42]);
        Assert.Throws<TrieNodeException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty));
    }

    [Test]
    public void When_resolving_an_unknown_node_without_keccak_and_rlp_trie_exception_should_be_thrown()
    {
        TrieNode trieNode = new(NodeType.Unknown);
        Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty));
    }

    [Test]
    public void When_resolving_an_unknown_node_without_rlp_trie_exception_should_be_thrown()
    {
        TrieNode trieNode = new(NodeType.Unknown, Keccak.Zero);
        Assert.Throws<TrieException>(() => trieNode.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty));
    }

    [Test]
    public void Encoding_leaf_without_key_throws_trie_exception()
    {
        TrieNode trieNode = new(NodeType.Leaf);
        trieNode.Value = new byte[] { 1, 2, 3 };
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath));
    }

    [Test]
    public void Throws_trie_exception_when_resolving_key_on_missing_rlp()
    {
        TrieNode trieNode = new(NodeType.Unknown);
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath, false));
    }

    [Test(Description = "This is controversial and only used in visitors. Can consider an exception instead.")]
    public void Get_child_hash_is_null_when_rlp_is_null()
    {
        TrieNode trieNode = new(NodeType.Branch);
        Assert.That(trieNode.GetChildHash(0), Is.Null);
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
                Assert.That(trieNode.IsValidWithOneNodeLess, Is.True);
            }
            else
            {
                Assert.That(trieNode.IsValidWithOneNodeLess, Is.False);
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

            TreePath emptyPath = TreePath.Empty;
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
            TrieNode restoredNode = new(NodeType.Branch, rlp);

            for (int childIndex = 0; childIndex < 16; childIndex++)
            {
                if (childIndex < nonNullChildrenCount)
                {
                    Assert.That(trieNode.IsChildNull(childIndex), Is.False, $"original {childIndex}");
                    Assert.That(restoredNode.IsChildNull(childIndex), Is.False, $"restored {childIndex}");
                }
                else
                {
                    Assert.That(trieNode.IsChildNull(childIndex), Is.True, $"original {childIndex}");
                    Assert.That(restoredNode.IsChildNull(childIndex), Is.True, $"restored {childIndex}");
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

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new(NodeType.Unknown, rlp);
        decoded.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);
        decodedTiniest.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

        Assert.That(decodedTiniest.Value.ToArray(), Is.EqualTo(ctx.TiniestLeaf.Value.ToArray()), "value");
        Assert.That(HexPrefix.ToBytes(decodedTiniest.Key!, true), Is.EqualTo(HexPrefix.ToBytes(ctx.TiniestLeaf.Key!, true)), "key");
    }

    [Test]
    public void Can_encode_decode_heavy_branch()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Branch);
        trieNode.SetChild(11, ctx.HeavyLeaf);

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new(NodeType.Unknown, rlp);
        decoded.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);

        Assert.That(decodedTiniest.Keccak, Is.EqualTo(decoded.GetChildHash(11)), "value");
    }

    [Test]
    public void Can_encode_decode_tiny_extension()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.Key = new byte[] { 5 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new(NodeType.Unknown, rlp);
        decoded.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        TrieNode? decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 0);
        decodedTiniest?.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

        Assert.That(decodedTiniest.Value.ToArray(), Is.EqualTo(ctx.TiniestLeaf.Value.ToArray()), "value");
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

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new(NodeType.Unknown, rlp);
        decoded.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 0);

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
        TreePath emptyPath = TreePath.Empty;
        TrieNode getResult = trieNode.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);
        Assert.That(getResult, Is.SameAs(tiniest));
    }

    [Test]
    public void Get_child_hash_works_on_hashed_child_of_a_branch()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Branch);
        trieNode[11] = ctx.HeavyLeaf;
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = new(NodeType.Branch, rlp);

        Hash256 getResult = decoded.GetChildHash(11);
        Assert.That(getResult, Is.Not.Null);
    }

    [Test]
    public void Get_child_hash_works_on_inlined_child_of_a_branch()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Branch);

        trieNode[11] = ctx.TiniestLeaf;
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = new(NodeType.Branch, rlp);

        Hash256 getResult = decoded.GetChildHash(11);
        Assert.That(getResult, Is.Null);
    }

    [Test]
    public void Get_child_hash_works_on_hashed_child_of_an_extension()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Extension);
        trieNode[0] = ctx.HeavyLeaf;
        trieNode.Key = new byte[] { 5 };
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = new(NodeType.Extension, rlp);

        Hash256 getResult = decoded.GetChildHash(0);
        Assert.That(getResult, Is.Not.Null);
    }

    [Test]
    public void Get_child_hash_works_on_inlined_child_of_an_extension()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Extension);
        trieNode[0] = ctx.TiniestLeaf;
        trieNode.Key = new byte[] { 5 };
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = new(NodeType.Extension, rlp);

        Hash256 getResult = decoded.GetChildHash(0);
        Assert.That(getResult, Is.Null);
    }

    [Test]
    public void Extension_can_accept_visitors()
    {
        ITreeVisitor<EmptyContext> visitor = Substitute.For<ITreeVisitor<EmptyContext>>();
        TrieVisitContext context = new();
        TrieNode ignore = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("ccc"), Array.Empty<byte>());
        TrieNode node = TrieNodeFactory.CreateExtension(Bytes.FromHexString("aa"), ignore);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, new EmptyContext(), NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.Received().VisitExtension(new EmptyContext(), node);
    }

    [Test]
    public void Unknown_node_with_missing_data_can_accept_visitor()
    {
        ITreeVisitor<EmptyContext> visitor = Substitute.For<ITreeVisitor<EmptyContext>>();
        TrieVisitContext context = new();
        TrieNode node = new(NodeType.Unknown);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, new EmptyContext(), NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.Received().VisitMissingNode(new EmptyContext(), node.Keccak);
    }

    [Test]
    public void Leaf_with_simple_account_can_accept_visitors()
    {
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        Account account = new(100);
        AccountDecoder decoder = new();
        TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitLeafReceived[(TreePath.Empty, node, node.Value.ToArray())].Should().Be(1);
    }

    [Test]
    public void Leaf_with_contract_without_storage_and_empty_code_can_accept_visitors()
    {
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        Account account = new(1, 100, Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);
        AccountDecoder decoder = new();
        TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitLeafReceived[(TreePath.Empty, node, node.Value.ToArray())].Should().Be(1);
    }

    [Test]
    public void Leaf_with_contract_without_storage_and_with_code_can_accept_visitors()
    {
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        Account account = new(1, 100, Keccak.EmptyTreeHash, Keccak.Zero);
        AccountDecoder decoder = new();
        TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitLeafReceived[(TreePath.Empty, node, node.Value.ToArray())].Should().Be(1);
    }

    [Test]
    public void Leaf_with_contract_with_storage_and_without_code_can_accept_visitors()
    {
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        Account account = new(1, 100, Keccak.Zero, Keccak.OfAnEmptyString);
        AccountDecoder decoder = new();
        TrieNode node = TrieNodeFactory.CreateLeaf(Bytes.FromHexString("aa"), decoder.Encode(account).Bytes);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitLeafReceived[(TreePath.Empty, node, node.Value.ToArray())].Should().Be(1);
    }

    [Test]
    public void Extension_with_leaf_can_be_visited()
    {
        Context ctx = new();
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        TrieNode node = TrieNodeFactory.CreateExtension(Bytes.FromHexString("aa"), ctx.AccountLeaf);

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitExtensionReceived[(TreePath.Empty, node)].Should().Be(1);
        visitor.VisitLeafReceived[(new(new(Bytes.FromHexString("0xa000000000000000000000000000000000000000000000000000000000000000")), 1), ctx.AccountLeaf, ctx.AccountLeaf.Value.ToArray())].Should().Be(1);
    }

    [Test]
    public void Branch_with_children_can_be_visited()
    {
        Context ctx = new();
        TreeVisitorMock visitor = new();
        TrieVisitContext context = new();
        TrieNode node = new(NodeType.Branch);
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, ctx.AccountLeaf);
        }

        TreePath emptyPath = TreePath.Empty;
        node.ResolveKey(NullTrieStore.Instance, ref emptyPath, true);
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitBranchReceived[(TreePath.Empty, node)].Should().Be(1);
        for (byte i = 0; i < 16; i++)
        {
            var hex = "0x" + i.ToString("x2")[1] + "000000000000000000000000000000000000000000000000000000000000000";
            visitor.VisitLeafReceived[(new(new(Bytes.FromHexString(hex)), 1), ctx.AccountLeaf, ctx.AccountLeaf.Value.ToArray())].Should().Be(1);
        }
    }

    [Test]
    public void Branch_can_accept_visitors()
    {
        ITreeVisitor<EmptyContext> visitor = Substitute.For<ITreeVisitor<EmptyContext>>();
        TrieVisitContext context = new();
        TrieNode node = new(NodeType.Branch);
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, null);
        }

        TreePath emptyPath = TreePath.Empty;
        node.Accept(visitor, new EmptyContext(), NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.Received().VisitBranch(new EmptyContext(), node);
    }

    [Test]
    public void Can_encode_branch_with_nulls()
    {
        TrieNode node = new(NodeType.Branch);
        TreePath emptyPath = TreePath.Empty;
        node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_null_returns_false()
    {
        TrieNode node = new(NodeType.Extension);
        Assert.That(node.IsChildDirty(0), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_null_node_returns_false()
    {
        TrieNode node = new(NodeType.Extension);
        node.SetChild(0, null);
        Assert.That(node.IsChildDirty(0), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_not_dirty_returns_false()
    {
        TrieNode node = new(NodeType.Extension);
        TrieNode cleanChild = new(NodeType.Leaf, Keccak.Zero);
        node.SetChild(0, cleanChild);
        Assert.That(node.IsChildDirty(0), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_dirty_returns_true()
    {
        TrieNode node = new(NodeType.Extension);
        TrieNode dirtyChild = new(NodeType.Leaf);
        node.SetChild(0, dirtyChild);
        Assert.That(node.IsChildDirty(0), Is.True);
    }

    [Test]
    public void Empty_branch_will_not_be_valid_with_one_child_less()
    {
        TrieNode node = new(NodeType.Branch);
        Assert.That(node.IsValidWithOneNodeLess, Is.False);
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

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode restoredNode = new(NodeType.Branch, rlp);

        restoredNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
    }

    [Test]
    public void Size_of_a_heavy_leaf_is_correct()
    {
        Context ctx = new();
        Assert.That(ctx.HeavyLeaf.GetMemorySize(false), Is.EqualTo(208));
    }

    [Test]
    public void Size_of_a_tiny_leaf_is_correct()
    {
        Context ctx = new();
        Assert.That(ctx.TiniestLeaf.GetMemorySize(false), Is.EqualTo(136));
    }

    [Test]
    public void Size_of_a_branch_is_correct()
    {
        Context ctx = new();
        TrieNode node = new(NodeType.Branch);
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, ctx.AccountLeaf);
        }

        Assert.That(node.GetMemorySize(true), Is.EqualTo(3376));
        Assert.That(node.GetMemorySize(false), Is.EqualTo(176));
    }

    [Test]
    public void Size_of_an_extension_is_correct()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.Key = new byte[] { 1 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        Assert.That(trieNode.GetMemorySize(false), Is.EqualTo(96));
    }

    [Test]
    public void Size_of_unknown_node_is_correct()
    {
        Context ctx = new();
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.Key = new byte[] { 1 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        Assert.That(trieNode.GetMemorySize(true), Is.EqualTo(232));
        Assert.That(trieNode.GetMemorySize(false), Is.EqualTo(96));
    }

    [Test]
    public void Size_of_an_unknown_empty_node_is_correct()
    {
        TrieNode trieNode = new(NodeType.Unknown);
        trieNode.GetMemorySize(false).Should().Be(48);
    }

    [Test]
    public void Size_of_an_unknown_node_with_keccak_is_correct()
    {
        TrieNode trieNode = new(NodeType.Unknown, Keccak.Zero);
        trieNode.GetMemorySize(false).Should().Be(96);
    }

    [Test]
    public void Size_of_extension_with_child()
    {
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, null);
        trieNode.GetMemorySize(false).Should().Be(64);
    }

    [Test]
    public void Size_of_branch_with_data()
    {
        TrieNode trieNode = new(NodeType.Branch);
        trieNode.SetChild(0, null);
        trieNode.GetMemorySize(false).Should().Be(176);
    }

    [Test]
    public void Size_of_leaf_with_value()
    {
        TrieNode trieNode = new(NodeType.Leaf);
        trieNode.Value = new byte[7];
        trieNode.GetMemorySize(false).Should().Be(104);
    }

    [Test]
    public void Size_of_an_unknown_node_with_full_rlp_is_correct()
    {
        TrieNode trieNode = new(NodeType.Unknown, new byte[7]);
        trieNode.GetMemorySize(false).Should().Be(112);
    }

    [Test]
    public void Size_of_keccak_is_correct()
    {
        Hash256.MemorySize.Should().Be(48);
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
        TreePath emptyPath = TreePath.Empty;
        trieNode.RlpEncode(NullTrieStore.Instance, ref emptyPath);
    }

    [Test]
    public void Extension_child_as_keccak()
    {
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        TreePath emptyPath = TreePath.Empty;
        trieNode.GetChild(NullTrieStore.Instance, ref emptyPath, 0).Should().BeOfType<TrieNode>();
    }

    [Test]
    public void Extension_child_as_keccak_memory_size()
    {
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        trieNode.GetMemorySize(false).Should().Be(112);
    }

    [Test]
    public void Extension_child_as_keccak_clone()
    {
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        TrieNode cloned = trieNode.Clone();

        cloned.GetMemorySize(false).Should().Be(112);
    }

    [Test]
    public void Unresolve_of_persisted()
    {
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);
        trieNode.Key = Bytes.FromHexString("abcd");
        TreePath emptyPath = TreePath.Empty;
        trieNode.ResolveKey(NullTrieStore.Instance, ref emptyPath, false);

        trieNode.PrunePersistedRecursively(1);
        trieNode.PrunePersistedRecursively(1);
    }

    [Test]
    public void Small_child_unresolve()
    {
        TrieNode child = new(NodeType.Leaf);
        child.Value = Bytes.FromHexString("a");
        child.Key = Bytes.FromHexString("b");
        TreePath emptyPath = TreePath.Empty;
        child.ResolveKey(NullTrieStore.Instance, ref emptyPath, false);
        child.IsPersisted = true;

        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);
        trieNode.Key = Bytes.FromHexString("abcd");
        trieNode.ResolveKey(NullTrieStore.Instance, ref emptyPath, false);

        trieNode.PrunePersistedRecursively(2);
        trieNode.GetChild(NullTrieStore.Instance, ref emptyPath, 0).Should().Be(child);
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
        TreePath emptyPath = TreePath.Empty;
        trieNode.CallRecursively((n, s, p) => count++, null, ref emptyPath, NullTrieStore.Instance, skipPersisted, LimboTraceLogger.Instance);
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
        TreePath emptyPath = TreePath.Empty;
        trieNode.RlpEncode(NullTrieStore.Instance, ref emptyPath);
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
        trieStore.FindCachedOrUnknown(Arg.Any<TreePath>(), Arg.Any<Hash256>()).Returns(child);
        TreePath emptyPath = TreePath.Empty;
        trieNode.GetChild(trieStore, ref emptyPath, 0).Should().Be(child);
        trieNode.GetChild(trieStore, ref emptyPath, 1).Should().BeNull();
        trieNode.GetChild(trieStore, ref emptyPath, 4).Should().Be(child);
    }

    [Test]
    public void Child_as_keccak_cached()
    {
        TrieNode child = new(NodeType.Unknown, Keccak.Zero);
        TrieNode trieNode = new(NodeType.Extension);
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        var trieStore = Substitute.For<ITrieNodeResolver>();
        trieStore.FindCachedOrUnknown(Arg.Any<TreePath>(), Arg.Any<Hash256>()).Returns(child);
        TreePath emptyPath = TreePath.Empty;
        trieNode.GetChild(trieStore, ref emptyPath, 0).Should().Be(child);
    }

    [Test]
    public void Batch_not_db_regression()
    {
        TrieNode child = new(NodeType.Leaf);
        child.Key = Bytes.FromHexString("abc");
        child.Value = new byte[200];
        child.Seal();

        TrieNode trieNode = new(NodeType.Extension);
        trieNode.Key = Bytes.FromHexString("000102030506");
        trieNode.SetChild(0, child);
        trieNode.Seal();

        ITrieNodeResolver trieStore = Substitute.For<ITrieNodeResolver>();
        trieStore.LoadRlp(Arg.Any<TreePath>(), Arg.Any<Hash256>()).Throws(new TrieException());
        TreePath emptyPath = TreePath.Empty;
        child.ResolveKey(trieStore, ref emptyPath, false);
        child.IsPersisted = true;

        trieStore.FindCachedOrUnknown(Arg.Any<TreePath>(), Arg.Any<Hash256>()).Returns(new TrieNode(NodeType.Unknown, child.Keccak!));
        trieNode.GetChild(trieStore, ref emptyPath, 0);
        Assert.Throws<TrieException>(() => trieNode.GetChild(trieStore, ref emptyPath, 0).ResolveNode(trieStore, TreePath.Empty));
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
        TreePath emptyPath = TreePath.Empty;
        trieNode.ResolveKey(Substitute.For<ITrieNodeResolver>(), ref emptyPath, false);

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
        ITrieStore fullTrieStore = TestTrieStoreFactory.Build(new MemDb(), NullLogManager.Instance);
        IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

        TrieNode leaf1 = new(NodeType.Leaf);
        leaf1.Key = Bytes.FromHexString("abc");
        leaf1.Value = new byte[111];
        TreePath emptyPath = TreePath.Empty;
        leaf1.ResolveKey(trieStore, ref emptyPath, false);
        leaf1.Seal();

        TrieNode leaf2 = new(NodeType.Leaf);
        leaf2.Key = Bytes.FromHexString("abd");
        leaf2.Value = new byte[222];
        leaf2.ResolveKey(trieStore, ref emptyPath, false);
        leaf2.Seal();

        TreePath path = TreePath.Empty;

        using (IBlockCommitter _ = fullTrieStore.BeginBlockCommit(0))
        {
            using (ICommitter? committer = trieStore.BeginCommit(leaf2))
            {
                committer.CommitNode(ref path, new NodeCommitInfo(leaf1));
                committer.CommitNode(ref path, new NodeCommitInfo(leaf2));
            }
        }

        TrieNode trieNode = new(NodeType.Branch);
        trieNode.SetChild(1, leaf1);
        trieNode.SetChild(2, leaf2);
        trieNode.ResolveKey(trieStore, ref emptyPath, true);
        CappedArray<byte> rlp = trieNode.FullRlp;

        TrieNode restoredBranch = new(NodeType.Branch, rlp);

        TrieNode clone = restoredBranch.Clone();
        var restoredLeaf1 = clone.GetChild(trieStore, ref emptyPath, 1);
        restoredLeaf1.Should().NotBeNull();
        restoredLeaf1.ResolveNode(trieStore, TreePath.Empty);
        restoredLeaf1.Value.ToArray().Should().BeEquivalentTo(leaf1.Value.ToArray());
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

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode restoredNode = new(NodeType.Branch, rlp);

        Parallel.For(0, 32, (index, _) =>
        {
            TreePath emptyPathParallel = TreePath.Empty;
            restoredNode.GetChild(NullTrieNodeResolver.Instance, ref emptyPathParallel, index % 3);
        });
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

    private class TreeVisitorMock : ITreeVisitor<TreePathContext>
    {
        public readonly Dictionary<(TreePath path, TrieNode), int> VisitExtensionReceived = new();
        public readonly Dictionary<(TreePath path, TrieNode), int> VisitBranchReceived = new();
        public readonly Dictionary<(TreePath path, TrieNode, byte[]), int> VisitLeafReceived = new(new LeafComparer());

        public bool IsFullDbScan => false;

        public bool ShouldVisit(in TreePathContext nodeContext, in ValueHash256 nextNode) => true;

        public void VisitTree(in TreePathContext nodeContext, in ValueHash256 rootHash)
        {
        }

        public void VisitMissingNode(in TreePathContext ctx, in ValueHash256 nodeHash)
        {
        }

        public void VisitBranch(in TreePathContext ctx, TrieNode node)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(VisitBranchReceived, (ctx.Path, node), out _) += 1;
        }

        public void VisitExtension(in TreePathContext ctx, TrieNode node)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(VisitExtensionReceived, (ctx.Path, node), out _) += 1;
        }

        public void VisitLeaf(in TreePathContext ctx, TrieNode node)
        {
            CollectionsMarshal.GetValueRefOrAddDefault(VisitLeafReceived, (ctx.Path, node, node.Value.ToArray()), out _) += 1;
        }

        public void VisitAccount(in TreePathContext ctx, TrieNode node, in AccountStruct account)
        {
        }

        private class LeafComparer : IEqualityComparer<(TreePath, TrieNode, byte[])>
        {
            public bool Equals((TreePath, TrieNode, byte[]) x, (TreePath, TrieNode, byte[]) y) =>
                Equals(x.Item1, y.Item1) && Equals(x.Item2, y.Item2) && Bytes.EqualityComparer.Equals(x.Item3, y.Item3);

            public int GetHashCode((TreePath, TrieNode, byte[]) obj) =>
                HashCode.Combine(obj.Item1, obj.Item2, Bytes.EqualityComparer.GetHashCode(obj.Item3));
        }
    }
}
