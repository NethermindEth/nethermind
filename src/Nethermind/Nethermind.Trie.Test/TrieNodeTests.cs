// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
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
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        Assert.Throws<TrieException>(() => trieNode.Value = new byte[] { 1, 2, 3 });
    }

    [Test]
    public void Throws_trie_exception_on_missing_node()
    {
        TrieNode trieNode = new TrieSyncNode();
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => TrieNode.ResolveNode(ref trieNode, NullTrieNodeResolver.Instance, in emptyPath));
    }

    [Test]
    public void Forward_read_flags_on_resolve()
    {
        ITrieNodeResolver resolver = Substitute.For<ITrieNodeResolver>();
        resolver.LoadRlp(TreePath.Empty, TestItem.KeccakA, ReadFlags.HintReadAhead).Returns((byte[])null);
        TrieNode trieNode = new TrieSyncNode(TestItem.KeccakA);
        TreePath emptyPath = TreePath.Empty;
        try
        {
            Assert.Throws<TrieException>(() => TrieNode.ResolveNode(ref trieNode, resolver, in emptyPath, ReadFlags.HintReadAhead));
        }
        catch (TrieException)
        {
        }
        resolver.Received().LoadRlp(TreePath.Empty, TestItem.KeccakA, ReadFlags.HintReadAhead);
    }

    [Test]
    public void Throws_trie_exception_on_unexpected_format()
    {
        TrieNode trieNode = new TrieSyncNode(new byte[42]);
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieNodeException>(() => TrieNode.ResolveNode(ref trieNode, NullTrieNodeResolver.Instance, in emptyPath));
    }

    [Test]
    public void When_resolving_an_unknown_node_without_keccak_and_rlp_trie_exception_should_be_thrown()
    {
        TrieNode trieNode = new TrieSyncNode();
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => TrieNode.ResolveNode(ref trieNode, NullTrieNodeResolver.Instance, in emptyPath));
    }

    [Test]
    public void When_resolving_an_unknown_node_without_rlp_trie_exception_should_be_thrown()
    {
        TrieNode trieNode = new TrieSyncNode(Keccak.Zero);
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => TrieNode.ResolveNode(ref trieNode, NullTrieNodeResolver.Instance, in emptyPath));
    }

    [Test]
    public void Encoding_leaf_without_key_throws_trie_exception()
    {
        TrieNode trieNode = TrieNode.CreateLeafTyped();
        trieNode.Value = new byte[] { 1, 2, 3 };
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath));
    }

    [Test]
    public void Throws_trie_exception_when_resolving_key_on_missing_rlp()
    {
        TrieNode trieNode = new TrieSyncNode();
        TreePath emptyPath = TreePath.Empty;
        Assert.Throws<TrieException>(() => trieNode.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath));
    }

    [Test(Description = "This is controversial and only used in visitors. Can consider an exception instead.")]
    public void Get_child_hash_is_null_when_rlp_is_null()
    {
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        Assert.That(trieNode.GetChildHash(0), Is.Null);
    }

    [Test]
    public void Can_check_if_branch_is_valid_with_one_child_less()
    {
        Context ctx = new();
        for (int i = 0; i < 16; i++)
        {
            TrieNode trieNode = TrieNode.CreateBranchTyped();
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
            TrieNode trieNode = TrieNode.CreateBranchTyped();
            for (int j = 0; j < nonNullChildrenCount; j++)
            {
                trieNode.SetChild(j, ctx.TiniestLeaf);
            }

            TreePath emptyPath = TreePath.Empty;
            CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
            TrieNode restoredNode = TrieNode.CreateBranchTyped(rlp);

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
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(11, ctx.TiniestLeaf);

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new TrieSyncNode(rlp);
        TrieNode.ResolveNode(ref decoded, NullTrieNodeResolver.Instance, in emptyPath);
        TrieNode decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);
        TrieNode.ResolveNode(ref decodedTiniest, NullTrieNodeResolver.Instance, in emptyPath);

        Assert.That(decodedTiniest.Value.ToArray(), Is.EqualTo(ctx.TiniestLeaf.Value.ToArray()), "value");
        Assert.That(HexPrefix.ToBytes(decodedTiniest.Key!, true), Is.EqualTo(HexPrefix.ToBytes(ctx.TiniestLeaf.Key!, true)), "key");
    }

    [Test]
    public void Can_encode_decode_heavy_branch()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(11, ctx.HeavyLeaf);

        TreePath emptyPath = TreePath.Empty;
        ctx.HeavyLeaf.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new TrieSyncNode(rlp);
        TrieNode.ResolveNode(ref decoded, NullTrieNodeResolver.Instance, in emptyPath);

        // The heavy leaf is stored by hash in slot 11 of the encoded branch. Verify that
        // decoding repopulates the slot with the original child hash; calling GetChild on
        // NullTrieNodeResolver would now eagerly try to load the leaf (and fail) instead of
        // returning a NodeType.Unknown placeholder.
        Assert.That(decoded.GetChildHash(11), Is.EqualTo(ctx.HeavyLeaf.Keccak), "encoded child hash");
    }

    [Test]
    public void Can_encode_decode_tiny_extension()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.Key = new byte[] { 5 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new TrieSyncNode(rlp);
        TrieNode.ResolveNode(ref decoded, NullTrieNodeResolver.Instance, in emptyPath);
        TrieNode? decodedTiniest = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 0);
        if (decodedTiniest is not null)
        {
            TrieNode.ResolveNode(ref decodedTiniest, NullTrieNodeResolver.Instance, in emptyPath);
        }

        Assert.That(decodedTiniest.Value.ToArray(), Is.EqualTo(ctx.TiniestLeaf.Value.ToArray()), "value");
        Assert.That(HexPrefix.ToBytes(decodedTiniest.Key!, true), Is.EqualTo(HexPrefix.ToBytes(ctx.TiniestLeaf.Key!, true)),
            "key");
    }

    [Test]
    public void Can_encode_decode_heavy_extension()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.Key = new byte[] { 5 };
        trieNode.SetChild(0, ctx.HeavyLeaf);

        TreePath emptyPath = TreePath.Empty;
        ctx.HeavyLeaf.ResolveKey(NullTrieNodeResolver.Instance, ref emptyPath);
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode decoded = new TrieSyncNode(rlp);
        TrieNode.ResolveNode(ref decoded, NullTrieNodeResolver.Instance, in emptyPath);

        // The heavy leaf is stored by hash in the extension's child slot. Verify the slot
        // round-trips the encoded hash; GetChild on a backing-less resolver now eagerly
        // tries to load and would fail instead of returning a placeholder.
        Assert.That(decoded.GetChildHash(0), Is.EqualTo(ctx.HeavyLeaf.Keccak), "encoded child hash");
    }

    [Test]
    public void Can_set_and_get_children_using_indexer()
    {
        TrieNode tiniest = TrieNode.CreateLeafTyped();
        tiniest.Key = new byte[] { 5 };
        tiniest.Value = new byte[] { 10 };

        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode[11] = tiniest;
        TreePath emptyPath = TreePath.Empty;
        TrieNode getResult = trieNode.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);
        Assert.That(getResult, Is.SameAs(tiniest));
    }

    [Test]
    public void Get_child_hash_works_on_hashed_child_of_a_branch()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode[11] = ctx.HeavyLeaf;
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = TrieNode.CreateBranchTyped(rlp);

        Hash256 getResult = decoded.GetChildHash(11);
        Assert.That(getResult, Is.Not.Null);
    }

    [Test]
    public void Get_child_hash_works_on_inlined_child_of_a_branch()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateBranchTyped();

        trieNode[11] = ctx.TiniestLeaf;
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = TrieNode.CreateBranchTyped(rlp);

        Hash256 getResult = decoded.GetChildHash(11);
        Assert.That(getResult, Is.Null);
    }

    [Test]
    public void Inline_child_slice_shares_parent_rlp_array()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateBranchTyped();

        trieNode[11] = ctx.TiniestLeaf;
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = TrieNode.CreateBranchTyped(rlp);

        TrieNode? child = decoded.GetChild(NullTrieNodeResolver.Instance, ref emptyPath, 11);

        child.Should().NotBeNull();
        CappedArray<byte> parentRlp = decoded.FullRlp;
        CappedArray<byte> childRlp = child!.FullRlp;
        childRlp.UnderlyingArray.Should().BeSameAs(parentRlp.UnderlyingArray);
        childRlp.Offset.Should().BeGreaterThan(parentRlp.Offset);
        childRlp.AsSpan().ToArray().Should().Equal(ctx.TiniestLeaf.FullRlp.AsSpan().ToArray());

        TrieNode clone = child.Clone();
        clone.FullRlp.AsSpan().ToArray().Should().Equal(childRlp.AsSpan().ToArray());
    }

    [Test]
    public void Get_child_hash_works_on_hashed_child_of_an_extension()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode[0] = ctx.HeavyLeaf;
        trieNode.Key = new byte[] { 5 };
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = TrieNode.CreateExtensionTyped(rlp);

        Hash256 getResult = decoded.GetChildHash(0);
        Assert.That(getResult, Is.Not.Null);
    }

    [Test]
    public void Get_child_hash_works_on_inlined_child_of_an_extension()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode[0] = ctx.TiniestLeaf;
        trieNode.Key = new byte[] { 5 };
        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = trieNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        TrieNode decoded = TrieNode.CreateExtensionTyped(rlp);

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
        TrieNode node = new TrieSyncNode();

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
        TrieNode node = TrieNode.CreateBranchTyped();
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, ctx.AccountLeaf);
        }

        TreePath emptyPath = TreePath.Empty;
        node.ResolveKey(NullTrieStore.Instance, ref emptyPath);
        node.Accept(visitor, default, NullTrieNodeResolver.Instance, ref emptyPath, context);

        visitor.VisitBranchReceived[(TreePath.Empty, node)].Should().Be(1);
        for (byte i = 0; i < 16; i++)
        {
            string hex = "0x" + i.ToString("x2")[1] + "000000000000000000000000000000000000000000000000000000000000000";
            visitor.VisitLeafReceived[(new(new(Bytes.FromHexString(hex)), 1), ctx.AccountLeaf, ctx.AccountLeaf.Value.ToArray())].Should().Be(1);
        }
    }

    [Test]
    public void Branch_can_accept_visitors()
    {
        ITreeVisitor<EmptyContext> visitor = Substitute.For<ITreeVisitor<EmptyContext>>();
        TrieVisitContext context = new();
        TrieNode node = TrieNode.CreateBranchTyped();
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
        TrieNode node = TrieNode.CreateBranchTyped();
        TreePath emptyPath = TreePath.Empty;
        node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_null_returns_false()
    {
        TrieNode node = TrieNode.CreateExtensionTyped();
        Assert.That(node.TryGetDirtyChild(0, out TrieNode? childNode), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_null_node_returns_false()
    {
        TrieNode node = TrieNode.CreateExtensionTyped();
        node.SetChild(0, null);
        Assert.That(node.TryGetDirtyChild(0, out TrieNode? dirtyChild), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_not_dirty_returns_false()
    {
        TrieNode node = TrieNode.CreateExtensionTyped();
        TrieNode cleanChild = TrieNode.CreateLeafTyped(Keccak.Zero);
        node.SetChild(0, cleanChild);
        Assert.That(node.TryGetDirtyChild(0, out TrieNode? dirtyChild), Is.False);
    }

    [Test]
    public void Is_child_dirty_on_extension_when_child_is_dirty_returns_true()
    {
        TrieNode node = TrieNode.CreateExtensionTyped();
        TrieNode dirtyChild = TrieNode.CreateLeafTyped();
        node.SetChild(0, dirtyChild);
        Assert.That(node.TryGetDirtyChild(0, out TrieNode? _), Is.True);
    }

    [Test]
    public void Empty_branch_will_not_be_valid_with_one_child_less()
    {
        TrieNode node = TrieNode.CreateBranchTyped();
        Assert.That(node.IsValidWithOneNodeLess, Is.False);
    }

    [Test]
    public void Cannot_ask_about_validity_on_non_branch_nodes()
    {
        TrieNode leaf = TrieNode.CreateLeafTyped();
        TrieNode extension = TrieNode.CreateExtensionTyped();
        Assert.Throws<TrieException>(() => _ = leaf.IsValidWithOneNodeLess, "leaf");
        Assert.Throws<TrieException>(() => _ = extension.IsValidWithOneNodeLess, "extension");
    }

    [Test]
    public void Can_encode_branch_with_unresolved_children()
    {
        TrieNode node = TrieNode.CreateBranchTyped();
        TrieNode randomTrieNode = TrieNode.CreateLeafTyped();
        randomTrieNode.Key = new byte[] { 1, 2, 3 };
        randomTrieNode.Value = new byte[] { 1, 2, 3 };
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, randomTrieNode);
        }

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode restoredNode = TrieNode.CreateBranchTyped(rlp);

        restoredNode.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
    }

    [Test]
    public void Size_of_a_heavy_leaf_is_correct()
    {
        Context ctx = new();
        // B4: -8 (no _nodeData reference, shape fields are inline on TrieNodeLeaf)
        ctx.HeavyLeaf.GetMemorySize(false).Should().Be(232);
    }

    [Test]
    public void Size_of_a_tiny_leaf_is_correct()
    {
        Context ctx = new();
        ctx.TiniestLeaf.GetMemorySize(false).Should().Be(160);
    }

    [Test]
    public void Size_of_a_branch_is_correct()
    {
        Context ctx = new();
        TrieNode node = TrieNode.CreateBranchTyped();
        for (int i = 0; i < 16; i++)
        {
            node.SetChild(i, ctx.AccountLeaf);
        }

        // B4: branch self -8, plus -8 per child leaf (16) = -136 for recursive total
        node.GetMemorySize(true).Should().Be(3784);
        node.GetMemorySize(false).Should().Be(200);
    }

    [Test]
    public void Size_of_an_extension_is_correct()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.Key = new byte[] { 1 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        Assert.That(trieNode.GetMemorySize(false), Is.EqualTo(120));
    }

    [Test]
    public void Size_of_unknown_node_is_correct()
    {
        Context ctx = new();
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.Key = new byte[] { 1 };
        trieNode.SetChild(0, ctx.TiniestLeaf);

        trieNode.GetMemorySize(true).Should().Be(280);
        trieNode.GetMemorySize(false).Should().Be(120);
    }

    [Test]
    public void Size_of_an_unknown_empty_node_is_correct()
    {
        TrieNode trieNode = new TrieSyncNode();
        trieNode.GetMemorySize(false).Should().Be(72);
    }

    [Test]
    public void Size_of_an_unknown_node_with_keccak_is_correct()
    {
        TrieNode trieNode = new TrieSyncNode(Keccak.Zero);
        trieNode.GetMemorySize(false).Should().Be(72);
    }

    [Test]
    public void Size_of_extension_with_child()
    {
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, null);
        trieNode.GetMemorySize(false).Should().Be(88);
    }

    [Test]
    public void Size_of_branch_with_data()
    {
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(0, null);
        trieNode.GetMemorySize(false).Should().Be(200);
    }

    [Test]
    public void Size_of_leaf_with_value()
    {
        TrieNode trieNode = TrieNode.CreateLeafTyped();
        trieNode.Value = new byte[7];
        trieNode.GetMemorySize(false).Should().Be(128);
    }

    [Test]
    public void Size_of_an_unknown_node_with_full_rlp_is_correct()
    {
        TrieNode trieNode = new TrieSyncNode(new byte[7]);
        trieNode.GetMemorySize(false).Should().Be(104);
    }

    [Test]
    public void Size_of_keccak_is_correct() => Hash256.MemorySize.Should().Be(48);

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
        TrieNode trieNode = TrieNode.CreateLeafTyped(Keccak.Zero);
        Assert.Throws<InvalidOperationException>(() => trieNode.Seal());
    }

    [Test]
    public void Cannot_change_value_on_sealed()
    {
        TrieNode trieNode = TrieNode.CreateLeafTyped(Keccak.Zero);
        Assert.Throws<InvalidOperationException>(() => trieNode.Value = new byte[5]);
    }

    [Test]
    public void Cannot_change_key_on_sealed()
    {
        TrieNode trieNode = TrieNode.CreateLeafTyped(Keccak.Zero);
        Assert.Throws<InvalidOperationException>(
            () => trieNode.Key = Bytes.FromHexString("aaa"));
    }

    [Test]
    public void Cannot_set_child_on_sealed()
    {
        TrieNode child = TrieNode.CreateLeafTyped(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped(Keccak.Zero);
        Assert.Throws<InvalidOperationException>(() => trieNode.SetChild(0, child));
    }

    [Test]
    public void Pruning_regression()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        trieNode.Key = Bytes.FromHexString("abcd");
        TreePath emptyPath = TreePath.Empty;
        trieNode.RlpEncode(NullTrieStore.Instance, ref emptyPath);
    }

    [Test]
    public void Extension_child_as_keccak()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        TreePath emptyPath = TreePath.Empty;
        // After step 4 made TrieNode abstract, no instance can be exactly the base type;
        // the test's intent is that the child slot is non-null after prune.
        trieNode.GetChild(NullTrieStore.Instance, ref emptyPath, 0).Should().NotBeNull();
    }

    [Test]
    public void Extension_child_as_keccak_memory_size()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        // Without parent RLP, PrunePersistedRecursively cannot drop the typed child
        // (the hash would be lost). Slot retains the typed reference.
        trieNode.PrunePersistedRecursively(1);
        // The exact byte count is shape-data only (no RLP, no Hash256 slot).
        trieNode.GetMemorySize(false).Should().BeGreaterThan(0);
    }

    [Test]
    public void Extension_child_as_keccak_clone()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        TrieNode cloned = trieNode.Clone();

        cloned.GetMemorySize(false).Should().BeGreaterThan(0);
    }

    [Test]
    public void Unresolve_of_persisted()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);
        trieNode.Key = Bytes.FromHexString("abcd");
        TreePath emptyPath = TreePath.Empty;
        trieNode.ResolveKey(NullTrieStore.Instance, ref emptyPath);

        trieNode.PrunePersistedRecursively(1);
        trieNode.PrunePersistedRecursively(1);
    }

    [Test]
    public void Small_child_unresolve()
    {
        TrieNode child = TrieNode.CreateLeafTyped();
        child.Value = Bytes.FromHexString("a");
        child.Key = Bytes.FromHexString("b");
        TreePath childPath = TreePath.FromHexString("abcd");
        child.ResolveKey(NullTrieStore.Instance, ref childPath);
        child.IsPersisted = true;

        TreePath emptyPath = TreePath.Empty;
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);
        trieNode.Key = Bytes.FromHexString("abcd");
        trieNode.ResolveKey(NullTrieStore.Instance, ref emptyPath);

        // After unresolve the slot is null and the canonical inline RLP lives in the
        // parent. Re-reading rebuilds an equivalent (but not reference-equal) child.
        trieNode.PrunePersistedRecursively(2);
        TrieNode? rebuilt = trieNode.GetChild(NullTrieStore.Instance, ref emptyPath, 0);
        rebuilt.Should().NotBeNull();
        rebuilt!.Key.Should().BeEquivalentTo(child.Key);
        rebuilt.Value.AsSpan().ToArray().Should().BeEquivalentTo(child.Value.AsSpan().ToArray());
    }

    [Test]
    public void Extension_child_as_keccak_not_dirty()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        trieNode.TryGetDirtyChild(0, out TrieNode? dirtyChild).Should().Be(false);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Extension_child_as_keccak_call_recursively(bool skipPersisted)
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        // Without parent RLP, PrunePersistedRecursively cannot drop the typed child;
        // CallRecursively therefore visits both the extension and its (Unknown) child.
        trieNode.PrunePersistedRecursively(1);
        int count = 0;
        TreePath emptyPath = TreePath.Empty;
        trieNode.CallRecursively((n, s, p) => count++, null, ref emptyPath, NullTrieStore.Instance, skipPersisted, LimboTraceLogger.Instance);
        count.Should().BeGreaterOrEqualTo(1);
    }

    [Test]
    public void Branch_child_as_keccak_encode()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(0, child);
        trieNode.SetChild(4, child);

        trieNode.PrunePersistedRecursively(1);
        TreePath emptyPath = TreePath.Empty;
        trieNode.RlpEncode(NullTrieStore.Instance, ref emptyPath);
    }

    [Test]
    public void Snap_stitched_branch_preserves_unmodified_by_hash_slot_hashes()
    {
        // Regression repro: simulates the snap-sync boundary stitching path.
        //
        // 1. Build a canonical branch with 4 by-hash children at slots 0, 4, 8, 12.
        //    Encode + re-decode through DecodeNode so the result has the exact shape a
        //    freshly-loaded branch would: _rlpArray retained, all 16 slots null, hashes
        //    pulled on demand via TryGetChildHash.
        // 2. Mutate slot 4 via SetChild with a NEW typed leaf whose keccak differs from
        //    the original slot-4 hash. This models snap-stitch recursion modifying an
        //    in-range subtree.
        // 3. Drop the old _rlpArray would normally happen if the encoder mistakenly
        //    rewrote it; instead the parent retains its original RLP because we only
        //    SetChild without ClearKeccak-then-encode-then-WriteRlp on the parent yet.
        // 4. Re-encode the parent and verify that:
        //    a. The mutated slot 4 encodes the NEW child's keccak.
        //    b. The unmodified slots 0, 8, 12 still encode the ORIGINAL canonical hashes
        //       from the parent's _rlpArray (the null-slot decode-on-demand path).
        //    c. The empty slots are still 0x80.
        //
        // Failure mode under the suspect bug: encoder emits stale / wrong bytes for the
        // unmodified by-hash slots, producing a parent whose hash is non-canonical and
        // whose re-decode yields a child hash the network never published.

        ValueHash256 hashAt0 = TestItem.Keccaks[0].ValueHash256;
        ValueHash256 hashAt4 = TestItem.Keccaks[1].ValueHash256;
        ValueHash256 hashAt8 = TestItem.Keccaks[2].ValueHash256;
        ValueHash256 hashAt12 = TestItem.Keccaks[3].ValueHash256;

        TrieNode source = TrieNode.CreateBranchTyped();
        source.SetChildHash(0, new Hash256(hashAt0));
        source.SetChildHash(4, new Hash256(hashAt4));
        source.SetChildHash(8, new Hash256(hashAt8));
        source.SetChildHash(12, new Hash256(hashAt12));

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> canonicalRlp = source.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        ValueHash256 canonicalHash = ValueKeccak.Compute(canonicalRlp.AsSpan());

        // Decode through the production DecodeNode contract: this yields a TrieNodeBranch
        // with retained _rlpArray, IsPersisted = true, all 16 slots null.
        TrieNode parent = TrieNode.DecodeNode(in emptyPath, in canonicalHash, canonicalRlp.AsSpan().ToArray());
        parent.IsBranch.Should().BeTrue();
        parent.GetRawChildRef(0).Should().BeNull("slot 0 should be unresolved (decoded on demand from RLP)");
        parent.GetRawChildRef(4).Should().BeNull("slot 4 should be unresolved");
        parent.GetRawChildRef(8).Should().BeNull("slot 8 should be unresolved");
        parent.GetRawChildRef(12).Should().BeNull("slot 12 should be unresolved");

        // Independent baseline: unmodified slots should still report the original canonical hashes.
        parent.TryGetChildHash(0, out ValueHash256 readBack0).Should().BeTrue();
        readBack0.Should().Be(hashAt0);
        parent.TryGetChildHash(4, out ValueHash256 readBack4).Should().BeTrue();
        readBack4.Should().Be(hashAt4);
        parent.TryGetChildHash(8, out ValueHash256 readBack8).Should().BeTrue();
        readBack8.Should().Be(hashAt8);
        parent.TryGetChildHash(12, out ValueHash256 readBack12).Should().BeTrue();
        readBack12.Should().Be(hashAt12);

        // Phase 2: mutate slot 4 with a typed leaf whose computed keccak DIFFERS from hashAt4.
        // Use a heavy-enough leaf to ensure ResolveKey produces a 32-byte hash entry.
        TrieNode replacementChild = TrieNode.CreateLeafTyped();
        replacementChild.Key = new byte[20];
        replacementChild.Value = Bytes.Concat(Keccak.EmptyTreeHash.Bytes, Keccak.EmptyTreeHash.Bytes);
        TreePath childPath = TreePath.Empty;
        childPath.AppendMut(4);
        replacementChild.ResolveKey(NullTrieNodeResolver.Instance, ref childPath);
        replacementChild.IsPersisted = true;
        ValueHash256 replacementHash = replacementChild.Keccak!.ValueHash256;
        replacementHash.Should().NotBe(hashAt4, "test setup: replacement must differ from original");

        parent.SetChild(4, replacementChild);

        // Phase 3: re-encode the parent. The encoder must:
        // - emit replacementHash at slot 4 (modified in-range slot)
        // - emit hashAt0, hashAt8, hashAt12 from the retained _rlpArray (unmodified by-hash slots)
        // - emit 0x80 (RLP empty) at slots 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15
        CappedArray<byte> reEncoded = parent.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);
        ValueHash256 reEncodedHash = ValueKeccak.Compute(reEncoded.AsSpan());

        // Phase 4: independently compute the expected canonical encoding by building a fresh
        // branch with the new slot-4 hash and the three unmodified hashes.
        TrieNode expectedParent = TrieNode.CreateBranchTyped();
        expectedParent.SetChildHash(0, new Hash256(hashAt0));
        expectedParent.SetChildHash(4, new Hash256(replacementHash));
        expectedParent.SetChildHash(8, new Hash256(hashAt8));
        expectedParent.SetChildHash(12, new Hash256(hashAt12));
        TreePath expectedPath = TreePath.Empty;
        CappedArray<byte> expectedRlp = expectedParent.RlpEncode(NullTrieNodeResolver.Instance, ref expectedPath);
        ValueHash256 expectedHash = ValueKeccak.Compute(expectedRlp.AsSpan());

        reEncoded.AsSpan().ToArray().Should().Equal(
            expectedRlp.AsSpan().ToArray(),
            "snap-stitched parent must encode bit-identically to a freshly-built branch with the same children");
        reEncodedHash.Should().Be(
            expectedHash,
            "snap-stitched parent keccak must match canonical recomputation");

        // Direct slot-hash readback after re-encode (write-through to _rlpArray).
        TrieNode reDecoded = TrieNode.DecodeNode(in emptyPath, in reEncodedHash, reEncoded.AsSpan().ToArray());
        reDecoded.TryGetChildHash(0, out ValueHash256 final0).Should().BeTrue();
        final0.Should().Be(hashAt0);
        reDecoded.TryGetChildHash(4, out ValueHash256 final4).Should().BeTrue();
        final4.Should().Be(replacementHash);
        reDecoded.TryGetChildHash(8, out ValueHash256 final8).Should().BeTrue();
        final8.Should().Be(hashAt8);
        reDecoded.TryGetChildHash(12, out ValueHash256 final12).Should().BeTrue();
        final12.Should().Be(hashAt12);
    }

    [Test]
    public void Branch_child_as_keccak_resolved()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(0, child);
        trieNode.SetChild(4, child);

        trieNode.PrunePersistedRecursively(1);
        ITrieNodeResolver trieStore = Substitute.For<ITrieNodeResolver>();
        trieStore.GetOrLoadNode(Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>()).Returns(child);
        TreePath emptyPath = TreePath.Empty;
        trieNode.GetChild(trieStore, ref emptyPath, 0).Should().Be(child);
        trieNode.GetChild(trieStore, ref emptyPath, 1).Should().BeNull();
        trieNode.GetChild(trieStore, ref emptyPath, 4).Should().Be(child);
    }

    [Test]
    public void Child_as_keccak_cached()
    {
        TrieNode child = new TrieSyncNode(Keccak.Zero);
        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.SetChild(0, child);

        trieNode.PrunePersistedRecursively(1);
        ITrieNodeResolver trieStore = Substitute.For<ITrieNodeResolver>();
        trieStore.GetOrLoadNode(Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>()).Returns(child);
        TreePath emptyPath = TreePath.Empty;
        trieNode.GetChild(trieStore, ref emptyPath, 0).Should().Be(child);
    }

    [Test]
    public void Batch_not_db_regression()
    {
        TrieNode child = TrieNode.CreateLeafTyped();
        child.Key = Bytes.FromHexString("abc");
        child.Value = new byte[200];
        child.Seal();

        TrieNode trieNode = TrieNode.CreateExtensionTyped();
        trieNode.Key = Bytes.FromHexString("000102030506");
        trieNode.SetChild(0, child);
        trieNode.Seal();

        // Empty store - the child was never persisted. After encoding the parent
        // retains the by-hash child reference; dropping the cached typed child
        // forces the lazy GetChild path to issue GetOrLoadNode against an empty
        // store, which surfaces TrieException via the missing-node path.
        IScopedTrieStore trieStore = new TestRawTrieStore(new MemDb()).GetTrieStore(null);
        TreePath emptyPath = TreePath.Empty;
        child.ResolveKey(trieStore, ref emptyPath);
        child.IsPersisted = true;
        trieNode.ResolveKey(trieStore, ref emptyPath);

        trieNode.UnresolveChild(0);
        Assert.Throws<MissingTrieNodeException>(() => trieNode.GetChild(trieStore, ref emptyPath, 0));
    }

    [Test]
    public async Task Trie_node_concurrent_child_hash_reads_are_safe()
    {
        TrieNode trieNode = TrieNode.CreateBranchTyped();
        for (int i = 0; i < 16; i++)
        {
            trieNode.SetChildHash(i, TestItem.Keccaks[i]);
        }

        trieNode.Seal();
        TreePath emptyPath = TreePath.Empty;
        trieNode.ResolveKey(Substitute.For<ITrieNodeResolver>(), ref emptyPath);

        void CheckChildren()
        {
            for (int i = 0; i < 16 * 10; i++)
            {
                trieNode.GetChildHash(i % 16).Should().BeEquivalentTo(TestItem.Keccaks[i % 16], i.ToString());
            }
        }

        List<Task> tasks = new();
        for (int i = 0; i < 4; i++)
        {
            Task task = new(CheckChildren);
            task.Start();
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
    }

    [Test]
    public void Rlp_is_cloned_when_cloning()
    {
        // Hash key scheme so child lookup at any path resolves by keccak alone:
        // the parent retains the by-hash references in its RLP, the lazy GetChild
        // path issues GetOrLoadNode, and the underlying store is keyed only by hash.
        TestRawTrieStore fullTrieStore = new(new NodeStorage(new MemDb(), INodeStorage.KeyScheme.Hash));
        IScopedTrieStore trieStore = fullTrieStore.GetTrieStore(null);

        TrieNode leaf1 = TrieNode.CreateLeafTyped();
        leaf1.Key = Bytes.FromHexString("abc");
        leaf1.Value = new byte[111];
        TreePath emptyPath = TreePath.Empty;
        leaf1.ResolveKey(trieStore, ref emptyPath);
        leaf1.Seal();

        TrieNode leaf2 = TrieNode.CreateLeafTyped();
        leaf2.Key = Bytes.FromHexString("abd");
        leaf2.Value = new byte[222];
        leaf2.ResolveKey(trieStore, ref emptyPath);
        leaf2.Seal();

        TreePath path = TreePath.Empty;

        using (IBlockCommitter _ = fullTrieStore.BeginBlockCommit(0))
        {
            using (ICommitter? committer = trieStore.BeginCommit(leaf2))
            {
                committer.CommitNode(ref path, leaf1);
                committer.CommitNode(ref path, leaf2);
            }
        }

        TrieNode trieNode = TrieNode.CreateBranchTyped();
        trieNode.SetChild(1, leaf1);
        trieNode.SetChild(2, leaf2);
        trieNode.ResolveKey(trieStore, ref emptyPath);
        CappedArray<byte> rlp = trieNode.FullRlp;

        TrieNode restoredBranch = TrieNode.CreateBranchTyped(rlp);

        TrieNode clone = restoredBranch.Clone();
        TrieNode restoredLeaf1 = clone.GetChild(trieStore, ref emptyPath, 1);
        restoredLeaf1.Should().NotBeNull();
        restoredLeaf1.Value.ToArray().Should().BeEquivalentTo(leaf1.Value.ToArray());
    }

    [Test]
    public void Can_parallel_read_unresolved_children()
    {
        TrieNode node = TrieNode.CreateBranchTyped();
        for (int i = 0; i < 16; i++)
        {
            TrieNode randomTrieNode = TrieNode.CreateLeafTyped();
            randomTrieNode.Key = new byte[] { (byte)i, 2, 3 };
            randomTrieNode.Value = new byte[] { 1, 2, 3 };
            node.SetChild(i, randomTrieNode);
        }

        TreePath emptyPath = TreePath.Empty;
        CappedArray<byte> rlp = node.RlpEncode(NullTrieNodeResolver.Instance, ref emptyPath);

        TrieNode restoredNode = TrieNode.CreateBranchTyped(rlp);

        Parallel.For(0, 32, (index, _) =>
        {
            TreePath emptyPathParallel = TreePath.Empty;
            restoredNode.GetChild(NullTrieNodeResolver.Instance, ref emptyPathParallel, index % 3);
        });
    }

    [Test]
    public void Do_Not_MarkUnpersistedChildAsPersisted()
    {
        InMemoryScopedTrieStore inMemoryScopedTrieStore = new();

        PatriciaTree tree = new(inMemoryScopedTrieStore, LimboLogs.Instance);
        tree.Set(Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000000"), [1]);
        tree.Set(Bytes.FromHexString("0000000000000000010000000000000000000000000000000000000000000000"), [1]);
        tree.Set(Bytes.FromHexString("0000000000000000011000000000000000000000000000000000000000000000"), [1]);
        tree.Commit();

        TreePath path = TreePath.FromHexString("00000000000000000");
        TrieNode parentExtension = inMemoryScopedTrieStore.GetOrLoadNode(path, Keccak.EmptyTreeHash.ValueHash256);
        parentExtension.IsPersisted = true;

        // Mark child as persisted
        TrieNode child = parentExtension.GetChild(inMemoryScopedTrieStore, ref path, 1);
        child.IsPersisted = true;

        // Trigger unresolve
        parentExtension.GetChild(inMemoryScopedTrieStore, ref path, 1);

        // Unmark persisted
        child.IsPersisted = false;

        // Should stay unpersisted
        child = parentExtension.GetChild(inMemoryScopedTrieStore, ref path, 1);
        Assert.That(child.IsPersisted, Is.False);
    }

    private class InMemoryScopedTrieStore : IScopedTrieStore
    {
        private readonly ConcurrentDictionary<TreePath, TrieNode> _nodes = new();

        private TrieNode GetOrAddNode(in TreePath path, TrieNode node) => _nodes.GetOrAdd(path, node);

        public TrieNode GetOrLoadNode(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) =>
            _nodes.GetOrAdd(path, new TrieSyncNode(in hash));

        public bool TryGetOrLoadNode(in TreePath path, in ValueHash256 hash, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TrieNode node, ReadFlags flags = ReadFlags.None)
        {
            node = _nodes.GetOrAdd(path, new TrieSyncNode(in hash));
            return true;
        }

        public byte[]? LoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) => null;

        public byte[]? TryLoadRlp(in TreePath path, in ValueHash256 hash, ReadFlags flags = ReadFlags.None) => null;

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => throw new InvalidOperationException($"{nameof(GetStorageTrieNodeResolver)} not supported");

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new Committer(this);

        private class Committer(InMemoryScopedTrieStore trieStore) : ICommitter
        {
            public void Dispose()
            {
            }

            public TrieNode CommitNode(ref TreePath path, TrieNode node) => trieStore.GetOrAddNode(path, node);
        }
    }

    private class Context
    {
        public TrieNode TiniestLeaf { get; }
        public TrieNode HeavyLeaf { get; }
        public TrieNode AccountLeaf { get; }

        public Context()
        {
            TiniestLeaf = TrieNode.CreateLeafTyped();
            TiniestLeaf.Key = new byte[] { 5 };
            TiniestLeaf.Value = new byte[] { 10 };

            HeavyLeaf = TrieNode.CreateLeafTyped();
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

        public void VisitBranch(in TreePathContext ctx, TrieNode node) => CollectionsMarshal.GetValueRefOrAddDefault(VisitBranchReceived, (ctx.Path, node), out _) += 1;

        public void VisitExtension(in TreePathContext ctx, TrieNode node) => CollectionsMarshal.GetValueRefOrAddDefault(VisitExtensionReceived, (ctx.Path, node), out _) += 1;

        public void VisitLeaf(in TreePathContext ctx, TrieNode node) => CollectionsMarshal.GetValueRefOrAddDefault(VisitLeafReceived, (ctx.Path, node, node.Value.ToArray()), out _) += 1;

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

    [Test]
    [Category("LongRunning")]
    public void FullRlp_concurrent_reads_and_writes_do_not_produce_torn_reads()
    {
        // Regression test: CappedArray<byte> is 12 bytes (ref + int), not atomically
        // readable on x64. The seqlock in TrieNode must ensure readers never observe
        // a length from one write paired with an array from another.
        byte[] rlp1 = new byte[100];
        Array.Fill(rlp1, (byte)0xAA);
        byte[] rlp2 = new byte[200];
        Array.Fill(rlp2, (byte)0xBB);

        TrieNode node = TrieNode.CreateLeafTyped(new CappedArray<byte>(rlp1));
        bool failed = false;
        const int iterations = 100_000;

        Parallel.Invoke(
            // Writer: alternate between two different-sized arrays via internal WriteRlp
            () =>
            {
                for (int i = 0; i < iterations && !Volatile.Read(ref failed); i++)
                {
                    CappedArray<byte> data = (i & 1) == 0
                        ? new CappedArray<byte>(rlp1)
                        : new CappedArray<byte>(rlp2);
                    node.WriteRlp(data);
                }
            },
            // Reader: verify length and array content are always consistent
            () =>
            {
                for (int i = 0; i < iterations && !Volatile.Read(ref failed); i++)
                {
                    CappedArray<byte> rlp = node.FullRlp;
                    if (rlp.IsNotNull && rlp.UnderlyingArray is not null)
                    {
                        int length = rlp.Length;
                        byte[]? array = rlp.UnderlyingArray;
                        // Detect length > array (classic torn read)
                        if (length > array!.Length) { Volatile.Write(ref failed, true); break; }
                        // Detect wrong-array-for-length (cross-read torn read)
                        if (length == 100 && array[0] != 0xAA) { Volatile.Write(ref failed, true); break; }
                        if (length == 200 && array[0] != 0xBB) { Volatile.Write(ref failed, true); break; }
                    }
                }
            }
        );

        failed.Should().BeFalse("a torn read was detected: length > array.Length");
    }

    [Test]
    public void FullRlp_seqlock_returns_consistent_length_and_array()
    {
        byte[] small = new byte[10];
        TrieNode node = TrieNode.CreateLeafTyped(new CappedArray<byte>(small));

        CappedArray<byte> result = node.FullRlp;
        result.IsNotNull.Should().BeTrue();
        result.Length.Should().Be(10);
        result.UnderlyingArray.Should().BeSameAs(small);
    }

    [Test]
    public void WriteRlp_preserves_non_zero_offset()
    {
        byte[] backing = [0, 1, 2, 3, 4];
        CappedArray<byte> slice = new(backing, 2, 2);
        TrieNode node = TrieNode.CreateLeafTyped(CappedArray<byte>.Empty);

        node.WriteRlp(slice);

        CappedArray<byte> result = node.FullRlp;
        result.UnderlyingArray.Should().BeSameAs(backing);
        result.Offset.Should().Be(2);
        result.AsSpan().ToArray().Should().Equal(new byte[] { 2, 3 });
    }

    [Test]
    [Category("LongRunning")]
    public void FullRlp_concurrent_writers_do_not_corrupt_seqlock()
    {
        byte[] rlp1 = new byte[50];
        Array.Fill(rlp1, (byte)0xCC);
        byte[] rlp2 = new byte[300];
        Array.Fill(rlp2, (byte)0xDD);
        TrieNode node = TrieNode.CreateLeafTyped(new CappedArray<byte>(rlp1));
        bool failed = false;
        const int iterations = 100_000;

        // Two concurrent writers + one reader
        Parallel.Invoke(
            () =>
            {
                for (int i = 0; i < iterations && !Volatile.Read(ref failed); i++)
                    node.WriteRlp(new CappedArray<byte>(rlp1));
            },
            () =>
            {
                for (int i = 0; i < iterations && !Volatile.Read(ref failed); i++)
                    node.WriteRlp(new CappedArray<byte>(rlp2));
            },
            () =>
            {
                for (int i = 0; i < iterations && !Volatile.Read(ref failed); i++)
                {
                    CappedArray<byte> rlp = node.FullRlp;
                    if (rlp.IsNotNull && rlp.UnderlyingArray is not null)
                    {
                        int length = rlp.Length;
                        byte[]? array = rlp.UnderlyingArray;
                        if (length > array!.Length) { Volatile.Write(ref failed, true); break; }
                        // Cross-check: length must match the array's content marker
                        if (length == 50 && array[0] != 0xCC) { Volatile.Write(ref failed, true); break; }
                        if (length == 300 && array[0] != 0xDD) { Volatile.Write(ref failed, true); break; }
                    }
                }
            }
        );

        failed.Should().BeFalse("seqlock corruption detected: invalid length or torn read");
    }
}
