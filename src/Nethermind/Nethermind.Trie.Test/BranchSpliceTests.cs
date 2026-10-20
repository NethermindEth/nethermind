// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

/// <summary>Re-encoding a branch that cannot be patched in place, which splices its changed children into its previous RLP.</summary>
/// <remarks>
/// The guest keeps the previous value rather than walking to it (see <c>TrieNode.zkevm.cs</c>), so
/// <c>Nethermind.Trie.ZkEvm.Test</c> links this file. Children are hashes or embedded leaves, so nothing is hashed: in a
/// ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest. The expected RLP comes from a
/// branch built from scratch.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class BranchSpliceTests
{
    public enum Change { Delete, Hash, Embedded }

    /// <remarks>Slots 1, 4, 7, 10 and 13 start empty, so a change there adds a child; 15 starts embedded.</remarks>
    [Test]
    public void Splice_matches_fresh_encoding(
        [Values(0, 1, 7, 14, 15)] int firstIndex, [Values] Change first,
        [Values(-1, 4, 9, 15)] int secondIndex, [Values] Change second)
    {
        TrieNode restored = DecodedBranch();
        TrieNode expected = FreshBranch();
        Apply(restored, expected, firstIndex, first);
        if (secondIndex > firstIndex) Apply(restored, expected, secondIndex, second);

        Assert.That(Encode(restored), Is.EqualTo(Encode(expected)));
    }

    [Test]
    public void Splice_across_a_length_header_size_change([Values] bool grow)
    {
        // 55 bytes of content fit a one-byte header and 56 need two: the branch crosses that boundary either way.
        TrieNode restored = new(NodeType.Branch);
        TrieNode expected = new(NodeType.Branch);
        int hashes = grow ? 1 : 2;
        for (int i = 0; i < hashes; i++)
        {
            restored.SetChild(i, HashChild(i));
            expected.SetChild(i, HashChild(i));
        }

        restored = Decode(Encode(restored));
        if (grow)
        {
            restored.SetChild(2, HashChild(2));
            expected.SetChild(2, HashChild(2));
        }
        else
        {
            restored.SetChild(1, null);
            expected.SetChild(1, null);
        }

        Assert.That(Encode(restored), Is.EqualTo(Encode(expected)));
    }

    /// <remarks>A full branch is patched in place unless a child is deleted or embedded, which falls back to the splice.</remarks>
    [Test]
    public void Splice_from_a_full_branch_matches_fresh_encoding([Values(0, 7, 15)] int index, [Values] Change change)
    {
        byte[] previous = Encode(FullBranch());
        Assert.That(previous, Has.Length.EqualTo(532));

        TrieNode restored = Decode(previous);
        TrieNode expected = FullBranch();
        Apply(restored, expected, index, change);

        Assert.That(Encode(restored), Is.EqualTo(Encode(expected)));
    }

    /// <remarks>The guest encodes a branch into scratch shared by its depth (see <c>TrieNode.zkevm.cs</c>), so a branch short
    /// enough to be embedded, whose RLP is kept, must not be left in it.</remarks>
    [Test]
    public void Embedded_branch_keeps_its_rlp_once_another_branch_at_its_depth_is_encoded([Values] bool restored)
    {
        TrieNode first = EmbeddedBranch(1, restored);
        TrieNode second = EmbeddedBranch(2, restored);

        ResolveKeyBelowRoot(first);
        ResolveKeyBelowRoot(second);

        Assert.That(first.Keccak, Is.Null);
        Assert.That(first.FullRlp.AsSpan().ToArray(), Is.EqualTo(Encode(EmbeddedBranch(1, restored: false))));
    }

    /// <summary>A branch of two embedded leaves, itself short enough to be embedded; when <paramref name="restored"/>, the
    /// second leaf is added after a decode, so the encoder splices it into the previous RLP.</summary>
    private static TrieNode EmbeddedBranch(int seed, bool restored)
    {
        TrieNode branch = new(NodeType.Branch);
        branch.SetChild(0, EmbeddedChild(seed));
        if (restored) branch = Decode(Encode(branch));
        branch.SetChild(1, EmbeddedChild(seed + 1));
        return branch;
    }

    private static void ResolveKeyBelowRoot(TrieNode node)
    {
        TreePath path = TreePath.FromNibble([0]);
        node.ResolveKey(NullTrieNodeResolver.Instance, ref path);
    }

    private static TrieNode FullBranch()
    {
        TrieNode branch = new(NodeType.Branch);
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            branch.SetChild(i, HashChild(i));
        }

        return branch;
    }

    private static void Apply(TrieNode restored, TrieNode expected, int index, Change change)
    {
        restored.SetChild(index, Child(change, index));
        expected.SetChild(index, Child(change, index));
    }

    private static TrieNode? Child(Change change, int index) => change switch
    {
        Change.Delete => null,
        Change.Hash => HashChild(0x40 + index),
        _ => EmbeddedChild(0x40 + index),
    };

    private static TrieNode FreshBranch()
    {
        TrieNode branch = new(NodeType.Branch);
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if (i % 3 == 1) continue;
            branch.SetChild(i, i == 15 ? EmbeddedChild(i) : HashChild(i));
        }

        return branch;
    }

    /// <summary>The branch <see cref="FreshBranch"/> builds, decoded from its RLP and unsealed, as a guest trie write leaves it.</summary>
    private static TrieNode DecodedBranch() => Decode(Encode(FreshBranch()));

    private static TrieNode Decode(byte[] rlp)
    {
        TrieNode node = new(NodeType.Unknown, rlp);
        node.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return node.Unseal();
    }

    private static TrieNode HashChild(int seed)
    {
        byte[] hash = new byte[Hash256.Size];
        hash.AsSpan().Fill((byte)seed);
        return new TrieNode(NodeType.Unknown, new Hash256(hash));
    }

    /// <summary>A leaf short enough to be embedded in its parent rather than hashed.</summary>
    private static TrieNode EmbeddedChild(int seed) => TrieNodeFactory.CreateLeaf([(byte)(seed & 0xF)], new CappedArray<byte>([(byte)seed]));

    private static byte[] Encode(TrieNode node)
    {
        TreePath path = TreePath.Empty;
        return node.RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
    }
}
