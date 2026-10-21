// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Buffers;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.ZkEvm.Test;

/// <summary>Tests for the guest's raw-key trie write, which creates, compares and splits leaves in hex-prefix form.</summary>
/// <remarks>The reference is the nibble walk, reached through a single-entry <c>BulkSet</c>. Without hashing: in a ZK_EVM build
/// the Keccak permutation is a zkVM precompile that throws outside the guest.
/// Writes must run nonparallel because the guest replaces <c>ThreadStatic</c> with a no-op, making the traversal stack process-wide.</remarks>
[NonParallelizable]
public class GuestRawKeyWriteTests
{
    [Test]
    public void Common_prefix_of_nibble_runs_matches_a_nibble_by_nibble_compare([Values(0, 1, 2, 3)] int leftStart, [Values(0, 1, 2, 5)] int rightStart)
    {
        byte[] left = RawKey(1);
        for (int changed = -1; changed < 2 * left.Length - leftStart; changed++)
        {
            byte[] right = new byte[left.Length + 2];
            for (int i = 0; i < 2 * left.Length - leftStart && rightStart + i < 2 * right.Length; i++)
            {
                SetNibble(right, rightStart + i, Nibbles.NibbleAt(left, leftStart + i) ^ (i == changed ? 0x5 : 0));
            }

            int maxCount = Math.Min(2 * left.Length - leftStart, 2 * right.Length - rightStart);
            foreach (int count in new[] { 0, 1, maxCount / 2, maxCount - 1, maxCount })
            {
                int expected = 0;
                while (expected < count && Nibbles.NibbleAt(left, leftStart + expected) == Nibbles.NibbleAt(right, rightStart + expected)) expected++;

                Assert.That(Nibbles.CommonPrefixLength(left, leftStart, right, rightStart, count), Is.EqualTo(expected), $"changed {changed}, count {count}");
            }
        }
    }

    [Test]
    public void Leaf_keyed_from_bytes_reads_and_encodes_as_its_nibbles([Range(0, 64)] int start)
    {
        byte[] rawKey = RawKey(2);
        byte[] nibbles = Nibbles.BytesToNibbleBytes(rawKey)[start..];
        CappedArray<byte> value = new([7, 8, 9]);

        TrieNode built = new(LeafData.WithKeyFrom(rawKey, start, value));
        TrieNode reference = TrieNodeFactory.CreateLeaf(nibbles, value);

        AssertSameLeaf(built, reference);
    }

    [Test]
    public void Raw_key_writes_build_the_trie_of_nibble_writes()
    {
        PatriciaTree tree = new();
        PatriciaTree reference = new();
        List<byte[]> keys = [];
        for (int i = 0; i < 48; i++)
        {
            byte[] key = RawKey((byte)i);
            // Keys sharing an odd or even number of leading nibbles with an earlier one split its leaf at either parity,
            // under an extension of that length.
            if (i % 3 != 0 && keys.Count > 0) ShareNibbles(keys[i * 5 % keys.Count], key, (i * 7) % 63 + 1);
            keys.Add(key);
            Write(tree, reference, key, [(byte)(i + 1)]);
            AssertSameTrie(tree.RootRef, reference.RootRef);
        }

        for (int i = 0; i < keys.Count; i += 2)
        {
            Write(tree, reference, keys[i], i % 4 == 0 ? [] : [(byte)(i + 100)]);
            AssertSameTrie(tree.RootRef, reference.RootRef);
        }

        // Writes that change nothing: a deleted key deleted again, a missing key deleted, and a value written again.
        Write(tree, reference, keys[0], []);
        Write(tree, reference, RawKey(0xEE), []);
        Write(tree, reference, keys[1], [2]);
        AssertSameTrie(tree.RootRef, reference.RootRef);
    }

    [Test]
    public void Raw_key_writes_reach_expanded_leaves_and_missing_keys()
    {
        PatriciaTree tree = new();
        PatriciaTree reference = new();
        byte[] a = RawKey(1);
        byte[] b = Sharing(a, 2, 9);
        byte[] c = Sharing(a, 3, 4);
        byte[] d = Sharing(b, 4, 20);

        void Step(byte[] key, byte[] value)
        {
            Write(tree, reference, key, value);
            AssertSameTrie(tree.RootRef, reference.RootRef);
        }

        Step(a, [1]);
        Step(b, [2]);
        Step(c, [3]);
        // Missing keys deleted: one diverging inside a leaf, one inside an extension.
        Step(Flipped(a, 40), []);
        Step(Flipped(a, 6), []);
        // Deleting a merges b into a leaf with an expanded key, which every write after it reaches: a missing key
        // diverging inside it, a value change, a split, a deletion, and an unchanged value.
        Step(a, []);
        Step(Flipped(b, 30), []);
        Step(b, [5]);
        Step(d, [6]);
        Step(b, []);
        Step(d, [6]);
    }

