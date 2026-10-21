// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's leaf key, kept in hex-prefix form until read, and the raw-key read walk matching it.</summary>
/// <remarks>Without hashing: in a ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest.</remarks>
[Parallelizable(ParallelScope.All)]
public class GuestLeafKeyTests
{
    [Test]
    public void Decoded_leaf_key_reads_as_its_nibbles([Values(0, 1, 2, 3, 4, 31, 64)] int keyLength)
    {
        byte[] key = NibblePath(keyLength);

        Assert.That(DecodedLeaf(key).Key, Is.EqualTo(key));
    }

    [TestCase(64, -1, ExpectedResult = true)]
    [TestCase(63, -1, ExpectedResult = true)]
    [TestCase(1, -1, ExpectedResult = true)]
    [TestCase(0, -1, ExpectedResult = true)]
    [TestCase(63, 0, ExpectedResult = false)]
    [TestCase(63, 1, ExpectedResult = false)]
    [TestCase(64, 0, ExpectedResult = false)]
    [TestCase(64, 63, ExpectedResult = false)]
    [TestCase(1, 0, ExpectedResult = false)]
    public bool Key_matches_the_suffix_of_a_raw_key(int keyLength, int flippedNibble)
    {
        byte[] rawKey = RawKey();
        byte[] leafKey = Nibbles.BytesToNibbleBytes(rawKey)[(64 - keyLength)..];
        if (flippedNibble >= 0) leafKey[flippedNibble] ^= 0x8;

        bool decoded = ((LeafData)DecodedLeaf(leafKey).NodeData!).KeyIsSuffixOf(rawKey, keyLength);
        bool built = ((LeafData)TrieNodeFactory.CreateLeaf(leafKey, new CappedArray<byte>([1])).NodeData!).KeyIsSuffixOf(rawKey, keyLength);

        Assert.That(built, Is.EqualTo(decoded));
        return decoded;
    }

    [Test]
    public void Key_of_another_length_does_not_match([Values(62, 63, 64)] int keyLength, [Values(-1, 1)] int lengthDelta)
    {
        byte[] rawKey = RawKey();
        byte[] leafKey = Nibbles.BytesToNibbleBytes(rawKey)[(64 - keyLength)..];

        Assert.That(((LeafData)DecodedLeaf(leafKey).NodeData!).KeyIsSuffixOf(rawKey, keyLength + lengthDelta), Is.False);
    }

    [Test]
    public void Key_stays_in_hex_prefix_form_across_a_value_change([Values(0, 1, 2, 31, 63, 64)] int keyLength)
    {
        byte[] key = NibblePath(keyLength);
        TrieNode leaf = DecodedLeaf(key).Unseal();
        leaf.Value = new CappedArray<byte>([2]);

        Assert.That(((LeafData)leaf.NodeData!).TryGetCompactKey(out _), Is.True);
        Assert.That(leaf.Key, Is.EqualTo(key));
    }

    [Test]
    public void Raw_key_read_finds_every_value_and_no_other()
    {
        PatriciaTree tree = new();
        byte[][] keys = new byte[40][];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = RawKey((byte)i);
            // Keys sharing their first bytes put extensions on the walk.
            if (i % 4 == 0) RawKey().AsSpan(0, 8).CopyTo(keys[i]);
            tree.Set(keys[i], [(byte)(i + 1)]);
        }

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.That(tree.Get(keys[i]).ToArray(), Is.EqualTo(new[] { (byte)(i + 1) }));
        }

        Assert.That(tree.Get(RawKey(0xFF)).IsEmpty, Is.True);
    }

    [Test]
    public void Sparse_branch_resolves_each_child_from_its_rlp([Values] bool longList)
    {
        const int embeddedIndex = 13;
        byte[] embedded = [0xc2, 0x20, 0x01];
        byte[]?[] children = new byte[TrieNode.BranchesCount][];
        for (int i = 0; i < children.Length; i++)
        {
            if (!(longList ? i % 3 == 0 : i == 9)) continue;
            // Empty-item bytes inside the hash, so a skip that lands in it reads a wrong child rather than falling back.
            byte[] hash = new byte[Hash256.Size];
            hash.AsSpan().Fill(0x80);
            hash[0] = (byte)(i + 1);
            children[i] = hash;
        }

        // An embedded child is resolved, and skipped over, by the general item scan.
        children[embeddedIndex] = embedded;
        TrieNode branch = new(NodeType.Unknown, SparseBranchRlp(children));
        branch.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < children.Length; i++)
            {
                TreePath path = TreePath.Empty;
                TrieNode? child = branch.GetChild(NullTrieNodeResolver.Instance, ref path, i);
                if (children[i] is null) Assert.That(child, Is.Null, $"child {i}");
                else if (i == embeddedIndex) Assert.That(child!.FullRlp.AsSpan().ToArray(), Is.EqualTo(embedded), $"child {i}");
                else Assert.That(child!.Keccak, Is.EqualTo(new Hash256(children[i]!)), $"child {i}");
            }
        }
    }

    /// <summary>A branch RLP holding <paramref name="children"/>, each a hash or an embedded node, and no value.</summary>
    private static byte[] SparseBranchRlp(byte[]?[] children)
    {
        List<byte> content = [];
        foreach (byte[]? child in children)
        {
            if (child is null) content.Add(0x80);
            else if (child.Length == Hash256.Size) content.AddRange([0xa0, .. child]);
            else content.AddRange(child);
        }

        content.Add(0x80);
        return content.Count < 56 ? [(byte)(0xc0 + content.Count), .. content] : [0xf8, (byte)content.Count, .. content];
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

    private static byte[] RawKey(byte seed = 0)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 37 + seed * 101 + 5);
        return key;
    }

    private static byte[] NibblePath(int length)
    {
        byte[] nibbles = new byte[length];
        for (int i = 0; i < length; i++) nibbles[i] = (byte)((i * 7 + 3) & 0xF);
        return nibbles;
    }
}
