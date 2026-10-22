// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's check that the secured-trie nodes its raw-key walks visit end at the key's length.</summary>
/// <remarks>Without hashing: in a ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest.</remarks>
[Parallelizable(ParallelScope.All)]
public class GuestSecuredPathTests
{
    private const int KeyNibbles = 64;
    private static readonly byte[] Key = RawKey(1);
    private static readonly byte[] KeyPath = Nibbles.BytesToNibbleBytes(Key);

    [Test]
    public void Visited_leaf_must_end_at_the_key([Values(-1, 0, 1)] int extraNibbles, [Values] bool write)
    {
        PatriciaTree tree = new() { RootRef = Branch((KeyPath[0], DecodedLeaf(LeafPath(extraNibbles)))) };

        Assert.That(() => ReadOrWrite(tree, write), extraNibbles == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    /// <remarks>An extension reaching the key's length, or a branch below it, leaves no nibble for the leaf.</remarks>
    [Test]
    public void Visited_extension_and_branch_must_stay_short_of_the_key([Values(KeyNibbles - 2, KeyNibbles - 1, KeyNibbles)] int extensionNibbles, [Values] bool write)
    {
        TrieNode leaf = DecodedLeaf(KeyPath[Math.Min(extensionNibbles + 1, KeyNibbles)..]);
        TrieNode extension = TrieNodeFactory.CreateExtension(KeyPath[..extensionNibbles], Branch((KeyPath[Math.Min(extensionNibbles, KeyNibbles - 1)], leaf)));
        PatriciaTree tree = new() { RootRef = extension };

        Assert.That(() => ReadOrWrite(tree, write), extensionNibbles < KeyNibbles ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    [Test]
    public void Unvisited_leaf_is_not_checked([Values] bool write)
    {
        PatriciaTree tree = new() { RootRef = Branch((KeyPath[0], DecodedLeaf(LeafPath(0))), (KeyPath[0] ^ 1, DecodedLeaf(new byte[KeyNibbles]))) };

        Assert.That(() => ReadOrWrite(tree, write), Throws.Nothing);
    }

    /// <remarks>Deleting the key collapses the branch into its sibling, which only then is visited.</remarks>
    [Test]
    public void Sibling_is_checked_once_a_delete_collapses_into_it([Values(-1, 0, 1)] int extraNibbles)
    {
        PatriciaTree tree = new()
        {
            RootRef = Branch((KeyPath[0], DecodedLeaf(LeafPath(0))), (KeyPath[0] ^ 1, DecodedLeaf(new byte[KeyNibbles - 1 + extraNibbles])))
        };

        Assert.That(() => tree.Set(Key, Array.Empty<byte>()), extraNibbles == 0 ? Throws.Nothing : Throws.TypeOf<TrieException>());
    }

    [Test]
    public void Unsecured_trie_lookup_misses_a_leaf_of_another_length()
    {
        byte[] shortKey = [0x12, 0x34];
        PatriciaTree tree = new() { RootRef = Branch((1, DecodedLeaf([2, 3, 4, 5]))) };

        Assert.That(tree.Get(shortKey).IsEmpty, Is.True);
    }

    private static void ReadOrWrite(PatriciaTree tree, bool write)
    {
        if (write) tree.Set(Key, [2]);
        else tree.Get(Key);
    }

    /// <summary>The key's path below a root branch, lengthened by <paramref name="extraNibbles"/>.</summary>
    private static byte[] LeafPath(int extraNibbles)
    {
        byte[] path = new byte[KeyNibbles - 1 + extraNibbles];
        KeyPath.AsSpan(1, Math.Min(path.Length, KeyNibbles - 1)).CopyTo(path);
        return path;
    }

    private static TrieNode Branch(params (int Index, TrieNode Child)[] children)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        foreach ((int index, TrieNode child) in children) branch[index] = child;
        return branch;
    }

    /// <summary>A sealed leaf decoded from RLP, as the guest reads one from its witness.</summary>
    private static TrieNode DecodedLeaf(byte[] key)
    {
        TreePath path = TreePath.Empty;
        byte[] rlp = TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>([1])).RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
        TrieNode leaf = new(NodeType.Unknown, rlp);
        leaf.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return leaf;
    }

    private static byte[] RawKey(byte seed)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 37 + seed * 101 + 5);
        return key;
    }
}