    [Test]
    public void Raw_key_write_splits_a_decoded_leaf([Values(0, 1, 2, 5, 62)] int sharedNibbles, [Values(0, 1)] int depth)
    {
        byte[] leafKey = RawKey(3);
        byte[] key = RawKey(4);
        ShareNibbles(leafKey, key, depth + sharedNibbles);
        if (Nibbles.NibbleAt(key, depth + sharedNibbles) == Nibbles.NibbleAt(leafKey, depth + sharedNibbles))
        {
            SetNibble(key, depth + sharedNibbles, Nibbles.NibbleAt(key, depth + sharedNibbles) ^ 0x1);
        }

        PatriciaTree tree = new() { RootRef = DecodedLeaf(Nibbles.BytesToNibbleBytes(leafKey)[depth..]) };
        PatriciaTree reference = new() { RootRef = DecodedLeaf(Nibbles.BytesToNibbleBytes(leafKey)[depth..]) };
        if (depth == 1)
        {
            // Under a branch, so the leaf's key starts at an odd nibble.
            tree.RootRef = Branch(Nibbles.NibbleAt(leafKey, 0), tree.RootRef);
            reference.RootRef = Branch(Nibbles.NibbleAt(leafKey, 0), reference.RootRef);
        }

        Write(tree, reference, key, [5]);

        AssertSameTrie(tree.RootRef, reference.RootRef);
    }

    [Test]
    public void Spliced_branch_encodes_as_the_branch_of_its_new_children([Values(0, 1, 9, 13, 15)] int changedIndex)
    {
        byte[]?[] children = new byte[TrieNode.BranchesCount][];
        for (int i = 0; i < children.Length; i += 3) children[i] = new Hash256(RawKey((byte)i)).BytesToArray();
        children[13] = [0xc2, 0x20, 0x01];
        TrieNode branch = new(NodeType.Unknown, GuestLeafKeyTests.SparseBranchRlp(children));
        branch.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);

        TrieNode child = TrieNodeFactory.CreateLeaf([1, 2], new CappedArray<byte>([7]));
        TrieNode changed = branch.Clone();
        changed[changedIndex] = child;
        children[changedIndex] = Encode(child);

        Assert.That(Encode(changed), Is.EqualTo(GuestLeafKeyTests.SparseBranchRlp(children)));
    }

    private static void Write(PatriciaTree tree, PatriciaTree reference, byte[] key, byte[] value)
    {
        tree.Set(key, value);
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(1);
        entries.Add(new PatriciaTree.BulkSetEntry(new ValueHash256(key), value));
        reference.BulkSet(entries);
    }

    private static void AssertSameTrie(TrieNode? actual, TrieNode? expected)
    {
        if (expected is null)
        {
            Assert.That(actual, Is.Null);
            return;
        }

        Assert.That(actual, Is.Not.Null);
        Assert.That(actual!.NodeType, Is.EqualTo(expected.NodeType));
        if (expected.IsLeaf)
        {
            AssertSameLeaf(actual, expected);
            return;
        }

        if (expected.IsExtension) Assert.That(actual.Key, Is.EqualTo(expected.Key));
        for (int i = 0; i < (expected.IsBranch ? 16 : 1); i++)
        {
            TreePath actualPath = TreePath.Empty;
            TreePath expectedPath = TreePath.Empty;
            AssertSameTrie(actual.GetChild(NullTrieNodeResolver.Instance, ref actualPath, i), expected.GetChild(NullTrieNodeResolver.Instance, ref expectedPath, i));
        }
    }

    private static void AssertSameLeaf(TrieNode actual, TrieNode expected)
    {
        byte[] actualRlp = Encode(actual);
        byte[] expectedRlp = Encode(expected);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualRlp, Is.EqualTo(expectedRlp));
            Assert.That(actual.Key, Is.EqualTo(expected.Key));
        }
    }

    private static byte[] Encode(TrieNode leaf)
    {
        TreePath path = TreePath.Empty;
        return leaf.RlpEncode(NullTrieNodeResolver.Instance, ref path).AsSpan().ToArray();
    }

    private static TrieNode Branch(int index, TrieNode child)
    {
        TrieNode branch = TrieNodeFactory.CreateBranch();
        branch[index] = child;
        return branch;
    }

    /// <summary>A sealed leaf decoded from RLP, as the guest reads one from its witness.</summary>
    private static TrieNode DecodedLeaf(byte[] key)
    {
        TrieNode leaf = new(NodeType.Unknown, Encode(TrieNodeFactory.CreateLeaf(key, new CappedArray<byte>([1]))));
        leaf.ResolveNode(NullTrieNodeResolver.Instance, TreePath.Empty);
        return leaf;
    }

    /// <summary>A key with the first <paramref name="count"/> nibbles of <paramref name="source"/> and a different next one.</summary>
    private static byte[] Sharing(byte[] source, byte seed, int count)
    {
        byte[] key = RawKey(seed);
        ShareNibbles(source, key, count);
        if (Nibbles.NibbleAt(key, count) == Nibbles.NibbleAt(source, count)) SetNibble(key, count, Nibbles.NibbleAt(key, count) ^ 0x1);
        return key;
    }

    private static byte[] Flipped(byte[] key, int nibble)
    {
        byte[] flipped = (byte[])key.Clone();
        SetNibble(flipped, nibble, Nibbles.NibbleAt(key, nibble) ^ 0x1);
        return flipped;
    }

    private static void ShareNibbles(byte[] source, byte[] key, int count)
    {
        for (int i = 0; i < count; i++) SetNibble(key, i, Nibbles.NibbleAt(source, i));
    }

    private static void SetNibble(byte[] bytes, int index, int nibble)
    {
        int shift = (~index & 1) << 2;
        bytes[index >> 1] = (byte)((bytes[index >> 1] & ~(0xF << shift)) | (nibble << shift));
    }

    private static byte[] RawKey(byte seed)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 37 + seed * 101 + 5);
        return key;
    }
}
